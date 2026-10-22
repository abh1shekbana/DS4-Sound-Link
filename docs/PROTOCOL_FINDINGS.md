# DualShock 4 Bluetooth Audio Protocol Specification

This document provides byte-level documentation of the proprietary Bluetooth HID reports used to stream audio to the Sony DualShock 4 controller.

---

## 1. Bluetooth HID Report Architecture

When connected via Bluetooth, the DualShock 4 exchanges all game, sensor, force feedback, and audio data using HID Reports. Audio is never transmitted via A2DP; instead, it is packed inside vendor-defined output reports.

All Bluetooth output reports sent to the DualShock 4 must terminate with a 4-byte CRC-32 checksum. The checksum is computed over the single prefix byte `0xA2` (the Bluetooth HID Output Report transaction type) followed by the entire payload prior to the checksum field.

$$\text{CRC} = \text{CRC32}\left(0\text{xA2} \mathbin{\Vert} \text{Report}[0 \dots N-5]\right)$$

---

## 2. Audio Control & Arming Report: Report `0x11` (78 Bytes)

Report `0x11` configures controller operating modes, arms or disarms the audio plane, adjusts headphone/speaker volume, and controls LED/rumble settings.

### Binary Layout

| Byte Offset | Field Name | Expected Value | Description |
|:---:|:---|:---:|:---|
| **0** | Report ID | `0x11` | Identifier for the extended configuration report |
| **1** | Polling Configuration | `0xC0` | Polling rate mask and Bluetooth timing flags |
| **2** | Audio Mode Flag | `0xA0` (or `0x00`) | **`0xA0`**: Enable audio plane in speaker mode.<br>**`0x00`**: Disarm audio plane. |
| **3** | Validity Mask | `0xF3` | Bitmask enabling individual hardware subsystems:<br>• `0x01`: Headphone volume valid<br>• `0x02`: Controller speaker valid<br>• `0xF0`: Extended feature valid bits |
| **4–20** | Rumble & Lightbar | `0x00` | Motor speed, LED red/green/blue, flash frequencies |
| **21** | Left Headphone Volume | `0x00`–`0x7F` | Left channel volume (0 = mute, 127 = maximum) |
| **22** | Right Headphone Volume| `0x00`–`0x7F` | Right channel volume (0 = mute, 127 = maximum) |
| **23** | Microphone Volume | `0x00` | Reserved / Mic input volume (0 = default) |
| **24** | Speaker Volume | `0x00` | Internal mono speaker volume (set to 0 when streaming to headphones) |
| **25–73** | Reserved / Padding | `0x00` | Zero-padded payload bytes |
| **74–77** | Bluetooth CRC-32 | uint32 (LE) | Little-endian CRC-32 of `0xA2` + `Report[0..73]` |

---

## 3. Audio Stream Report: Report `0x17` (462 Bytes)

Report `0x17` transmits 4 consecutive SBC frames (16 ms of 32 kHz stereo audio) directly to the controller's internal audio DSP.

### Binary Layout

| Byte Offset | Field Name | Expected Value | Description |
|:---:|:---|:---:|:---|
| **0** | Report ID | `0x17` | Identifier for 4-frame audio stream report |
| **1** | Stream Header | `0x40` | Streaming control flags |
| **2** | Input Mode Preserver | `0xA0` | **Crucial**: Maintains active gamepad input state. Using `0xA2` starves input reports. |
| **3–4** | Frame Counter | uint16 (LE) | Sequential frame counter incremented by +4 each report |
| **5** | Audio Target Opcode | `0x24` | **`0x24`**: Routes stereo audio to the 3.5 mm headphone jack.<br>**`0x02`**: Routes audio to internal mono speaker. |
| **6–114** | SBC Frame 0 | 109 bytes | SBC compressed frame (128 samples / 4 ms) |
| **115–223**| SBC Frame 1 | 109 bytes | SBC compressed frame (128 samples / 4 ms) |
| **224–332**| SBC Frame 2 | 109 bytes | SBC compressed frame (128 samples / 4 ms) |
| **333–441**| SBC Frame 3 | 109 bytes | SBC compressed frame (128 samples / 4 ms) |
| **442–457**| Audio Sync / Padding| `0x00` | 16 bytes reserved synchronization padding |
| **458–461**| Bluetooth CRC-32 | uint32 (LE) | Little-endian CRC-32 of `0xA2` + `Report[0..457]` |

---

## 4. SBC Frame Structure (109 Bytes)

Each SBC frame adheres to the Bluetooth A2DP Subband Codec specification configured with:
- **Sampling Frequency**: 32 kHz (`0x02` -> `0x80` in byte 1)
- **Blocks**: 16 (`0x03` -> `0x30` in byte 1)
- **Channel Mode**: Joint Stereo (`0x01` -> `0x04` in byte 1)
- **Allocation**: SNR (`0x01` -> `0x02` in byte 2)
- **Subbands**: 8 (`0x01` -> `0x01` in byte 2)
- **Bitpool**: 48 (`0x30` in byte 3)

### Byte Breakdown:
- **Byte 0**: Syncword `0x9C`
- **Byte 1**: `0x7F` (Configuration byte: 32 kHz, 16 blocks, Joint Stereo)
- **Byte 2**: `0x30` (Bitpool 48)
- **Byte 3**: SBC Header CRC-8
- **Bytes 4–7**: Joint stereo band flags and scale factors
- **Bytes 8–108**: Subband quantized audio bits

---

## 5. Critical Firmware Quirks Discovered

1. **The Input Starvation Bug (`0xA0` vs `0xA2`)**:
   In older experimental scripts, report byte 2 was often set to `0xA2`. On many CUH-ZCT2 revisions, this causes the controller to stop transmitting regular gamepad input reports (`0x11`/`0x31`) for the duration of the stream. Setting byte 2 to `0xA0` keeps gamepad button and stick telemetry flowing concurrently.

2. **Endpoint Opcode `0x24` vs `0x02`**:
   Setting byte 5 to `0x02` sends audio to the internal mono speaker. Setting byte 5 to `0x24` directs high-quality stereo audio to the 3.5 mm jack on the bottom of the controller.

3. **Buffer Underflow Silence Injection**:
   If PC audio playback pauses, the transmitter must not halt packet transmission abruptly. Halting packets causes the controller DSP to pop and power down the headphone amplifier. Transmitting SBC silence frames keeps the audio amplifier warm and silent without pops.
