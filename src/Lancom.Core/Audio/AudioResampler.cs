using System.Buffers.Binary;

namespace Lancom.Core.Audio;

/// <summary>
/// Fast mono PCM audio resampler optimized for voice communication and speech-to-text engines.
/// Converts 48,000 Hz 16-bit PCM mono into 16,000 Hz 16-bit PCM or normalized float arrays [-1.0f, 1.0f].
/// </summary>
public static class AudioResampler
{
    public const int SourceSampleRate = 48000;
    public const int TargetSampleRate = 16000;
    public const int DecimationFactor = SourceSampleRate / TargetSampleRate; // 3

    /// <summary>
    /// Resamples 48,000 Hz 16-bit mono PCM into 16,000 Hz 16-bit mono PCM using 3:1 decimation with a 3-tap anti-aliasing FIR filter.
    /// </summary>
    /// <param name="pcm48k">Raw 48kHz 16-bit mono PCM bytes.</param>
    /// <returns>Resampled 16kHz 16-bit mono PCM bytes.</returns>
    public static byte[] Resample48kTo16k(ReadOnlySpan<byte> pcm48k)
    {
        int totalSourceSamples = pcm48k.Length / 2;
        int targetSampleCount = totalSourceSamples / DecimationFactor;
        if (targetSampleCount <= 0)
        {
            return [];
        }

        byte[] output = new byte[targetSampleCount * 2];
        for (int i = 0; i < targetSampleCount; i++)
        {
            int sampleIndex = i * DecimationFactor;
            int byteOffset = sampleIndex * 2;

            short s0 = BinaryPrimitives.ReadInt16LittleEndian(pcm48k.Slice(byteOffset, 2));
            short s1 = BinaryPrimitives.ReadInt16LittleEndian(pcm48k.Slice(byteOffset + 2, 2));
            short s2 = BinaryPrimitives.ReadInt16LittleEndian(pcm48k.Slice(byteOffset + 4, 2));

            // 3-tap triangular FIR filter [0.25, 0.50, 0.25]
            int filtered = (s0 + (s1 << 1) + s2) >> 2;
            short outSample = (short)Math.Clamp(filtered, short.MinValue, short.MaxValue);

            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i * 2, 2), outSample);
        }

        return output;
    }

    /// <summary>
    /// Resamples 48,000 Hz 16-bit mono PCM into 16,000 Hz 16-bit mono PCM.
    /// </summary>
    public static byte[] Resample48kTo16k(byte[]? pcm48k)
    {
        if (pcm48k == null || pcm48k.Length < 6)
        {
            return [];
        }

        return Resample48kTo16k(pcm48k.AsSpan());
    }

    /// <summary>
    /// Resamples 48,000 Hz 16-bit mono PCM into 16,000 Hz float samples normalized [-1.0f, 1.0f] as required by Whisper.
    /// </summary>
    /// <param name="pcm48k">Raw 48kHz 16-bit mono PCM bytes.</param>
    /// <returns>Resampled 16kHz float array normalized between -1.0f and 1.0f.</returns>
    public static float[] Resample48kTo16kFloat(ReadOnlySpan<byte> pcm48k)
    {
        int totalSourceSamples = pcm48k.Length / 2;
        int targetSampleCount = totalSourceSamples / DecimationFactor;
        if (targetSampleCount <= 0)
        {
            return [];
        }

        float[] output = new float[targetSampleCount];
        for (int i = 0; i < targetSampleCount; i++)
        {
            int sampleIndex = i * DecimationFactor;
            int byteOffset = sampleIndex * 2;

            short s0 = BinaryPrimitives.ReadInt16LittleEndian(pcm48k.Slice(byteOffset, 2));
            short s1 = BinaryPrimitives.ReadInt16LittleEndian(pcm48k.Slice(byteOffset + 2, 2));
            short s2 = BinaryPrimitives.ReadInt16LittleEndian(pcm48k.Slice(byteOffset + 4, 2));

            // 3-tap triangular FIR filter [0.25, 0.50, 0.25]
            int filtered = (s0 + (s1 << 1) + s2) >> 2;
            output[i] = Math.Clamp((float)filtered / 32768.0f, -1.0f, 1.0f);
        }

        return output;
    }

    /// <summary>
    /// Resamples 48,000 Hz 16-bit mono PCM into 16,000 Hz float samples normalized [-1.0f, 1.0f].
    /// </summary>
    public static float[] Resample48kTo16kFloat(byte[]? pcm48k)
    {
        if (pcm48k == null || pcm48k.Length < 6)
        {
            return [];
        }

        return Resample48kTo16kFloat(pcm48k.AsSpan());
    }

    /// <summary>
    /// Creates a RIFF/WAVE memory stream with a 44-byte standard PCM header around 16-bit PCM data.
    /// </summary>
    /// <param name="pcmData">Raw PCM data bytes.</param>
    /// <param name="sampleRate">Sample rate in Hz (default: 16,000).</param>
    /// <param name="channels">Number of channels (default: 1 for mono).</param>
    /// <param name="bitsPerSample">Bits per sample (default: 16).</param>
    /// <returns>A readable MemoryStream positioned at offset 0 containing the valid WAV container.</returns>
    public static MemoryStream CreateWavStream(ReadOnlySpan<byte> pcmData, int sampleRate = TargetSampleRate, short channels = 1, short bitsPerSample = 16)
    {
        int bytesPerSample = bitsPerSample / 8;
        int byteRate = sampleRate * channels * bytesPerSample;
        short blockAlign = (short)(channels * bytesPerSample);
        int subchunk2Size = pcmData.Length;
        int chunkSize = 36 + subchunk2Size;

        Span<byte> header = stackalloc byte[44];

        // RIFF chunk descriptor
        header[0] = (byte)'R';
        header[1] = (byte)'I';
        header[2] = (byte)'F';
        header[3] = (byte)'F';
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), chunkSize);

        // WAVE format
        header[8] = (byte)'W';
        header[9] = (byte)'A';
        header[10] = (byte)'V';
        header[11] = (byte)'E';

        // "fmt " subchunk
        header[12] = (byte)'f';
        header[13] = (byte)'m';
        header[14] = (byte)'t';
        header[15] = (byte)' ';
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(28, 4), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(32, 2), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(34, 2), bitsPerSample);

        // "data" subchunk
        header[36] = (byte)'d';
        header[37] = (byte)'a';
        header[38] = (byte)'t';
        header[39] = (byte)'a';
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(40, 4), subchunk2Size);

        var stream = new MemoryStream(44 + subchunk2Size);
        stream.Write(header);
        stream.Write(pcmData);
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Creates a RIFF/WAVE memory stream with a 44-byte standard PCM header around 16-bit PCM data.
    /// </summary>
    public static MemoryStream CreateWavStream(byte[]? pcmData, int sampleRate = TargetSampleRate, short channels = 1, short bitsPerSample = 16)
    {
        return CreateWavStream(pcmData.AsSpan(), sampleRate, channels, bitsPerSample);
    }
}
