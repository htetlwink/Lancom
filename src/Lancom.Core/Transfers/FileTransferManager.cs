using System.Collections.Concurrent;
using System.Security.Cryptography;
using Lancom.Core.Messaging;
using Lancom.Core.Packets;

namespace Lancom.Core.Transfers;

public class OutboundTransfer
{
    public Guid TransferId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public byte ChannelId { get; set; }
    public Guid TargetUserId { get; set; }
    public FileCategory Category { get; set; }
    public int TotalChunks { get; set; }
    public int ChunkSize { get; set; } = 1200;
    public bool IsAccepted { get; set; }
    public int LastAcknowledgedChunk { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsCancelled { get; set; }
    public string? ThumbnailBase64 { get; set; }
    public TaskCompletionSource<bool> CompletionTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public class InboundTransfer
{
    public Guid TransferId { get; set; }
    public Guid SenderId { get; set; }
    public byte ChannelId { get; set; } = 1;
    public string SenderName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public FileCategory Category { get; set; }
    public int TotalChunks { get; set; }
    public int ChunkSize { get; set; }
    public string TempFilePath { get; set; } = string.Empty;
    public string FinalFilePath { get; set; } = string.Empty;
    public bool[] ReceivedChunks { get; set; } = Array.Empty<bool>();
    public int ReceivedChunkCount { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsCancelled { get; set; }
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityTime { get; set; } = DateTime.UtcNow;
    public DateTime LastMissingRequestTime { get; set; } = DateTime.MinValue;
    public int MissingRequestCount { get; set; }
    public FileStream? Stream { get; set; }
    public object Lock { get; } = new();
}

public class FileTransferManager : IDisposable
{
    private const int DefaultChunkSize = 1200;
    private readonly string _downloadDir;
    private readonly ConcurrentDictionary<Guid, OutboundTransfer> _outbound = new();
    private readonly ConcurrentDictionary<Guid, InboundTransfer> _inbound = new();
    private readonly Timer _auditTimer;

    public event Action<Guid, double>? OnTransferProgress;
    public event Action<Guid, string>? OnTransferCompleted; // TransferId, FinalFilePath
    public event Action<Guid, string>? OnTransferFailed; // TransferId, ErrorMessage
    public event Action<Guid, Guid, byte, List<int>>? OnMissingChunksRequested; // TransferId, SenderId, ChannelId, MissingIndices

    public string DownloadDirectory => _downloadDir;

    public FileTransferManager(string? downloadDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(downloadDirectory))
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _downloadDir = Path.Combine(localAppData, "Lancom", "Downloads");
        }
        else
        {
            _downloadDir = downloadDirectory;
        }

        Directory.CreateDirectory(_downloadDir);
        _auditTimer = new Timer(AuditInboundTransfers, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    public static FileCategory CategorizeFile(string extension)
    {
        string ext = extension.ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".webp")
            return FileCategory.Photo;
        if (ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".m4v")
            return FileCategory.Video;
        return FileCategory.GeneralFile;
    }

    public static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        byte[] hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public OutboundTransfer PrepareOutboundTransfer(string filePath, byte channelId, Guid targetUserId, string senderName, string? thumbnailBase64 = null)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists) throw new FileNotFoundException("File not found", filePath);

        long fileSize = fileInfo.Length;
        int totalChunks = (int)Math.Ceiling((double)fileSize / DefaultChunkSize);
        if (totalChunks == 0) totalChunks = 1;

        string sha256 = ComputeSha256(filePath);
        FileCategory category = CategorizeFile(fileInfo.Extension);
        Guid transferId = Guid.NewGuid();

        var transfer = new OutboundTransfer
        {
            TransferId = transferId,
            FilePath = filePath,
            FileName = fileInfo.Name,
            FileSize = fileSize,
            Sha256 = sha256,
            ChannelId = channelId,
            TargetUserId = targetUserId,
            Category = category,
            TotalChunks = totalChunks,
            ChunkSize = DefaultChunkSize,
            ThumbnailBase64 = thumbnailBase64
        };

        _outbound[transferId] = transfer;
        return transfer;
    }

    public FileOfferPayload CreateOfferPayload(OutboundTransfer transfer, string senderName)
    {
        return new FileOfferPayload
        {
            TransferId = transfer.TransferId,
            SenderName = senderName,
            FileName = transfer.FileName,
            FileSize = transfer.FileSize,
            Sha256 = transfer.Sha256,
            ChannelId = transfer.ChannelId,
            Category = transfer.Category,
            TotalChunks = transfer.TotalChunks,
            ChunkSize = transfer.ChunkSize,
            ThumbnailBase64 = transfer.ThumbnailBase64
        };
    }

    public async Task StreamChunksAsync(Guid transferId, Func<int, int, byte[], Task> sendChunkCallback, CancellationToken ct = default)
    {
        if (!_outbound.TryGetValue(transferId, out var transfer)) return;

        using var fileStream = new FileStream(transfer.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] buffer = new byte[transfer.ChunkSize];

        for (int i = 0; i < transfer.TotalChunks; i++)
        {
            if (ct.IsCancellationRequested || transfer.IsCancelled) break;

            int bytesRead = await fileStream.ReadAsync(buffer, ct);
            if (bytesRead <= 0 && transfer.FileSize > 0) break;

            byte[] chunkData = new byte[bytesRead];
            Buffer.BlockCopy(buffer, 0, chunkData, 0, bytesRead);

            await sendChunkCallback(i, transfer.TotalChunks, chunkData);

            double progress = Math.Min(0.95, (double)(i + 1) / transfer.TotalChunks);
            OnTransferProgress?.Invoke(transferId, progress);

            // Adaptive LAN pacing: Burst send 8 chunks before yielding to prevent socket buffer exhaustion while avoiding 15ms Windows timer throttling
            if (i % 8 == 0)
            {
                await Task.Delay(1, ct);
            }
        }

        if (ct.IsCancellationRequested || transfer.IsCancelled) return;

        // Active reliability handshake: Wait for Receiver's FileChunkAck(isComplete: true)
        int timeoutSeconds = Math.Max(30, (int)(transfer.FileSize / (150 * 1024)) + 15);
        int maxRetries = Math.Max(25, timeoutSeconds * 1000 / 800);
        int retry = 0;

        while (!transfer.IsCompleted && !transfer.IsCancelled && !ct.IsCancellationRequested && retry < maxRetries)
        {
            var completedTask = await Task.WhenAny(transfer.CompletionTcs.Task, Task.Delay(800, ct));
            if (completedTask == transfer.CompletionTcs.Task && transfer.IsCompleted)
            {
                break;
            }

            if (transfer.IsCompleted || transfer.IsCancelled || ct.IsCancellationRequested)
            {
                break;
            }

            retry++;

            // Retransmit the last chunk to stimulate receiver response if stalled
            if (transfer.TotalChunks > 0)
            {
                int lastChunkIndex = transfer.TotalChunks - 1;
                long lastOffset = (long)lastChunkIndex * transfer.ChunkSize;
                fileStream.Seek(lastOffset, SeekOrigin.Begin);
                int lastBytes = await fileStream.ReadAsync(buffer, ct);
                if (lastBytes > 0)
                {
                    byte[] lastData = new byte[lastBytes];
                    Buffer.BlockCopy(buffer, 0, lastData, 0, lastBytes);
                    await sendChunkCallback(lastChunkIndex, transfer.TotalChunks, lastData);
                }
            }
        }

        if (transfer.IsCompleted)
        {
            OnTransferProgress?.Invoke(transferId, 1.0);
            OnTransferCompleted?.Invoke(transferId, transfer.FilePath);
        }
        else if (!transfer.IsCancelled)
        {
            transfer.IsCancelled = true;
            OnTransferFailed?.Invoke(transferId, "Transfer timed out waiting for receiver confirmation.");
        }
    }

    public InboundTransfer RegisterInboundTransfer(FileOfferPayload offer, Guid senderId, byte channelId = 1)
    {
        if (_inbound.TryGetValue(offer.TransferId, out var existing))
        {
            return existing;
        }

        string safeFileName = Path.GetFileName(offer.FileName);
        string tempFileName = $"{offer.TransferId}_{safeFileName}.tmp";
        string tempFilePath = Path.Combine(_downloadDir, tempFileName);

        // Resolve final destination path (avoiding overwrite if file already exists)
        string finalFilePath = ResolveUniqueFilePath(Path.Combine(_downloadDir, safeFileName));

        var inbound = new InboundTransfer
        {
            TransferId = offer.TransferId,
            SenderId = senderId,
            ChannelId = channelId != 0 ? channelId : offer.ChannelId,
            SenderName = offer.SenderName,
            FileName = safeFileName,
            FileSize = offer.FileSize,
            Sha256 = offer.Sha256,
            Category = offer.Category,
            TotalChunks = offer.TotalChunks,
            ChunkSize = offer.ChunkSize,
            TempFilePath = tempFilePath,
            FinalFilePath = finalFilePath,
            ReceivedChunks = new bool[offer.TotalChunks]
        };

        try
        {
            inbound.Stream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            if (offer.FileSize > 0)
            {
                inbound.Stream.SetLength(offer.FileSize);
            }
        }
        catch (Exception ex)
        {
            OnTransferFailed?.Invoke(offer.TransferId, ex.Message);
        }

        if (_inbound.TryAdd(offer.TransferId, inbound))
        {
            return inbound;
        }

        // Handle race condition: dispose redundant stream and return existing
        inbound.Stream?.Dispose();
        inbound.Stream = null;
        return _inbound[offer.TransferId];
    }

    public void ProcessIncomingChunk(Guid transferId, int chunkIndex, int totalChunks, byte[] chunkData, Action<Guid, int, bool> sendAckCallback)
    {
        if (!_inbound.TryGetValue(transferId, out var transfer)) return;
        if (transfer.IsCancelled) return;

        lock (transfer.Lock)
        {
            if (transfer.IsCompleted)
            {
                // Already completed! Re-send completion ACK in case sender missed it.
                sendAckCallback(transferId, chunkIndex, true);
                return;
            }

            if (chunkIndex < 0 || chunkIndex >= transfer.TotalChunks) return;
            transfer.LastActivityTime = DateTime.UtcNow;

            if (transfer.ReceivedChunks[chunkIndex]) return; // already got this chunk

            try
            {
                if (transfer.Stream != null)
                {
                    long offset = (long)chunkIndex * transfer.ChunkSize;
                    transfer.Stream.Seek(offset, SeekOrigin.Begin);
                    transfer.Stream.Write(chunkData, 0, chunkData.Length);
                }

                transfer.ReceivedChunks[chunkIndex] = true;
                transfer.ReceivedChunkCount++;

                double progress = (double)transfer.ReceivedChunkCount / transfer.TotalChunks;
                OnTransferProgress?.Invoke(transferId, progress);

                bool isComplete = transfer.ReceivedChunkCount >= transfer.TotalChunks;

                if (isComplete)
                {
                    transfer.Stream?.Flush();
                    transfer.Stream?.Dispose();
                    transfer.Stream = null;

                    // Verify SHA-256
                    string calculatedHash = ComputeSha256(transfer.TempFilePath);
                    if (string.Equals(calculatedHash, transfer.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Move(transfer.TempFilePath, transfer.FinalFilePath, overwrite: true);
                        transfer.IsCompleted = true;
                        sendAckCallback(transferId, chunkIndex, true);
                        OnTransferCompleted?.Invoke(transferId, transfer.FinalFilePath);
                    }
                    else
                    {
                        transfer.IsCancelled = true;
                        try { File.Delete(transfer.TempFilePath); } catch { }
                        OnTransferFailed?.Invoke(transferId, "SHA-256 integrity hash verification failed.");
                    }
                }
                else
                {
                    if (chunkIndex == transfer.TotalChunks - 1 && transfer.ReceivedChunkCount < transfer.TotalChunks)
                    {
                        if ((DateTime.UtcNow - transfer.LastMissingRequestTime).TotalMilliseconds >= 300)
                        {
                            TriggerMissingChunksRequest(transfer);
                        }
                    }
                    else if (chunkIndex % 16 == 0)
                    {
                        sendAckCallback(transferId, chunkIndex, false);
                    }
                }
            }
            catch (Exception ex)
            {
                transfer.IsCancelled = true;
                OnTransferFailed?.Invoke(transferId, ex.Message);
            }
        }
    }

    public void HandleChunkAck(Guid transferId, int chunkIndex, bool isComplete)
    {
        if (_outbound.TryGetValue(transferId, out var transfer))
        {
            transfer.LastAcknowledgedChunk = chunkIndex;
            if (isComplete)
            {
                transfer.IsCompleted = true;
                transfer.CompletionTcs.TrySetResult(true);
            }
        }
    }

    public async Task ResendChunksAsync(Guid transferId, IEnumerable<int> chunkIndices, Func<int, int, byte[], Task> sendChunkCallback, CancellationToken ct = default)
    {
        if (!_outbound.TryGetValue(transferId, out var transfer)) return;
        if (transfer.IsCancelled) return;

        try
        {
            using var fileStream = new FileStream(transfer.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] buffer = new byte[transfer.ChunkSize];

            int count = 0;
            foreach (int index in chunkIndices)
            {
                if (ct.IsCancellationRequested || transfer.IsCancelled) break;
                if (index < 0 || index >= transfer.TotalChunks) continue;

                long offset = (long)index * transfer.ChunkSize;
                if (offset >= transfer.FileSize) continue;

                fileStream.Seek(offset, SeekOrigin.Begin);
                int toRead = (int)Math.Min(transfer.ChunkSize, transfer.FileSize - offset);
                int bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                if (bytesRead <= 0) continue;

                byte[] chunkData = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, chunkData, 0, bytesRead);

                await sendChunkCallback(index, transfer.TotalChunks, chunkData);

                count++;
                if (count % 8 == 0)
                {
                    await Task.Delay(1, ct);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ResendChunks Error] {ex.Message}");
        }
    }

    private void AuditInboundTransfers(object? state)
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var inbound in _inbound.Values)
            {
                if (inbound.IsCompleted || inbound.IsCancelled) continue;

                lock (inbound.Lock)
                {
                    if (inbound.IsCompleted || inbound.IsCancelled) continue;

                    if (inbound.ReceivedChunkCount < inbound.TotalChunks)
                    {
                        var referenceTime = inbound.ReceivedChunkCount > 0 ? inbound.LastActivityTime : inbound.StartTime;
                        if ((now - referenceTime).TotalMilliseconds >= 300)
                        {
                            if (inbound.MissingRequestCount < 30)
                            {
                                inbound.MissingRequestCount++;
                                inbound.LastActivityTime = now;
                                TriggerMissingChunksRequest(inbound);
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }

    private void TriggerMissingChunksRequest(InboundTransfer inbound)
    {
        var missing = new List<int>();
        for (int i = 0; i < inbound.TotalChunks; i++)
        {
            if (!inbound.ReceivedChunks[i])
            {
                missing.Add(i);
                if (missing.Count >= 150) break;
            }
        }

        if (missing.Count > 0)
        {
            inbound.LastMissingRequestTime = DateTime.UtcNow;
            OnMissingChunksRequested?.Invoke(inbound.TransferId, inbound.SenderId, inbound.ChannelId, missing);
        }
    }

    private static string ResolveUniqueFilePath(string desiredPath)
    {
        if (!File.Exists(desiredPath)) return desiredPath;

        string dir = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        string nameWithoutExt = Path.GetFileNameWithoutExtension(desiredPath);
        string ext = Path.GetExtension(desiredPath);

        int counter = 1;
        while (true)
        {
            string candidate = Path.Combine(dir, $"{nameWithoutExt} ({counter}){ext}");
            if (!File.Exists(candidate)) return candidate;
            counter++;
        }
    }

    public void CancelTransfer(Guid transferId)
    {
        if (_outbound.TryGetValue(transferId, out var outT))
        {
            outT.IsCancelled = true;
            outT.CompletionTcs.TrySetCanceled();
        }

        if (_inbound.TryGetValue(transferId, out var inT))
        {
            lock (inT.Lock)
            {
                inT.IsCancelled = true;
                inT.Stream?.Dispose();
                inT.Stream = null;
                try { if (File.Exists(inT.TempFilePath)) File.Delete(inT.TempFilePath); } catch { }
            }
        }
    }

    public void Dispose()
    {
        _auditTimer.Dispose();
        foreach (var inT in _inbound.Values)
        {
            lock (inT.Lock)
            {
                inT.Stream?.Dispose();
                inT.Stream = null;
            }
        }
        _inbound.Clear();
        _outbound.Clear();
    }
}
