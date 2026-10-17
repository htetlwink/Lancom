using System.Net;
using System.Net.Sockets;
using Lancom.Core.Network;
using Lancom.Core.Packets;

namespace Lancom.Windows.Network;

public class WindowsUdpTransport : IDisposable
{
    private readonly int _port;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private readonly PeerManager _peerManager;
    private readonly Guid _localId;
    private string _displayName;
    private byte _currentChannel = 1;
    private IPEndPoint? _relayEndPoint;
    private uint _sequenceCounter;
    private readonly List<NetworkAdapterInfo> _adapters;

    public event Action<LancomPacket, IPEndPoint>? OnPacketReceived;

    public IPEndPoint? RelayEndPoint
    {
        get => _relayEndPoint;
        set => _relayEndPoint = value;
    }

    public byte CurrentChannel
    {
        get => _currentChannel;
        set => _currentChannel = value;
    }

    public string DisplayName
    {
        get => _displayName;
        set => _displayName = value;
    }

    public WindowsUdpTransport(Guid localId, string displayName, PeerManager peerManager, int port = 37733)
    {
        _localId = localId;
        _displayName = displayName;
        _peerManager = peerManager;
        _port = port;
        _adapters = PhysicalNetworkHelper.GetPhysicalAdapters();
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
            _udpClient.EnableBroadcast = true;

            try
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                _udpClient.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null);
            }
            catch { }

            Task.Run(() => ReceiveLoopAsync(_cts.Token));
            Task.Run(() => BeaconLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Transport Bind Error] {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(ct);

                if (!LancomPacket.TryDeserialize(result.Buffer, out var packet)) continue;

                // Ignore packets originating from this machine
                if (packet.SenderId == _localId) continue;

                // Update peer table if presence beacon
                if (packet.Type == MessageType.PresenceBeacon)
                {
                    string name = "Colleague";
                    bool isAfk = false;
                    int afkMinutes = 0;
                    if (LancomPacket.TryParseBeacon(packet, out var parsedName, out _, out var parsedAfk, out var parsedMinutes))
                    {
                        if (!string.IsNullOrWhiteSpace(parsedName)) name = parsedName;
                        isAfk = parsedAfk;
                        afkMinutes = parsedMinutes;
                    }

                    _peerManager.RegisterOrUpdate(packet.SenderId, name, result.RemoteEndPoint, packet.ChannelId, isAfk, afkMinutes);
                }

                OnPacketReceived?.Invoke(packet, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Transport Receive Error] {ex.Message}");
            }
        }
    }

    private async Task BeaconLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2.0));
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
        {
            SendPresenceBeacon();
        }
    }

    public bool IsAfk { get; set; }
    public int AfkMinutes { get; set; }

    public void SendPresenceBeacon()
    {
        var beacon = LancomPacket.CreateBeacon(_localId, _displayName, _currentChannel, _port, IsAfk, AfkMinutes);
        beacon.SequenceNumber = Interlocked.Increment(ref _sequenceCounter);
        BroadcastPacket(beacon);
    }

    public void BroadcastPacket(LancomPacket packet)
    {
        packet.SenderId = _localId;
        packet.ChannelId = _currentChannel;
        packet.TimestampMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        byte[] raw = packet.Serialize();

        // 1. If Relay is configured (corporate AP isolation mode), send to relay
        if (_relayEndPoint != null)
        {
            SendRaw(raw, _relayEndPoint);
            return;
        }

        // 2. Direct Unicast to known peers on this channel
        var targetPeers = _peerManager.GetPeersForChannel(_currentChannel);
        foreach (var peer in targetPeers)
        {
            SendRaw(raw, peer.EndPoint);
        }

        // 3. For beacons, floor requests, or page alerts, also broadcast on all physical subnets
        if (packet.Type == MessageType.PresenceBeacon || 
            packet.Type == MessageType.PttStart || 
            packet.Type == MessageType.PttEnd || 
            packet.Type == MessageType.PageAlert)
        {
            foreach (var adapter in _adapters)
            {
                SendRaw(raw, new IPEndPoint(adapter.BroadcastAddress, _port));
            }
            // Global fallback broadcast
            SendRaw(raw, new IPEndPoint(IPAddress.Broadcast, _port));
        }
    }

    public void SendTo(LancomPacket packet, IPEndPoint destination)
    {
        packet.SenderId = _localId;
        if (packet.TimestampMs == 0)
        {
            packet.TimestampMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        byte[] raw = packet.Serialize();

        if (_relayEndPoint != null)
        {
            SendRaw(raw, _relayEndPoint);
            return;
        }

        SendRaw(raw, destination);
    }

    private void SendRaw(byte[] data, IPEndPoint destination)
    {
        if (_udpClient == null) return;
        try
        {
            _udpClient.Send(data, data.Length, destination);
        }
        catch
        {
            // Drop on individual interface failure
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
