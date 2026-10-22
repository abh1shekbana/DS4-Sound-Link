using System.Buffers.Binary;
using ControllerAudioSwitcher.Core.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ControllerAudioSwitcher.Core.Tests;

[TestClass]
public class DualShock4BluetoothAudioProtocolTests
{
    [TestMethod]
    public void BuildAudioControlReport_Enabled_HasCorrectFieldsAndCrc()
    {
        byte volume = 115;
        byte[] report = DualShock4BluetoothAudioProtocol.BuildAudioControlReport(speakerEnabled: true, headphoneVolume: volume);

        Assert.AreEqual(78, report.Length);
        Assert.AreEqual(0x11, report[0]);
        Assert.AreEqual(0xA0, report[2]); // Speaker mode
        Assert.AreEqual(0xF3, report[3]); // Validity mask
        Assert.AreEqual(volume, report[21]); // Left headphone
        Assert.AreEqual(volume, report[22]); // Right headphone

        // Verify CRC-32 over prefix 0xA2 + report[0..73]
        uint expectedCrc = DualShock4Crc32.ComputeBluetoothCrc32(report, 0, 74);
        uint reportCrc = BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(74, 4));
        Assert.AreEqual(expectedCrc, reportCrc);
    }

    [TestMethod]
    public void BuildAudioControlReport_Disabled_DisarmsAudio()
    {
        byte[] report = DualShock4BluetoothAudioProtocol.BuildAudioControlReport(speakerEnabled: false);

        Assert.AreEqual(78, report.Length);
        Assert.AreEqual(0x11, report[0]);
        Assert.AreEqual(0x00, report[2]); // Disabled
        Assert.AreEqual(0xF0, report[3]); // Validity mask
        Assert.AreEqual(0, report[21]);
        Assert.AreEqual(0, report[22]);

        uint expectedCrc = DualShock4Crc32.ComputeBluetoothCrc32(report, 0, 74);
        uint reportCrc = BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(74, 4));
        Assert.AreEqual(expectedCrc, reportCrc);
    }

    [TestMethod]
    public void BuildStreamReport_HeadsetMode_TargetsHeadphoneJackWithCorrectFraming()
    {
        ushort frameNumber = 0x1234;
        byte[][] frames = new byte[4][];
        for (int i = 0; i < 4; i++)
        {
            frames[i] = new byte[109];
            frames[i][0] = 0x9C; // SBC syncword
            frames[i][1] = (byte)(i + 1);
        }

        byte[] report = DualShock4BluetoothAudioProtocol.BuildStreamReport(frameNumber, frames, headsetMode: true);

        Assert.AreEqual(462, report.Length);
        Assert.AreEqual(0x17, report[0]); // Report ID
        Assert.AreEqual(0x40, report[1]);
        Assert.AreEqual(0xA0, report[2]); // Mode A0 to preserve controller input
        Assert.AreEqual(0x34, report[3]); // Low byte of 0x1234
        Assert.AreEqual(0x12, report[4]); // High byte of 0x1234
        Assert.AreEqual(0x24, report[5]); // AudioTargetHeadphoneJack (3.5 mm jack)

        // Verify SBC frames payload (bytes 6 to 441)
        for (int i = 0; i < 4; i++)
        {
            int offset = 6 + (i * 109);
            Assert.AreEqual(0x9C, report[offset]);
            Assert.AreEqual((byte)(i + 1), report[offset + 1]);
        }

        // Verify CRC-32 over prefix 0xA2 + report[0..457]
        uint expectedCrc = DualShock4Crc32.ComputeBluetoothCrc32(report, 0, 458);
        uint reportCrc = BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(458, 4));
        Assert.AreEqual(expectedCrc, reportCrc);
    }

    [TestMethod]
    public void BuildStreamReport_SpeakerMode_TargetsInternalSpeaker()
    {
        ushort frameNumber = 1;
        byte[][] frames = new byte[4][];
        for (int i = 0; i < 4; i++)
        {
            frames[i] = new byte[109];
        }

        byte[] report = DualShock4BluetoothAudioProtocol.BuildStreamReport(frameNumber, frames, headsetMode: false);
        Assert.AreEqual(0x02, report[5]); // AudioTargetInternalSpeaker
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void BuildStreamReport_InsufficientFrames_ThrowsArgumentException()
    {
        byte[][] frames = new byte[3][];
        DualShock4BluetoothAudioProtocol.BuildStreamReport(1, frames);
    }
}
