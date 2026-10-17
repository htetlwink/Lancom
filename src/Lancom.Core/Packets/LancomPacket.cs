using System.Buffers.Binary;
using System.Text;

namespace Lancom.Core.Packets;

public enum MessageType : byte
{
    AudioData = 1,
    PttStart = 2,
    PttEnd = 3,
    PresenceBeacon = 4,
    Ping = 5,
    TextPing = 6,
    PageAlert = 7,
    WhisperVoice = 8,
    WhisperPttStart = 9,
    WhisperPttEnd = 10
}

public class LancomPacket
{
    public const ushort MagicHeader = 0x4C43; // 'L', 'C' (Lancom)
    public const ushort LegacyMagicHeader = 0x5754; // 'W', 'T' (Walkie-Talkie)
    public const byte CurrentVersion = 1;
    public const int HeaderSize = 36; // Fixed 36-byte binary header

    public const byte FlagEmergency = 0x01;
    public const byte FlagEncrypted = 0x02;
    public const byte FlagWhisper = 0x04;

    public byte Version { get; set; } = CurrentVersion;
    public MessageType Type { get; set; }
    public byte ChannelId { get; set; } = 1;
    public byte Flags { get; set; } = 0; // 0x01 = Priority/Emergency, 0x02 = Encrypted, 0x04 = Whisper
    public uint SequenceNumber { get; set; }
    public ulong TimestampMs { get; set; }
    public Guid SenderId { get; set; }
    public Guid TargetUserId { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    public bool IsEmergency => (Flags & FlagEmergency) != 0 || ChannelId == 0xFF;
    public bool IsEncrypted => (Flags & FlagEncrypted) != 0;
    public bool IsWhisper => (Flags & FlagWhisper) != 0 || Type == MessageType.WhisperVoice || TargetUserId != Guid.Empty;

    public byte[] Serialize()
    {
        byte[] payload = Payload ?? Array.Empty<byte>();

        if (TargetUserId != Guid.Empty &&
            (Type == MessageType.WhisperVoice || Type == MessageType.WhisperPttStart || Type == MessageType.WhisperPttEnd))
        {
            Flags |= FlagWhisper;
            byte[] whisperPayload = new byte[16 + payload.Length];
            TargetUserId.TryWriteBytes(whisperPayload.AsSpan(0, 16));
            if (payload.Length > 0)
            {
                payload.CopyTo(whisperPayload.AsSpan(16));
            }
            payload = whisperPayload;
        }
        else if (payload.Length == 0 && TargetUserId != Guid.Empty && Type == MessageType.PageAlert)
        {
            payload = Encoding.UTF8.GetBytes($"{TargetUserId}|");
        }

        byte[] buffer = new byte[HeaderSize + payload.Length];
        Span<byte> span = buffer;

        // 0-1: Magic
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], MagicHeader);
        // 2: Version
        span[2] = Version;
        // 3: Type
        span[3] = (byte)Type;
        // 4: ChannelId
        span[4] = ChannelId;
        // 5: Flags
        span[5] = Flags;
        // 6-9: SequenceNumber
        BinaryPrimitives.WriteUInt32BigEndian(span[6..10], SequenceNumber);
        // 10-17: TimestampMs
        BinaryPrimitives.WriteUInt64BigEndian(span[10..18], TimestampMs);
        // 18-33: SenderId (16 bytes)
        SenderId.TryWriteBytes(span[18..34]);
        // 34-35: PayloadLength
        BinaryPrimitives.WriteUInt16BigEndian(span[34..36], (ushort)payload.Length);

        if (payload.Length > 0)
        {
            payload.CopyTo(span[HeaderSize..]);
        }

        return buffer;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> data, out LancomPacket packet)
    {
        packet = null!;
        if (data.Length < HeaderSize) return false;

        ushort magic = BinaryPrimitives.ReadUInt16BigEndian(data[0..2]);
        if (magic != MagicHeader && magic != LegacyMagicHeader) return false;

        byte version = data[2];
        MessageType type = (MessageType)data[3];
        byte channelId = data[4];
        byte flags = data[5];
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(data[6..10]);
        ulong timestamp = BinaryPrimitives.ReadUInt64BigEndian(data[10..18]);
        Guid senderId = new(data[18..34]);
        ushort payloadLen = BinaryPrimitives.ReadUInt16BigEndian(data[34..36]);

        if (data.Length < HeaderSize + payloadLen) return false;

        byte[] payload = new byte[payloadLen];
        if (payloadLen > 0)
        {
            data.Slice(HeaderSize, payloadLen).CopyTo(payload);
        }

        Guid targetUserId = Guid.Empty;
        if ((flags & FlagWhisper) != 0 || 
            type == MessageType.WhisperVoice || 
            type == MessageType.WhisperPttStart || 
            type == MessageType.WhisperPttEnd)
        {
            if (payload.Length >= 16)
            {
                targetUserId = new Guid(payload.AsSpan(0, 16));
                payload = payload[16..];
            }
        }

        packet = new LancomPacket
        {
            Version = version,
            Type = type,
            ChannelId = channelId,
            Flags = flags,
            SequenceNumber = seq,
            TimestampMs = timestamp,
            SenderId = senderId,
            TargetUserId = targetUserId,
            Payload = payload
        };

        if (type == MessageType.PageAlert && payload.Length > 0)
        {
            if (TryParsePageAlert(packet, out var targetId, out _))
            {
                packet.TargetUserId = targetId;
            }
        }

        return true;
    }

    /// <summary>
    /// Creates a 1-on-1 whisper voice packet targeted to a specific peer.
    /// </summary>
    public static LancomPacket CreateWhisperVoice(Guid senderId, Guid targetUserId, uint seq, ulong timestamp, byte[] opusPayload, bool isEncrypted = false)
    {
        byte flags = FlagWhisper;
        if (isEncrypted)
        {
            flags |= FlagEncrypted;
        }

        return new LancomPacket
        {
            Type = MessageType.WhisperVoice,
            SenderId = senderId,
            TargetUserId = targetUserId,
            SequenceNumber = seq,
            TimestampMs = timestamp != 0 ? timestamp : (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Flags = flags,
            Payload = opusPayload ?? Array.Empty<byte>()
        };
    }

    /// <summary>
    /// Creates a 1-on-1 whisper PTT start packet announcing transmission start to a specific peer.
    /// </summary>
    public static LancomPacket CreateWhisperPttStart(Guid senderId, Guid targetUserId, string senderName)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(senderName ?? string.Empty);

        return new LancomPacket
        {
            Type = MessageType.WhisperPttStart,
            SenderId = senderId,
            TargetUserId = targetUserId,
            TimestampMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Flags = FlagWhisper,
            Payload = payloadBytes
        };
    }

    /// <summary>
    /// Creates a 1-on-1 whisper PTT end packet announcing transmission completion to a specific peer.
    /// </summary>
    public static LancomPacket CreateWhisperPttEnd(Guid senderId, Guid targetUserId)
    {
        return new LancomPacket
        {
            Type = MessageType.WhisperPttEnd,
            SenderId = senderId,
            TargetUserId = targetUserId,
            TimestampMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Flags = FlagWhisper,
            Payload = Array.Empty<byte>()
        };
    }

    public static LancomPacket CreatePageAlert(Guid senderId, Guid targetUserId, string senderName, byte channelId)
    {
        string payloadStr = $"{targetUserId}|{senderName}";
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadStr);

        return new LancomPacket
        {
            Type = MessageType.PageAlert,
            ChannelId = channelId,
            SenderId = senderId,
            TargetUserId = targetUserId,
            TimestampMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = payloadBytes
        };
    }

    public static bool TryParsePageAlert(LancomPacket packet, out Guid targetUserId, out string senderName)
    {
        targetUserId = packet.TargetUserId;
        senderName = string.Empty;

        if (packet.Payload == null || packet.Payload.Length == 0)
        {
            return targetUserId != Guid.Empty;
        }

        try
        {
            string str = Encoding.UTF8.GetString(packet.Payload);
            int sepIndex = str.IndexOf('|');
            if (sepIndex >= 0)
            {
                if (Guid.TryParse(str[..sepIndex], out var parsedGuid))
                {
                    targetUserId = parsedGuid;
                    senderName = str[(sepIndex + 1)..];
                    return true;
                }
            }
            else if (Guid.TryParse(str, out var directGuid))
            {
                targetUserId = directGuid;
                return true;
            }
        }
        catch { }

        if (packet.Payload.Length >= 16)
        {
            targetUserId = new Guid(packet.Payload.AsSpan(0, 16));
            if (packet.Payload.Length > 16)
            {
                try
                {
                    senderName = Encoding.UTF8.GetString(packet.Payload, 16, packet.Payload.Length - 16);
                }
                catch { }
            }
            return true;
        }

        return targetUserId != Guid.Empty;
    }

    public static LancomPacket CreateBeacon(Guid senderId, string displayName, byte channelId, int audioPort, bool isAfk = false, int afkMinutes = 0)
    {
        string payloadStr = $"{displayName}|{audioPort}|{isAfk}|{afkMinutes}";
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadStr);

        return new LancomPacket
        {
            Type = MessageType.PresenceBeacon,
            ChannelId = channelId,
            SenderId = senderId,
            TimestampMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = payloadBytes
        };
    }

    public static bool TryParseBeaconPayload(byte[]? payload, out string displayName, out int audioPort, out bool isAfk, out int afkMinutes)
    {
        displayName = string.Empty;
        audioPort = 0;
        isAfk = false;
        afkMinutes = 0;

        if (payload == null || payload.Length == 0)
            return false;

        try
        {
            string payloadStr = Encoding.UTF8.GetString(payload);
            return TryParseBeaconString(payloadStr, out displayName, out audioPort, out isAfk, out afkMinutes);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryParseBeaconString(string? payloadStr, out string displayName, out int audioPort, out bool isAfk, out int afkMinutes)
    {
        displayName = string.Empty;
        audioPort = 0;
        isAfk = false;
        afkMinutes = 0;

        if (string.IsNullOrEmpty(payloadStr))
            return false;

        string[] parts = payloadStr.Split('|');

        if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
        {
            displayName = parts[0];
        }

        if (parts.Length > 1 && int.TryParse(parts[1], out int port))
        {
            audioPort = port;
        }

        if (parts.Length > 2 && bool.TryParse(parts[2], out bool afk))
        {
            isAfk = afk;
        }

        if (parts.Length > 3 && int.TryParse(parts[3], out int minutes))
        {
            afkMinutes = minutes;
        }

        return true;
    }

    public static bool TryParseBeacon(LancomPacket packet, out string displayName, out int audioPort, out bool isAfk, out int afkMinutes)
    {
        if (packet == null || packet.Type != MessageType.PresenceBeacon)
        {
            displayName = string.Empty;
            audioPort = 0;
            isAfk = false;
            afkMinutes = 0;
            return false;
        }

        return TryParseBeaconPayload(packet.Payload, out displayName, out audioPort, out isAfk, out afkMinutes);
    }
}
