using ControllerAudioSwitcher.Core.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ControllerAudioSwitcher.Core.Tests;

[TestClass]
public class DualShock4BatteryParserTests
{
    [TestMethod]
    public void TryParse_BluetoothReport0x11_DischargingLevels()
    {
        // 78-byte Bluetooth report
        byte[] report = new byte[78];
        report[0] = 0x11;

        // Level 8 (80%), discharging (cable bit 4 = 0)
        report[32] = 0x08;
        report[33] = 0x00;

        bool parsed = DualShock4BatteryParser.TryParse(report, out int percent, out bool isCharging);
        Assert.IsTrue(parsed);
        Assert.AreEqual(80, percent);
        Assert.IsFalse(isCharging);

        // Level 5 (50%), discharging
        report[32] = 0x05;
        parsed = DualShock4BatteryParser.TryParse(report, out percent, out isCharging);
        Assert.IsTrue(parsed);
        Assert.AreEqual(50, percent);
        Assert.IsFalse(isCharging);

        // Level 10 (100% full), discharging
        report[32] = 0x0A;
        parsed = DualShock4BatteryParser.TryParse(report, out percent, out isCharging);
        Assert.IsTrue(parsed);
        Assert.AreEqual(100, percent);
        Assert.IsFalse(isCharging);

        // Level 0 (Critical 0-9%)
        report[32] = 0x00;
        parsed = DualShock4BatteryParser.TryParse(report, out percent, out isCharging);
        Assert.IsTrue(parsed);
        Assert.AreEqual(5, percent);
        Assert.IsFalse(isCharging);
    }

    [TestMethod]
    public void TryParse_BluetoothReport0x11_ChargingStates()
    {
        byte[] report = new byte[78];
        report[0] = 0x11;

        // Level 6 (60%), cable plugged in (bit 4 set: 0x16)
        report[32] = 0x16;
        report[33] = 0x01; // charging bit

        bool parsed = DualShock4BatteryParser.TryParse(report, out int percent, out bool isCharging);
        Assert.IsTrue(parsed);
        Assert.AreEqual(60, percent);
        Assert.IsTrue(isCharging);

        // Level 10 (100%), cable plugged in, fully charged
        report[32] = 0x1A;
        report[33] = 0x00; // not actively charging anymore
        parsed = DualShock4BatteryParser.TryParse(report, out percent, out isCharging);
        Assert.IsTrue(parsed);
        Assert.AreEqual(100, percent);
        Assert.IsFalse(isCharging);
    }

    [TestMethod]
    public void TryParse_UsbReport0x01_ParsesAtOffset30()
    {
        // 64-byte USB report
        byte[] report = new byte[64];
        report[0] = 0x01;

        // Offset 30 has status0: level 9 (90%), cable connected
        report[30] = 0x19;
        report[31] = 0x01; // status1 charging

        bool parsed = DualShock4BatteryParser.TryParse(report, out int percent, out bool isCharging);
        Assert.IsTrue(parsed);
        Assert.AreEqual(90, percent);
        Assert.IsTrue(isCharging);
    }

    [TestMethod]
    public void TryParse_InvalidOrTruncatedReports_ReturnsFalse()
    {
        // Empty array
        Assert.IsFalse(DualShock4BatteryParser.TryParse(Array.Empty<byte>(), out _, out _));

        // Short buffer
        byte[] shortReport = new byte[10];
        shortReport[0] = 0x11;
        Assert.IsFalse(DualShock4BatteryParser.TryParse(shortReport, out _, out _));

        // Unknown report ID
        byte[] unknownReport = new byte[78];
        unknownReport[0] = 0x99;
        Assert.IsFalse(DualShock4BatteryParser.TryParse(unknownReport, out _, out _));
    }
}
