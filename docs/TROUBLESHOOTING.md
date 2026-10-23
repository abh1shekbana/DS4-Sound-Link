# DualShock 4 Bluetooth Audio Troubleshooting Guide

This guide details resolutions for common issues encountered when streaming PC audio to a DualShock 4 controller over Bluetooth.

---

## 1. Controller Shows "USB (Wired)" Instead of "Bluetooth"

### Cause:
The controller is plugged in with a USB cable. Over USB, the DualShock 4 communicates through standard USB HID and USB Audio Class (UAC) 1.0, rather than the proprietary Bluetooth HID 0x17 transport.

### Solution:
1. Unplug the USB cable from the controller and PC.
2. Put the controller in pairing mode by holding **Share + PS** until the lightbar rapidly blinks white.
3. In Windows 11 **Settings > Bluetooth & devices**, select **Add device > Bluetooth** and click **Wireless Controller**.
4. The application will detect the controller interface change and update to `Bluetooth (Wireless)`.

---

## 2. "Failed to open HID communication handle" (Access Denied / Error 5)

### Cause:
Another application on your system has opened the DualShock 4's HID device handle with exclusive access (`dwShareMode = 0` or missing `FILE_SHARE_WRITE`). Common culprits include:
- **Steam** (with PlayStation Controller Support enabled)
- **DS4Windows** or **reWASD**
- Certain games that hook the controller exclusively using Raw Input or DirectInput.

### Solution:
1. Exit Steam completely (check the Windows system tray) or uncheck PlayStation Controller Support in Steam Settings.
2. Close DS4Windows, reWASD, or any other virtual controller software.
3. In `ControllerAudioSwitcher`, the application opens devices using `FILE_SHARE_READ | FILE_SHARE_WRITE`, allowing cooperative sharing with non-exclusive applications.
4. If issues persist, power cycle the controller (hold PS button 10 seconds, then reconnect).

---

## 3. Audio Stutters, Pops, or Has Frequent Dropouts

### Cause:
Bluetooth packet loss or transmission scheduling delays. DualShock 4 audio transmission requires ~29 KB/sec of continuous Bluetooth bandwidth, which can saturate low-quality or interference-prone Bluetooth adapters.

### Solutions:
1. **Reduce Bluetooth Interference**:
   - If using a USB Bluetooth dongle, plug it into a **USB 2.0 port** rather than a USB 3.0 port (USB 3.0 ports emit RF noise in the 2.4 GHz band that degrades Bluetooth signal).
   - Use an extension cable to bring the Bluetooth dongle closer to your playing position.
2. **Close Heavy Bluetooth Peripherals**:
   - Disconnect other high-bandwidth Bluetooth devices (e.g., Bluetooth keyboards, mice, or file transfers) sharing the same adapter.
3. **Check Buffer Underruns**:
   - In the application dashboard, observe the **Buffer Underruns** counter. If it is steadily increasing, your PC is taking longer than 16 ms to capture or transmit audio. Ensure PC power plan is set to "Balanced" or "High Performance".

---

## 4. Gamepad Buttons or Sticks Stop Working While Streaming Audio

### Cause:
Older experimental scripts set byte 2 of Report 0x17 to `0xA2`, which triggered a firmware lockup on input reports.

### Solution:
- `ControllerAudioSwitcher` has been specifically implemented with the `0xA0` input-preservation flag. If you encounter input sluggishness, verify your controller is running standard Sony firmware and is not paired to an aggressive virtual controller driver.

---

## 5. Streaming Status Shows Active, But No Sound in Headphones

### Checklist:
1. **Physical Connection**:
   - Ensure the 3.5 mm headphone plug is pressed completely into the bottom jack until it clicks firmly.
2. **Volume Slider**:
   - Verify that the **Headphone Jack Volume** slider in `ControllerAudioSwitcher` is not set to 0. Recommended setting: 80–90% (`100/127`).
3. **Windows Audio Source**:
   - In the application, verify the **Windows Playback Audio Source** matches the device currently making sound in Windows.
   - Click the **↻** button to refresh audio devices.
4. **Muted Application**:
   - Ensure the media player or browser tab is not muted in the Windows Volume Mixer.

---

## 6. Diagnostic Capture Test Mode

To verify that PC audio capture is working independently of the Bluetooth controller:
1. Check the **Diagnostic Capture Test Mode** box in the dashboard.
2. Play audio on your PC.
3. The diagnostic log will report active loopback capture. If an error is reported (e.g. `AUDCLNT_E_DEVICE_INVALIDATED`), select another audio endpoint from the dropdown.

---

## 7. Audible Hiss, Static Noise, or Background Buzzing

### Cause Analysis:
1. **Software Resampling Glitches (Fixed in v2.1)**:
   - *Cause*: In previous releases, when converting 48 kHz WASAPI frames to 32 kHz DualShock SBC frames, sub-sample phase accumulation was reset at each buffer boundary. This produced a microscopic discontinuity every 10 ms, manifesting as an audible 100 Hz harmonic buzz/hiss.
   - *Fix*: DS4SoundLink v2.1 features a stateful fractional carry interpolation engine that guarantees phase continuity across arbitrary buffer sizes.
2. **Controller Analog Hardware Noise Floor (Physical Hardware Limit)**:
   - *Cause*: The DualShock 4 controller's integrated DAC and headphone amplifier have a relatively high baseline noise floor (SNR ~75 dB). If you use ultra-sensitive, low-impedance in-ear monitors (IEMs), you will naturally hear faint background analog thermal hiss regardless of whether audio is streaming.
   - *Mitigation*: Use standard 32-ohm headphones or higher impedance headsets. Software cannot eliminate the physical thermal noise floor of the controller's analog circuitry.
3. **Bluetooth Packet Jitter & Dropped Frames**:
   - Check the **Telemetry** section in DS4SoundLink. If **Buffer Underruns** or **Dropped Frames** are incrementing, intermittent audio packet starvation is causing audible clicks and micro-dropouts that sound like static. Follow the Bluetooth interference steps in Section 3.

---

## 8. Low Volume Output & Preventing Cracking on Highs

### Causes:
1. **Low Volume on High-Impedance Headsets**: The DualShock 4 headphone amplifier operates from a low-voltage battery rail and lacks high-power output stages. High-impedance headphones (e.g., 80 ohm to 250 ohm) sound quieter than when plugged into a powered PC sound card.
2. **Audio Cracking on Max Volume / High Peaks**: When the volume is maxed out and high-frequency transients (cymbals, snares, explosions) reach 0 dBFS (32767 PCM), the controller's internal hardware SBC synthesis filter overshoots beyond 32767 due to filter ringing and Gibbs phenomenon. Because the controller DAC uses 16-bit signed arithmetic, this overshoot wraps around or hard-clips, producing sharp acoustic cracking.

### Solutions (Automated in v2.1):
1. **Automatic Loudness AGC**: DS4SoundLink v2.1 automatically applies a clean +2 dB boost to quiet and moderate passages so dialogue and game audio are punchy and loud without requiring manual software gain adjustment.
2. **Safe True-Peak Ceiling (-1.41 dBFS)**: Peaks are strictly constrained to `0.85f` (27850 PCM), ensuring the controller's internal SBC decoder reconstruction filter never exceeds 32767 even during sharp high-frequency attacks.
3. **Smooth Hyperbolic Tangent Limiting**: Transients above `0.60f` (-4.4 dBFS) are compressed smoothly with zero sudden corners, eliminating harsh digital clipping clicks.
4. **Adjust Controller Hardware Volume**: Set the **Volume** slider in DS4SoundLink to 85–95% (raw register value 108–120) for optimal analog amplifier linearity.

---

## 9. Output Routing: Internal Speaker vs. AUX / Headphone Jack

### Supported Behavior:
DS4SoundLink v2.1 provides exclusive output routing:
- **AUX / Headphone Jack**: Directs 32 kHz stereo audio exclusively to headphones plugged into the 3.5 mm jack, while commanding the controller hardware to mute the internal speaker register.
- **Internal Speaker**: Directs 32 kHz audio exclusively to the controller's internal speaker, automatically folding down left and right channels to mono (`(L + R) * 0.5`) to prevent acoustic phase cancellation, while muting the headphone DAC.

### Live Switching:
- You can switch between Internal Speaker and AUX Headphone Jack live while audio is streaming.
- The stream does not restart, preserving Bluetooth connection stability and SBC encoder sync.

---

## 10. Application Responsiveness During Stop Streaming

In DS4SoundLink v2.1:
- Clicking **⏹ Stop Streaming** immediately transitions the application to the `Stopping` state.
- Worker threads and audio buffers are terminated asynchronously without blocking the WPF UI thread, preventing deadlocks or application freezes.
- Once stopped, you can immediately restart streaming without restarting the application or unplugging the controller.

---

## 11. Battery Level Indicator & Telemetry

DS4SoundLink v2.1 queries hardware battery telemetry directly from the DualShock 4:
- **Bluetooth Connection**: Parsed from Report `0x11` status bytes (offset 32 and 33).
- **USB Connection**: Parsed from Report `0x01` status bytes (offset 30 and 31).
- **Indicator States**:
  - **Green (50–100%)**: Good battery charge.
  - **Yellow (21–50%)**: Moderate battery charge.
  - **Red (0–20%)**: Low / critical battery charge; plug in USB cable to charge.
  - **Cyan + ⚡ Icon**: Controller is plugged into a power source and actively charging.
  - **`--%`**: Controller connected, reading telemetry payload.
