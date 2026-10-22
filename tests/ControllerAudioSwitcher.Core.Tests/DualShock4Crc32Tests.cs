using ControllerAudioSwitcher.Core.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ControllerAudioSwitcher.Core.Tests;

[TestClass]
public class DualShock4Crc32Tests
{
    [TestMethod]
    public void ComputeCrc32_EmptyArray_ReturnsZero()
    {
        var data = Array.Empty<byte>();
        uint crc = DualShock4Crc32.Compute(data);
        Assert.AreEqual(0u, crc);
    }

    [TestMethod]
    public void ComputeCrc32_KnownVector_ReturnsExpectedValue()
    {
        // Standard ASCII "123456789" with IEEE 802.3 CRC-32 polynomial (0xEDB88320)
        byte[] data = "123456789"u8.ToArray();
        uint crc = DualShock4Crc32.Compute(data);
        Assert.AreEqual(0xCBF43926u, crc);
    }

    [TestMethod]
    public void ComputeBluetoothCrc32_IncludesPrefix0xA2()
    {
        // When computing Bluetooth CRC, 0xA2 is prepended into the calculation
        byte[] payload = new byte[] { 0x11, 0x80, 0xA0 };
        uint btCrc = DualShock4Crc32.ComputeBluetoothCrc32(payload, 0, payload.Length);

        // Compute manual reference: 0xA2 followed by payload
        byte[] withPrefix = new byte[] { 0xA2, 0x11, 0x80, 0xA0 };
        uint referenceCrc = DualShock4Crc32.Compute(withPrefix);

        Assert.AreEqual(referenceCrc, btCrc);
    }

    [TestMethod]
    public void ComputeBluetoothCrc32_WithOffsetAndCount_MatchesSubarray()
    {
        byte[] buffer = new byte[] { 0xFF, 0x11, 0x80, 0xA0, 0xFF };
        uint crc = DualShock4Crc32.ComputeBluetoothCrc32(buffer, 1, 3);

        byte[] withPrefix = new byte[] { 0xA2, 0x11, 0x80, 0xA0 };
        uint referenceCrc = DualShock4Crc32.Compute(withPrefix);

        Assert.AreEqual(referenceCrc, crc);
    }
}
