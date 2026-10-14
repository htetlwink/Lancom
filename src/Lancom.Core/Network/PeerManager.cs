using System.Collections.Concurrent;
using System.Net;
using Lancom.Core.Packets;

namespace Lancom.Core.Network;

public class PeerManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, PeerInfo> _peers = new();
    private readonly Timer _cleanupTimer;

    public event Action<PeerInfo>? OnPeerJoined;
    public event Action<PeerInfo>? OnPeerLeft;
    public event Action<PeerInfo>? OnPeerUpdated;

    public PeerManager()
    {
        // Check for timed out peers every 2.5 seconds
        _cleanupTimer = new Timer(CleanupStalePeers, null, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(2.5));
    }

    public void RegisterOrUpdate(Guid id, string displayName, IPEndPoint endPoint, byte channelId, bool isAfk = false, int afkMinutes = 0)
    {
        if (displayName.Contains('|') && LancomPacket.TryParseBeaconString(displayName, out var parsedName, out _, out var parsedAfk, out var parsedMinutes))
        {
            displayName = string.IsNullOrWhiteSpace(parsedName) ? displayName : parsedName;
            isAfk = parsedAfk;
            afkMinutes = parsedMinutes;
        }

        bool isNew = false;
        var peer = _peers.AddOrUpdate(id,
            addValueFactory: key =>
            {
                isNew = true;
                return new PeerInfo
                {
                    Id = key,
                    DisplayName = displayName,
                    EndPoint = endPoint,
                    ChannelId = channelId,
                    IsAfk = isAfk,
                    AfkMinutes = afkMinutes,
                    LastSeen = DateTime.UtcNow
                };
            },
            updateValueFactory: (key, existing) =>
            {
                existing.DisplayName = displayName;
                existing.EndPoint = endPoint;
                existing.ChannelId = channelId;
                existing.IsAfk = isAfk;
                existing.AfkMinutes = afkMinutes;
                existing.LastSeen = DateTime.UtcNow;
                return existing;
            });

        if (isNew)
        {
            OnPeerJoined?.Invoke(peer);
        }
        else
        {
            OnPeerUpdated?.Invoke(peer);
        }
    }

    public void UpdateFromBeacon(LancomPacket beaconPacket, IPEndPoint endPoint)
    {
        if (beaconPacket == null || beaconPacket.Type != MessageType.PresenceBeacon) return;

        string displayName = "Colleague";
        bool isAfk = false;
        int afkMinutes = 0;

        if (LancomPacket.TryParseBeacon(beaconPacket, out var name, out _, out var afk, out var minutes))
        {
            if (!string.IsNullOrWhiteSpace(name)) displayName = name;
            isAfk = afk;
            afkMinutes = minutes;
        }

        RegisterOrUpdate(beaconPacket.SenderId, displayName, endPoint, beaconPacket.ChannelId, isAfk, afkMinutes);
    }

    public void UpdateFromBeacon(Guid senderId, byte[] payload, IPEndPoint endPoint, byte channelId)
    {
        string displayName = "Colleague";
        bool isAfk = false;
        int afkMinutes = 0;

        if (LancomPacket.TryParseBeaconPayload(payload, out var name, out _, out var afk, out var minutes))
        {
            if (!string.IsNullOrWhiteSpace(name)) displayName = name;
            isAfk = afk;
            afkMinutes = minutes;
        }

        RegisterOrUpdate(senderId, displayName, endPoint, channelId, isAfk, afkMinutes);
    }

    public bool TryGetPeer(Guid id, out PeerInfo? peer) => _peers.TryGetValue(id, out peer);

    public PeerInfo? GetPeer(Guid id) => _peers.TryGetValue(id, out var peer) ? peer : null;

    public void SetTransmitting(Guid id, bool isTransmitting)
    {
        if (_peers.TryGetValue(id, out var peer))
        {
            peer.IsTransmitting = isTransmitting;
            OnPeerUpdated?.Invoke(peer);
        }
    }

    public IReadOnlyList<PeerInfo> GetActivePeers()
    {
        return _peers.Values.Where(p => p.IsActive()).ToList();
    }

    public IReadOnlyList<PeerInfo> GetPeersForChannel(byte channelId)
    {
        // 0xFF = All-Call receives everything; otherwise match channel or All-Call
        return _peers.Values
            .Where(p => p.IsActive() && (p.ChannelId == channelId || channelId == 0xFF))
            .ToList();
    }

    private void CleanupStalePeers(object? state)
    {
        foreach (var kvp in _peers)
        {
            if (!kvp.Value.IsActive())
            {
                if (_peers.TryRemove(kvp.Key, out var removed))
                {
                    OnPeerLeft?.Invoke(removed);
                }
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        _peers.Clear();
    }
}
