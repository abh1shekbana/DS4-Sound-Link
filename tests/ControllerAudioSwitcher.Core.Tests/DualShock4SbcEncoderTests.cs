using ControllerAudioSwitcher.Core.Codec;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ControllerAudioSwitcher.Core.Tests;

[TestClass]
public class DualShock4SbcEncoderTests
{
    [TestMethod]
    public void EncoderConstants_MatchDualShock4Specification()
    {
        var encoder = new DualShock4SbcEncoder(32000);
        Assert.AreEqual(128, encoder.SamplesPerFrame);
        Assert.AreEqual(109, encoder.EncodedFrameLength);
    }

    [TestMethod]
    public void EncodeFrame_Silence_ProducesValid109ByteSbcFrame()
    {
        var encoder = new DualShock4SbcEncoder(32000);
        short[] left = new short[128];
        short[] right = new short[128];
        byte[] output = new byte[109];

        bool success = encoder.EncodeFrame(left, right, output);

        Assert.IsTrue(success);
        Assert.AreEqual(0x9C, output[0]); // SBC syncword
        Assert.AreEqual(0x7F, output[1]); // 32kHz config
        Assert.AreEqual(48, output[2]);   // Bitpool 48
        // Byte 3 is CRC-8
    }

    [TestMethod]
    public void EncodeFrame_SineWave_ProducesValidSbcFrameWithNonZeroEnergy()
    {
        var encoder = new DualShock4SbcEncoder(32000);
        short[] left = new short[128];
        short[] right = new short[128];

        // 1 kHz sine wave @ 32 kHz sample rate
        for (int i = 0; i < 128; i++)
        {
            double angle = 2.0 * Math.PI * 1000.0 * i / 32000.0;
            short val = (short)(Math.Sin(angle) * 16000.0);
            left[i] = val;
            right[i] = val;
        }

        byte[] output = new byte[109];
        bool success = encoder.EncodeFrame(left, right, output);

        Assert.IsTrue(success);
        Assert.AreEqual(0x9C, output[0]);
        Assert.AreEqual(0x7F, output[1]);
        Assert.AreEqual(48, output[2]);

        // Frame should contain non-zero payload data beyond header
        bool hasNonZeroPayload = false;
        for (int i = 4; i < 109; i++)
        {
            if (output[i] != 0)
            {
                hasNonZeroPayload = true;
                break;
            }
        }
        Assert.IsTrue(hasNonZeroPayload, "Encoded sine wave should contain non-zero subband samples/scale factors.");
    }

    [TestMethod]
    public void EncodeFrame_UndersizedBuffers_ReturnsFalseGracefully()
    {
        var encoder = new DualShock4SbcEncoder(32000);
        short[] shortBuffer = new short[64];
        byte[] shortOutput = new byte[100];
        byte[] validOutput = new byte[109];
        short[] validPcm = new short[128];

        Assert.IsFalse(encoder.EncodeFrame(shortBuffer, validPcm, validOutput));
        Assert.IsFalse(encoder.EncodeFrame(validPcm, shortBuffer, validOutput));
        Assert.IsFalse(encoder.EncodeFrame(validPcm, validPcm, shortOutput));
    }

    [TestMethod]
    public void Reset_ClearsHistoryWithoutError()
    {
        var encoder = new DualShock4SbcEncoder(32000);
        short[] left = new short[128];
        short[] right = new short[128];
        byte[] output = new byte[109];

        encoder.EncodeFrame(left, right, output);
        encoder.Reset();

        bool success = encoder.EncodeFrame(left, right, output);
        Assert.IsTrue(success);
    }
}
