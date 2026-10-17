using System.Buffers.Binary;

namespace Lancom.Core.Messaging;

public class TextMessagePayload
{
    public Guid MessageId { get; set; } = Guid.NewGuid();
    public string SenderName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public ulong TimestampMs { get; set; }
}

public class MessageAckPayload
{
    public Guid MessageId { get; set; }
}

public class FileOfferPayload
{
    public Guid TransferId { get; set; } = Guid.NewGuid();
    public string SenderName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public FileCategory Category { get; set; } = FileCategory.GeneralFile;
    public int TotalChunks { get; set; }
    public int ChunkSize { get; set; } = 1200;
    public string? ThumbnailBase64 { get; set; }
}

public class FileOfferAnswerPayload
{
    public Guid TransferId { get; set; }
    public bool Accepted { get; set; }
}

public class FileChunkAckPayload
{
    public Guid TransferId { get; set; }
    public int ChunkIndex { get; set; }
    public bool IsComplete { get; set; }
}

public static class FileChunkSerializer
{
    // Chunk Binary Format:
    // [0..16]: TransferId (16 bytes)
    // [16..20]: ChunkIndex (4 bytes BigEndian)
    // [20..24]: TotalChunks (4 bytes BigEndian)
    // [24..]: Data
    public const int HeaderSize = 24;

    public static byte[] Serialize(Guid transferId, int chunkIndex, int totalChunks, byte[] chunkData)
    {
        byte[] buffer = new byte[HeaderSize + chunkData.Length];
        Span<byte> span = buffer;
        transferId.TryWriteBytes(span[..16]);
        BinaryPrimitives.WriteInt32BigEndian(span[16..20], chunkIndex);
        BinaryPrimitives.WriteInt32BigEndian(span[20..24], totalChunks);
        chunkData.CopyTo(span[HeaderSize..]);
        return buffer;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> buffer, out Guid transferId, out int chunkIndex, out int totalChunks, out byte[] chunkData)
    {
        transferId = Guid.Empty;
        chunkIndex = 0;
        totalChunks = 0;
        chunkData = Array.Empty<byte>();

        if (buffer.Length < HeaderSize) return false;

        transferId = new Guid(buffer[..16]);
        chunkIndex = BinaryPrimitives.ReadInt32BigEndian(buffer[16..20]);
        totalChunks = BinaryPrimitives.ReadInt32BigEndian(buffer[20..24]);

        int dataLen = buffer.Length - HeaderSize;
        chunkData = new byte[dataLen];
        buffer.Slice(HeaderSize, dataLen).CopyTo(chunkData);
        return true;
    }
}
