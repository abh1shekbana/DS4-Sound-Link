# DS4SoundLink - Universal PlayStation Controller Wireless Audio Streamer

A high-performance, standalone Windows 10/11 application that captures PC playback audio via WASAPI Loopback and streams it wirelessly with ultra-low latency to Sony DualShock 4 and DualSense controllers connected over standard Bluetooth.

---

## Overview

When connected via USB, PlayStation controllers expose standard USB Audio Class (UAC) endpoints to Windows. However, when connected over Bluetooth, Sony's firmware **does not register as a standard Windows A2DP or HFP audio device**. Instead, audio must be encapsulated inside vendor-defined HID Output Reports and encoded using specialized audio codecs (Subband Codec SBC for DualShock 4, vendor frame framing for DualSense).

`DS4SoundLink` solves this limitation by providing:
- **Pure User-Mode Architecture**: No kernel drivers, no virtual audio cables, and no system security changes required.
- **Universal PlayStation Controller Support**: Automatic discovery and protocol negotiation for Sony DualShock 4 (v1, v2, variants, official USB Wireless Adapter), Sony DualSense (PS5), Sony DualSense Edge, and compatible third-party gamepads.
- **Hardware-Isolated Exclusive Output Routing**: Dedicated mutually exclusive routing between the **AUX / Headphone Jack** (3.5 mm stereo) and the **Internal Speaker** (mono downmix). Completely eliminates audio leakage and bleed into the controller speaker when headphones are selected.
- **Hardware 3.5 mm Jack Detection**: Real-time detection of headphone insertion and removal (on DualSense status telemetry), preventing unintended public playback through the speaker when headphones are disconnected.
- **Simultaneous Gameplay**: Uses the `0xA0` mode byte on DualShock 4 and non-intrusive HID reports on DualSense to preserve full gamepad input reports, allowing uninterrupted gaming while listening to wireless audio.
- **Hardware Battery Level Indicator**: Live battery percentage (0–100%) and charging status widget on the dashboard, extracted directly from Sony HID input reports (Bluetooth 0x11/0x31 and USB 0x01).
- **Crack-Free True-Peak SBC Headroom & Dynamic AGC**: Automatic gain and dynamic headroom management enforcing a -1.41 dBFS ceiling (0.85f max) with smooth hyperbolic tangent limiting. Completely eliminates high-frequency cracking/overshoot in the controller's internal SBC decoder and DAC while maximizing clear, punchy loudness automatically.
- **Continuous Phase Resampling**: Fractional carry interpolation across WASAPI buffer chunks, completely eliminating cyclic boundary phase discontinuities and high-frequency harmonic hiss.
- **Asynchronous, Non-Blocking Pipeline Lifecycle**: Reliable cancellation coordination that prevents UI deadlocks when stopping, restarting, or switching audio routes live while streaming.
- **Extended Diagnostics & Telemetry**: Live tracking of buffer underruns, dropped audio frames, encoder errors, active output routes, and Bluetooth transmission status.

---

## Supported Controller Matrix & Capabilities

| Controller Model | VID / PID | Bluetooth Audio Streaming | USB Windows Audio | Jack Detection | Exclusive Speaker/AUX Isolation |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Sony DualShock 4 v1** (`CUH-ZCT1`) | `0x054C` / `0x05C4` | **Supported** (SBC over HID 0x17) | Native UAC 1.0 (Windows Endpoint) | Not reported in HID | **Supported** (Report 0x11 Byte 25 arming + register zeroing) |
| **Sony DualShock 4 v2** (`CUH-ZCT2`) | `0x054C` / `0x09CC` | **Supported** (SBC over HID 0x17) | Native UAC 1.0 (Windows Endpoint) | Not reported in HID | **Supported** (Report 0x11 Byte 25 arming + register zeroing) |
| **Sony DualShock 4 Variant** | `0x054C` / `0x05C5` | **Supported** (SBC over HID 0x17) | Native UAC 1.0 (Windows Endpoint) | Not reported in HID | **Supported** (Report 0x11 Byte 25 arming + register zeroing) |
| **Sony DualShock 4 USB Wireless Adapter** | `0x054C` / `0x0BA0` | Native UAC Audio via Dongle | Native UAC 1.0 (Windows Endpoint) | Hardware Dongle | Handled by Dongle Hardware |
| **Sony DualSense (PS5)** | `0x054C` / `0x0CE6` | **Supported** (Report 0x31 routing & audio protocol) | Native UAC 2.0 (Windows Endpoint) | **Supported** (Input report byte 56 bit 0) | **Supported** (Hardware path 0x00 vs 0x30 + volume isolation) |
| **Sony DualSense Edge** | `0x054C` / `0x0DF2` | **Supported** (Report 0x31 routing & audio protocol) | Native UAC 2.0 (Windows Endpoint) | **Supported** (Input report byte 56 bit 0) | **Supported** (Hardware path 0x00 vs 0x30 + volume isolation) |
| **Compatible Third-Party Clones** | `0x0C12`, `0x0F0D`, `0x146B` | **Supported** (Standard DS4 HID Report 0x11/0x17) | Model dependent | Not reported in HID | **Supported** |

*Note on USB Connections*: When connected via USB, Sony PlayStation controllers expose a native Windows Audio playback endpoint. DS4SoundLink detects USB connections and informs the user to select the native Windows audio device directly, reserving custom HID streaming for wireless Bluetooth connections where native Windows audio endpoints do not exist.

---

## Exclusive AUX / Headphone Routing & Leakage Fix

### The Problem
In earlier versions, when the user selected **AUX / Headphones**, audio played through the headphones but faint audio continued to bleed or leak through the controller's internal speaker.

### Root Causes & Technical Solutions

1. **DualShock 4 Hardware Arming Handshake (`Byte 25 = 0x85`)**:
   - In Sony's DualShock 4 Bluetooth Report `0x11` specification, Byte 25 consists of two packed bitfields: `UNK_AUDIO1 : 7` (set to 5 for audio) and `UNK_AUDIO2 : 1` (set to 1 for audio), forming `0x85`.
   - When Byte 25 was previously sent as `0x00`, the controller's internal DSP routing plane did not reliably latch the output routing commands, leaving the physical speaker amplifier partially unlatched and leaking audio.
   - **Fix**: Setting Byte 25 to `0x85` upon arming forces the DSP routing switch to latch immediately.

2. **DAC Hardware Volume Register Clamping and Zeroing**:
   - The DualShock 4 hardware volume registers (`VolumeLeft`, `VolumeRight`, `VolumeSpeaker` at bytes 21, 22, 24) accept a maximum value of `0x4F` (79). Values exceeding `0x4F` overflow into adjacent DSP register bitfields.
   - **Fix**: Volume levels are proportionally scaled to `0x00..0x4F`. When in **Headset Mode**, `VolumeSpeaker` is strictly forced to `0x00`. When in **Speaker Mode**, `VolumeLeft` and `VolumeRight` are strictly forced to `0x00`.

3. **In-Flight PCM Buffer and Queue Drainage**:
   - During live output destination switching while streaming, lingering PCM frames remaining in the resampler (`_pendingLeft`, `_pendingRight`) and the pre-encoded queue (`_encodedFrameQueue`) contained frames intended for the previous destination (e.g. mono downmixed speaker audio).
   - **Fix**: Implemented a serialized transition lock (`_routeTransitionLock`) that pauses frame emission, drains all residual buffers, dispatches the armed hardware control report, verifies the confirmed hardware state, and seamlessly resumes streaming.

4. **DualSense Bluetooth Report `0x31` Output Protocol**:
   - The DualSense uses a 78-byte Bluetooth report with CRC-32 seed `0xA2`.
   - Output paths are strictly isolated via Byte 7: `0x00` routes to the 3.5 mm headphone jack, while `0x30` routes to the internal speaker.
   - Dedicated enable flags (`flag0 = 0x10 | 0x20 | 0x80`, `flag1 = 0x80`) and preamp control (Byte 37 = `0x02`) ensure only the active output path is powered.

---

## Building and Testing

### Quick Standalone Build (Zero Dependencies)
To build a **self-contained standalone single-file executable** with embedded icon:
```powershell
.\build.ps1 -Standalone
```
The resulting standalone executable will be located at:
`bin\standalone\DS4SoundLink.exe` (also available as `bin\standalone\ControllerAudioSwitcher.exe`).

### Framework-Dependent Build (Requires .NET 8)
```powershell
.\build.ps1 -Publish
```
The published binaries will be located at: `bin\publish\ControllerAudioSwitcher.App.exe`.

### Automated Unit Test Suite
Run the comprehensive 58-test automated test suite:
```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/ControllerAudioSwitcher.Core.Tests/ -c Release
```
Test coverage spans:
- Controller capability detection across models (DS4 v1, v2, Dongle, DualSense, DualSense Edge, Clones).
- DualShock 4 Bluetooth Report 0x11 and 0x17 construction, byte 25 handshake, and volume scaling.
- DualSense Bluetooth Report 0x31 headset/speaker routing, audio path switching, and 3.5 mm jack detection.
- Audio streaming controller lifecycle, live destination switching, capability rejection, and buffer drainage.
- Phase-continuous resampling, true-peak soft limiter, and SBC frame encoding.

---

## How to Use

1. **Pair Controller via Bluetooth**:
   - DualShock 4: Hold **Share + PS** until the lightbar flashes white.
   - DualSense: Hold **Create + PS** until the lightbar pulses blue.
   - Go to Windows **Settings > Bluetooth & devices > Add device > Bluetooth** and select **Wireless Controller**.
2. **Launch Application**:
   - Run `DS4SoundLink.exe` from `bin/standalone/` (or `bin/publish/`).
   - The dashboard will identify the specific controller model (e.g., `Sony DualSense (PS5)` or `Sony DualShock 4`) and display battery and connection status.
3. **Choose Output Destination**:
   - Select **AUX / Headphone Jack** or **Internal Speaker** on the dashboard.
   - If headphones are plugged in, DualSense controllers will display `Headphones connected`.
4. **Start Streaming**:
   - Click **▶ Start Streaming**.
   - PC playback audio will route exclusively to your selected controller output.
   - You can toggle between headphones and internal speaker live while streaming with zero leakage and zero audio dropouts.
   - Click **⏹ Stop Streaming** at any time; the stream halts cleanly and can be restarted immediately.

---

## Licensing & Third-Party Notices

This project is licensed under the **MIT License**.
See [LICENSE](LICENSE) for details.

Third-party components and references:
- **NAudio**: MIT License (Mark Heath & contributors)
- **SbcSharp**: Apache License 2.0 (asimonf & Nefarius)
- **DSZ**: MIT License (MaximumFreak & contributors)
- **Linux hid-playstation**: GPL-2.0 / Dual MIT (Sony Interactive Entertainment Inc.)
- See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for full notices and attributions.

