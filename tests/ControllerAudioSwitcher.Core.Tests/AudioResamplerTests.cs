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
            Assert.IsTrue(outLeft[i] <= short.MaxValue && outLeft[i] >= 27000, 
                $"Extreme positive value must stay safely within short bounds without digital clipping. Actual: {outLeft[i]}");
            Assert.IsTrue(outRight[i] >= short.MinValue && outRight[i] <= -27000, 
                $"Extreme negative value must stay safely within short bounds without digital clipping. Actual: {outRight[i]}");
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

    [TestMethod]
    public void ResampleStereoFloat_ContinuousSineWave_MatchesSingleChunkResampling()
    {
        // 960 input frames @ 48kHz (20 ms)
        int totalFrames = 960;
        float[] fullInput = new float[totalFrames * 2];
        for (int i = 0; i < totalFrames; i++)
        {
            float sine = (float)Math.Sin(2.0 * Math.PI * 440.0 * i / 48000.0) * 0.7f;
            fullInput[i * 2] = sine;
            fullInput[i * 2 + 1] = sine;
        }

        // Single chunk resample
        var singleResampler = new AudioResampler(48000, 32000);
        short[] singleLeft = new short[700];
        short[] singleRight = new short[700];
        int singleCount = singleResampler.ResampleStereoFloat(fullInput, singleLeft, singleRight);

        // Split into two 480-frame chunks
        var chunkResampler = new AudioResampler(48000, 32000);
        short[] chunkLeft = new short[700];
        short[] chunkRight = new short[700];

        int count1 = chunkResampler.ResampleStereoFloat(fullInput.AsSpan(0, 480 * 2), chunkLeft.AsSpan(), chunkRight.AsSpan());
        int count2 = chunkResampler.ResampleStereoFloat(fullInput.AsSpan(480 * 2), chunkLeft.AsSpan(count1), chunkRight.AsSpan(count1));
        int totalChunkCount = count1 + count2;

        Assert.AreEqual(singleCount, totalChunkCount, "Total output frame count must be identical.");

        // Compare samples across boundary: max delta between split and single should be <= 1 (rounding only)
        for (int i = 0; i < singleCount; i++)
        {
            int diff = Math.Abs(singleLeft[i] - chunkLeft[i]);
            Assert.IsTrue(diff <= 1, $"Sample mismatch at index {i}: single={singleLeft[i]}, chunked={chunkLeft[i]}, diff={diff}");
        }
    }

    [TestMethod]
    public void FloatToPcm16_SoftKneeLimiter_CompressesSmoothlyWithoutClipping()
    {
        // Inside linear region (<= 0.95f)
        short pcmHalf = AudioResampler.FloatToPcm16(0.5f, 1.0f);
        Assert.AreEqual((short)Math.Round(0.5f * 32767.0f), pcmHalf);

        // Over-unity values must be smoothly compressed and stay strictly within short bounds
        short pcm12 = AudioResampler.FloatToPcm16(1.2f, 1.0f);
        short pcm15 = AudioResampler.FloatToPcm16(1.5f, 1.0f);
        short pcmOver = AudioResampler.FloatToPcm16(3.0f, 1.0f);

        Assert.IsTrue(pcm12 < short.MaxValue);
        Assert.IsTrue(pcm15 <= short.MaxValue);
        Assert.IsTrue(pcmOver <= short.MaxValue);
        Assert.IsTrue(pcm15 >= pcm12, "Limiter response must be monotonically increasing.");

        // Negative symmetry
        short pcmNeg12 = AudioResampler.FloatToPcm16(-1.2f, 1.0f);
        short pcmNegOver = AudioResampler.FloatToPcm16(-3.0f, 1.0f);

        Assert.IsTrue(pcmNeg12 > short.MinValue);
        Assert.IsTrue(pcmNegOver >= short.MinValue);
    }

    [TestMethod]
    public void SoftwareGain_ScalesVolumeWithinBounds()
    {
        var resampler = new AudioResampler(48000, 32000, 1.5f);
        Assert.AreEqual(1.5f, resampler.SoftwareGain, 0.001f);

        resampler.SoftwareGain = 3.0f;
        Assert.AreEqual(2.5f, resampler.SoftwareGain, 0.001f, "Gain should clamp at upper bound 2.5f");

        resampler.SoftwareGain = 0.1f;
        Assert.AreEqual(0.5f, resampler.SoftwareGain, 0.001f, "Gain should clamp at lower bound 0.5f");
    }

    [TestMethod]
    public void FloatToPcm16_EnforcesSafeTruePeakCeiling_PreventsSbcFilterOvershoot()
    {
        // Even for extreme inputs (e.g. 5.0f full scale or 10.0f)
        short pcmMaxOver = AudioResampler.FloatToPcm16(10.0f, 1.0f);
        short pcmNegMaxOver = AudioResampler.FloatToPcm16(-10.0f, 1.0f);

        // Safe true-peak ceiling: 0.85f * 32767 = 27852
        Assert.IsTrue(pcmMaxOver <= 27852, $"Output peak must not exceed safe SBC ceiling 27852. Actual: {pcmMaxOver}");
        Assert.IsTrue(pcmNegMaxOver >= -27853, $"Output peak must not exceed safe SBC ceiling -27853. Actual: {pcmNegMaxOver}");
    }
}
