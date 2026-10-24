# DualShock 4 Bluetooth Audio Streamer for Windows 11

A standalone, driverless Windows 10/11 application that captures PC playback audio via WASAPI Loopback and streams it wirelessly to the 3.5 mm headphone jack of a Sony DualShock 4 controller connected over standard Bluetooth.

---
## How to Use

1. **Pair Controller via Bluetooth**:
   - Disconnect the USB cable.
   - Hold **Share + PS** until the lightbar flashes white.
   - Go to Windows **Settings > Bluetooth & devices > Add device > Bluetooth** and select **Wireless Controller**.
2. **Launch Application**:
   - Run `DS4SoundLink.exe` from `bin/standalone/` or releases.
   - The dashboard will show `Sony DualShock 4` and the status badge `Bluetooth Ready`.
3. **Choose Output Destination**:
   - Use sliding toggle to select **AUX / Headphone Jack** or **Internal Speaker**.
   - If using headphones, insert standard 3.5 mm headphones into the bottom jack.
4. **Start Streaming**:
   - Click **▶ Start Streaming**.
   - PC audio will immediately route to your chosen controller output.
   - You can switch between headphones and internal speaker live while streaming without dropping audio.
   - Click **⏹ Stop Streaming** at any time; the stream gracefully halts with zero UI freezing and can be restarted immediately.

---



## Overview

When connected via USB, the Sony DualShock 4 exposes a standard USB Audio Class (UAC) 1.0 device. However, when connected over Bluetooth, Sony's firmware **does not register as a standard Windows A2DP or HFP audio device**. Instead, audio must be encapsulated inside vendor-defined HID Output Reports (`0x11` and `0x17`) and encoded using the Subband Codec (SBC) algorithm.

`DS4 Sound Link` solves this limitation by providing:
- **Pure User-Mode Architecture**: No kernel drivers, no virtual audio cables, and no system security changes required.
- **Hardware-Accurate Protocol**: Direct HID Overlapped communication using Report `0x11` (arming/volume) and Report `0x17` (4 SBC frames = 16 ms @ 32 kHz stereo).
- **Simultaneous Gameplay**: Uses the `0xA0` mode byte to preserve gamepad input reports, allowing uninterrupted gaming while listening to wireless audio.
- **Modern Windows 11 Dashboard**: Real-time telemetry, buffer depth, latency monitoring, volume control, and system tray integration.
- **Hardware-Isolated Output Selector (v2.1)**: Dedicated segmented slider to route audio exclusively to either the **AUX / Headphone Jack** (3.5 mm stereo) or the **Internal Speaker** (mono downmix), preventing simultaneous audio bleed.
- **Hardware Battery Level Indicator (v2.1)**: Live battery percentage (0–100%) and charging status widget on the dashboard, extracted directly from Sony HID input reports (Bluetooth 0x11 and USB 0x01).
- **Crack-Free True-Peak SBC Headroom & Dynamic AGC (v2.1)**: Automatic gain and dynamic headroom management enforcing a -1.41 dBFS ceiling (0.85f max) with smooth hyperbolic tangent limiting. Completely eliminates high-frequency cracking/overshoot in the controller's internal SBC decoder and DAC while maximizing clear, punchy loudness automatically.
- **Continuous Phase Resampling (v2.1)**: Fractional carry interpolation across WASAPI buffer chunks, completely eliminating cyclic boundary phase discontinuities and high-frequency harmonic hiss.
- **Asynchronous, Non-Blocking Pipeline Lifecycle (v2.1)**: Reliable cancellation coordination that prevents UI deadlocks when stopping or restarting audio streaming.
- **Extended Diagnostics & Telemetry (v2.1)**: Live tracking of buffer underruns, dropped audio frames, encoder errors, and Bluetooth transmission failures.

---

## System Requirements

- **Operating System**: Windows 11 (or Windows 10 build 19041+) 64-bit.
- **Hardware**: Genuine Sony DualShock 4 controller (`CUH-ZCT1` v1 or `CUH-ZCT2` v2).
- **Audio Output**: 3.5 mm stereo headphones plugged into the controller's jack OR controller's built-in speaker.
- **Bluetooth**: Bluetooth 4.0, 4.2, or 5.x adapter.
- **Runtime**: .NET 8.0 Runtime (or self-contained standalone build).

---

## Building and Testing

### Quick Standalone Build (Zero Dependencies / Anyone Can Use)
To build a **self-contained standalone single-file executable** with embedded icon (users do NOT need .NET installed):
```powershell
.\build.ps1 -Standalone
```
Or double-click `build-standalone.bat`.

The resulting standalone executable will be located at:
`bin\standalone\DS4SoundLink.exe` (also available as `bin\standalone\ControllerAudioSwitcher.exe`).

### Framework-Dependent Build (Smaller footprint, requires .NET 8)
```powershell
.\build.ps1 -Publish
```

### Manual Build & Test via .NET CLI
```powershell
# Restore dependencies and build
dotnet build ControllerAudioSwitcher.sln -c Release

# Run automated tests (40 tests across protocol, resampling, transport, and concurrency)
dotnet test tests/ControllerAudioSwitcher.Core.Tests/ -c Release

# Publish self-contained single-file executable with embedded icon
dotnet publish src/ControllerAudioSwitcher.App/ControllerAudioSwitcher.App.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None `
    -o bin/standalone



## Licensing & Third-Party Notices

This project is licensed under the **MIT License**.
See [LICENSE](LICENSE) for details.

Third-party components:
- **NAudio**: MIT License (Mark Heath & contributors)
- **SbcSharp**: Apache License 2.0 (asimonf & Nefarius)
- See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for full notices and attributions.
