using System.Buffers.Binary;

namespace ControllerAudioSwitcher.Core.Protocol;

/// <summary>
/// Sony DualSense (PS5) and DualSense Edge HID protocol implementation.
/// Handles output report generation for audio path routing (Report 0x31 over Bluetooth,
/// Report 0x02 over USB) and input report telemetry parsing (battery level, charging,
/// and physical 3.5 mm headphone jack detection).
/// </summary>
public static class DualSenseBluetoothAudioProtocol
{
    public const int BluetoothOutputReportLength = 78; // Report 0x31
    public const int UsbOutputReportLength = 48;       // Report 0x02

    public const byte ReportIdBluetoothOutput = 0x31;
    public const byte ReportIdUsbOutput = 0x02;

    public const byte SpeakerVolumeMin = 0x3D; // 61
    public const byte SpeakerVolumeMax = 0x64; // 100
    public const byte HeadphoneVolumeMax = 0x7F; // 127

    public const byte AudioPathHeadset = 0x00;
    public const byte AudioPathSpeaker = 0x30; // Path 3 (bits 5:4)

    /// <summary>
    /// Builds the 78-byte Bluetooth Report 0x31 configuring exclusive speaker or headphone audio.
    /// </summary>
    public static byte[] BuildBluetoothControlReport(
        bool headsetMode,
        byte volume = 100,
        bool isMuted = false)
    {
        var buffer = new byte[BluetoothOutputReportLength];
        WriteBluetoothControlReport(buffer, headsetMode, volume, isMuted);
        return buffer;
    }

    /// <summary>
    /// Writes the 78-byte Bluetooth Report 0x31 into a destination span.
    /// </summary>
    public static void WriteBluetoothControlReport(
        Span<byte> report,
        bool headsetMode,
        byte volume = 100,
        bool isMuted = false)
    {
        if (report.Length < BluetoothOutputReportLength)
            throw new ArgumentException($"Buffer must be at least {BluetoothOutputReportLength} bytes.", nameof(report));

        report.Clear();
        report[0] = ReportIdBluetoothOutput;
        report[1] = 0x02; // Tag byte

        // Common body starts at offset 2 (47 bytes, ending at byte 48)
        byte flag0 = 0x04 | 0x08; // Triggers valid
        byte flag1 = 0x01 | 0x04 | 0x10 | 0x40; // LEDs and motor power valid

        if (isMuted)
        {
            flag0 |= 0x80; // Audio path valid
            report[9] = AudioPathHeadset;
        }
        else if (headsetMode)
        {
            // Headset active: flag0 bit 4 (hp vol), bit 5 (spk vol), bit 7 (audio path)
            flag0 |= 0x10 | 0x20 | 0x80;
            report[6] = Math.Min(volume, HeadphoneVolumeMax); // Headphone volume
            report[7] = 0;                                     // Speaker volume strictly MUTED (0)
            report[9] = AudioPathHeadset;                      // Path 0 (Headphones)
        }
        else
        {
            // Internal speaker active: flag0 bit 5 (spk vol), bit 7 (audio path); flag1 bit 7 (audio control 2)
            flag0 |= 0x20 | 0x80;
            flag1 |= 0x80;
            report[6] = 0; // Headphone volume strictly MUTED (0)
            report[7] = Math.Clamp(volume, SpeakerVolumeMin, SpeakerVolumeMax); // Clamped speaker volume
            report[9] = AudioPathSpeaker;                                       // Path 3 (Speaker)
            report[39] = 0x02; // Speaker preamp gain +6 dB
        }

        report[2] = flag0;
        report[3] = flag1;
        report[40] = 0x03; // Lightbar setup flag
        report[43] = 0x02; // Take lightbar color
        report[46] = 0x00; // Blue
        report[47] = 0x80;
        report[48] = 0xFF;

        DualShock4Crc32.AppendChecksum(DualShock4Crc32.OutputReportPrefix, report[..BluetoothOutputReportLength]);
    }

    /// <summary>
    /// Parses DualSense input report status, extracting battery level, charging status, and 3.5 mm jack detection.
    /// Supports Bluetooth Report 0x31 and USB Report 0x01.
    /// </summary>
    public static bool TryParseInputReport(
        ReadOnlySpan<byte> report,
        out int batteryPercent,
        out bool isCharging,
        out bool headphoneJackConnected)
    {
        batteryPercent = -1;
        isCharging = false;
        headphoneJackConnected = false;

        if (report.IsEmpty)
            return false;

        byte reportId = report[0];
        int offset;

        if (reportId == 0x31 && report.Length >= 57)
        {
            // Bluetooth input report: 2-byte header (ID + seq)
            offset = 2;
        }
        else if (reportId == 0x01 && report.Length >= 56)
        {
            // USB input report: 1-byte header (ID)
            offset = 1;
        }
        else
        {
            return false;
        }

        int batteryIndex = 53 + offset;
        int status1Index = 54 + offset;

        if (report.Length <= status1Index)
            return false;

        byte bat = report[batteryIndex];
        int rawCap = bat & 0x0F;
        batteryPercent = Math.Min(rawCap * 10, 100);

        int status = (bat >> 4) & 0x0F;
        isCharging = status == 0x01 || status == 0x02;
        if (status == 0x02)
        {
            batteryPercent = 100;
        }

        // status[1] bit 0: DS_STATUS1_HP_DETECT
        byte status1 = report[status1Index];
        headphoneJackConnected = (status1 & 0x01) != 0;

        return true;
    }
}
