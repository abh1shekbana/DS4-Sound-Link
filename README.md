# DualShock 4 Bluetooth Audio Streamer for Windows 11

A standalone, driverless Windows 10/11 application that captures PC playback audio via WASAPI Loopback and streams it wirelessly to the 3.5 mm headphone jack of a Sony DualShock 4 controller connected over standard Bluetooth.

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

### Manual Build via .NET CLI
```powershell
# Restore dependencies and build
dotnet build ControllerAudioSwitcher.sln -c Release

# Run automated tests
dotnet test tests/ControllerAudioSwitcher.Core.Tests/ -c Release

# Publish self-contained single-file executable with embedded icon
dotnet publish src/ControllerAudioSwitcher.App/ControllerAudioSwitcher.App.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None `
    -o bin/standalone
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



## Licensing & Third-Party Notices

This project is licensed under the **MIT License**.
See [LICENSE](LICENSE) for details.

Third-party components:
- **NAudio**: MIT License (Mark Heath & contributors)
- **SbcSharp**: Apache License 2.0 (asimonf & Nefarius)
- See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for full notices and attributions.
