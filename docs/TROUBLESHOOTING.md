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
