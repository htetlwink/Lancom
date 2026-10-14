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
    PageAlert = 7
}

public class LancomPacket
{
    public const ushort MagicHeader = 0x4C43; // 'L', 'C' (Lancom)
    public const ushort LegacyMagicHeader = 0x5754; // 'W', 'T' (Walkie-Talkie)
    public const byte CurrentVersion = 1;
    public const int HeaderSize = 36; // Fixed 36-byte binary header

    public byte Version { get; set; } = CurrentVersion;
    public MessageType Type { get; set; }
    public byte ChannelId { get; set; } = 1;
    public byte Flags { get; set; } = 0; // 0x01 = Priority/Emergency
    public uint SequenceNumber { get; set; }
    public ulong TimestampMs { get; set; }
    public Guid SenderId { get; set; }
    public Guid TargetUserId { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    public bool IsEmergency => (Flags & 0x01) != 0 || ChannelId == 0xFF;

    public byte[] Serialize()
    {
        byte[] payload = Payload;
        if ((payload == null || payload.Length == 0) && TargetUserId != Guid.Empty && Type == MessageType.PageAlert)
        {
            payload = Encoding.UTF8.GetBytes($"{TargetUserId}|");
        }
        payload ??= Array.Empty<byte>();

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

        packet = new LancomPacket
        {
            Version = version,
            Type = type,
            ChannelId = channelId,
            Flags = flags,
            SequenceNumber = seq,
            TimestampMs = timestamp,
            SenderId = senderId,
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
