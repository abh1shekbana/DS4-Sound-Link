# DualShock 4 Bluetooth Audio: Research and Prior Art Analysis

## Executive Summary

When a Sony DualShock 4 (CUH-ZCT1 or CUH-ZCT2) controller is connected to a PC via USB, it exposes a standard USB Audio Class (UAC) 1.0 composite device, enabling Windows to route PCM audio to the 3.5 mm headphone jack as a standard audio endpoint.

However, when connected over Bluetooth, Sony's firmware **does not expose standard Bluetooth A2DP (Advanced Audio Distribution Profile) or HFP (Hands-Free Profile)**. Instead, the controller registers strictly as a Human Interface Device (HID), and audio streaming is achieved via proprietary vendor-defined HID Output Reports (`0x11` and `0x17`) containing Subband Codec (SBC) compressed audio packets.

This document records the reverse-engineering research, protocol verification, and architectural decisions made to develop a standalone, driverless Windows 11 application capable of streaming PC playback audio directly to the DualShock 4's 3.5 mm jack over standard Bluetooth.

---

## 1. Prior Art Investigation

### A. DS4AudioStreamer (asimonf & nefarius)
- **Primary Source**: `https://github.com/asimonf/DS4AudioStreamer` / `https://github.com/nefarius/DS4AudioStreamer`
- **Architecture**: A prototype Windows console utility demonstrating audio streaming to the DualShock 4 over Bluetooth.
- **Key Findings**:
  - Identified the report structures: Report `0x11` (78 bytes) arms the audio plane; Report `0x17` (462 bytes) streams 4 SBC frames every 16 ms.
  - Provided the managed Subband Codec (SBC) encoder implementation (`SbcSharp`) configured for 32 kHz stereo, 8 subbands, 16 blocks, Joint Stereo, SNR bit allocation, and bitpool 48 (yielding exactly 109 bytes per frame).
  - Identified the CRC-32 checksum requirement: standard IEEE 802.3 CRC-32 polynomial (`0xEDB88320`), but prepended with the Bluetooth HID Output Report header byte `0xA2`.
  - **Limitation**: The original PoC set Report Byte 2 to `0xA2`, which triggered a firmware state that halted controller gamepad input reports while streaming audio. Setting this byte to `0xA0` resolves this issue and keeps normal input reports active.

### B. DS4Windows (Ryochan7 & forks)
- **Primary Source**: `https://github.com/Ryochan7/DS4Windows` / `https://github.com/hbashton/DS4Windows`
- **Architecture**: Comprehensive gamepad remapper and virtual controller emulator.
- **Key Findings**:
  - DS4Windows uses Windows SetupAPI and HID API to enumerate DualShock 4 controllers by Vendor ID (`0x054C`) and Product IDs (`0x05C4` for v1, `0x09CC` for v2).
  - Input reports over Bluetooth are Report `0x11` (v1) or `0x31` (v2), length 78 bytes.
  - Output reports are sent via `WriteFile` with `FILE_FLAG_OVERLAPPED` on the opened device handle.
  - Investigated the VIIPER audio branch: VIIPER is a virtual USB Audio Class driver/emulator designed for specific games or virtual audio cables. For general Windows desktop streaming, pure user-mode WASAPI Loopback capture eliminates any driver installation or system security degradation.

### C. Linux Kernel `hid-sony` Driver
- **Primary Source**: Linux kernel source (`drivers/hid/hid-sony.c`)
- **Key Findings**:
  - Documents the operational state transition of the DualShock 4.
  - Confirms that the controller's internal DSP switches between the internal mono speaker and the 3.5 mm stereo headphone jack depending on the audio target opcode in the stream packet (`0x02` for internal speaker, `0x24` for headphone jack).

---

## 2. Audio Pipeline & Encoding Specifications

### A. Windows Audio Capture
- **Mechanism**: Windows Audio Session API (WASAPI) in Loopback mode (`AUDCLNT_STREAMFLAGS_LOOPBACK`).
- **Characteristics**: WASAPI loopback captures the mixed output render stream from the active playback device without requiring virtual audio cable drivers or system-wide configuration modifications.
- **Source Format**: Typically 32-bit IEEE float, 48,000 Hz (or 44,100 Hz), stereo.

### B. Resampling and Format Conversion
- **Requirement**: The DualShock 4 DSP expects 32,000 Hz 16-bit signed PCM audio.
- **Implementation**: A linear resampler with fractional phase preservation across buffer boundaries. This avoids discontinuities, clicks, or phase distortion when processing consecutive audio buffers.

### C. SBC Codec Parameters
The DualShock 4 Bluetooth audio pipeline requires specific SBC parameters:
- **Sample Rate**: 32,000 Hz
- **Channels**: 2 (Stereo / Joint Stereo)
- **Subbands**: 8
- **Blocks**: 16
- **Allocation Method**: SNR (Signal-to-Noise Ratio)
- **Bitpool**: 48
- **Samples per Channel per Frame**: 128 samples (4 ms of audio at 32 kHz)
- **Encoded Frame Length**:
  $$\text{Length} = 4 + \left(\frac{4 \times 8 \times 2}{8}\right) + \left\lfloor\frac{16 \times 48}{8}\right\rfloor + 1 = 4 + 8 + 96 + 1 = 109\text{ bytes}$$

---

## 3. Protocol Framing & Timing

### A. Report 0x11: Audio Control / Arming
- **Length**: 78 bytes
- **Report ID**: `0x11`
- **Byte 1**: `0xC0` (or `0x80` depending on Bluetooth poll rate)
- **Byte 2**: `0xA0` (Audio plane enabled; speaker mode)
- **Byte 3**: `0xF3` (Validity mask enabling headphone volume and speaker audio)
- **Bytes 21-22**: Left and Right headphone volume (0–127)
- **Bytes 74-77**: CRC-32 over `0xA2` + `report[0..73]`

### B. Report 0x17: Audio Streaming
- **Length**: 462 bytes
- **Report ID**: `0x17`
- **Byte 1**: `0x40`
- **Byte 2**: `0xA0` (Preserves gamepad input reports)
- **Bytes 3-4**: 16-bit sequential frame counter (little-endian)
- **Byte 5**: Audio target opcode:
  - `0x24`: 3.5 mm stereo headphone jack
  - `0x02`: Internal controller mono speaker
- **Bytes 6-441**: 4 SBC frames ($4 \times 109 = 436$ bytes), representing 16 ms of audio
- **Bytes 458-461**: CRC-32 over `0xA2` + `report[0..457]`

### C. Cadence & Buffering
- Transmitting 4 SBC frames every 16 ms maintains exact real-time playback ($16\text{ ms} = 4 \times 4\text{ ms}$).
- A pre-buffering (priming) threshold of 16 frames (~64 ms) is essential to absorb WASAPI capture scheduling jitter and Windows thread-dispatch variance.

---

## 4. Licensing and Legal Compliance

1. **NAudio**: MIT License. Permits commercial and open-source distribution with attribution.
2. **SbcSharp**: Apache License 2.0. Requires preservation of copyright notices, license copy, and modification markers (documented in `THIRD_PARTY_NOTICES.md`).
3. **Microsoft .NET 8.0**: MIT License.
4. **Project Code**: MIT License. Zero proprietary or leaked SDK dependencies.
