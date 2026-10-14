using Lancom.Core.Packets;

namespace Lancom.Core.Network;

public class JitterBuffer
{
    private readonly SortedDictionary<uint, LancomPacket> _packets = new();
    private readonly object _lock = new();
    private uint? _expectedSequence;
    private readonly int _targetBufferFrames;

    public event Action? OnMissingPacket;
    public event Action<LancomPacket>? OnPacketReady;

    public JitterBuffer(int targetBufferFrames = 1) // 1 frame = 20ms buffer
    {
        _targetBufferFrames = Math.Max(1, targetBufferFrames);
    }

    public void Push(LancomPacket packet)
    {
        lock (_lock)
        {
            if (packet.Type == MessageType.PttStart)
            {
                // New talk-spurt started: reset sequence
                _packets.Clear();
                _expectedSequence = packet.SequenceNumber;
            }

            if (_expectedSequence == null)
            {
                _expectedSequence = packet.SequenceNumber;
            }

            // Reject old packets
            if (packet.SequenceNumber < _expectedSequence.Value &&
                (_expectedSequence.Value - packet.SequenceNumber) < 1000)
            {
                return;
            }

            _packets[packet.SequenceNumber] = packet;

            // If we have buffered enough frames or sequence is ready, drain
            Drain();
        }
    }

    private void Drain()
    {
        if (_expectedSequence == null) return;

        // While packets are in order, dispatch
        while (_packets.TryGetValue(_expectedSequence.Value, out var packet))
        {
            _packets.Remove(_expectedSequence.Value);
            _expectedSequence = _expectedSequence.Value + 1;
            OnPacketReady?.Invoke(packet);
        }

        // If buffer depth exceeds threshold, skip missing packet and trigger PLC
        if (_packets.Count > _targetBufferFrames + 2)
        {
            // Missing packet deadline passed
            OnMissingPacket?.Invoke();
            _expectedSequence = _expectedSequence.Value + 1;

            // Try draining again with advanced sequence
            while (_packets.TryGetValue(_expectedSequence.Value, out var packet))
            {
                _packets.Remove(_expectedSequence.Value);
                _expectedSequence = _expectedSequence.Value + 1;
                OnPacketReady?.Invoke(packet);
            }
        }

        // Fast resync on large packet gap (>4 frames dropped in a burst)
        if (_packets.Count > 0 && _packets.Keys.First() > (_expectedSequence.Value + 4))
        {
            _expectedSequence = _packets.Keys.First();
            while (_packets.TryGetValue(_expectedSequence.Value, out var packet))
            {
                _packets.Remove(_expectedSequence.Value);
                _expectedSequence = _expectedSequence.Value + 1;
                OnPacketReady?.Invoke(packet);
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _packets.Clear();
            _expectedSequence = null;
        }
    }
}
