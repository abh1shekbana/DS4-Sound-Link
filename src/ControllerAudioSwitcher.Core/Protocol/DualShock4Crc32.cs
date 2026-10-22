namespace ControllerAudioSwitcher.Core.Protocol;

/// <summary>
/// DualShock 4 Bluetooth CRC-32 Calculator.
/// Uses standard IEEE 802.3 polynomial (0xEDB88320) with seed 0xFFFFFFFF,
/// seeded with a Bluetooth report prefix (0xA2 for Output Reports, 0xA1 for Input Reports).
/// </summary>
public static class DualShock4Crc32
{
    private const uint Polynomial = 0xEDB88320u;
    public const byte OutputReportPrefix = 0xA2;
    public const byte InputReportPrefix = 0xA1;

    private static readonly uint[] Table = GenerateCrcTable();

    private static uint[] GenerateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? Polynomial : 0);
            }
            table[i] = crc;
        }
        return table;
    }

    public static uint Compute(byte prefix, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        crc = (crc >> 8) ^ Table[(crc ^ prefix) & 0xFF];
        for (int i = 0; i < data.Length; i++)
        {
            crc = (crc >> 8) ^ Table[(crc ^ data[i]) & 0xFF];
        }
        return ~crc;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return 0;
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
        {
            crc = (crc >> 8) ^ Table[(crc ^ data[i]) & 0xFF];
        }
        return ~crc;
    }

    public static uint ComputeBluetoothCrc32(ReadOnlySpan<byte> data)
    {
        return Compute(OutputReportPrefix, data);
    }

    public static uint ComputeBluetoothCrc32(byte[] buffer, int offset, int count)
    {
        return Compute(OutputReportPrefix, buffer.AsSpan(offset, count));
    }

    public static void AppendChecksum(byte prefix, Span<byte> reportWithSpaceForCrc)
    {
        if (reportWithSpaceForCrc.Length < 4)
            throw new ArgumentException("Report buffer must be at least 4 bytes to append CRC.", nameof(reportWithSpaceForCrc));

        int dataLength = reportWithSpaceForCrc.Length - 4;
        uint crc = Compute(prefix, reportWithSpaceForCrc[..dataLength]);

        reportWithSpaceForCrc[dataLength] = (byte)(crc & 0xFF);
        reportWithSpaceForCrc[dataLength + 1] = (byte)((crc >> 8) & 0xFF);
        reportWithSpaceForCrc[dataLength + 2] = (byte)((crc >> 16) & 0xFF);
        reportWithSpaceForCrc[dataLength + 3] = (byte)((crc >> 24) & 0xFF);
    }

    public static bool ValidateChecksum(byte prefix, ReadOnlySpan<byte> report)
    {
        if (report.Length < 4)
            return false;

        int dataLength = report.Length - 4;
        uint expectedCrc = (uint)(report[dataLength] |
                                  (report[dataLength + 1] << 8) |
                                  (report[dataLength + 2] << 16) |
                                  (report[dataLength + 3] << 24));

        uint actualCrc = Compute(prefix, report[..dataLength]);
        return expectedCrc == actualCrc;
    }
}
