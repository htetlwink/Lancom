using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Lancom.Core.Security;

/// <summary>
/// Hardware-accelerated AES-256-GCM End-to-End Encryption engine for Lancom voice frames.
/// Requires zero administrator privileges and zero extra drivers (100% user-mode .NET 9 BCL).
/// </summary>
public sealed class ChannelCrypto : IDisposable
{
    private static readonly byte[] Salt = "Lancom-Channel-E2EE-v1"u8.ToArray();
    private static readonly byte[] Info = "LancomVoiceKey"u8.ToArray();
    public const int TagSize = 16;
    public const int NonceSize = 12;

    private AesGcm? _aesGcm;
    private bool _isDisposed;

    /// <summary>
    /// Indicates whether channel encryption is currently active.
    /// </summary>
    public bool IsEnabled => _aesGcm != null && !_isDisposed;

    /// <summary>
    /// Initializes a new ChannelCrypto engine with an optional passphrase.
    /// </summary>
    public ChannelCrypto(string? passphrase = null)
    {
        SetPassphrase(passphrase);
    }

    /// <summary>
    /// Sets or updates the encryption passphrase. Passing null or empty disables encryption.
    /// </summary>
    public void SetPassphrase(string? passphrase)
    {
        _aesGcm?.Dispose();
        _aesGcm = null;

        if (string.IsNullOrWhiteSpace(passphrase))
        {
            return;
        }

        byte[] masterKey = Encoding.UTF8.GetBytes(passphrase);
        byte[] derivedKey = new byte[32]; // 256-bit AES key
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, derivedKey, Salt, Info);

        _aesGcm = new AesGcm(derivedKey, TagSize);
    }

    /// <summary>
    /// Encrypts an Opus audio payload using AES-256-GCM.
    /// Nonce is deterministically constructed from the packet sequence number and timestamp.
    /// Returns [16-byte Tag][Ciphertext].
    /// </summary>
    public byte[] Encrypt(uint sequenceNumber, ulong timestampMs, ReadOnlySpan<byte> plaintext)
    {
        if (_aesGcm == null || plaintext.IsEmpty)
        {
            return plaintext.ToArray();
        }

        Span<byte> nonce = stackalloc byte[NonceSize];
        BinaryPrimitives.WriteUInt32BigEndian(nonce[..4], sequenceNumber);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..12], timestampMs);

        byte[] output = new byte[TagSize + plaintext.Length];
        Span<byte> tagSpan = output.AsSpan(0, TagSize);
        Span<byte> cipherSpan = output.AsSpan(TagSize);

        _aesGcm.Encrypt(nonce, plaintext, cipherSpan, tagSpan);
        return output;
    }

    /// <summary>
    /// Attempts to decrypt an encrypted payload using AES-256-GCM.
    /// Returns true if authentication succeeded and plaintext was recovered.
    /// Returns false if authentication failed (wrong channel key or packet corrupted).
    /// </summary>
    public bool TryDecrypt(uint sequenceNumber, ulong timestampMs, ReadOnlySpan<byte> encryptedData, out byte[] decrypted)
    {
        decrypted = [];

        if (_aesGcm == null)
        {
            // If crypto is not initialized locally, cannot decrypt
            return false;
        }

        if (encryptedData.Length < TagSize)
        {
            return false;
        }

        Span<byte> nonce = stackalloc byte[NonceSize];
        BinaryPrimitives.WriteUInt32BigEndian(nonce[..4], sequenceNumber);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..12], timestampMs);

        ReadOnlySpan<byte> tag = encryptedData[..TagSize];
        ReadOnlySpan<byte> ciphertext = encryptedData[TagSize..];

        byte[] plainBuffer = new byte[ciphertext.Length];
        try
        {
            _aesGcm.Decrypt(nonce, ciphertext, tag, plainBuffer);
            decrypted = plainBuffer;
            return true;
        }
        catch (CryptographicException)
        {
            // Decryption/Authentication tag mismatch (e.g. wrong channel key)
            return false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _aesGcm?.Dispose();
        _aesGcm = null;
    }
}
