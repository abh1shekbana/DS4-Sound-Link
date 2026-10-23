namespace ControllerAudioSwitcher.Core.Protocol;

/// <summary>
/// Parser for Sony DualShock 4 hardware battery telemetry from HID input reports.
/// Extracts battery percentage (0-100%) and charging status across Bluetooth (Report 0x11)
/// and USB/Basic (Report 0x01) protocols based on Sony hardware specifications.
/// </summary>
public static class DualShock4BatteryParser
{
    /// <summary>
    /// Parses battery percentage and charging state from a raw DualShock 4 HID input report.
    /// </summary>
    /// <param name="report">Raw input report bytes.</param>
    /// <param name="batteryPercent">Calculated battery percentage [0, 100].</param>
    /// <param name="isCharging">True if controller is plugged into a power source / actively charging.</param>
    /// <returns>True if a valid battery reading was extracted; false otherwise.</returns>
    public static bool TryParse(ReadOnlySpan<byte> report, out int batteryPercent, out bool isCharging)
    {
        batteryPercent = -1;
        isCharging = false;

        if (report.IsEmpty)
            return false;

        byte reportId = report[0];

        // 1. Extended Bluetooth Input Report (Report ID 0x11, typically 78+ bytes)
        if (reportId == 0x11 && report.Length >= 34)
        {
            byte status0 = report[32];
            byte status1 = report[33];

            int rawCapacity = status0 & 0x0F;
            bool cableConnected = (status0 & 0x10) != 0;
            bool chargingBit = (status1 & 0x01) != 0;

            // Valid raw battery range reported by Sony firmware is 0 to 11
            if (rawCapacity <= 11)
            {
                isCharging = chargingBit || (cableConnected && rawCapacity < 10);
                batteryPercent = MapRawCapacityToPercent(rawCapacity, cableConnected);
                return true;
            }

            // Fallback: Check offset 30 in case transport stripped 2-byte BT header
            if (report.Length >= 32)
            {
                byte fbStatus0 = report[30];
                int fbCapacity = fbStatus0 & 0x0F;
                if (fbCapacity <= 11)
                {
                    bool fbCable = (fbStatus0 & 0x10) != 0;
                    bool fbCharging = report.Length > 31 && (report[31] & 0x01) != 0;
                    isCharging = fbCharging || (fbCable && fbCapacity < 10);
                    batteryPercent = MapRawCapacityToPercent(fbCapacity, fbCable);
                    return true;
                }
            }
        }
        // 2. USB or Simple Bluetooth Input Report (Report ID 0x01, typically 64 bytes)
        else if (reportId == 0x01 && report.Length >= 32)
        {
            byte status0 = report[30];
            int rawCapacity = status0 & 0x0F;
            bool cableConnected = (status0 & 0x10) != 0;
            bool chargingBit = report.Length > 31 && (report[31] & 0x01) != 0;

            if (rawCapacity <= 11)
            {
                isCharging = chargingBit || (cableConnected && rawCapacity < 10);
                batteryPercent = MapRawCapacityToPercent(rawCapacity, cableConnected);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Maps the DualShock 4 discrete raw 4-bit battery counter (0..11) to a readable percentage [0..100%].
    /// Sony hardware reports:
    /// 0 = 0-9% (Critical Low)
    /// 1..9 = 10% to 90%
    /// 10 = 100% Full Charge
    /// 11 = Charging Complete / Float
    /// </summary>
    private static int MapRawCapacityToPercent(int rawCapacity, bool cableConnected)
    {
        if (rawCapacity >= 10)
            return 100;

        if (rawCapacity == 0)
            return 5; // Midpoint representation of critical 0-9%

        return Math.Clamp(rawCapacity * 10, 0, 100);
    }
}
