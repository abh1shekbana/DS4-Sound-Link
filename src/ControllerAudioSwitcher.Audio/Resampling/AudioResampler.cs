namespace ControllerAudioSwitcher.Audio.Resampling;

/// <summary>
/// High-quality linear resampler and format converter for real-time audio.
/// Converts arbitrarily sampled stereo float audio (e.g. 48 kHz or 44.1 kHz IEEE float)
/// into 32 kHz (or target rate) 16-bit signed PCM separate left and right channels.
/// Preserves fractional phase across packet boundaries so no pops or clicks occur.
/// </summary>
public sealed class AudioResampler
{
    private readonly int _sourceSampleRate;
    private readonly int _targetSampleRate;
    private readonly double _step;
    private double _phase;
    private float _carryLeft;
    private float _carryRight;

    public AudioResampler(int sourceSampleRate, int targetSampleRate = 32000)
    {
        if (sourceSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));
        if (targetSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetSampleRate));

        _sourceSampleRate = sourceSampleRate;
        _targetSampleRate = targetSampleRate;
        _step = (double)_sourceSampleRate / _targetSampleRate;
    }

    /// <summary>
    /// Resamples interleaved stereo float samples to separate left and right 16-bit PCM arrays.
    /// </summary>
    public int ResampleStereoFloat(ReadOnlySpan<float> interleavedStereo, Span<short> outLeft, Span<short> outRight)
    {
        int inputFrames = interleavedStereo.Length / 2;
        if (inputFrames == 0) return 0;

        double position = _phase;
        int outputFrames = 0;

        while (position < inputFrames)
        {
            if (outputFrames >= outLeft.Length || outputFrames >= outRight.Length)
                break;

            int current = (int)position;
            double fraction = position - current;

            float left0 = interleavedStereo[current * 2];
            float right0 = interleavedStereo[current * 2 + 1];

            float left1 = (current + 1 < inputFrames) ? interleavedStereo[(current + 1) * 2] : left0;
            float right1 = (current + 1 < inputFrames) ? interleavedStereo[(current + 1) * 2 + 1] : right0;

            float interpLeft = (float)(left0 * (1.0 - fraction) + left1 * fraction);
            float interpRight = (float)(right0 * (1.0 - fraction) + right1 * fraction);

            outLeft[outputFrames] = FloatToPcm16(interpLeft);
            outRight[outputFrames] = FloatToPcm16(interpRight);

            outputFrames++;
            position += _step;
        }

        _phase = Math.Max(0.0, position - inputFrames);
        _carryLeft = interleavedStereo[(inputFrames - 1) * 2];
        _carryRight = interleavedStereo[(inputFrames - 1) * 2 + 1];

        return outputFrames;
    }

    private static short FloatToPcm16(float value)
    {
        float clamped = Math.Clamp(value, -1.0f, 1.0f);
        int sample = clamped < 0 
            ? (int)Math.Round(clamped * 32768.0f) 
            : (int)Math.Round(clamped * 32767.0f);
        return (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
    }

    public void Reset()
    {
        _phase = 0;
        _carryLeft = 0;
        _carryRight = 0;
    }
}
