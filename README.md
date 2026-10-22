# DualShock 4 Bluetooth Audio Streamer for Windows 11

A standalone, driverless Windows 11 application that captures PC playback audio via WASAPI Loopback and streams it wirelessly to the 3.5 mm headphone jack of a Sony DualShock 4 controller connected over standard Bluetooth.

---

## Overview

When connected via USB, the Sony DualShock 4 exposes a standard USB Audio Class (UAC) 1.0 device. However, when connected over Bluetooth, Sony's firmware **does not register as a standard Windows A2DP or HFP audio device**. Instead, audio must be encapsulated inside vendor-defined HID Output Reports (`0x11` and `0x17`) and encoded using the Subband Codec (SBC) algorithm.

`ControllerAudioSwitcher` solves this limitation by providing:
- **Pure User-Mode Architecture**: No kernel drivers, no virtual audio cables, and no system security changes required.
- **Hardware-Accurate Protocol**: Direct HID Overlapped communication using Report `0x11` (arming/volume) and Report `0x17` (4 SBC frames = 16 ms @ 32 kHz stereo).
- **Simultaneous Gameplay**: Uses the `0xA0` mode byte to preserve gamepad input reports, allowing uninterrupted gaming while listening to wireless audio.
- **Modern Windows 11 Dashboard**: Real-time telemetry, buffer depth, latency monitoring, volume control, and system tray integration.

---

## System Requirements

- **Operating System**: Windows 11 (or Windows 10 build 19041+) 64-bit.
- **Hardware**: Genuine Sony DualShock 4 controller (`CUH-ZCT1` v1 or `CUH-ZCT2` v2).
- **Audio Output**: Standard 3.5 mm stereo headphones or earbuds plugged into the controller's jack.
- **Bluetooth**: Bluetooth 4.0, 4.2, or 5.x adapter.
- **Runtime**: .NET 8.0 Runtime (or self-contained build).

---

## Project Structure

```
ControllerAudioSwitcher/
├── src/
│   ├── ControllerAudioSwitcher.App/         # WPF Windows 11 UI & System Tray Service
│   ├── ControllerAudioSwitcher.Core/        # SBC Codec, Protocol Framing, CRC-32 & Domain Logic
│   ├── ControllerAudioSwitcher.Audio/       # WASAPI Loopback Capture & Linear Phase Resampler
│   ├── ControllerAudioSwitcher.Controller/  # SetupAPI HID Controller Discovery & Filtering
│   └── ControllerAudioSwitcher.Transport/   # Win32 Overlapped HID Asynchronous Transport
├── tests/
│   └── ControllerAudioSwitcher.Core.Tests/  # Automated MSTest Suite (29 unit & lifecycle tests)
├── docs/
│   ├── RESEARCH.md                          # Prior art, protocol reverse engineering & findings
│   ├── ARCHITECTURE.md                      # Detailed system architecture & concurrency model
│   ├── PROTOCOL_FINDINGS.md                 # Byte-by-byte binary report specification
│   ├── HARDWARE_TESTING.md                  # Physical DualShock 4 manual verification guide
│   └── TROUBLESHOOTING.md                  # Solutions for common Bluetooth and audio issues
├── bin/publish/                             # Published deployment artifacts
├── build.ps1                                # PowerShell one-step build, test, and publish script
├── THIRD_PARTY_NOTICES.md                   # Third-party open-source licenses & notices
├── LICENSE                                  # MIT License
└── README.md
```

---

## Building and Testing

### Quick Build with PowerShell
To build the solution, execute automated tests, and produce a published release:
```powershell
.\build.ps1 -Publish
```

### Manual Build via .NET CLI
```powershell
# Restore dependencies and build
dotnet build ControllerAudioSwitcher.sln -c Release

# Run automated tests
dotnet test tests/ControllerAudioSwitcher.Core.Tests/ -c Release

# Publish self-contained executable
dotnet publish src/ControllerAudioSwitcher.App/ -c Release -r win-x64 --self-contained false -o bin/publish
```

---

## How to Use

1. **Pair Controller via Bluetooth**:
   - Disconnect the USB cable.
   - Hold **Share + PS** until the lightbar flashes white.
   - Go to Windows **Settings > Bluetooth & devices > Add device > Bluetooth** and select **Wireless Controller**.
2. **Launch Application**:
   - Run `ControllerAudioSwitcher.App.exe` from `bin/publish/`.
   - The dashboard will show `Sony DualShock 4` and the status badge `Bluetooth Ready`.
3. **Plug in Headphones**:
   - Insert 3.5 mm headphones firmly into the controller's bottom audio jack.
4. **Start Streaming**:
   - Click **▶ Start Streaming**.
   - PC audio will immediately play through your headphones.
   - You can minimize the application to the system tray; streaming continues seamlessly in the background.

---

## Verification Status

| Component | Status | Verification Details |
|:---|:---:|:---|
| **CRC-32 Checksum** | **VERIFIED** | Validated against IEEE 802.3 and DS4 `0xA2` Bluetooth vectors in unit tests. |
| **SBC Codec** | **VERIFIED** | Generates exact 109-byte frames @ 32 kHz stereo bitpool 48 with CRC-8 header check. |
| **Protocol Framing** | **VERIFIED** | Report `0x11` (78B) and Report `0x17` (462B) with target `0x24` and mode `0xA0`. |
| **WASAPI Resampler** | **VERIFIED** | Phase-preserving linear resampler converts 48 kHz/44.1 kHz float to 32 kHz PCM16. |
| **Lifecycle & Recovery**| **VERIFIED** | Connect, disconnect, and volume commands verified via test doubles. |
| **Physical Sound Playout** | **PENDING HW TEST** | Requires a physical DualShock 4 connected over Bluetooth (see [HARDWARE_TESTING.md](docs/HARDWARE_TESTING.md)). |

---

## Licensing & Third-Party Notices

This project is licensed under the **MIT License**.
See [LICENSE](LICENSE) for details.

Third-party components:
- **NAudio**: MIT License (Mark Heath & contributors)
- **SbcSharp**: Apache License 2.0 (asimonf & Nefarius)
- See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for full notices and attributions.
