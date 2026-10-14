using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Lancom.Core.Packets;

namespace Lancom.Relay;

public class RelayClient
{
    public Guid Id { get; set; }
    public IPEndPoint EndPoint { get; set; } = null!;
    public byte ChannelId { get; set; } = 1;
    public string DisplayName { get; set; } = "Unknown";
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public bool IsActive(int timeoutSeconds = 10) => (DateTime.UtcNow - LastSeen).TotalSeconds < timeoutSeconds;
}

public class RelayServer : IDisposable
{
    private readonly int _port;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<Guid, RelayClient> _clients = new();
    private long _packetsForwarded;
    private long _bytesForwarded;

    public int ActiveClientCount => _clients.Values.Count(c => c.IsActive());
    public long PacketsForwarded => _packetsForwarded;
    public long BytesForwarded => _bytesForwarded;

    public RelayServer(int port = 37733)
    {
        _port = port;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
        _udpClient.EnableBroadcast = true;

        Task.Run(() => ReceiveLoopAsync(_cts.Token));
        Task.Run(() => CleanupLoopAsync(_cts.Token));
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(ct);
                ProcessPacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Relay Error] {ex.Message}");
                Console.ResetColor();
            }
        }
    }

    private void ProcessPacket(byte[] data, IPEndPoint senderEndPoint)
    {
        if (!LancomPacket.TryDeserialize(data, out var packet)) return;

        // Register or update client session
        var client = _clients.AddOrUpdate(packet.SenderId,
            addValueFactory: id => new RelayClient
            {
                Id = id,
                EndPoint = senderEndPoint,
                ChannelId = packet.ChannelId,
                LastSeen = DateTime.UtcNow
            },
            updateValueFactory: (id, existing) =>
            {
                existing.EndPoint = senderEndPoint;
                existing.ChannelId = packet.ChannelId;
                existing.LastSeen = DateTime.UtcNow;
                return existing;
            });

        if (packet.Type == MessageType.PresenceBeacon)
        {
            try
            {
                string payload = System.Text.Encoding.UTF8.GetString(packet.Payload);
                string[] parts = payload.Split('|');
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                {
                    client.DisplayName = parts[0];
                }
            }
            catch { }
        }

        // Forward packet to all other active clients on this channel (or all if 0xFF)
        ForwardPacket(packet, data, senderEndPoint);
    }

    private void ForwardPacket(LancomPacket packet, byte[] rawBytes, IPEndPoint senderEndPoint)
    {
        if (_udpClient == null) return;

        foreach (var client in _clients.Values)
        {
            // Do not echo back to the sender
            if (client.EndPoint.Equals(senderEndPoint)) continue;
            if (!client.IsActive()) continue;

            // Channel filter: Match channel, or emergency broadcast (0xFF)
            if (packet.ChannelId != 0xFF && client.ChannelId != packet.ChannelId) continue;

            try
            {
                _udpClient.Send(rawBytes, rawBytes.Length, client.EndPoint);
                Interlocked.Increment(ref _packetsForwarded);
                Interlocked.Add(ref _bytesForwarded, rawBytes.Length);
            }
            catch
            {
                // Non-blocking drop on failure
            }
        }
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
        {
            foreach (var kvp in _clients)
            {
                if (!kvp.Value.IsActive())
                {
                    _clients.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
