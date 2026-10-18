using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using Lancom.Core.Audio;
using Lancom.Core.Codec;
using Lancom.Core.Dsp;
using Lancom.Core.Network;
using Lancom.Core.Packets;
using Lancom.Core.Security;
using Lancom.App.Subtitles;
using Lancom.Windows.Audio;
using Lancom.Windows.Input;
using Lancom.Windows.Network;
using Lancom.Core.Messaging;
using Lancom.Core.Transfers;
using Lancom.App.Utils;
using System.Windows.Media.Imaging;

namespace Lancom.App.ViewModels;

public class PttKeyPreset
{
    public string Name { get; set; } = string.Empty;
    public int VKey { get; set; }

    public override string ToString() => Name;
}

public class LancomSettings
{
    public Guid LocalId { get; set; } = Guid.Empty;
    public string DisplayName { get; set; } = Environment.MachineName;
    public byte CurrentChannel { get; set; } = 1;
    public int TargetVKey { get; set; } = 0x14;
    public RogerBeepType SelectedRogerBeep { get; set; } = RogerBeepType.MotorolaTactical;
    public bool EnableRadioFilter { get; set; } = true;
    public string RelayHost { get; set; } = string.Empty;
    public string ChannelKey { get; set; } = string.Empty;
    public bool EnableSubtitles { get; set; } = true;
    public Dictionary<string, string> PeerNicknames { get; set; } = new();
}

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private Guid _localId;
    private readonly ConcurrentDictionary<string, string> _peerNicknames = new();
    private string _displayName = Environment.MachineName;
    private byte _currentChannel = 1;
    private bool _isTransmitting;
    private bool _isReceiving;
    private string _activeSpeakerName = "Channel Idle";
    private float _activeSpeakerLevel;
    private float _localMicLevel;
    private RogerBeepType _selectedRogerBeep = RogerBeepType.MotorolaTactical;
    private bool _enableRadioFilter = true;
    private string _pttKeyDisplay = "Caps Lock / Mouse 4";
    private string _relayHost = string.Empty;
    private uint _sequenceCounter;
    private bool _isRebinding;
    private PttKeyPreset? _selectedPttPreset;

    // Core Engines
    private readonly PeerManager _peerManager;
    private readonly ConcentusOpusCodec _opusCodec;
    private readonly RadioDsp _radioDsp;
    private readonly WasapiAudioCapture _capture;
    private readonly WasapiAudioPlayback _playback;
    private readonly WindowsUdpTransport _transport;
    private readonly RawInputPttHook _pttHook;
    private readonly JitterBuffer _jitterBuffer;
    private readonly Timer _afkTimer;
    private readonly Timer _incomingTransmissionWatchdogTimer;
    private volatile bool _isIncomingWhisperActive;

    // AFK, Replay, Voicemail, and Paging State
    private bool _isLocalAfk;
    private int _localAfkMinutes;
    public VoiceReplayBuffer ReplayBuffer { get; } = new(3);
    public VoicemailBox VoicemailBox { get; } = new();
    private readonly List<byte> _incomingPcmAccumulator = new();
    private readonly object _incomingPcmLock = new();
    private Guid _incomingSenderId = Guid.Empty;
    private string _incomingSenderName = "Colleague";

    // Security & E2EE Engine
    private readonly ChannelCrypto _channelCrypto = new();
    private string _channelKey = string.Empty;

    // Speech-to-Text & Live Subtitles Engine
    public SpeechToTextService SpeechToTextService { get; } = new();
    private bool _enableSubtitles = true;
    private string _subtitleText = string.Empty;
    private string _subtitleSender = string.Empty;
    private bool _isSubtitleVisible;
    private readonly Timer _subtitleFadeTimer;

    // 1-on-1 Whisper State
    private PeerInfo? _selectedWhisperPeer;
    private bool _isReceivingWhisper;

    public event Action<string, string>? OnPageAlertReceived;

    // Instant Messaging & File Transfer Engine
    public FileTransferManager FileTransferManager { get; } = new();
    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public ObservableCollection<ChatMessage> DisplayMessages { get; } = new();
    private readonly ConcurrentDictionary<Guid, long> _processedMessageIds = new();
    private int _selectedDeckTab; // 0 = Roster, 1 = Chat & Files
    private int _unreadChatCount;
    private string _chatInputText = string.Empty;

    public int SelectedDeckTab
    {
        get => _selectedDeckTab;
        set
        {
            if (SetField(ref _selectedDeckTab, value))
            {
                if (_selectedDeckTab == 1)
                {
                    UnreadChatCount = 0;
                }
                OnPropertyChanged(nameof(IsRosterTabActive));
                OnPropertyChanged(nameof(IsChatTabActive));
            }
        }
    }

    public bool IsRosterTabActive => SelectedDeckTab == 0;
    public bool IsChatTabActive => SelectedDeckTab == 1;

    public int UnreadChatCount
    {
        get => _unreadChatCount;
        set
        {
            if (SetField(ref _unreadChatCount, value))
            {
                OnPropertyChanged(nameof(HasUnreadChat));
                OnPropertyChanged(nameof(ChatTabHeader));
            }
        }
    }

    public bool HasUnreadChat => _unreadChatCount > 0;
    public string ChatTabHeader => _unreadChatCount > 0 ? $"💬 CHAT ({_unreadChatCount})" : "💬 CHAT & FILES";

    public string ChatInputText
    {
        get => _chatInputText;
        set => SetField(ref _chatInputText, value);
    }

    public string CurrentChatTargetDisplay => IsWhisperActive && SelectedWhisperPeer != null
        ? $"WHISPER TO {SelectedWhisperPeer.DisplayName.ToUpperInvariant()}"
        : CurrentChannelDisplay;

    public bool IsLocalAfk
    {
        get => _isLocalAfk;
        private set
        {
            if (SetField(ref _isLocalAfk, value))
            {
                OnPropertyChanged(nameof(LocalStatusText));
            }
        }
    }

    public int LocalAfkMinutes
    {
        get => _localAfkMinutes;
        private set
        {
            if (SetField(ref _localAfkMinutes, value))
            {
                OnPropertyChanged(nameof(LocalStatusText));
            }
        }
    }

    public string LocalStatusText => _isLocalAfk ? $"AFK ({_localAfkMinutes}m)" : "ONLINE";

    public bool CanReplay => ReplayBuffer.GetLast() != null;
    public string LastReplayInfo
    {
        get
        {
            var last = ReplayBuffer.GetLast();
            return last != null ? $"⏪ Replay: {last.SenderName} ({last.DurationSeconds}s)" : "⏪ Replay (None)";
        }
    }

    public int UnreadVoicemailCount => VoicemailBox.UnreadCount;
    public bool HasUnreadVoicemails => VoicemailBox.UnreadCount > 0;
    public Visibility VoicemailBannerVisibility => HasUnreadVoicemails ? Visibility.Visible : Visibility.Collapsed;
    public string VoicemailBannerText => HasUnreadVoicemails ? $"📬 {UnreadVoicemailCount} Missed Voice Memo(s)" : string.Empty;

    public ObservableCollection<PeerInfo> Peers { get; } = new();

    public ObservableCollection<PttKeyPreset> PttKeyPresets { get; } = new()
    {
        new PttKeyPreset { Name = "Caps Lock", VKey = 0x14 },
        new PttKeyPreset { Name = "Left Alt", VKey = 0x12 },
        new PttKeyPreset { Name = "Left Ctrl", VKey = 0x11 },
        new PttKeyPreset { Name = "Left Shift", VKey = 0x10 },
        new PttKeyPreset { Name = "Spacebar", VKey = 0x20 },
        new PttKeyPreset { Name = "Tilde / Grave (~)", VKey = 0xC0 },
        new PttKeyPreset { Name = "F1", VKey = 0x70 },
        new PttKeyPreset { Name = "F2", VKey = 0x71 },
        new PttKeyPreset { Name = "F3", VKey = 0x72 },
        new PttKeyPreset { Name = "F4", VKey = 0x73 },
        new PttKeyPreset { Name = "Key V", VKey = 0x56 },
        new PttKeyPreset { Name = "Key T", VKey = 0x54 },
        new PttKeyPreset { Name = "Key B", VKey = 0x42 },
        new PttKeyPreset { Name = "Mouse 4 / 5 Only", VKey = 0x00 }
    };

    public IEnumerable<RogerBeepType> RogerBeepOptions => Enum.GetValues<RogerBeepType>();

    #region Bindable Properties
    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetField(ref _displayName, value))
            {
                _transport.DisplayName = value;
                _transport.SendPresenceBeacon();
                SaveSettings();
            }
        }
    }

    public byte CurrentChannel
    {
        get => _currentChannel;
        set
        {
            if (SetField(ref _currentChannel, value))
            {
                _transport.CurrentChannel = value;
                _transport.SendPresenceBeacon();
                OnPropertyChanged(nameof(CurrentChannelDisplay));
                OnPropertyChanged(nameof(CurrentChatTargetDisplay));
                OnPropertyChanged(nameof(TacticalStatusBannerText));
                RefreshPeersList();
                RefreshDisplayMessages();
                SaveSettings();
            }
        }
    }

    public string CurrentChannelDisplay => CurrentChannel == 0xFF ? "ALL-CALL (EMERGENCY)" : $"CHANNEL {CurrentChannel:D2}";

    public bool IsTransmitting
    {
        get => _isTransmitting;
        private set
        {
            if (SetField(ref _isTransmitting, value))
            {
                OnPropertyChanged(nameof(TacticalStatusBannerText));
            }
        }
    }

    public bool IsReceiving
    {
        get => _isReceiving;
        private set
        {
            if (SetField(ref _isReceiving, value))
            {
                OnPropertyChanged(nameof(TacticalStatusBannerText));
            }
        }
    }

    public string ActiveSpeakerName
    {
        get => _activeSpeakerName;
        private set
        {
            if (SetField(ref _activeSpeakerName, value))
            {
                OnPropertyChanged(nameof(TacticalStatusBannerText));
            }
        }
    }

    public string TacticalStatusBannerText
    {
        get
        {
            if (IsTransmitting)
            {
                return IsWhisperActive && SelectedWhisperPeer != null
                    ? $"🎙️ WHISPERING: {SelectedWhisperPeer.DisplayName.ToUpper()}"
                    : $"🎙️ YOU ARE TRANSMITTING (CH {CurrentChannel:D2})";
            }

            if (IsReceivingWhisper)
            {
                return $"🟣 {_incomingSenderName} (WHISPERING)";
            }

            if (IsReceiving)
            {
                return $"🔊 {_incomingSenderName} (TRANSMITTING)";
            }

            if (!string.IsNullOrWhiteSpace(_activeSpeakerName) && _activeSpeakerName != "Channel Idle")
            {
                return _activeSpeakerName;
            }

            return $"🟢 Channel {CurrentChannel:D2} Idle";
        }
    }

    public float ActiveSpeakerLevel
    {
        get => _activeSpeakerLevel;
        private set => SetField(ref _activeSpeakerLevel, value);
    }

    public float LocalMicLevel
    {
        get => _localMicLevel;
        private set => SetField(ref _localMicLevel, value);
    }

    public RogerBeepType SelectedRogerBeep
    {
        get => _selectedRogerBeep;
        set
        {
            if (SetField(ref _selectedRogerBeep, value))
            {
                SaveSettings();
            }
        }
    }

    public void PreviewRogerBeep()
    {
        byte[] roger = SfxGenerator.GenerateRogerBeep(_selectedRogerBeep, 0.45f);
        byte[] squelch = SfxGenerator.GenerateSquelchTail(0.25f);
        byte[] combined = SfxGenerator.CombineAudio(roger, squelch);
        if (combined.Length > 0)
        {
            _playback.PlayPcm(combined);
        }
    }

    public bool EnableRadioFilter
    {
        get => _enableRadioFilter;
        set
        {
            if (SetField(ref _enableRadioFilter, value))
            {
                _radioDsp.EnableRadioEffect = value;
                SaveSettings();
            }
        }
    }

    public string PttKeyDisplay
    {
        get => _pttKeyDisplay;
        set => SetField(ref _pttKeyDisplay, value);
    }

    public PttKeyPreset? SelectedPttPreset
    {
        get => _selectedPttPreset;
        set
        {
            if (SetField(ref _selectedPttPreset, value) && value != null)
            {
                _pttHook.TargetVKey = value.VKey;
                PttKeyDisplay = value.VKey == 0 ? "Mouse 4 / Mouse 5" : $"{value.Name} / Mouse 4";
                SaveSettings();
            }
        }
    }

    public bool IsRebinding
    {
        get => _isRebinding;
        private set
        {
            if (SetField(ref _isRebinding, value))
            {
                OnPropertyChanged(nameof(RebindButtonText));
            }
        }
    }

    public string RebindButtonText => IsRebinding ? "PRESS KEY..." : "Record";

    public string RelayHost
    {
        get => _relayHost;
        set
        {
            if (SetField(ref _relayHost, value))
            {
                ApplyRelayEndpoint(value);
                SaveSettings();
            }
        }
    }

    public int OnlinePeerCount => Peers.Count;

    private bool _isSettingsOpen;
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set
        {
            if (SetField(ref _isSettingsOpen, value))
            {
                OnPropertyChanged(nameof(SettingsVisibility));
                OnPropertyChanged(nameof(SettingsButtonText));
            }
        }
    }

    public Visibility SettingsVisibility => _isSettingsOpen ? Visibility.Visible : Visibility.Collapsed;
    public string SettingsButtonText => _isSettingsOpen ? "⚙ SETTINGS ▴" : "⚙ SETTINGS ▾";

    public void ToggleSettings()
    {
        IsSettingsOpen = !IsSettingsOpen;
    }

    #region E2EE Properties
    public string ChannelKey
    {
        get => _channelKey;
        set
        {
            if (SetField(ref _channelKey, value))
            {
                _channelCrypto.SetPassphrase(value);
                OnPropertyChanged(nameof(IsEncrypted));
                OnPropertyChanged(nameof(E2eeStatusText));
                OnPropertyChanged(nameof(E2eeIcon));
                SaveSettings();
            }
        }
    }

    public bool IsEncrypted => _channelCrypto.IsEnabled;
    public string E2eeStatusText => IsEncrypted ? "E2EE ENCRYPTED" : "OPEN CHANNEL";
    public string E2eeIcon => IsEncrypted ? "🔒" : "🔓";
    #endregion

    #region Whisper Properties
    public PeerInfo? SelectedWhisperPeer
    {
        get => _selectedWhisperPeer;
        set
        {
            if (SetField(ref _selectedWhisperPeer, value))
            {
                OnPropertyChanged(nameof(IsWhisperActive));
                OnPropertyChanged(nameof(WhisperTargetName));
                OnPropertyChanged(nameof(WhisperBannerVisibility));
                OnPropertyChanged(nameof(CurrentChatTargetDisplay));
                OnPropertyChanged(nameof(TacticalStatusBannerText));
                RefreshDisplayMessages();
            }
        }
    }

    public bool IsWhisperActive => _selectedWhisperPeer != null;
    public string WhisperTargetName => _selectedWhisperPeer?.DisplayName ?? string.Empty;
    public Visibility WhisperBannerVisibility => IsWhisperActive ? Visibility.Visible : Visibility.Collapsed;

    public bool IsReceivingWhisper
    {
        get => _isReceivingWhisper;
        private set
        {
            if (SetField(ref _isReceivingWhisper, value))
            {
                OnPropertyChanged(nameof(TacticalStatusBannerText));
            }
        }
    }

    public void SetWhisperPeer(PeerInfo peer)
    {
        if (peer == null) return;
        if (SelectedWhisperPeer?.Id == peer.Id)
        {
            SelectedWhisperPeer = null; // Toggle off
        }
        else
        {
            SelectedWhisperPeer = peer;
        }
    }

    public void ClearWhisper()
    {
        SelectedWhisperPeer = null;
    }
    #endregion

    #region Subtitles & STT Properties
    public bool EnableSubtitles
    {
        get => _enableSubtitles;
        set
        {
            if (SetField(ref _enableSubtitles, value))
            {
                SpeechToTextService.IsEnabled = value;
                SaveSettings();
            }
        }
    }

    public string SubtitleText
    {
        get => _subtitleText;
        private set
        {
            if (SetField(ref _subtitleText, value))
            {
                OnPropertyChanged(nameof(HasSubtitle));
            }
        }
    }

    public string SubtitleSender
    {
        get => _subtitleSender;
        private set => SetField(ref _subtitleSender, value);
    }

    public bool IsSubtitleVisible
    {
        get => _isSubtitleVisible;
        private set
        {
            if (SetField(ref _isSubtitleVisible, value))
            {
                OnPropertyChanged(nameof(HasSubtitle));
            }
        }
    }

    public bool HasSubtitle => !string.IsNullOrEmpty(_subtitleText) && _isSubtitleVisible;
    public string SttModelStatus => SpeechToTextService.ModelStatus;
    #endregion
    #endregion

    public MainViewModel()
    {
        _peerManager = new PeerManager();
        _peerManager.AliasResolver = (id, machineName) =>
        {
            if (_peerNicknames.TryGetValue(id.ToString(), out var alias) && !string.IsNullOrWhiteSpace(alias))
                return alias;
            if (!string.IsNullOrWhiteSpace(machineName) && _peerNicknames.TryGetValue(machineName, out var aliasByName) && !string.IsNullOrWhiteSpace(aliasByName))
                return aliasByName;
            return null;
        };

        _opusCodec = new ConcentusOpusCodec();
        _radioDsp = new RadioDsp { EnableRadioEffect = _enableRadioFilter };
        _capture = new WasapiAudioCapture();
        _playback = new WasapiAudioPlayback();
        _pttHook = new RawInputPttHook();
        _jitterBuffer = new JitterBuffer(1);

        LoadSettings();
        if (_localId == Guid.Empty)
        {
            _localId = Guid.NewGuid();
            SaveSettings();
        }

        _transport = new WindowsUdpTransport(_localId, _displayName, _peerManager);

        // Initialize Speech-to-Text subtitle fade timer
        _subtitleFadeTimer = new Timer(_ =>
        {
            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                IsSubtitleVisible = false;
                SubtitleText = string.Empty;
            });
        }, null, Timeout.Infinite, Timeout.Infinite);

        // Initialize incoming transmission watchdog timer (recovers safely if PttEnd packet is dropped)
        _incomingTransmissionWatchdogTimer = new Timer(_ =>
        {
            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                FinishIncomingTransmission(_isIncomingWhisperActive);
            });
        }, null, Timeout.Infinite, Timeout.Infinite);

        // Bind Speech-to-Text transcript events
        SpeechToTextService.OnTranscriptReceived += (sender, e) =>
        {
            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                SubtitleSender = e.IsWhisper ? $"🟣 {e.SenderName} (Whisper)" : e.SenderName;
                SubtitleText = e.Text;
                IsSubtitleVisible = true;
                _subtitleFadeTimer.Change(7000, Timeout.Infinite);
            });
        };

        // Initialize STT model asynchronously in user-mode
        Task.Run(async () =>
        {
            await SpeechToTextService.InitializeAsync();
            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                OnPropertyChanged(nameof(SttModelStatus));
            });
        });

        // Bind events
        _pttHook.OnPttStateChanged += HandlePttStateChanged;
        _capture.OnAudioCaptured += HandleAudioCaptured;
        _transport.OnPacketReceived += HandlePacketReceived;
        _jitterBuffer.OnPacketReady += HandleJitterPacketReady;
        _jitterBuffer.OnMissingPacket += HandleJitterMissingPacket;
        _pttHook.OnRebindKeyCaptured += vkey => Application.Current?.Dispatcher?.Invoke(() => HandleKeyRebound(vkey));

        _peerManager.OnPeerJoined += peer => Application.Current?.Dispatcher?.Invoke(RefreshPeersList);
        _peerManager.OnPeerLeft += peer => Application.Current?.Dispatcher?.Invoke(RefreshPeersList);
        _peerManager.OnPeerUpdated += peer => Application.Current?.Dispatcher?.Invoke(RefreshPeersList);

        FileTransferManager.OnTransferProgress += HandleTransferProgress;
        FileTransferManager.OnTransferCompleted += HandleTransferCompleted;
        FileTransferManager.OnTransferFailed += HandleTransferFailed;
        FileTransferManager.OnMissingChunksRequested += HandleMissingChunksRequested;

        if (_selectedPttPreset == null)
        {
            SelectedPttPreset = PttKeyPresets[0];
        }

        // Start subsystems
        _capture.Start();
        _playback.Start();
        _transport.Start();

        // Check local user idle / AFK state every 4 seconds (zero admin Win32 GetLastInputInfo)
        _afkTimer = new Timer(CheckIdleState, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4));
    }

    private void CheckIdleState(object? state)
    {
        try
        {
            PruneProcessedMessageIds();

            bool idle = IdleTracker.Default.IsIdle(300); // 5 minutes inactivity threshold
            int minutes = IdleTracker.Default.GetIdleMinutes();

            if (idle != _isLocalAfk || minutes != _localAfkMinutes)
            {
                _isLocalAfk = idle;
                _localAfkMinutes = minutes;
                _transport.IsAfk = idle;
                _transport.AfkMinutes = minutes;
                _transport.SendPresenceBeacon();

                Application.Current?.Dispatcher?.BeginInvoke(() =>
                {
                    OnPropertyChanged(nameof(IsLocalAfk));
                    OnPropertyChanged(nameof(LocalAfkMinutes));
                    OnPropertyChanged(nameof(LocalStatusText));
                });
            }
        }
        catch { }
    }

    private void PruneProcessedMessageIds()
    {
        try
        {
            long now = Environment.TickCount64;
            const long maxAgeMs = 60_000; // Keep entries for up to 60 seconds

            foreach (var kvp in _processedMessageIds)
            {
                if (now - kvp.Value > maxAgeMs)
                {
                    _processedMessageIds.TryRemove(kvp.Key, out _);
                }
            }

            // Bounded capacity safeguard: if dictionary exceeds limit, remove oldest entries
            const int maxCapacity = 5_000;
            if (_processedMessageIds.Count > maxCapacity)
            {
                var oldestEntries = _processedMessageIds
                    .OrderBy(kvp => kvp.Value)
                    .Take(_processedMessageIds.Count - (maxCapacity / 2));

                foreach (var kvp in oldestEntries)
                {
                    _processedMessageIds.TryRemove(kvp.Key, out _);
                }
            }
        }
        catch { }
    }

    public void PagePeer(PeerInfo peer)
    {
        if (peer == null) return;
        try
        {
            var page = LancomPacket.CreatePageAlert(_localId, peer.Id, DisplayName, CurrentChannel);
            page.SequenceNumber = Interlocked.Increment(ref _sequenceCounter);
            _transport.BroadcastPacket(page);

            _playback.PlayPcm(SfxGenerator.GenerateMicClick(0.3f));
            ActiveSpeakerName = $"PAGED {peer.DisplayName.ToUpper()}";
        }
        catch { }
    }

    public void ReplayLast()
    {
        var last = ReplayBuffer.GetLast();
        if (last != null && last.PcmData.Length > 0)
        {
            _playback.PlayPcm(last.PcmData);
            ActiveSpeakerName = $"REPLAYING: {last.SenderName}";
        }
    }

    public void PlayLatestVoicemail()
    {
        var unread = VoicemailBox.GetUnread();
        if (unread.Count > 0)
        {
            var latest = unread.Last();
            _playback.PlayPcm(latest.PcmData);
            latest.IsRead = true;
            ActiveSpeakerName = $"VOICEMAIL: {latest.SenderName}";
            OnPropertyChanged(nameof(UnreadVoicemailCount));
            OnPropertyChanged(nameof(HasUnreadVoicemails));
            OnPropertyChanged(nameof(VoicemailBannerVisibility));
            OnPropertyChanged(nameof(VoicemailBannerText));
        }
    }

    public void DismissVoicemails()
    {
        VoicemailBox.MarkAllRead();
        OnPropertyChanged(nameof(UnreadVoicemailCount));
        OnPropertyChanged(nameof(HasUnreadVoicemails));
        OnPropertyChanged(nameof(VoicemailBannerVisibility));
        OnPropertyChanged(nameof(VoicemailBannerText));
    }

    private void HandlePttStateChanged(bool pressed)
    {
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            if (pressed)
            {
                // Reset audio pipeline state for fresh talk-spurt
                _opusCodec.Reset();
                _capture.ResetAccumulator();

                // Instant local tactile chirp
                _playback.PlayPcm(SfxGenerator.GenerateMicClick(0.3f));

                IsTransmitting = true;
                if (IsWhisperActive && SelectedWhisperPeer != null)
                {
                    ActiveSpeakerName = $"WHISPERING: {SelectedWhisperPeer.DisplayName.ToUpper()}";

                    var whisperStart = LancomPacket.CreateWhisperPttStart(_localId, SelectedWhisperPeer.Id, DisplayName);
                    whisperStart.SequenceNumber = Interlocked.Increment(ref _sequenceCounter);
                    _transport.BroadcastPacket(whisperStart);
                    if (SelectedWhisperPeer.EndPoint != null)
                    {
                        _transport.SendTo(whisperStart, SelectedWhisperPeer.EndPoint);
                    }
                }
                else
                {
                    ActiveSpeakerName = "YOU ARE TRANSMITTING";

                    // Broadcast PTT Start packet
                    var pttStart = new LancomPacket
                    {
                        Type = MessageType.PttStart,
                        SequenceNumber = Interlocked.Increment(ref _sequenceCounter),
                        Flags = _channelCrypto.IsEnabled ? LancomPacket.FlagEncrypted : (byte)0
                    };
                    _transport.BroadcastPacket(pttStart);
                }
            }
            else
            {
                IsTransmitting = false;
                LocalMicLevel = 0.0f;
                ActiveSpeakerName = "Channel Idle";

                // Clear buffers
                _capture.ResetAccumulator();
                _opusCodec.Reset();

                // Play local Roger Beep + Squelch
                byte[] roger = SfxGenerator.GenerateRogerBeep(_selectedRogerBeep);
                byte[] squelch = SfxGenerator.GenerateSquelchTail();
                byte[] combinedSfx = SfxGenerator.CombineAudio(roger, squelch);
                if (combinedSfx.Length > 0)
                {
                    _playback.PlayPcm(combinedSfx);
                }

                if (IsWhisperActive && SelectedWhisperPeer != null)
                {
                    // Broadcast Whisper PTT End packet (burst 3 copies for network loss resilience)
                    for (int i = 0; i < 3; i++)
                    {
                        var whisperEnd = LancomPacket.CreateWhisperPttEnd(_localId, SelectedWhisperPeer.Id);
                        whisperEnd.SequenceNumber = Interlocked.Increment(ref _sequenceCounter);
                        _transport.BroadcastPacket(whisperEnd);
                        if (SelectedWhisperPeer.EndPoint != null)
                        {
                            _transport.SendTo(whisperEnd, SelectedWhisperPeer.EndPoint);
                        }
                    }
                }
                else
                {
                    // Broadcast PTT End packet (burst 3 copies for network loss resilience)
                    for (int i = 0; i < 3; i++)
                    {
                        var pttEnd = new LancomPacket
                        {
                            Type = MessageType.PttEnd,
                            SequenceNumber = Interlocked.Increment(ref _sequenceCounter),
                            Flags = _channelCrypto.IsEnabled ? LancomPacket.FlagEncrypted : (byte)0
                        };
                        _transport.BroadcastPacket(pttEnd);
                    }
                }
            }
        });
    }

    private void HandleAudioCaptured(byte[] rawPcm)
    {
        if (!IsTransmitting) return;

        // Calculate local mic level for VU meter (non-blocking UI dispatch)
        var frame = new AudioFrame(rawPcm);
        Application.Current?.Dispatcher?.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            LocalMicLevel = frame.RmsVolume * 2.5f;
        });

        // Encode to Opus
        byte[] encoded = _opusCodec.Encode(rawPcm);
        if (encoded.Length == 0) return;

        uint seq = Interlocked.Increment(ref _sequenceCounter);
        ulong ts = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        byte[] payload = encoded;
        bool isEncrypted = _channelCrypto.IsEnabled;
        if (isEncrypted)
        {
            payload = _channelCrypto.Encrypt(seq, ts, encoded);
        }

        if (IsWhisperActive && SelectedWhisperPeer != null)
        {
            var whisperPacket = LancomPacket.CreateWhisperVoice(_localId, SelectedWhisperPeer.Id, seq, ts, payload, isEncrypted);
            _transport.BroadcastPacket(whisperPacket);
            if (SelectedWhisperPeer.EndPoint != null)
            {
                _transport.SendTo(whisperPacket, SelectedWhisperPeer.EndPoint);
            }
        }
        else
        {
            byte flags = 0;
            if (isEncrypted) flags |= LancomPacket.FlagEncrypted;

            var audioPacket = new LancomPacket
            {
                Type = MessageType.AudioData,
                SequenceNumber = seq,
                TimestampMs = ts,
                Flags = flags,
                Payload = payload
            };
            _transport.BroadcastPacket(audioPacket);
        }
    }

    private void HandlePacketReceived(LancomPacket packet, IPEndPoint sender)
    {
        // Check Whisper / Targeted Packets: if target is specified and not us, ignore
        if (packet.TargetUserId != Guid.Empty && packet.TargetUserId != _localId)
        {
            return;
        }

        // ---------------------------------------------------------------
        // Instant Messaging & File Transfer Packets
        // ---------------------------------------------------------------
        if (packet.Type == MessageType.TextMessage)
        {
            if (packet.TargetUserId == Guid.Empty && packet.ChannelId != 0xFF && packet.ChannelId != CurrentChannel)
                return;

            byte[] payloadBytes = packet.Payload;
            if (packet.IsEncrypted)
            {
                if (_channelCrypto.TryDecrypt(packet.SequenceNumber, packet.TimestampMs, packet.Payload, out var dec))
                {
                    payloadBytes = dec;
                }
                else
                {
                    return;
                }
            }

            try
            {
                var textPayload = System.Text.Json.JsonSerializer.Deserialize<TextMessagePayload>(payloadBytes);
                if (textPayload != null)
                {
                    var ack = LancomPacket.CreateMessageAck(_localId, packet.SenderId, packet.ChannelId, textPayload.MessageId);
                    if (_processedMessageIds.ContainsKey(textPayload.MessageId))
                    {
                        _transport.BroadcastPacket(ack);
                        return;
                    }

                    _processedMessageIds[textPayload.MessageId] = Environment.TickCount64;

                    Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        var peer = _peerManager.GetPeer(packet.SenderId);
                        string senderDisplayName = peer != null ? peer.DisplayName : textPayload.SenderName;
                        if (peer == null && _peerNicknames.TryGetValue(packet.SenderId.ToString(), out var alias))
                        {
                            senderDisplayName = alias;
                        }

                        var msg = new ChatMessage
                        {
                            Id = textPayload.MessageId,
                            SenderId = packet.SenderId,
                            SenderName = senderDisplayName,
                            ChannelId = packet.ChannelId,
                            TargetUserId = packet.TargetUserId,
                            Text = textPayload.Text,
                            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)textPayload.TimestampMs).UtcDateTime,
                            IsOutgoing = false,
                            IsEncrypted = packet.IsEncrypted
                        };

                        AddChatMessage(msg);

                        if (!IsChatTabActive)
                        {
                            UnreadChatCount++;
                        }

                        _playback.PlayPcm(SfxGenerator.GenerateMessageChime());
                    });

                    // Send ACK back
                    _transport.BroadcastPacket(ack);
                }
            }
            catch { }
            return;
        }

        if (packet.Type == MessageType.MessageAck)
        {
            if (LancomPacket.TryParseMessageAck(packet, out var ackMessageId))
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    var existing = Messages.FirstOrDefault(m => m.Id == ackMessageId);
                    if (existing != null)
                    {
                        existing.IsDelivered = true;
                    }
                });
            }
            return;
        }

        if (packet.Type == MessageType.FileOffer)
        {
            if (packet.TargetUserId == Guid.Empty && packet.ChannelId != 0xFF && packet.ChannelId != CurrentChannel)
                return;

            byte[] payloadBytes = packet.Payload;
            if (packet.IsEncrypted)
            {
                if (_channelCrypto.TryDecrypt(packet.SequenceNumber, packet.TimestampMs, packet.Payload, out var dec))
                {
                    payloadBytes = dec;
                }
                else
                {
                    return;
                }
            }

            try
            {
                var offer = System.Text.Json.JsonSerializer.Deserialize<FileOfferPayload>(payloadBytes);
                if (offer != null)
                {
                    // Register inbound transfer IMMEDIATELY on background network thread to prevent chunk drop race condition
                    FileTransferManager.RegisterInboundTransfer(offer, packet.SenderId, packet.ChannelId);

                    if (_processedMessageIds.ContainsKey(offer.TransferId))
                    {
                        return;
                    }

                    Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        if (_processedMessageIds.ContainsKey(offer.TransferId) || Messages.Any(m => m.TransferId == offer.TransferId))
                        {
                            return;
                        }

                        var peer = _peerManager.GetPeer(packet.SenderId);
                        string senderDisplayName = peer != null ? peer.DisplayName : offer.SenderName;
                        if (peer == null && _peerNicknames.TryGetValue(packet.SenderId.ToString(), out var alias))
                        {
                            senderDisplayName = alias;
                        }

                        var fileMsg = new ChatMessage
                        {
                            Id = offer.TransferId,
                            SenderId = packet.SenderId,
                            SenderName = senderDisplayName,
                            ChannelId = packet.ChannelId,
                            TargetUserId = packet.TargetUserId,
                            Text = offer.Category switch
                            {
                                FileCategory.Photo => $"📷 Shared a photo: {offer.FileName}",
                                FileCategory.Video => $"🎥 Shared a video: {offer.FileName}",
                                _ => $"📁 Shared a file: {offer.FileName}"
                            },
                            Timestamp = DateTime.UtcNow,
                            IsOutgoing = false,
                            IsEncrypted = packet.IsEncrypted,
                            TransferId = offer.TransferId,
                            FileName = offer.FileName,
                            FileSize = offer.FileSize,
                            Category = offer.Category,
                            ThumbnailBase64 = offer.ThumbnailBase64,
                            TransferProgress = 0.0,
                            IsTransferCompleted = false
                        };

                        AddChatMessage(fileMsg);

                        if (!IsChatTabActive)
                        {
                            UnreadChatCount++;
                        }

                        _playback.PlayPcm(SfxGenerator.GenerateMessageChime());
                    });
                }
            }
            catch { }
            return;
        }

        if (packet.Type == MessageType.FileChunk)
        {
            byte[] rawChunk = packet.Payload;
            if (packet.IsEncrypted)
            {
                if (_channelCrypto.TryDecrypt(packet.SequenceNumber, packet.TimestampMs, packet.Payload, out var dec))
                {
                    rawChunk = dec;
                }
                else
                {
                    return;
                }
            }

            if (FileChunkSerializer.TryDeserialize(rawChunk, out var transferId, out int chunkIndex, out int totalChunks, out byte[] chunkData))
            {
                FileTransferManager.ProcessIncomingChunk(transferId, chunkIndex, totalChunks, chunkData, (tId, cIdx, isDone) =>
                {
                    uint ackSeq = Interlocked.Increment(ref _sequenceCounter);
                    ulong ackTs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    byte[] ackPayload = JsonSerializer.SerializeToUtf8Bytes(new FileChunkAckPayload { TransferId = tId, ChunkIndex = cIdx, IsComplete = isDone });
                    bool isEncrypted = _channelCrypto.IsEnabled;
                    if (isEncrypted)
                    {
                        ackPayload = _channelCrypto.Encrypt(ackSeq, ackTs, ackPayload);
                    }

                    var ackPacket = new LancomPacket
                    {
                        Type = MessageType.FileChunkAck,
                        SenderId = _localId,
                        TargetUserId = packet.SenderId,
                        ChannelId = packet.ChannelId,
                        SequenceNumber = ackSeq,
                        TimestampMs = ackTs,
                        Flags = (byte)((packet.SenderId != Guid.Empty ? LancomPacket.FlagWhisper : 0) | (isEncrypted ? LancomPacket.FlagEncrypted : 0)),
                        Payload = ackPayload
                    };

                    _transport.BroadcastPacket(ackPacket);
                    if (isDone)
                    {
                        // Send redundant copy of completion ACK for network loss resilience
                        _transport.BroadcastPacket(ackPacket);
                    }
                });
            }
            return;
        }

        if (packet.Type == MessageType.FileChunkAck)
        {
            byte[] payloadBytes = packet.Payload;
            if (packet.IsEncrypted)
            {
                if (_channelCrypto.TryDecrypt(packet.SequenceNumber, packet.TimestampMs, packet.Payload, out var dec))
                {
                    payloadBytes = dec;
                }
                else
                {
                    return;
                }
            }

            try
            {
                var ack = JsonSerializer.Deserialize<FileChunkAckPayload>(payloadBytes);
                if (ack != null)
                {
                    FileTransferManager.HandleChunkAck(ack.TransferId, ack.ChunkIndex, ack.IsComplete);
                }
            }
            catch { }
            return;
        }

        if (packet.Type == MessageType.FileMissingChunksRequest)
        {
            byte[] payloadBytes = packet.Payload;
            if (packet.IsEncrypted)
            {
                if (_channelCrypto.TryDecrypt(packet.SequenceNumber, packet.TimestampMs, packet.Payload, out var dec))
                {
                    payloadBytes = dec;
                }
                else
                {
                    return;
                }
            }

            try
            {
                var req = JsonSerializer.Deserialize<FileMissingChunksRequestPayload>(payloadBytes);
                if (req != null && req.MissingIndices?.Count > 0)
                {
                    Task.Run(async () =>
                    {
                        await FileTransferManager.ResendChunksAsync(req.TransferId, req.MissingIndices, (chunkIndex, totalChunks, chunkData) =>
                        {
                            SendChunkPacket(req.TransferId, packet.SenderId, packet.ChannelId, chunkIndex, totalChunks, chunkData);
                            return Task.CompletedTask;
                        });
                    });
                }
            }
            catch { }
            return;
        }

        // Check Whisper Voice Packets
        if (packet.IsWhisper || packet.Type == MessageType.WhisperVoice || packet.Type == MessageType.WhisperPttStart || packet.Type == MessageType.WhisperPttEnd)
        {
            if (packet.Type == MessageType.WhisperPttStart)
            {
                _isIncomingWhisperActive = true;
                _incomingTransmissionWatchdogTimer.Change(450, Timeout.Infinite);
                _jitterBuffer.Reset();
                _opusCodec.Reset();
                _radioDsp.Reset();

                lock (_incomingPcmLock)
                {
                    _incomingPcmAccumulator.Clear();
                    _incomingSenderId = packet.SenderId;
                    var peer = Peers.FirstOrDefault(p => p.Id == packet.SenderId);
                    string senderName = peer != null ? peer.DisplayName : (packet.Payload.Length > 0 ? System.Text.Encoding.UTF8.GetString(packet.Payload) : "Colleague");
                    _incomingSenderName = senderName;
                }

                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    IsReceiving = true;
                    IsReceivingWhisper = true;
                    ActiveSpeakerName = $"🟣 WHISPER: {_incomingSenderName}";
                });

                // Distinctive soft whisper chime
                _playback.PlayPcm(SfxGenerator.GeneratePagerChime(0.45f));
                return;
            }
            else if (packet.Type == MessageType.WhisperVoice)
            {
                _isIncomingWhisperActive = true;
                _incomingTransmissionWatchdogTimer.Change(450, Timeout.Infinite);
                if (!_isReceiving)
                {
                    lock (_incomingPcmLock)
                    {
                        _incomingSenderId = packet.SenderId;
                        var peer = Peers.FirstOrDefault(p => p.Id == packet.SenderId);
                        string senderName = peer != null ? peer.DisplayName : "Colleague";
                        _incomingSenderName = senderName;
                    }
                    Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        IsReceiving = true;
                        IsReceivingWhisper = true;
                        ActiveSpeakerName = $"🟣 WHISPER: {_incomingSenderName}";
                    });
                }
                _jitterBuffer.Push(packet);
                return;
            }
            else if (packet.Type == MessageType.WhisperPttEnd)
            {
                FinishIncomingTransmission(isWhisper: true);
                return;
            }
        }

        // Filter out if not on current channel (unless All-Call Emergency 0xFF)
        if (packet.ChannelId != 0xFF && packet.ChannelId != CurrentChannel) return;

        // Handle Targeted Paging / Call Alert
        if (packet.Type == MessageType.PageAlert)
        {
            if (LancomPacket.TryParsePageAlert(packet, out var targetId, out var senderName))
            {
                if (targetId == _localId || targetId == Guid.Empty || packet.ChannelId == 0xFF)
                {
                    _playback.PlayPcm(SfxGenerator.GeneratePagerChime(0.6f));
                    Application.Current?.Dispatcher?.BeginInvoke(() =>
                    {
                        ActiveSpeakerName = $"🔔 {senderName} PAGED YOU!";
                        OnPageAlertReceived?.Invoke(senderName, $"Paged you on {CurrentChannelDisplay}!");
                    });
                }
            }
            return;
        }

        if (packet.Type == MessageType.PttStart)
        {
            _isIncomingWhisperActive = false;
            _incomingTransmissionWatchdogTimer.Change(450, Timeout.Infinite);
            _jitterBuffer.Reset();
            _opusCodec.Reset();
            _radioDsp.Reset();

            lock (_incomingPcmLock)
            {
                _incomingPcmAccumulator.Clear();
                _incomingSenderId = packet.SenderId;
                var peer = Peers.FirstOrDefault(p => p.Id == packet.SenderId);
                _incomingSenderName = peer != null ? peer.DisplayName : "Colleague";
            }

            Application.Current?.Dispatcher?.Invoke(() =>
            {
                IsReceiving = true;
                IsReceivingWhisper = false;
                var peer = Peers.FirstOrDefault(p => p.Id == packet.SenderId);
                ActiveSpeakerName = peer != null ? $"{peer.DisplayName} (TRANSMITTING)" : "INCOMING TRANSMISSION";
            });

            // Remote mic chirp
            _playback.PlayPcm(SfxGenerator.GenerateMicClick(0.25f));
        }
        else if (packet.Type == MessageType.AudioData)
        {
            _isIncomingWhisperActive = false;
            _incomingTransmissionWatchdogTimer.Change(450, Timeout.Infinite);
            if (!_isReceiving)
            {
                lock (_incomingPcmLock)
                {
                    _incomingSenderId = packet.SenderId;
                    var peer = Peers.FirstOrDefault(p => p.Id == packet.SenderId);
                    _incomingSenderName = peer != null ? peer.DisplayName : "Colleague";
                }
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    IsReceiving = true;
                    IsReceivingWhisper = false;
                    var peer = Peers.FirstOrDefault(p => p.Id == packet.SenderId);
                    ActiveSpeakerName = peer != null ? $"{peer.DisplayName} (TRANSMITTING)" : "INCOMING TRANSMISSION";
                });
            }
            _jitterBuffer.Push(packet);
        }
        else if (packet.Type == MessageType.PttEnd)
        {
            FinishIncomingTransmission(isWhisper: false);
        }
    }

    private void FinishIncomingTransmission(bool isWhisper)
    {
        // Disarm watchdog timer
        _incomingTransmissionWatchdogTimer.Change(Timeout.Infinite, Timeout.Infinite);

        // Reset jitter buffer to drop stale/late packets
        _jitterBuffer.Reset();

        if (!_isReceiving && !_isReceivingWhisper)
        {
            return;
        }

        _isIncomingWhisperActive = false;

        byte[]? accumulatedBytes = null;
        string senderName = "Colleague";

        lock (_incomingPcmLock)
        {
            if (_incomingPcmAccumulator.Count >= AudioConfig.SampleRate * 2 / 2) // at least 0.5s of audio
            {
                accumulatedBytes = _incomingPcmAccumulator.ToArray();
                senderName = _incomingSenderName;

                var msg = new VoiceMessage(_incomingSenderId, _incomingSenderName, accumulatedBytes);
                ReplayBuffer.Add(msg);

                if (_isLocalAfk)
                {
                    VoicemailBox.AddVoicemail(msg);
                }

                Application.Current?.Dispatcher?.BeginInvoke(() =>
                {
                    OnPropertyChanged(nameof(CanReplay));
                    OnPropertyChanged(nameof(LastReplayInfo));
                    OnPropertyChanged(nameof(UnreadVoicemailCount));
                    OnPropertyChanged(nameof(HasUnreadVoicemails));
                    OnPropertyChanged(nameof(VoicemailBannerVisibility));
                    OnPropertyChanged(nameof(VoicemailBannerText));
                });
            }
            _incomingPcmAccumulator.Clear();
        }

        // Pass to Speech-to-Text service if audio was accumulated
        if (accumulatedBytes != null && accumulatedBytes.Length > 0)
        {
            SpeechToTextService.EnqueueAudioSegment(senderName, accumulatedBytes, isWhisper);
        }

        void ResetUi()
        {
            IsReceiving = false;
            IsReceivingWhisper = false;
            ActiveSpeakerLevel = 0.0f;
            ActiveSpeakerName = "Channel Idle";
        }

        if (Application.Current?.Dispatcher?.CheckAccess() == true)
        {
            ResetUi();
        }
        else
        {
            Application.Current?.Dispatcher?.Invoke(ResetUi);
        }

        // Combine Roger Beep + Squelch tail into single audio stream
        byte[] roger = SfxGenerator.GenerateRogerBeep(_selectedRogerBeep, 0.4f);
        byte[] squelch = SfxGenerator.GenerateSquelchTail(0.25f);
        byte[] combinedSfx = SfxGenerator.CombineAudio(roger, squelch);
        if (combinedSfx.Length > 0)
        {
            _playback.PlayPcm(combinedSfx);
        }
    }

    private void HandleJitterPacketReady(LancomPacket packet)
    {
        byte[] payload = packet.Payload;

        // Decrypt if packet was encrypted
        if (packet.IsEncrypted)
        {
            if (_channelCrypto.TryDecrypt(packet.SequenceNumber, packet.TimestampMs, packet.Payload, out var decrypted))
            {
                payload = decrypted;
            }
            else
            {
                // Key mismatch or tampered: drop encrypted frame safely
                return;
            }
        }

        // Decode Opus to PCM
        byte[] pcm = _opusCodec.Decode(payload);
        if (pcm.Length == 0) return;

        // Apply authentic Walkie-Talkie DSP filter & saturation
        _radioDsp.ProcessPcm(pcm, 0, pcm.Length);

        // Accumulate audio in memory for Instant Replay / Voicemail / Speech-to-Text
        lock (_incomingPcmLock)
        {
            _incomingPcmAccumulator.AddRange(pcm);
        }

        // Update speaker VU meter (non-blocking UI dispatch)
        var frame = new AudioFrame(pcm);
        Application.Current?.Dispatcher?.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (_isReceiving || _isReceivingWhisper)
            {
                ActiveSpeakerLevel = frame.RmsVolume * 2.5f;
            }
        });

        // Output to headset/speakers via WASAPI
        _playback.PlayPcm(pcm);
    }

    private void HandleJitterMissingPacket()
    {
        // Invoke Opus Packet Loss Concealment (PLC)
        byte[] pcm = _opusCodec.Decode(null, decodeFec: true);
        if (pcm.Length > 0)
        {
            _radioDsp.ProcessPcm(pcm, 0, pcm.Length);
            lock (_incomingPcmLock)
            {
                _incomingPcmAccumulator.AddRange(pcm);
            }
            _playback.PlayPcm(pcm);
        }
    }

    private void ApplyRelayEndpoint(string hostPort)
    {
        if (string.IsNullOrWhiteSpace(hostPort))
        {
            _transport.RelayEndPoint = null;
            return;
        }

        try
        {
            string[] parts = hostPort.Trim().Split(':');
            string host = parts[0];
            int port = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : 37733;

            if (IPAddress.TryParse(host, out var ip))
            {
                _transport.RelayEndPoint = new IPEndPoint(ip, port);
            }
            else
            {
                var entry = Dns.GetHostEntry(host);
                var resolved = entry.AddressList.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (resolved != null)
                {
                    _transport.RelayEndPoint = new IPEndPoint(resolved, port);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Relay Resolve Error] {ex.Message}");
        }
    }

    private void RefreshPeersList()
    {
        var activePeers = _peerManager.GetActivePeers().ToList();

        // Remove peers that left or timed out
        for (int i = Peers.Count - 1; i >= 0; i--)
        {
            if (!activePeers.Any(p => p.Id == Peers[i].Id))
            {
                Peers.RemoveAt(i);
            }
        }

        // Add or update active peers
        foreach (var active in activePeers)
        {
            var existing = Peers.FirstOrDefault(p => p.Id == active.Id);
            if (existing == null)
            {
                Peers.Add(active);
            }
            else
            {
                existing.DisplayName = active.DisplayName;
                existing.CustomAlias = active.CustomAlias;
                existing.MachineName = active.MachineName;
                existing.ChannelId = active.ChannelId;
                existing.IsTransmitting = active.IsTransmitting;
                existing.EndPoint = active.EndPoint;
                existing.LastSeen = active.LastSeen;
                existing.IsAfk = active.IsAfk;
                existing.AfkMinutes = active.AfkMinutes;
            }
        }
        OnPropertyChanged(nameof(OnlinePeerCount));
    }

    public void TriggerPttManual(bool pressed)
    {
        _pttHook.SetPttState(pressed);
    }

    public void SwitchChannel(byte channel)
    {
        CurrentChannel = channel;
    }

    public void StartRebindingKey()
    {
        IsRebinding = true;
        _pttHook.IsListeningForRebind = true;
    }

    private void HandleKeyRebound(int vkey)
    {
        IsRebinding = false;
        string keyName = GetKeyName(vkey);

        var existing = PttKeyPresets.FirstOrDefault(p => p.VKey == vkey);
        if (existing != null)
        {
            SelectedPttPreset = existing;
        }
        else
        {
            var custom = new PttKeyPreset { Name = keyName, VKey = vkey };
            PttKeyPresets.Insert(0, custom);
            SelectedPttPreset = custom;
        }
    }

    public static string GetKeyName(int vkey) => vkey switch
    {
        0x14 => "Caps Lock",
        0x20 => "Spacebar",
        0x12 => "Left Alt",
        0x11 => "Left Ctrl",
        0x10 => "Left Shift",
        0x09 => "Tab",
        0xC0 => "Tilde / Grave (~)",
        0x70 => "F1",
        0x71 => "F2",
        0x72 => "F3",
        0x73 => "F4",
        0x74 => "F5",
        0x75 => "F6",
        0x76 => "F7",
        0x77 => "F8",
        0x78 => "F9",
        0x79 => "F10",
        0x7A => "F11",
        0x7B => "F12",
        0x56 => "Key V",
        0x54 => "Key T",
        0x42 => "Key B",
        0x43 => "Key C",
        0x58 => "Key X",
        0x5A => "Key Z",
        0x00 => "Mouse 4 / 5 Only",
        >= 0x41 and <= 0x5A => $"Key {(char)vkey}",
        >= 0x30 and <= 0x39 => $"Key {(char)vkey}",
        _ => $"Key 0x{vkey:X2}"
    };

    private void LoadSettings()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Lancom");
            string file = Path.Combine(dir, "settings.json");
            if (File.Exists(file))
            {
                string json = File.ReadAllText(file);
                var s = JsonSerializer.Deserialize<LancomSettings>(json);
                if (s != null)
                {
                    if (s.LocalId != Guid.Empty) _localId = s.LocalId;
                    if (s.PeerNicknames != null)
                    {
                        _peerNicknames.Clear();
                        foreach (var kvp in s.PeerNicknames)
                        {
                            _peerNicknames[kvp.Key] = kvp.Value;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(s.DisplayName)) _displayName = s.DisplayName;
                    _currentChannel = s.CurrentChannel;
                    _enableRadioFilter = s.EnableRadioFilter;
                    _selectedRogerBeep = s.SelectedRogerBeep;
                    _relayHost = s.RelayHost ?? string.Empty;
                    _channelKey = s.ChannelKey ?? string.Empty;
                    _enableSubtitles = s.EnableSubtitles;
                    _channelCrypto.SetPassphrase(_channelKey);
                    SpeechToTextService.IsEnabled = _enableSubtitles;

                    var preset = PttKeyPresets.FirstOrDefault(p => p.VKey == s.TargetVKey);
                    if (preset != null)
                    {
                        _selectedPttPreset = preset;
                        _pttHook.TargetVKey = s.TargetVKey;
                        _pttKeyDisplay = s.TargetVKey == 0 ? "Mouse 4 / Mouse 5" : $"{preset.Name} / Mouse 4";
                    }
                    else if (s.TargetVKey > 0)
                    {
                        var custom = new PttKeyPreset { Name = GetKeyName(s.TargetVKey), VKey = s.TargetVKey };
                        PttKeyPresets.Insert(0, custom);
                        _selectedPttPreset = custom;
                        _pttHook.TargetVKey = s.TargetVKey;
                        _pttKeyDisplay = $"{custom.Name} / Mouse 4";
                    }

                    if (!string.IsNullOrWhiteSpace(_relayHost))
                    {
                        ApplyRelayEndpoint(_relayHost);
                    }
                }
            }
        }
        catch { }
    }

    public void SaveSettings()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Lancom");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string file = Path.Combine(dir, "settings.json");
            var s = new LancomSettings
            {
                LocalId = _localId,
                DisplayName = DisplayName,
                CurrentChannel = CurrentChannel,
                TargetVKey = SelectedPttPreset?.VKey ?? 0x14,
                SelectedRogerBeep = SelectedRogerBeep,
                EnableRadioFilter = EnableRadioFilter,
                RelayHost = RelayHost,
                ChannelKey = ChannelKey,
                EnableSubtitles = EnableSubtitles,
                PeerNicknames = new Dictionary<string, string>(_peerNicknames)
            };
            File.WriteAllText(file, JsonSerializer.Serialize(s));
        }
        catch { }
    }

    public void RenamePeer(Guid peerId, string newNickname)
    {
        string trimmed = newNickname?.Trim() ?? string.Empty;
        var peer = _peerManager.GetPeer(peerId);

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            _peerNicknames.TryRemove(peerId.ToString(), out _);
            if (peer != null && !string.IsNullOrWhiteSpace(peer.MachineName))
            {
                _peerNicknames.TryRemove(peer.MachineName, out _);
            }
            _peerManager.SetPeerAlias(peerId, null);
        }
        else
        {
            _peerNicknames[peerId.ToString()] = trimmed;
            if (peer != null && !string.IsNullOrWhiteSpace(peer.MachineName))
            {
                _peerNicknames[peer.MachineName] = trimmed;
            }
            _peerManager.SetPeerAlias(peerId, trimmed);
        }

        SaveSettings();
        RefreshPeersList();
        OnPropertyChanged(nameof(WhisperTargetName));
        OnPropertyChanged(nameof(TacticalStatusBannerText));
    }

    public string GetPeerNickname(Guid peerId)
    {
        if (_peerNicknames.TryGetValue(peerId.ToString(), out var alias)) return alias;
        return string.Empty;
    }

    #region Messaging & File Transfer Methods
    public void SendTextMessage(string? text = null)
    {
        string messageText = text ?? ChatInputText;
        if (string.IsNullOrWhiteSpace(messageText)) return;

        Guid targetUserId = IsWhisperActive && SelectedWhisperPeer != null ? SelectedWhisperPeer.Id : Guid.Empty;
        byte channelId = CurrentChannel;
        ulong ts = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        uint seq = Interlocked.Increment(ref _sequenceCounter);

        var payload = new TextMessagePayload
        {
            MessageId = Guid.NewGuid(),
            SenderName = DisplayName,
            Text = messageText.Trim(),
            TimestampMs = ts
        };

        bool isEncrypted = _channelCrypto.IsEnabled;
        byte[] serializedPayload = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (isEncrypted)
        {
            serializedPayload = _channelCrypto.Encrypt(seq, ts, serializedPayload);
        }

        var packet = new LancomPacket
        {
            Type = MessageType.TextMessage,
            SenderId = _localId,
            TargetUserId = targetUserId,
            ChannelId = channelId,
            SequenceNumber = seq,
            TimestampMs = ts,
            Flags = (byte)((targetUserId != Guid.Empty ? LancomPacket.FlagWhisper : 0) | (isEncrypted ? LancomPacket.FlagEncrypted : 0)),
            Payload = serializedPayload
        };

        _transport.BroadcastPacket(packet);

        var chatMsg = new ChatMessage
        {
            Id = payload.MessageId,
            SenderId = _localId,
            SenderName = DisplayName,
            ChannelId = channelId,
            TargetUserId = targetUserId,
            Text = payload.Text,
            Timestamp = DateTime.UtcNow,
            IsOutgoing = true,
            IsEncrypted = isEncrypted,
            IsDelivered = false
        };

        AddChatMessage(chatMsg);
        ChatInputText = string.Empty;
    }

    public void SendFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        Guid targetUserId = IsWhisperActive && SelectedWhisperPeer != null ? SelectedWhisperPeer.Id : Guid.Empty;
        byte channelId = CurrentChannel;
        ulong ts = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        string? thumbnailBase64 = null;
        var category = FileTransferManager.CategorizeFile(Path.GetExtension(filePath));
        if (category == FileCategory.Photo)
        {
            thumbnailBase64 = ImageThumbnailHelper.CreateThumbnailBase64(filePath);
        }

        var transfer = FileTransferManager.PrepareOutboundTransfer(filePath, channelId, targetUserId, DisplayName, thumbnailBase64);
        var offerPayload = FileTransferManager.CreateOfferPayload(transfer, DisplayName);

        bool isEncrypted = _channelCrypto.IsEnabled;
        byte[] serializedOffer = JsonSerializer.SerializeToUtf8Bytes(offerPayload);
        uint seq = Interlocked.Increment(ref _sequenceCounter);
        if (isEncrypted)
        {
            serializedOffer = _channelCrypto.Encrypt(seq, ts, serializedOffer);
        }

        var offerPacket = new LancomPacket
        {
            Type = MessageType.FileOffer,
            SenderId = _localId,
            TargetUserId = targetUserId,
            ChannelId = channelId,
            SequenceNumber = seq,
            TimestampMs = ts,
            Flags = (byte)((targetUserId != Guid.Empty ? LancomPacket.FlagWhisper : 0) | (isEncrypted ? LancomPacket.FlagEncrypted : 0)),
            Payload = serializedOffer
        };

        _transport.BroadcastPacket(offerPacket);

        var chatMsg = new ChatMessage
        {
            Id = Guid.NewGuid(),
            SenderId = _localId,
            SenderName = DisplayName,
            ChannelId = channelId,
            TargetUserId = targetUserId,
            Text = category switch
            {
                FileCategory.Photo => $"📷 Shared a photo: {transfer.FileName}",
                FileCategory.Video => $"🎥 Shared a video: {transfer.FileName}",
                _ => $"📁 Shared a file: {transfer.FileName}"
            },
            Timestamp = DateTime.UtcNow,
            IsOutgoing = true,
            IsEncrypted = isEncrypted,
            TransferId = transfer.TransferId,
            FileName = transfer.FileName,
            FileSize = transfer.FileSize,
            Category = category,
            ThumbnailBase64 = thumbnailBase64,
            LocalFilePath = filePath,
            TransferProgress = 0.0,
            IsTransferCompleted = false
        };

        AddChatMessage(chatMsg);

        // Stream chunks over UDP
        Task.Run(async () =>
        {
            await Task.Delay(50);
            await FileTransferManager.StreamChunksAsync(transfer.TransferId, (chunkIndex, totalChunks, chunkData) =>
            {
                SendChunkPacket(transfer.TransferId, targetUserId, channelId, chunkIndex, totalChunks, chunkData);
                return Task.CompletedTask;
            });
        });
    }

    private void SendChunkPacket(Guid transferId, Guid targetUserId, byte channelId, int chunkIndex, int totalChunks, byte[] chunkData)
    {
        uint chunkSeq = Interlocked.Increment(ref _sequenceCounter);
        ulong chunkTs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        byte[] rawChunk = FileChunkSerializer.Serialize(transferId, chunkIndex, totalChunks, chunkData);
        bool isEncrypted = _channelCrypto.IsEnabled;
        if (isEncrypted)
        {
            rawChunk = _channelCrypto.Encrypt(chunkSeq, chunkTs, rawChunk);
        }

        var chunkPacket = new LancomPacket
        {
            Type = MessageType.FileChunk,
            SenderId = _localId,
            TargetUserId = targetUserId,
            ChannelId = channelId,
            SequenceNumber = chunkSeq,
            TimestampMs = chunkTs,
            Flags = (byte)((targetUserId != Guid.Empty ? LancomPacket.FlagWhisper : 0) | (isEncrypted ? LancomPacket.FlagEncrypted : 0)),
            Payload = rawChunk
        };

        _transport.BroadcastPacket(chunkPacket);
    }

    private void HandleMissingChunksRequested(Guid transferId, Guid targetUserId, byte channelId, List<int> missingIndices)
    {
        uint seq = Interlocked.Increment(ref _sequenceCounter);
        ulong ts = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var payload = new FileMissingChunksRequestPayload
        {
            TransferId = transferId,
            MissingIndices = missingIndices
        };

        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(payload);
        bool isEncrypted = _channelCrypto.IsEnabled;
        if (isEncrypted)
        {
            serialized = _channelCrypto.Encrypt(seq, ts, serialized);
        }

        var packet = new LancomPacket
        {
            Type = MessageType.FileMissingChunksRequest,
            SenderId = _localId,
            TargetUserId = targetUserId,
            ChannelId = channelId,
            SequenceNumber = seq,
            TimestampMs = ts,
            Flags = (byte)((targetUserId != Guid.Empty ? LancomPacket.FlagWhisper : 0) | (isEncrypted ? LancomPacket.FlagEncrypted : 0)),
            Payload = serialized
        };

        _transport.BroadcastPacket(packet);
    }

    public void SendClipboardImage()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image != null)
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "Lancom");
                    Directory.CreateDirectory(tempDir);
                    string fileName = $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                    string filePath = Path.Combine(tempDir, fileName);

                    using var fileStream = new FileStream(filePath, FileMode.Create);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(fileStream);

                    SendFile(filePath);
                }
            }
        }
        catch { }
    }

    public void OpenDownloadedFile(ChatMessage? message)
    {
        if (message?.LocalFilePath != null && File.Exists(message.LocalFilePath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(message.LocalFilePath)
                {
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    public void OpenDownloadsFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(FileTransferManager.DownloadDirectory)
            {
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void AddChatMessage(ChatMessage msg)
    {
        Messages.Add(msg);
        if (ShouldDisplayMessage(msg))
        {
            DisplayMessages.Add(msg);
        }
    }

    private bool ShouldDisplayMessage(ChatMessage msg)
    {
        if (IsWhisperActive && SelectedWhisperPeer != null)
        {
            return (msg.TargetUserId == SelectedWhisperPeer.Id && msg.SenderId == _localId) ||
                   (msg.TargetUserId == _localId && msg.SenderId == SelectedWhisperPeer.Id);
        }
        else
        {
            return msg.TargetUserId == Guid.Empty && (msg.ChannelId == CurrentChannel || msg.ChannelId == 0xFF);
        }
    }

    public void RefreshDisplayMessages()
    {
        DisplayMessages.Clear();
        foreach (var msg in Messages)
        {
            if (ShouldDisplayMessage(msg))
            {
                DisplayMessages.Add(msg);
            }
        }
        OnPropertyChanged(nameof(CurrentChatTargetDisplay));
    }

    private void HandleTransferProgress(Guid transferId, double progress)
    {
        Application.Current?.Dispatcher?.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            var msg = Messages.FirstOrDefault(m => m.TransferId == transferId);
            if (msg != null)
            {
                msg.TransferProgress = progress;
            }
        });
    }

    private void HandleTransferCompleted(Guid transferId, string finalPath)
    {
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            var msg = Messages.FirstOrDefault(m => m.TransferId == transferId);
            if (msg != null)
            {
                msg.TransferProgress = 1.0;
                msg.IsTransferCompleted = true;
                msg.LocalFilePath = finalPath;
            }
        });
    }

    private void HandleTransferFailed(Guid transferId, string error)
    {
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            var msg = Messages.FirstOrDefault(m => m.TransferId == transferId);
            if (msg != null)
            {
                msg.IsTransferFailed = true;
                msg.Text += $" (Transfer failed: {error})";
            }
        });
    }
    #endregion

    #region INotifyPropertyChanged
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
    #endregion

    public void Dispose()
    {
        _incomingTransmissionWatchdogTimer?.Dispose();
        _processedMessageIds.Clear();
        FileTransferManager.Dispose();
        _subtitleFadeTimer?.Dispose();
        SpeechToTextService.Dispose();
        _channelCrypto.Dispose();
        _afkTimer?.Dispose();
        _pttHook.Dispose();
        _capture.Dispose();
        _playback.Dispose();
        _transport.Dispose();
        _peerManager.Dispose();
        _opusCodec.Dispose();
    }
}
