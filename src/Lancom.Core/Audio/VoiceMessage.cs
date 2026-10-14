namespace Lancom.Core.Audio;

public class VoiceMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SenderId { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public byte[] PcmData { get; set; } = Array.Empty<byte>();
    public int DurationSeconds { get; set; }
    public bool IsRead { get; set; }

    public VoiceMessage() { }

    public VoiceMessage(Guid senderId, string senderName, byte[] pcmData, int durationSeconds = 0, bool isRead = false)
    {
        Id = Guid.NewGuid();
        SenderId = senderId;
        SenderName = senderName;
        Timestamp = DateTime.UtcNow;
        PcmData = pcmData;
        IsRead = isRead;
        DurationSeconds = durationSeconds > 0
            ? durationSeconds
            : (int)Math.Max(1, Math.Round((double)pcmData.Length / (AudioConfig.SampleRate * sizeof(short) * AudioConfig.Channels)));
    }
}

public class VoiceReplayBuffer
{
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Queue<VoiceMessage> _buffer;

    public int Capacity => _capacity;

    public int Count
    {
        get
        {
            lock (_lock) return _buffer.Count;
        }
    }

    public VoiceReplayBuffer(int capacity = 3)
    {
        _capacity = capacity > 0 ? capacity : 3;
        _buffer = new Queue<VoiceMessage>(_capacity);
    }

    public void Add(VoiceMessage msg)
    {
        if (msg == null) return;

        lock (_lock)
        {
            while (_buffer.Count >= _capacity)
            {
                _buffer.Dequeue();
            }
            _buffer.Enqueue(msg);
        }
    }

    public VoiceMessage? GetLast()
    {
        lock (_lock)
        {
            if (_buffer.Count == 0) return null;

            VoiceMessage? last = null;
            foreach (var item in _buffer)
            {
                last = item;
            }
            return last;
        }
    }

    public IReadOnlyList<VoiceMessage> GetHistory()
    {
        lock (_lock)
        {
            return _buffer.ToArray();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Clear();
        }
    }
}

public class VoicemailBox
{
    private readonly object _lock = new();
    private readonly List<VoiceMessage> _messages = new();

    public event Action<VoiceMessage>? OnVoicemailReceived;

    public int Count
    {
        get
        {
            lock (_lock) return _messages.Count;
        }
    }

    public int UnreadCount
    {
        get
        {
            lock (_lock) return _messages.Count(m => !m.IsRead);
        }
    }

    public void AddVoicemail(VoiceMessage msg)
    {
        if (msg == null) return;

        lock (_lock)
        {
            _messages.Add(msg);
        }

        OnVoicemailReceived?.Invoke(msg);
    }

    public IReadOnlyList<VoiceMessage> GetUnread()
    {
        lock (_lock)
        {
            return _messages.Where(m => !m.IsRead).ToList();
        }
    }

    public IReadOnlyList<VoiceMessage> GetAll()
    {
        lock (_lock)
        {
            return _messages.ToList();
        }
    }

    public void MarkAllRead()
    {
        lock (_lock)
        {
            foreach (var message in _messages)
            {
                message.IsRead = true;
            }
        }
    }

    public void MarkAsRead(Guid id)
    {
        lock (_lock)
        {
            var msg = _messages.FirstOrDefault(m => m.Id == id);
            if (msg != null)
            {
                msg.IsRead = true;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _messages.Clear();
        }
    }
}
