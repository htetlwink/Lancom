# 🎙️ Lancom

> **Zero-configuration, ultra-low-latency (<45ms) Push-to-Talk (PTT) voice walkie-talkie designed for Windows over local Wi-Fi and Ethernet networks.**

Built with **C# / .NET 9**, **WPF**, **NAudio (WASAPI Shared)**, and **Concentus (Pure Managed Opus)**.

[![Latest Release](https://img.shields.io/github/v/release/htetlwink/Lancom?color=10b981&label=Release&logo=github)](https://github.com/htetlwink/Lancom/releases/latest)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-0078d4?logo=windows)](https://github.com/htetlwink/Lancom/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

### 📥 Direct Downloads (Windows x64)

| Asset | Description | Download |
| :--- | :--- | :--- |
| **Lancom (GUI)** | Standalone single-file client. No installation required. | [**Download Lancom.exe**](https://github.com/htetlwink/Lancom/releases/latest/download/Lancom.exe) *(122 MB)* |
| **Complete Bundle** | Includes both `Lancom.exe` and `Lancom.Relay.exe`. | [**Download Lancom-win-x64.zip**](https://github.com/htetlwink/Lancom/releases/latest/download/Lancom-v1.0.0-win-x64.zip) *(81 MB)* |

> 💡 *Always grabs the newest version. You can also view all versions on the [**Releases Page**](https://github.com/htetlwink/Lancom/releases).*

---

## 🌟 Key Features

- **🛡️ 100% Non-Admin & Corporate Safe:** Runs entirely with standard user privileges (`asInvoker`). Requires zero administrator rights, zero driver installations, and writes zero entries into protected system folders.
- **⚡ Ultra-Low Mouth-to-Ear Latency (<45ms):** Event-driven WASAPI Shared audio capture + 20ms Opus frames + minimal 10–20ms adaptive jitter buffer.
- **🎙️ Global Push-to-Talk (PTT) Engine:**
  - Operates globally even when the app is minimized to the system tray or while working inside full-screen apps and games.
  - Native support for **Mouse 4 / Mouse 5 side thumb buttons** and keyboard keys (Caps Lock, Spacebar, F-keys, etc.) via Win32 Raw Input (`RIDEV_INPUTSINK`).
  - **Stuck-Mic Watchdog:** 50ms hardware key poller and foreground monitor ensures the microphone never gets stuck transmitting if you Alt-Tab or lock Windows (<kbd>Win</kbd> + <kbd>L</kbd>).
- **📻 Authentic Walkie-Talkie DSP:**
  - Motorola / NASA Quindar / Tactical Roger Beeps on PTT release.
  - Squelch tail analog white-noise cutoff ("Ksshh-chk").
  - Tactile mic click chirp on PTT press.
  - 80 Hz rumble cut + 350 Hz to 3200 Hz VHF radio bandpass filter + `tanh()` soft analog saturation waveshaper.
- **🏢 Corporate Wi-Fi AP Isolation Bypass:**
  - Includes **`Lancom.Relay`**: a lightweight, zero-dependency relay bridge that allows computers to communicate even on enterprise corporate Wi-Fi networks where access points block peer-to-peer traffic.
- **🎨 Cyber-Tactical HUD UI:**
  - Sleek, always-on-top draggable acrylic pill overlay with animated LED audio level meters (VU meter), active speaker badge, and channel quick-selector.
  - **Banner Settings Dropdown (`⚙ SETTINGS ▾`):** Header button toggles an overlay drawer for PTT key rebinding, Roger Beep selection, relay IP, and audio DSP toggles.
  - **High-Contrast Dark Theme:** Custom-styled dark controls (charcoal field bars, emerald accents, and crisp white typography).

---

## 🏗️ Architecture & Topology

```
+───────────────────────────────────────────────────────────────────────────────────+
|                                ADAPTIVE MODES                                     |
+───────────────────────────────────────────────────────────────────────────────────+
  Mode 1: Direct LAN P2P (Home / Unmanaged Wi-Fi)
    [ Laptop A ] ◄────── Unicast UDP (Port 37733) ──────► [ Laptop B ]

  Mode 2: Corporate AP Isolation Relay (Enterprise Office Wi-Fi)
    [ Laptop A ] ─── Outbound UDP ───► [ Lancom.Relay.exe ] ◄─── Outbound UDP ─── [ Laptop B ]
    (Wi-Fi Laptop)                      (Any Colleague / PC)                       (Wi-Fi Laptop)
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

## 🚀 Quick Start

### Standard Home or Unmanaged LAN
1. Download [**`Lancom.exe`**](https://github.com/htetlwink/Lancom/releases/latest/download/Lancom.exe) (or compile from source).
2. Run `Lancom.exe` on two or more Windows machines connected to the same Wi-Fi or Ethernet network.
3. Devices automatically discover each other via UDP presence beacons.
4. Press and hold **Caps Lock** or **Mouse 4 (Side Thumb Button)** or click **HOLD TO TALK** to speak!

---

### Corporate Office Wi-Fi (With Client / AP Isolation)
In corporate offices where Wi-Fi routers block direct peer-to-peer traffic:
1. One colleague runs **`Lancom.Relay.exe`** on their computer.
   - The relay console displays its local IP address (e.g., `10.20.1.45:37733`).
2. On all colleagues' machines, open `Lancom.exe`, click **`⚙ SETTINGS ▾`**, and enter that IP in the **Corporate Relay Host / IP** field.
3. All voice packets now route through the relay over outbound connections, bypassing corporate AP isolation with **<50ms latency**!

---

## 📂 Project Structure

```
lancom/
├── Lancom.sln                      # Visual Studio / .NET 9 Solution
├── Directory.Build.props           # Common solution build properties & metadata
├── LICENSE                         # MIT License
├── .gitignore                      # Git ignore rules for .NET, IDEs & artifacts
│
├── .github/
│   └── workflows/
│       └── build.yml               # GitHub Actions CI/CD for automated builds & releases
│
├── src/
│   ├── Lancom.Core/                # [net9.0] Pure Managed Audio, Codec & Protocol
│   │   ├── Audio/                  # AudioConfig (48kHz 20ms), AudioFrame level meters
│   │   ├── Codec/                  # ConcentusOpusCodec (100% pure C# Opus encoder/decoder)
│   │   ├── Dsp/                    # RadioDsp (rumble cut, bandpass, tanh saturation), SfxGenerator
│   │   ├── Network/                # JitterBuffer, PeerManager, PeerInfo
│   │   └── Packets/                # LancomPacket (Magic 'LC', 36-byte binary header)
│   │
│   ├── Lancom.Windows/             # [net9.0-windows] Windows OS Integration
│   │   ├── Audio/                  # WasapiAudioCapture, WasapiAudioPlayback (NAudio WASAPI)
│   │   ├── Input/                  # RawInputPttHook (Win32 RIDEV_INPUTSINK, Mouse 4/5, Watchdog)
│   │   ├── Network/                # PhysicalNetworkHelper (filters WSL/Hyper-V), WindowsUdpTransport
│   │   └── UI/                     # TrayIconManager (Native Shell_NotifyIconW P/Invoke)
│   │
│   ├── Lancom.Relay/               # [net9.0] Cross-Platform Corporate Relay Bridge
│   │   ├── RelayServer.cs          # Multi-channel UDP forwarding engine
│   │   └── Program.cs              # Console entrypoint & live telemetry dashboard
│   │
│   └── Lancom.App/                 # [net9.0-windows] WPF Cyber-Tactical Application
│       ├── App.xaml / App.xaml.cs  # Single-instance mutex, app theme resources
│       ├── MainWindow.xaml / .cs   # Cyber-Tactical HUD Pill + Expandable Command Deck
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
- `dist\app\Lancom.exe`
- `dist\relay\Lancom.Relay.exe`

### On Linux (Cross-compilation):
```bash
./scripts/build.sh
```

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
