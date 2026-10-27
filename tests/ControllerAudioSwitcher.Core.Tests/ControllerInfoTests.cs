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

    [TestMethod]
    public void ControllerInfo_DualSense_Bluetooth_IsCompatible()
    {
        var info = new ControllerInfo
        {
            VendorId = 0x054C,
            ProductId = 0x0CE6, // DualSense (PS5)
            Model = ControllerModel.DualSense,
            ConnectionType = ControllerConnectionType.Bluetooth,
            DisplayName = "Sony DualSense (PS5)",
            Capabilities = ControllerCapabilities.Create(ControllerModel.DualSense, ControllerConnectionType.Bluetooth)
        };

        Assert.IsTrue(info.IsCompatible);
        Assert.AreEqual(ControllerCapabilityState.Supported, info.Capabilities.BtAudioStreaming);
        Assert.AreEqual(ControllerCapabilityState.Supported, info.Capabilities.InternalSpeakerRouting);
        Assert.AreEqual(ControllerCapabilityState.Supported, info.Capabilities.HeadphoneRouting);
        Assert.AreEqual(ControllerCapabilityState.Supported, info.Capabilities.JackDetection);
    }

    [TestMethod]
    public void ControllerInfo_DualSenseEdge_Bluetooth_IsCompatible()
    {
        var info = new ControllerInfo
        {
            VendorId = 0x054C,
            ProductId = 0x0DF2, // DualSense Edge
            Model = ControllerModel.DualSenseEdge,
            ConnectionType = ControllerConnectionType.Bluetooth,
            DisplayName = "Sony DualSense Edge",
            Capabilities = ControllerCapabilities.Create(ControllerModel.DualSenseEdge, ControllerConnectionType.Bluetooth)
        };

        Assert.IsTrue(info.IsCompatible);
        Assert.AreEqual(ControllerCapabilityState.Supported, info.Capabilities.BtAudioStreaming);
        Assert.AreEqual(ControllerCapabilityState.Supported, info.Capabilities.JackDetection);
    }

    [TestMethod]
    public void ControllerInfo_DualSense_Usb_CapabilitiesReflectUsbAudio()
    {
        var caps = ControllerCapabilities.Create(ControllerModel.DualSense, ControllerConnectionType.Usb);

        Assert.AreEqual(ControllerCapabilityState.Unsupported, caps.BtAudioStreaming);
        Assert.AreEqual(ControllerCapabilityState.Supported, caps.UsbAudioStreaming);
        Assert.AreEqual(ControllerCapabilityState.Supported, caps.InternalSpeakerRouting);
        Assert.AreEqual(ControllerCapabilityState.Supported, caps.HeadphoneRouting);
        Assert.AreEqual(ControllerCapabilityState.Supported, caps.JackDetection);
    }

    [TestMethod]
    public void ControllerInfo_DualShock4Dongle_CapabilitiesReflectAdapter()
    {
        var caps = ControllerCapabilities.Create(ControllerModel.DualShock4Dongle, ControllerConnectionType.SonyWirelessAdapter);

        Assert.AreEqual(ControllerCapabilityState.Unsupported, caps.BtAudioStreaming);
        Assert.AreEqual(ControllerCapabilityState.Supported, caps.UsbAudioStreaming);
        Assert.AreEqual(ControllerCapabilityState.Unsupported, caps.JackDetection);
    }

    [TestMethod]
    public void ControllerInfo_UnknownController_CapabilitiesAreUnknown()
    {
        var caps = ControllerCapabilities.Create(ControllerModel.UnknownPlayStation, ControllerConnectionType.Unknown);

        Assert.AreEqual(ControllerCapabilityState.Unknown, caps.BtAudioStreaming);
        Assert.AreEqual(ControllerCapabilityState.Unknown, caps.InternalSpeakerRouting);
        Assert.AreEqual(ControllerCapabilityState.Unknown, caps.HeadphoneRouting);
    }
}
