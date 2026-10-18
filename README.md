# 🎙️ Lancom

> **Zero-configuration, ultra-low-latency (<45ms) Push-to-Talk (PTT) voice intercom, military-grade E2EE, 1-on-1 private whisper, offline live subtitles (STT), instant messaging, and high-speed LAN file transfer suite designed for Windows.**

Built with **C# / .NET 9**, **WPF**, **NAudio (WASAPI Shared)**, **Concentus (Pure Managed Opus)**, **AES-256-GCM HKDF**, and **Whisper.net (Offline AI Subtitles)**.

[![Latest Release](https://img.shields.io/github/v/release/htetlwink/Lancom?color=10b981&label=Release&logo=github)](https://github.com/htetlwink/Lancom/releases/latest)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-0078d4?logo=windows)](https://github.com/htetlwink/Lancom/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

### 📥 Direct Downloads (Windows x64)

| Asset | Description | Download |
| :--- | :--- | :--- |
| **Lancom (GUI)** | Standalone single-file client. No installation required. | [**Download Lancom.exe**](https://github.com/htetlwink/Lancom/releases/latest/download/Lancom.exe) |
| **Complete Bundle** | Includes both `Lancom.exe`, `Lancom.Relay.exe`, and native Whisper runtimes. | [**Download Lancom-win-x64.zip**](https://github.com/htetlwink/Lancom/releases/latest/download/Lancom-win-x64.zip) |

> 💡 *Always grabs the newest release. You can also view all releases and release notes on the [**Releases Page**](https://github.com/htetlwink/Lancom/releases).*

---

## 🌟 Key Features

### 🎙️ Ultra-Low-Latency Voice Intercom
- **⚡ Mouth-to-Ear Latency under 45ms:** Event-driven WASAPI Shared audio capture + 20ms Opus frames @ 24–32 kbps with In-Band FEC + 15ms adaptive jitter buffer.
- **🎙️ Global Push-to-Talk (PTT) Engine:**
  - Operates globally even when minimized to the system tray or running full-screen games and apps.
  - Native support for **Mouse 4 / Mouse 5 side thumb buttons** and keyboard keys (Caps Lock, Spacebar, F-keys, etc.) via Win32 Raw Input (`RIDEV_INPUTSINK`).
  - **Stuck-Mic Watchdog:** 50ms hardware key poller and foreground monitor ensures the microphone never gets stuck transmitting if you Alt-Tab or lock Windows (<kbd>Win</kbd> + <kbd>L</kbd>).
- **📻 16-Preset Tactical Roger Beep Suite:**
  - 16 tactical, military, and sci-fi roger beep presets synthesized dynamically in 48kHz PCM: *Motorola Tactical, NASA Quindar, Short Pip, Harris Falcon Tri-Tone, MDC-1200 Police, Aviation Double-Pip, Kenwood Two-Tone, Ham Repeater, Cyberpunk Sweep, Aliens USCM Marine, Star Wars Stormtrooper, Battlestar Viper, Star Trek Communicator, HAL 9000, and Blade Runner Spinner*.
  - Squelch tail analog white-noise cutoff ("Ksshh-chk") and tactile mic-click chirp on PTT press.
  - In-app **"TEST"** audio preview button in Settings drawer.
  - 80 Hz rumble cut + 350 Hz to 3200 Hz VHF radio bandpass filter + `tanh()` soft analog saturation waveshaper.

### 🔒 Military-Grade Channel E2EE & Privacy
- **AES-256-GCM End-to-End Encryption:** Zero-dependency authenticated encryption with HKDF-SHA256 key derivation from a channel passphrase.
- All channel voice packets, instant messages, and file transfers are protected with unique 96-bit nonces.
- Header lock icon and channel encryption status indicator.

### 🤫 1-on-1 Targeted Whisper Line
- Point-to-point private voice and messaging channel directly to any specific colleague in your roster without switching channels or interrupting team communications.
- Dedicated purple Whisper status banner and individual peer whisper action buttons.
- Fully routable over local LAN and through `Lancom.Relay`.

### 📝 Offline Live Subtitles (AI Speech-to-Text)
- **100% On-Device AI Transcription:** Uses `Whisper.net` and `whisper.cpp` CPU runtimes. Zero cloud calls, zero external API keys, zero audio data leaving your machine.
- Automatic 48kHz to 16kHz audio resampler pipeline.
- Live subtitle transcript banner appears above the command deck during incoming speech.

### 💬 Instant Messaging & Media Sharing
- **Dual Command Deck:** Switch effortlessly between **ONLINE COLLEAGUES** and **CHAT & FILES**.
- **Rich Chat:** Expandable multi-line text input with <kbd>Shift</kbd> + <kbd>Enter</kbd> newline support, unread notification badges, and tactical audio alert chimes.
- **Inline Photo Viewer:** Image thumbnail cards with click-to-open full-resolution inspection.
- **Clipboard Screenshot Paste:** Hit <kbd>Ctrl</kbd> + <kbd>V</kbd> anywhere in the window to immediately capture and send clipboard screenshots.

### 📁 High-Speed Resilient File Transfers
- Chunked MTU-safe file transfer engine operating directly over UDP with 8-chunk burst pacing to bypass Windows timer throttling.
- **SHA-256 Checksum Verification:** Cryptographic hash check for guaranteed bit-perfect file reception.
- **Active Retransmission (NACK):** Automatic recovery of lost UDP packets via missing-chunk requests.
- Real-time animated transfer progress cards with cancel and open-folder actions.

### 🏷️ Persistent Colleague Renaming
- Customize names for any colleague on your network; custom aliases persist across restarts in `%LOCALAPPDATA%\Lancom\settings.json`.
- Displays friendly names while preserving original network hostnames for identification.

### 🛡️ Corporate AP Isolation Relay & Reliability
- **AP Isolation Bypass:** Includes `Lancom.Relay`, enabling computers on enterprise Wi-Fi (where client-to-client traffic is blocked) to communicate over outbound UDP with <50ms latency.
- **5s Packet Deduplication:** Sliding-window deduplication eliminates multi-adapter broadcast echo loops.
- **Triple PTT-End Burst & 450ms Watchdog:** High-reliability transmission termination prevents stuck UI or audio states on lossy Wi-Fi links.
- **100% Non-Admin & Portable:** Runs entirely under standard user privileges (`asInvoker`). No drivers or registry writes required.

---

## 🏗️ Architecture & Topology

```
+───────────────────────────────────────────────────────────────────────────────────+
|                                ADAPTIVE MODES                                     |
+───────────────────────────────────────────────────────────────────────────────────+
  Mode 1: Direct LAN P2P (Home / Unmanaged Wi-Fi / Ethernet)
    [ Laptop A ] ◄────── Unicast / Broadcast UDP (Port 37733) ──────► [ Laptop B ]
    • Automatic UDP Presence Beacons
    • Subnet Directed Broadcast + Direct Unicast

  Mode 2: Corporate AP Isolation Relay (Enterprise Office Wi-Fi)
    [ Laptop A ] ─── Outbound UDP ───► [ Lancom.Relay.exe ] ◄─── Outbound UDP ─── [ Laptop B ]
    (Colleague PC)                      (Bridge Host PC)                           (Colleague PC)
    • Bypasses wireless client isolation
    • Relays Channel Audio, Whisper Lines, Chat & File Chunks
+───────────────────────────────────────────────────────────────────────────────────+
```

### End-to-End Latency Budget

| Stage | Component | Latency |
| :--- | :--- | :--- |
| **Microphone Capture** | WASAPI Event-Driven Shared Mode | **10.0 ms** |
| **Opus Voice Encoding** | 20ms Frame @ 24 kbps with In-Band FEC | **5.0 ms** |
| **LAN Transit** | Physical Wi-Fi 802.11 Block ACK Unicast | **3.0 ms** |
| **Receiver Jitter Buffer** | Adaptive Resequencer (1 frame) | **15.0 ms** |
| **Opus Voice Decoding** | Concentus Decoder + Packet Loss Concealment (PLC) | **1.0 ms** |
| **Audio Rendering** | WASAPI Playback Buffer | **10.0 ms** |
| **Total Mouth-to-Ear** | | **~44.0 ms** |

---

## 📦 Binary Packet Protocol

All network communications use a unified 36-byte binary header followed by a variable-length payload:

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|       Magic Header (0x4C43)   |    Version    |  Message Type |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|   Channel ID  |     Flags     |        Sequence Number        |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|      Sequence Number (cont)   |       Timestamp (ms)...       |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                    Timestamp (ms) (cont)...                   |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                                                               |
+                   Sender GUID (16 bytes)                      +
|                                                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|        Payload Length         |    Payload (Opus/JSON/Data)   |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

### Supported Message Types:
* `AudioData (1)`: Opus voice frame (raw or AES-GCM encrypted).
* `PttStart (2)` / `PttEnd (3)`: Transmission signaling and roger beep trigger.
* `PresenceBeacon (4)` / `Ping (5)` / `TextPing (6)` / `PageAlert (7)`: Discovery and alerts.
* `WhisperVoice (8)` / `WhisperPttStart (9)` / `WhisperPttEnd (10)`: Private targeted communications.
* `TextMessage (20)` / `MessageAck (21)`: Instant messaging with delivery confirmation.
* `FileOffer (22)` / `FileOfferAnswer (23)` / `FileChunk (24)` / `FileChunkAck (25)` / `FileMissingChunksRequest (26)`: Chunked file transfer protocol.

---

## 🚀 Quick Start

### 1. Standard LAN or Home Wi-Fi
1. Download [**`Lancom.exe`**](https://github.com/htetlwink/Lancom/releases/latest/download/Lancom.exe) (or compile from source).
2. Launch `Lancom.exe` on two or more Windows PCs on the same network.
3. Colleagues automatically appear in the **ONLINE COLLEAGUES** tab.
4. Press and hold <kbd>Caps Lock</kbd>, <kbd>Mouse 4</kbd> (thumb button), or click **HOLD TO TALK** to speak.

### 2. Corporate Office Wi-Fi (AP Isolation Relay)
If peer discovery is blocked by your corporate Wi-Fi:
1. One user runs **`Lancom.Relay.exe`** on their PC (the console prints the local IP, e.g. `10.20.1.45:37733`).
2. Colleagues open `Lancom.exe`, open **`⚙ SETTINGS ▾`**, and enter that IP in the **Corporate Relay Host / IP** field.
3. All voice, chat, whisper, and file transfers seamlessly route through the relay with zero degradation.

### 3. Enabling End-to-End Encryption (E2EE)
1. In `Lancom.exe`, open **`⚙ SETTINGS ▾`**.
2. Enter a shared **Channel Passphrase**.
3. All traffic on that channel is instantly secured with AES-256-GCM. Unmatched passphrase clients cannot eavesdrop.

### 4. 1-on-1 Whisper & File Sharing
* **Whisper:** Click the **WHISPER** button next to any peer's name in the list to open a private line.
* **Instant Message:** Switch to the **CHAT & FILES** tab and type your message.
* **Send Files / Screenshots:** Drag and drop files onto the window, or press <kbd>Ctrl</kbd> + <kbd>V</kbd> to send a clipboard screenshot.

---

## 📂 Project Structure

```
lancom/
├── Lancom.sln                      # Visual Studio / .NET 9 Solution
├── Directory.Build.props           # Common solution build properties & version metadata (1.3.1)
├── LICENSE                         # MIT License
├── .gitignore                      # Git ignore rules for .NET, IDEs & artifacts
│
├── .github/
│   └── workflows/
│       └── build.yml               # GitHub Actions CI/CD for automated builds & releases
│
├── src/
│   ├── Lancom.Core/                # [net9.0] Pure Managed Audio, Codec & Protocol
│   │   ├── Audio/                  # AudioConfig (48kHz 20ms), AudioResampler (48kHz -> 16kHz for STT)
│   │   ├── Codec/                  # ConcentusOpusCodec (100% pure C# Opus encoder/decoder)
│   │   ├── Dsp/                    # RadioDsp (rumble cut, bandpass, tanh saturation), SfxGenerator (16 Roger Beeps)
│   │   ├── Messaging/              # ChatMessage, FileTransferModels
│   │   ├── Network/                # JitterBuffer, PeerManager, PeerInfo
│   │   ├── Packets/                # LancomPacket (Magic 'LC', 36-byte binary header)
│   │   ├── Security/               # ChannelCrypto (AES-256-GCM + HKDF-SHA256)
│   │   └── Transfers/              # FileTransferManager (Chunked UDP, Burst Pacing, NACK Retransmission)
│   │
│   ├── Lancom.Windows/             # [net9.0-windows] Windows OS Integration
│   │   ├── Audio/                  # WasapiAudioCapture, WasapiAudioPlayback (NAudio WASAPI)
│   │   ├── Input/                  # RawInputPttHook (Win32 RIDEV_INPUTSINK, Mouse 4/5, Watchdog)
│   │   ├── Network/                # PhysicalNetworkHelper (filters WSL/Hyper-V), WindowsUdpTransport (Deduplication)
│   │   └── UI/                     # TrayIconManager (Native Shell_NotifyIconW P/Invoke)
│   │
│   ├── Lancom.Relay/               # [net9.0] Cross-Platform Corporate Relay Bridge
│   │   ├── RelayServer.cs          # Multi-channel UDP forwarding engine with Whisper routing
│   │   └── Program.cs              # Console entrypoint & live telemetry dashboard
│   │
│   └── Lancom.App/                 # [net9.0-windows] WPF Cyber-Tactical Application
│       ├── App.xaml / App.xaml.cs  # Single-instance mutex, app theme resources
│       ├── MainWindow.xaml / .cs   # Cyber-Tactical HUD Pill + Dual-Deck Interface
│       ├── Subtitles/              # SpeechToTextService (Whisper.net offline AI transcription)
│       ├── Utils/                  # ImageThumbnailHelper, Base64ImageConverter, Converters
│       ├── ViewModels/             # MainViewModel (Reactive MVVM engine)
│       ├── app.ico                 # Multi-resolution application icon
│       └── app.manifest            # asInvoker (Standard non-admin execution)
│
└── scripts/
    ├── build.sh                    # Linux build & cross-compilation script
    └── publish-windows.bat         # 1-click Windows build batch script
```

---

## 🛠️ Building From Source

### Prerequisites
- [.NET SDK 9.0](https://dotnet.microsoft.com/download/dotnet/9.0)

### On Windows:
Double-click `scripts\publish-windows.bat` or run:
```cmd
dotnet publish src\Lancom.App\Lancom.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\app
dotnet publish src\Lancom.Relay\Lancom.Relay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist\relay
```
The output executables will be generated in:
- `dist\app\Lancom.exe` (embedded with Whisper native runtimes)
- `dist\relay\Lancom.Relay.exe`

### On Linux (Cross-compilation):
```bash
./scripts/build.sh
```

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
