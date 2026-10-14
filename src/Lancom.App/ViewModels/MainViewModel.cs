using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using Lancom.Core.Audio;
using Lancom.Core.Codec;
using Lancom.Core.Dsp;
using Lancom.Core.Network;
using Lancom.Core.Packets;
using Lancom.Windows.Audio;
using Lancom.Windows.Input;
using Lancom.Windows.Network;

namespace Lancom.App.ViewModels;

public class PttKeyPreset
{
    public string Name { get; set; } = string.Empty;
    public int VKey { get; set; }

    public override string ToString() => Name;
}

public class LancomSettings
{
    public string DisplayName { get; set; } = Environment.MachineName;
    public byte CurrentChannel { get; set; } = 1;
    public int TargetVKey { get; set; } = 0x14;
    public RogerBeepType SelectedRogerBeep { get; set; } = RogerBeepType.MotorolaTactical;
    public bool EnableRadioFilter { get; set; } = true;
    public string RelayHost { get; set; } = string.Empty;
}

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Guid _localId = Guid.NewGuid();
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

    // AFK, Replay, Voicemail, and Paging State
    private bool _isLocalAfk;
    private int _localAfkMinutes;
    public VoiceReplayBuffer ReplayBuffer { get; } = new(3);
    public VoicemailBox VoicemailBox { get; } = new();
    private readonly List<byte> _incomingPcmAccumulator = new();
    private readonly object _incomingPcmLock = new();
    private Guid _incomingSenderId = Guid.Empty;
    private string _incomingSenderName = "Colleague";

    public event Action<string, string>? OnPageAlertReceived;

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
                RefreshPeersList();
                SaveSettings();
            }
        }
    }

    public string CurrentChannelDisplay => CurrentChannel == 0xFF ? "ALL-CALL (EMERGENCY)" : $"CHANNEL {CurrentChannel:D2}";

    public bool IsTransmitting
    {
        get => _isTransmitting;
        private set => SetField(ref _isTransmitting, value);
    }

    public bool IsReceiving
    {
        get => _isReceiving;
        private set => SetField(ref _isReceiving, value);
    }

    public string ActiveSpeakerName
    {
        get => _activeSpeakerName;
        private set => SetField(ref _activeSpeakerName, value);
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
    #endregion

    public MainViewModel()
    {
        _peerManager = new PeerManager();
        _opusCodec = new ConcentusOpusCodec();
        _radioDsp = new RadioDsp { EnableRadioEffect = _enableRadioFilter };
        _capture = new WasapiAudioCapture();
        _playback = new WasapiAudioPlayback();
        _transport = new WindowsUdpTransport(_localId, _displayName, _peerManager);
        _pttHook = new RawInputPttHook();
        _jitterBuffer = new JitterBuffer(1);

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

        LoadSettings();
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
                ActiveSpeakerName = "YOU ARE TRANSMITTING";

                // Broadcast PTT Start packet
                var pttStart = new LancomPacket
                {
                    Type = MessageType.PttStart,
                    SequenceNumber = Interlocked.Increment(ref _sequenceCounter)
                };
                _transport.BroadcastPacket(pttStart);
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
                if (roger.Length > 0) _playback.PlayPcm(roger);
                if (squelch.Length > 0) _playback.PlayPcm(squelch);

                // Broadcast PTT End packet (send 2 copies for network loss resilience)
                var pttEnd = new LancomPacket
                {
                    Type = MessageType.PttEnd,
                    SequenceNumber = Interlocked.Increment(ref _sequenceCounter)
                };
                _transport.BroadcastPacket(pttEnd);
                _transport.BroadcastPacket(pttEnd);
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

        // Send Audio Packet
        var audioPacket = new LancomPacket
        {
            Type = MessageType.AudioData,
            SequenceNumber = Interlocked.Increment(ref _sequenceCounter),
            Payload = encoded
        };

        _transport.BroadcastPacket(audioPacket);
    }

    private void HandlePacketReceived(LancomPacket packet, IPEndPoint sender)
    {
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
                var peer = Peers.FirstOrDefault(p => p.Id == packet.SenderId);
                ActiveSpeakerName = peer != null ? $"{peer.DisplayName} (TRANSMITTING)" : "INCOMING TRANSMISSION";
            });

            // Remote mic chirp
            _playback.PlayPcm(SfxGenerator.GenerateMicClick(0.25f));
        }
        else if (packet.Type == MessageType.AudioData)
        {
            _jitterBuffer.Push(packet);
        }
        else if (packet.Type == MessageType.PttEnd)
        {
            lock (_incomingPcmLock)
            {
                if (_incomingPcmAccumulator.Count >= AudioConfig.SampleRate * 2 / 2) // at least 0.5s of audio
                {
                    var msg = new VoiceMessage(_incomingSenderId, _incomingSenderName, _incomingPcmAccumulator.ToArray());
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

            Application.Current?.Dispatcher?.Invoke(() =>
            {
                IsReceiving = false;
                ActiveSpeakerLevel = 0.0f;
                ActiveSpeakerName = "Channel Idle";
            });

            // Play Roger Beep + Squelch tail
            byte[] roger = SfxGenerator.GenerateRogerBeep(_selectedRogerBeep, 0.4f);
            byte[] squelch = SfxGenerator.GenerateSquelchTail(0.25f);
            if (roger.Length > 0) _playback.PlayPcm(roger);
            if (squelch.Length > 0) _playback.PlayPcm(squelch);
        }
    }

    private void HandleJitterPacketReady(LancomPacket packet)
    {
        // Decode Opus to PCM
        byte[] pcm = _opusCodec.Decode(packet.Payload);
        if (pcm.Length == 0) return;

        // Apply authentic Walkie-Talkie DSP filter & saturation
        _radioDsp.ProcessPcm(pcm, 0, pcm.Length);

        // Accumulate audio in memory for Instant Replay / Voicemail
        lock (_incomingPcmLock)
        {
            _incomingPcmAccumulator.AddRange(pcm);
        }

        // Update speaker VU meter (non-blocking UI dispatch)
        var frame = new AudioFrame(pcm);
        Application.Current?.Dispatcher?.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            ActiveSpeakerLevel = frame.RmsVolume * 2.5f;
            IsReceiving = true;
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
                    if (!string.IsNullOrWhiteSpace(s.DisplayName)) _displayName = s.DisplayName;
                    _currentChannel = s.CurrentChannel;
                    _enableRadioFilter = s.EnableRadioFilter;
                    _selectedRogerBeep = s.SelectedRogerBeep;
                    _relayHost = s.RelayHost ?? string.Empty;

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
                DisplayName = DisplayName,
                CurrentChannel = CurrentChannel,
                TargetVKey = SelectedPttPreset?.VKey ?? 0x14,
                SelectedRogerBeep = SelectedRogerBeep,
                EnableRadioFilter = EnableRadioFilter,
                RelayHost = RelayHost
            };
            File.WriteAllText(file, JsonSerializer.Serialize(s));
        }
        catch { }
    }

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
        _afkTimer?.Dispose();
        _pttHook.Dispose();
        _capture.Dispose();
        _playback.Dispose();
        _transport.Dispose();
        _peerManager.Dispose();
        _opusCodec.Dispose();
    }
}
