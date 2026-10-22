using ControllerAudioSwitcher.Audio.Resampling;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ControllerAudioSwitcher.Core.Tests;

[TestClass]
public class AudioResamplerTests
{
    [TestMethod]
    public void ResampleStereoFloat_48kHzTo32kHz_ConvertsAtCorrectRatio()
    {
        var resampler = new AudioResampler(48000, 32000);

        // 480 frames of interleaved stereo float = 960 floats (10 ms @ 48kHz)
        float[] input = new float[960];
        short[] outLeft = new short[512];
        short[] outRight = new short[512];

        int generatedFrames = resampler.ResampleStereoFloat(input, outLeft, outRight);

        // 10 ms @ 32 kHz = 320 frames
        Assert.AreEqual(320, generatedFrames);
    }

    [TestMethod]
    public void ResampleStereoFloat_Silence_ProducesSilenceOutput()
    {
        var resampler = new AudioResampler(48000, 32000);
        float[] input = new float[480]; // 240 stereo frames
        short[] outLeft = new short[256];
        short[] outRight = new short[256];

        int count = resampler.ResampleStereoFloat(input, outLeft, outRight);

        for (int i = 0; i < count; i++)
        {
            Assert.AreEqual((short)0, outLeft[i]);
            Assert.AreEqual((short)0, outRight[i]);
        }
    }

    [TestMethod]
    public void ResampleStereoFloat_ExtremeValues_ClampToShortRange()
    {
        var resampler = new AudioResampler(48000, 32000);
        // Overdriven floats (+2.5f and -3.0f)
        float[] input = new float[4] { 2.5f, -3.0f, 2.5f, -3.0f };
        short[] outLeft = new short[4];
        short[] outRight = new short[4];

        int count = resampler.ResampleStereoFloat(input, outLeft, outRight);
        Assert.IsTrue(count > 0);

        for (int i = 0; i < count; i++)
        {
            Assert.AreEqual(short.MaxValue, outLeft[i]);
            Assert.AreEqual(short.MinValue, outRight[i]);
        }
    }

    [TestMethod]
    public void ResampleStereoFloat_PhaseContinuity_MaintainsAcrossChunks()
    {
        var resampler = new AudioResampler(48000, 32000);

        // Send two consecutive chunks of 240 frames each (total 480 frames = 320 output frames)
        float[] chunk = new float[480]; // 240 frames
        short[] left1 = new short[256];
        short[] right1 = new short[256];
        short[] left2 = new short[256];
        short[] right2 = new short[256];

        int count1 = resampler.ResampleStereoFloat(chunk, left1, right1);
        int count2 = resampler.ResampleStereoFloat(chunk, left2, right2);

        Assert.AreEqual(320, count1 + count2);
    }
}
