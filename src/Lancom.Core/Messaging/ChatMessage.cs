using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Lancom.Core.Messaging;

public enum FileCategory : byte
{
    GeneralFile = 0,
    Photo = 1,
    Video = 2
}

public class ChatMessage : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private Guid _id = Guid.NewGuid();
    public Guid Id
    {
        get => _id;
        set { _id = value; OnPropertyChanged(); }
    }

    private Guid _senderId;
    public Guid SenderId
    {
        get => _senderId;
        set { _senderId = value; OnPropertyChanged(); }
    }

    private string _senderName = string.Empty;
    public string SenderName
    {
        get => _senderName;
        set { _senderName = value; OnPropertyChanged(); }
    }

    private byte _channelId = 1;
    public byte ChannelId
    {
        get => _channelId;
        set { _channelId = value; OnPropertyChanged(); }
    }

    private Guid _targetUserId = Guid.Empty;
    public Guid TargetUserId
    {
        get => _targetUserId;
        set
        {
            _targetUserId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWhisper));
        }
    }
    public bool IsWhisper => TargetUserId != Guid.Empty;

    private string _text = string.Empty;
    public string Text
    {
        get => _text;
        set { _text = value; OnPropertyChanged(); }
    }

    private DateTime _timestamp = DateTime.UtcNow;
    public DateTime Timestamp
    {
        get => _timestamp;
        set
        {
            _timestamp = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FormattedTime));
        }
    }

    private bool _isOutgoing;
    public bool IsOutgoing
    {
        get => _isOutgoing;
        set { _isOutgoing = value; OnPropertyChanged(); }
    }

    private bool _isEncrypted;
    public bool IsEncrypted
    {
        get => _isEncrypted;
        set { _isEncrypted = value; OnPropertyChanged(); }
    }

    private bool _isDelivered;
    public bool IsDelivered
    {
        get => _isDelivered;
        set { _isDelivered = value; OnPropertyChanged(); }
    }

    // File / Media Transfer Attachment
    private Guid? _transferId;
    public Guid? TransferId
    {
        get => _transferId;
        set
        {
            _transferId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAttachment));
            OnPropertyChanged(nameof(IsFileTransferInProgress));
        }
    }

    private string? _fileName;
    public string? FileName
    {
        get => _fileName;
        set { _fileName = value; OnPropertyChanged(); }
    }

    private long _fileSize;
    public long FileSize
    {
        get => _fileSize;
        set
        {
            _fileSize = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FormattedSize));
        }
    }

    private FileCategory _category = FileCategory.GeneralFile;
    public FileCategory Category
    {
        get => _category;
        set
        {
            _category = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsImage));
            OnPropertyChanged(nameof(IsVideo));
        }
    }

    private string? _thumbnailBase64;
    public string? ThumbnailBase64
    {
        get => _thumbnailBase64;
        set
        {
            _thumbnailBase64 = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsImage));
        }
    }

    private string? _localFilePath;
    public string? LocalFilePath
    {
        get => _localFilePath;
        set { _localFilePath = value; OnPropertyChanged(); }
    }

    private double _transferProgress; // 0.0 to 1.0
    public double TransferProgress
    {
        get => _transferProgress;
        set
        {
            _transferProgress = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TransferPercentText));
        }
    }

    private bool _isTransferCompleted;
    public bool IsTransferCompleted
    {
        get => _isTransferCompleted;
        set
        {
            _isTransferCompleted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFileTransferInProgress));
        }
    }

    private bool _isTransferFailed;
    public bool IsTransferFailed
    {
        get => _isTransferFailed;
        set
        {
            _isTransferFailed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFileTransferInProgress));
        }
    }

    public bool HasAttachment => TransferId.HasValue;
    public bool IsImage => Category == FileCategory.Photo && !string.IsNullOrEmpty(ThumbnailBase64);
    public bool IsVideo => Category == FileCategory.Video;
    public bool IsFileTransferInProgress => HasAttachment && !IsTransferCompleted && !IsTransferFailed;

    public string TransferPercentText => $"{Math.Clamp((int)(TransferProgress * 100), 0, 100)}%";

    public string FormattedSize
    {
        get
        {
            if (FileSize < 1024) return $"{FileSize} B";
            if (FileSize < 1024 * 1024) return $"{FileSize / 1024.0:F1} KB";
            return $"{FileSize / (1024.0 * 1024.0):F1} MB";
        }
    }

    public string FormattedTime => Timestamp.ToLocalTime().ToString("HH:mm");
}
