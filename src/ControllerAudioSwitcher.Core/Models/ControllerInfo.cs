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
    public ControllerConnectionType ConnectionType { get; set; } = ControllerConnectionType.Unknown;
    public int InputReportByteLength { get; set; }
    public int OutputReportByteLength { get; set; }
    public int BatteryPercent { get; set; } = -1; // -1 = unknown / reading, 0-100%
    public bool IsCharging { get; set; }
    public bool HasBatteryInfo => BatteryPercent >= 0;
    public bool IsCompatible => VendorId == 0x054C && 
                                (ProductId == 0x05C4 || ProductId == 0x09CC) && 
                                ConnectionType == ControllerConnectionType.Bluetooth;

    public override string ToString()
    {
        return $"{DisplayName} ({ConnectionType}) - VID: 0x{VendorId:X4}, PID: 0x{ProductId:X4}";
    }
}
