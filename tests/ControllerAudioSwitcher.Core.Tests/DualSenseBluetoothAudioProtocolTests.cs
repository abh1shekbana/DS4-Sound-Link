using System.Buffers.Binary;
using ControllerAudioSwitcher.Core.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ControllerAudioSwitcher.Core.Tests;

[TestClass]
public class DualSenseBluetoothAudioProtocolTests
{
    [TestMethod]
    public void BuildBluetoothControlReport_HeadsetMode_MutesSpeakerAndConfiguresHeadphones()
    {
        byte volume = 110;
        byte[] report = DualSenseBluetoothAudioProtocol.BuildBluetoothControlReport(headsetMode: true, volume: volume);

        Assert.AreEqual(DualSenseBluetoothAudioProtocol.BluetoothOutputReportLength, report.Length);
        Assert.AreEqual(DualSenseBluetoothAudioProtocol.ReportIdBluetoothOutput, report[0]); // 0x31
        Assert.AreEqual(0x02, report[1]); // Tag

        // Flag0 must have bit 4 (hp vol valid), bit 5 (spk vol valid), bit 7 (audio control valid)
        byte flag0 = report[2];
        Assert.AreNotEqual(0, flag0 & 0x10, "Headphone volume valid flag must be set.");
        Assert.AreNotEqual(0, flag0 & 0x20, "Speaker volume valid flag must be set.");
        Assert.AreNotEqual(0, flag0 & 0x80, "Audio path valid flag must be set.");

        // Headphone volume set, internal speaker strictly MUTED (0), audio path 0
        Assert.AreEqual(volume, report[6], "Headphone volume must match.");
        Assert.AreEqual(0, report[7], "Speaker volume must be strictly muted (0) in headset mode.");
        Assert.AreEqual(DualSenseBluetoothAudioProtocol.AudioPathHeadset, report[9], "Audio path must be 0 (Headphones).");

        // Verify CRC-32 over prefix 0xA2 + report[0..73]
        uint expectedCrc = DualShock4Crc32.ComputeBluetoothCrc32(report, 0, 74);
        uint reportCrc = BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(74, 4));
        Assert.AreEqual(expectedCrc, reportCrc);
    }

    [TestMethod]
    public void BuildBluetoothControlReport_SpeakerMode_MutesHeadphonesAndClampsSpeakerVolume()
    {
        byte volume = 120; // Above SpeakerVolumeMax (100 / 0x64)
        byte[] report = DualSenseBluetoothAudioProtocol.BuildBluetoothControlReport(headsetMode: false, volume: volume);

        Assert.AreEqual(DualSenseBluetoothAudioProtocol.BluetoothOutputReportLength, report.Length);
        Assert.AreEqual(DualSenseBluetoothAudioProtocol.ReportIdBluetoothOutput, report[0]);

        byte flag0 = report[2];
        byte flag1 = report[3];
        Assert.AreNotEqual(0, flag0 & 0x20, "Speaker volume valid flag must be set.");
        Assert.AreNotEqual(0, flag0 & 0x80, "Audio path valid flag must be set.");
        Assert.AreNotEqual(0, flag1 & 0x80, "Audio control 2 valid flag must be set.");

        // Headphone volume strictly MUTED (0), speaker volume clamped to SpeakerVolumeMax (0x64), path 3 (0x30)
        Assert.AreEqual(0, report[6], "Headphone volume must be strictly muted (0) in speaker mode.");
        Assert.AreEqual(DualSenseBluetoothAudioProtocol.SpeakerVolumeMax, report[7], "Speaker volume must be clamped to max (0x64).");
        Assert.AreEqual(DualSenseBluetoothAudioProtocol.AudioPathSpeaker, report[9], "Audio path must be Path 3 (0x30).");
        Assert.AreEqual(0x02, report[39], "Speaker preamp gain must be set to +6 dB.");

        uint expectedCrc = DualShock4Crc32.ComputeBluetoothCrc32(report, 0, 74);
        uint reportCrc = BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(74, 4));
        Assert.AreEqual(expectedCrc, reportCrc);
    }

    [TestMethod]
    public void BuildBluetoothControlReport_Muted_MutesBothOutputs()
    {
        byte[] report = DualSenseBluetoothAudioProtocol.BuildBluetoothControlReport(headsetMode: false, volume: 80, isMuted: true);

        Assert.AreEqual(DualSenseBluetoothAudioProtocol.AudioPathHeadset, report[9]);
        uint expectedCrc = DualShock4Crc32.ComputeBluetoothCrc32(report, 0, 74);
        uint reportCrc = BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(74, 4));
        Assert.AreEqual(expectedCrc, reportCrc);
    }

    [TestMethod]
    public void TryParseInputReport_DualSenseBluetooth_ParsesBatteryAndJackDetect()
    {
        var report = new byte[78];
        report[0] = 0x31; // Bluetooth input report
        report[1] = 0x00; // Sequence
        // Offset for Bluetooth: offset = 2
        // Battery byte: index 53 + 2 = 55
        // 0x18: charging bit (0x10) + capacity 8 (80%)
        report[55] = 0x18;
        // Status1 byte: index 54 + 2 = 56
        // bit 0 set: DS_STATUS1_HP_DETECT = 0x01 (Headphones plugged into 3.5 mm jack)
        report[56] = 0x01;

        bool success = DualSenseBluetoothAudioProtocol.TryParseInputReport(report, out int battery, out bool charging, out bool jackConnected);

        Assert.IsTrue(success);
        Assert.AreEqual(80, battery);
        Assert.IsTrue(charging);
        Assert.IsTrue(jackConnected, "Physical 3.5 mm jack must be detected as connected.");
    }

    [TestMethod]
    public void TryParseInputReport_DualSenseUsb_ParsesBatteryAndJackDetect()
    {
        var report = new byte[64];
        report[0] = 0x01; // USB input report
        // Offset for USB: offset = 1
        // Battery byte: index 53 + 1 = 54
        // 0x0A: discharging (0x00) + capacity 10 (100%)
        report[54] = 0x0A;
        // Status1 byte: index 54 + 1 = 55
        // bit 0 clear: no headphones plugged in
        report[55] = 0x00;

        bool success = DualSenseBluetoothAudioProtocol.TryParseInputReport(report, out int battery, out bool charging, out bool jackConnected);

        Assert.IsTrue(success);
        Assert.AreEqual(100, battery);
        Assert.IsFalse(charging);
        Assert.IsFalse(jackConnected, "Physical 3.5 mm jack must be detected as disconnected.");
    }
}
