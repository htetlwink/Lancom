using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;

namespace Lancom.Core.Network;

public class PeerInfo : INotifyPropertyChanged
{
    private Guid _id;
    private string _displayName = string.Empty;
    private IPEndPoint _endPoint = null!;
    private byte _channelId = 1;
    private DateTime _lastSeen = DateTime.UtcNow;
    private bool _isTransmitting;
    private int _rttMs;
    private bool _isAfk;
    private int _afkMinutes;

    public Guid Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetField(ref _displayName, value))
            {
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    public IPEndPoint EndPoint
    {
        get => _endPoint;
        set => SetField(ref _endPoint, value);
    }

    public byte ChannelId
    {
        get => _channelId;
        set => SetField(ref _channelId, value);
    }

    public DateTime LastSeen
    {
        get => _lastSeen;
        set => SetField(ref _lastSeen, value);
    }

    public bool IsTransmitting
    {
        get => _isTransmitting;
        set => SetField(ref _isTransmitting, value);
    }

    public int RttMs
    {
        get => _rttMs;
        set => SetField(ref _rttMs, value);
    }

    public bool IsAfk
    {
        get => _isAfk;
        set
        {
            if (SetField(ref _isAfk, value))
            {
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    public int AfkMinutes
    {
        get => _afkMinutes;
        set
        {
            if (SetField(ref _afkMinutes, value))
            {
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    public string StatusDisplay => IsAfk ? $"{DisplayName} [AFK {AfkMinutes}m]" : DisplayName;

    public bool IsActive(int timeoutSeconds = 6) => (DateTime.UtcNow - LastSeen).TotalSeconds < timeoutSeconds;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
