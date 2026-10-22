using ControllerAudioSwitcher.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ControllerAudioSwitcher.Core.Tests;

[TestClass]
public class ControllerInfoTests
{
    [TestMethod]
    public void ControllerInfo_DualShock4V1_Bluetooth_IsCompatible()
    {
        var info = new ControllerInfo
        {
            VendorId = 0x054C,
            ProductId = 0x05C4, // CUH-ZCT1
            ConnectionType = ControllerConnectionType.Bluetooth,
            DisplayName = "Sony DualShock 4 (v1)"
        };

        Assert.IsTrue(info.IsCompatible);
    }

    [TestMethod]
    public void ControllerInfo_DualShock4V2_Bluetooth_IsCompatible()
    {
        var info = new ControllerInfo
        {
            VendorId = 0x054C,
            ProductId = 0x09CC, // CUH-ZCT2
            ConnectionType = ControllerConnectionType.Bluetooth,
            DisplayName = "Sony DualShock 4 (v2)"
        };

        Assert.IsTrue(info.IsCompatible);
    }

    [TestMethod]
    public void ControllerInfo_DualShock4_Usb_IsNotCompatibleForBluetoothAudio()
    {
        var info = new ControllerInfo
        {
            VendorId = 0x054C,
            ProductId = 0x09CC,
            ConnectionType = ControllerConnectionType.Usb,
            DisplayName = "Sony DualShock 4 (v2)"
        };

        // Even though it's a genuine DS4, over USB it cannot use the Bluetooth HID 0x17 report
        Assert.IsFalse(info.IsCompatible);
    }

    [TestMethod]
    public void ControllerInfo_ThirdPartyController_IsNotCompatible()
    {
        var info = new ControllerInfo
        {
            VendorId = 0x045E, // Microsoft Xbox Controller
            ProductId = 0x02EA,
            ConnectionType = ControllerConnectionType.Bluetooth
        };

        Assert.IsFalse(info.IsCompatible);
    }

    [TestMethod]
    public void ControllerInfo_ToString_ContainsVidPidAndConnection()
    {
        var info = new ControllerInfo
        {
            VendorId = 0x054C,
            ProductId = 0x09CC,
            ConnectionType = ControllerConnectionType.Bluetooth,
            DisplayName = "DualShock 4 v2"
        };

        string text = info.ToString();
        Assert.IsTrue(text.Contains("054C", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(text.Contains("09CC", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(text.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase));
    }
}
