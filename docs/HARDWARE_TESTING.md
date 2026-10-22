# DualShock 4 Physical Hardware Testing Procedure

## Verification Disclaimer

> [!IMPORTANT]
> The automated test suite (`ControllerAudioSwitcher.Core.Tests`) verifies all algorithms, binary protocols, CRC-32 calculations, SBC encoding, sample-rate conversions, and lifecycle state machines without physical hardware.
>
> However, **physical audio delivery through headphones connected to the controller's 3.5 mm jack over Bluetooth requires a physical controller and must be verified using the manual procedure below.**

---

## 1. Hardware Requirements

1. **DualShock 4 Controller**:
   - Model `CUH-ZCT1` (v1) or `CUH-ZCT2` (v2).
   - Ensure the battery is adequately charged (> 50%).
2. **Headphones / Earphones**:
   - Standard 3.5 mm stereo audio plug inserted firmly into the jack on the bottom of the controller.
3. **Bluetooth Adapter**:
   - Internal motherboard Bluetooth or a USB Bluetooth 4.0/4.2/5.x dongle supporting standard HID.
4. **Windows 11 PC**:
   - Audio playing through default output (YouTube, Spotify, or Windows test tones).

---

## 2. Pre-Flight Configuration

1. **Disconnect USB Cable**:
   - The controller **must not** be connected via USB. A USB connection forces Windows into USB Audio Class composite mode and disables the Bluetooth HID streaming transport.
2. **Pair Controller via Windows Bluetooth**:
   - Turn off the controller (hold PS button for 10 seconds).
   - Hold **Share + PS** until the lightbar rapidly flashes white (pairing mode).
   - On Windows 11: Go to **Settings > Bluetooth & devices > Add device > Bluetooth**.
   - Select **Wireless Controller**.
   - Verify the lightbar turns solid blue.
3. **Close Conflicting Software**:
   - Close **Steam**, **DS4Windows**, or other gamepad remappers that might open the DualShock 4 HID device with exclusive write access (`GENERIC_WRITE` without `FILE_SHARE_WRITE`).

---

## 3. Step-by-Step Test Execution

### Step 1: Launch ControllerAudioSwitcher
Run the published executable or launch from PowerShell:
```powershell
.\bin\publish\ControllerAudioSwitcher.App.exe
```

### Step 2: Verify Controller Detection
- Inspect the **Controller Hardware** card on the dashboard:
  - **Detected Controller**: Should display `Sony DualShock 4 (v1)` or `Sony DualShock 4 (v2)`.
  - **Connection Interface**: Must show `Bluetooth (Wireless)`.
  - **Audio Capability**: Must display `Compatible (3.5 mm Jack Supported)`.
  - **Status Badge**: Should display blue `Bluetooth Ready`.

### Step 3: Configure Audio Output & Volume
- In the **Streaming Controls** card:
  - Ensure the **Windows Playback Audio Source** is set to your active Windows sound card.
  - Set the **3.5 mm Headphone Jack Volume** slider to approximately 80–90% (`100/127`).

### Step 4: Initiate Audio Streaming
- Click **▶ Start Streaming**:
  - The status badge changes to green `Streaming Active`.
  - The **Stream Telemetry** panel should display incrementing **Packets Streamed** (e.g. ~62 packets/sec).
  - The **Buffer Health** progress bar should stabilize around 16–24 frames (~64–96 ms).
  - The **Estimated Latency** indicator should show `~64 - 80 ms`.

### Step 5: Verify Audible Sound
- Play an audio track or video on the PC.
- Listen through the headphones plugged into the DualShock 4 3.5 mm jack:
  - **Pass Criteria 1**: Audio is clear, audible in both left and right ears, and synchronizes with video playback.
  - **Pass Criteria 2**: Adjusting the application's volume slider audibly changes the headphone volume in real-time.
  - **Pass Criteria 3**: Moving the analog sticks and pressing buttons on the DualShock 4 continues to work without freezing or stuttering.

### Step 6: Test Disconnection & Reconnection
- While audio is streaming, turn off the controller (hold PS button for 10 seconds):
  - Application logs should note: `[WARN] Controller disconnected: DualShock 4`.
  - The status badge should cleanly transition to gray `Disconnected`.
  - Background transmission should halt without application crash or memory leak.
- Turn the controller back on (tap PS button):
  - When reconnected via Bluetooth, if **Auto-start** is checked, streaming should automatically resume within 2 seconds.

---

## 4. Test Result Documentation Matrix

| Test Step | Verification Objective | Automated Test Status | Physical Hardware Status |
|:---|:---|:---:|:---:|
| 1. CRC-32 Validation | IEEE 802.3 with `0xA2` prefix | **PASS** (Unit Tests) | Requires HW confirmation |
| 2. SBC Encoder Output | 109 bytes, 32 kHz, Joint Stereo | **PASS** (Unit Tests) | Requires HW confirmation |
| 3. Report 0x11 Framing | 78 bytes, Mode `0xA0`, volume offsets | **PASS** (Unit Tests) | Requires HW confirmation |
| 4. Report 0x17 Framing | 462 bytes, target `0x24`, 4 frames | **PASS** (Unit Tests) | Requires HW confirmation |
| 5. WASAPI Resampler | 48 kHz to 32 kHz PCM16 conversion | **PASS** (Unit Tests) | Verified with live WASAPI |
| 6. HID Overlapped Write | Asynchronous Win32 transmission | **PASS** (Integration) | Requires Physical Controller |
| 7. Headphone Audio Playout | Sound audible from 3.5 mm jack | *N/A (Hardware only)* | **Pending Physical HW Test** |
