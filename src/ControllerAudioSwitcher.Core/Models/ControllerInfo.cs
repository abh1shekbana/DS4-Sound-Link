namespace ControllerAudioSwitcher.Core.Models;

public enum ControllerConnectionType
{
    Unknown = 0,
    Usb = 1,
    Bluetooth = 2,
    SonyWirelessAdapter = 3
}

public sealed class ControllerInfo
{
    public string DevicePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = "DualShock 4";
    public string MacAddress { get; set; } = string.Empty;
    public int VendorId { get; set; }
    public int ProductId { get; set; }
    public ControllerModel Model { get; set; } = ControllerModel.Unknown;
    public ControllerConnectionType ConnectionType { get; set; } = ControllerConnectionType.Unknown;
    public ControllerCapabilities Capabilities { get; set; } = new();
    public int InputReportByteLength { get; set; }
    public int OutputReportByteLength { get; set; }
    public int BatteryPercent { get; set; } = -1; // -1 = unknown / reading, 0-100%
    public bool IsCharging { get; set; }
    public bool HasBatteryInfo => BatteryPercent >= 0;

    /// <summary>
    /// Physical 3.5 mm headphone jack insertion state (null if not supported by hardware/protocol).
    /// </summary>
    public bool? IsHeadphoneJackInserted { get; set; }

    /// <summary>
    /// Whether audio streaming is supported over the controller's active connection.
    /// </summary>
    public bool IsCompatible => Capabilities.BtAudioStreaming == ControllerCapabilityState.Supported ||
                                (ConnectionType == ControllerConnectionType.Bluetooth &&
                                 VendorId == 0x054C &&
                                 (ProductId == 0x05C4 || ProductId == 0x09CC || ProductId == 0x0CE6 || ProductId == 0x0DF2));

    public override string ToString()
    {
        return $"{DisplayName} ({ConnectionType}) - VID: 0x{VendorId:X4}, PID: 0x{ProductId:X4} [Model: {Model}]";
    }
}
