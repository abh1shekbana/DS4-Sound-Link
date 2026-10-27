namespace ControllerAudioSwitcher.Core.Protocol;

/// <summary>
/// DualShock 4 Bluetooth Audio Protocol Framing.
/// DualShock 4 receives audio packets encapsulated inside vendor-defined HID Output Reports.
/// Audio is not transmitted using standard Windows A2DP or HFP Bluetooth profiles.
/// </summary>
public static class DualShock4BluetoothAudioProtocol
{
    public const int AudioControlReportLength = 78;      // Report 0x11
    public const int AudioStreamReportLength = 462;       // Report 0x17 (4 frames = 16 ms)
    public const int AudioStreamSmallReportLength = 270;  // Report 0x14 (2 frames = 8 ms)
    public const int AudioStreamRealtimeReportLength = 142;// Report 0x12 (1 frame = 4 ms)

    public const int SpeakerSampleRate = 32000;
    public const int SpeakerSamplesPerFrame = 128;
    public const int SpeakerSbcFrameLength = 109;
    public const int FramesPerStandardReport = 4;
    public const int StandardReportCadenceMs = 16;

    public const byte AudioModeSpeaker = 0xA0;
    public const byte AudioModeDisabled = 0x00;

    public const byte AudioTargetHeadphoneJack = 0x24; // 3.5 mm jack on bottom of DS4
    public const byte AudioTargetInternalSpeaker = 0x02; // Internal mono speaker

    public const byte AudioArmHandshake = 0x85; // UNK_AUDIO1=5 | UNK_AUDIO2=0x80 (latch audio plane)
    public const byte AudioDisarmHandshake = 0x00;
    public const byte MaxHardwareVolume = 0x4F; // 79 in DS4 DAC registers

    /// <summary>
    /// Scales UI/software volume (0..127) to DS4 hardware DAC register range (0..0x4F / 0..79).
    /// </summary>
    public static byte ScaleToHardwareVolume(byte volume0To127)
    {
        if (volume0To127 == 0) return 0;
        return (byte)Math.Clamp((int)Math.Round(volume0To127 * (79.0 / 127.0)), 0, MaxHardwareVolume);
    }

    /// <summary>
    /// Builds the 78-byte Report 0x11 to arm or disarm the audio plane on the DualShock 4.
    /// </summary>
    public static byte[] BuildAudioControlReport(
        bool speakerEnabled,
        byte headphoneVolume = 110,
        byte speakerVolume = 0,
        byte bluetoothPollRate = 0)
    {
        var report = new byte[AudioControlReportLength];
        WriteAudioControlReport(report, speakerEnabled, headphoneVolume, speakerVolume, bluetoothPollRate);
        return report;
    }

    /// <summary>
    /// Writes the 78-byte Report 0x11 into a preallocated buffer.
    /// In Headphone mode, headphone volume is applied and speaker volume is strictly muted (0).
    /// In Speaker mode, speaker volume is applied and headphone volume is strictly muted (0).
    /// Byte 25 is programmed with the verified 0x85 audio handshake required for the hardware DAC to latch routing.
    /// </summary>
    public static void WriteAudioControlReport(
        Span<byte> report,
        bool speakerEnabled,
        byte headphoneVolume = 110,
        byte speakerVolume = 0,
        byte bluetoothPollRate = 0)
    {
        if (report.Length < AudioControlReportLength)
            throw new ArgumentException($"Buffer must be at least {AudioControlReportLength} bytes.", nameof(report));

        report.Clear();
        report[0] = 0x11; // Report ID
        report[1] = (byte)(0xC0 | Math.Min(bluetoothPollRate, (byte)16));
        report[2] = speakerEnabled ? AudioModeSpeaker : AudioModeDisabled;
        report[3] = (byte)(speakerEnabled ? 0xF3 : 0xF0); // Validity mask: 0xF0 updates all audio volume channels

        // Bytes 4-20 are reserved/effects
        // Volumes (0 to 127, hardware DAC responds up to 0x4F)
        report[21] = speakerEnabled ? headphoneVolume : (byte)0; // Left headphone
        report[22] = speakerEnabled ? headphoneVolume : (byte)0; // Right headphone
        report[23] = 0;                                           // Mic volume
        report[24] = speakerEnabled ? speakerVolume : (byte)0;   // Controller speaker

        // Byte 25: Verified hardware audio handshake (UNK_AUDIO1=5, UNK_AUDIO2=1 => 0x85)
        // Enables and latches the controller's internal DAC audio routing path
        report[25] = speakerEnabled ? AudioArmHandshake : AudioDisarmHandshake;

        DualShock4Crc32.AppendChecksum(DualShock4Crc32.OutputReportPrefix, report[..AudioControlReportLength]);
    }

    /// <summary>
    /// Builds the 462-byte Report 0x17 containing 4 SBC frames (16 ms of 32 kHz stereo audio).
    /// </summary>
    public static byte[] BuildStreamReport(
        ushort frameNumber,
        byte[][] frames,
        bool headsetMode = true,
        byte bluetoothPollRate = 0)
    {
        var report = new byte[AudioStreamReportLength];
        WriteStreamReport(report, frameNumber, frames, headsetMode, bluetoothPollRate);
        return report;
    }

    /// <summary>
    /// Writes the 462-byte Report 0x17 into a preallocated buffer.
    /// </summary>
    public static void WriteStreamReport(
        Span<byte> report,
        ushort frameNumber,
        byte[][] frames,
        bool headsetMode = true,
        byte bluetoothPollRate = 0)
    {
        if (frames == null || frames.Length < FramesPerStandardReport)
            throw new ArgumentException($"Must provide at least {FramesPerStandardReport} SBC frames.", nameof(frames));

        if (report.Length < AudioStreamReportLength)
            throw new ArgumentException($"Buffer must be at least {AudioStreamReportLength} bytes.", nameof(report));

        report.Clear();
        report[0] = 0x17; // Report ID for 4 audio frames
        report[1] = (byte)(0x40 | Math.Min(bluetoothPollRate, (byte)16));
        report[2] = AudioModeSpeaker; // Preserves normal gamepad input while streaming
        report[3] = (byte)(frameNumber & 0xFF);
        report[4] = (byte)((frameNumber >> 8) & 0xFF);
        report[5] = headsetMode ? AudioTargetHeadphoneJack : AudioTargetInternalSpeaker;

        int offset = 6;
        for (int i = 0; i < FramesPerStandardReport; i++)
        {
            if (frames[i] == null || frames[i].Length != SpeakerSbcFrameLength)
                throw new ArgumentException($"Each SBC frame must be exactly {SpeakerSbcFrameLength} bytes.", nameof(frames));

            frames[i].CopyTo(report.Slice(offset, SpeakerSbcFrameLength));
            offset += SpeakerSbcFrameLength;
        }

        DualShock4Crc32.AppendChecksum(DualShock4Crc32.OutputReportPrefix, report[..AudioStreamReportLength]);
    }
}
