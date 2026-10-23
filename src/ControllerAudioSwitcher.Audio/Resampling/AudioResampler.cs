namespace ControllerAudioSwitcher.Audio.Resampling;

/// <summary>
/// High-quality linear resampler and format converter for real-time audio.
/// Converts arbitrarily sampled stereo float audio (e.g. 48 kHz or 44.1 kHz IEEE float)
/// into 32 kHz (or target rate) 16-bit signed PCM separate left and right channels.
/// Preserves fractional phase across packet boundaries so no pops or clicks occur.
/// Features smooth gain ramping and a soft-knee limiter to prevent clipping.
/// </summary>
public sealed class AudioResampler
{
    private readonly int _sourceSampleRate;
    private readonly int _targetSampleRate;
    private readonly double _step;
    private double _phase;
    private float _carryLeft;
    private float _carryRight;
    private bool _hasCarry;

    private float _currentGain = 1.0f;
    private float _targetGain = 1.0f;

    public float SoftwareGain
    {
        get => _targetGain;
        set => _targetGain = Math.Clamp(value, 0.5f, 2.5f);
    }

    public AudioResampler(int sourceSampleRate, int targetSampleRate = 32000, float initialGain = 1.0f)
    {
        if (sourceSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));
        if (targetSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetSampleRate));

        _sourceSampleRate = sourceSampleRate;
        _targetSampleRate = targetSampleRate;
        _step = (double)_sourceSampleRate / _targetSampleRate;
        _targetGain = Math.Clamp(initialGain, 0.5f, 2.5f);
        _currentGain = _targetGain;
    }

    /// <summary>
    /// Resamples interleaved stereo float samples to separate left and right 16-bit PCM arrays.
    /// </summary>
    public int ResampleStereoFloat(ReadOnlySpan<float> interleavedStereo, Span<short> outLeft, Span<short> outRight)
    {
        int inputFrames = interleavedStereo.Length / 2;
        if (inputFrames == 0) return 0;

        int outputFrames = 0;
        int maxOutputs = Math.Min(outLeft.Length, outRight.Length);

        // Position is measured relative to sample 0 of the current chunk.
        // If we have carry from the previous chunk, the carry sample is at position -1.0.
        // _phase is the position where the first output sample should be taken.
        double position = _phase;

        // First, check if any output sample falls in the carry interval [-1.0, 0.0)
        if (_hasCarry)
        {
            while (position < 0.0)
            {
                if (outputFrames >= maxOutputs) break;

                // position is in [-1.0, 0.0).
                // sample at -1.0 is _carry, sample at 0.0 is interleavedStereo[0]
                double fraction = position + 1.0; // in [0.0, 1.0)
                float left0 = _carryLeft;
                float right0 = _carryRight;
                float left1 = interleavedStereo[0];
                float right1 = interleavedStereo[1];

                float interpLeft = (float)(left0 * (1.0 - fraction) + left1 * fraction);
                float interpRight = (float)(right0 * (1.0 - fraction) + right1 * fraction);

                RampGain();
                outLeft[outputFrames] = FloatToPcm16(interpLeft, _currentGain);
                outRight[outputFrames] = FloatToPcm16(interpRight, _currentGain);
                outputFrames++;

                position += _step;
            }
        }
        else
        {
            // First chunk: start position at 0
            if (position < 0.0) position = 0.0;
        }

        // Now process samples within current chunk: [0, inputFrames - 1]
        // An output sample at position p where 0 <= p < inputFrames - 1 interpolates
        // between input[current] and input[current + 1].
        while (position < inputFrames - 1)
        {
            if (outputFrames >= maxOutputs) break;

            int current = (int)position;
            double fraction = position - current;

            float left0 = interleavedStereo[current * 2];
            float right0 = interleavedStereo[current * 2 + 1];
            float left1 = interleavedStereo[(current + 1) * 2];
            float right1 = interleavedStereo[(current + 1) * 2 + 1];

            float interpLeft = (float)(left0 * (1.0 - fraction) + left1 * fraction);
            float interpRight = (float)(right0 * (1.0 - fraction) + right1 * fraction);

            RampGain();
            outLeft[outputFrames] = FloatToPcm16(interpLeft, _currentGain);
            outRight[outputFrames] = FloatToPcm16(interpRight, _currentGain);
            outputFrames++;

            position += _step;
        }

        // At the end of the chunk:
        // Any remaining position >= inputFrames - 1 requires the sample at index inputFrames,
        // which will be sample 0 of the NEXT chunk!
        _carryLeft = interleavedStereo[(inputFrames - 1) * 2];
        _carryRight = interleavedStereo[(inputFrames - 1) * 2 + 1];
        _hasCarry = true;
        _phase = position - inputFrames;

        return outputFrames;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void RampGain()
    {
        if (Math.Abs(_currentGain - _targetGain) > 0.0001f)
        {
            _currentGain += (_targetGain - _currentGain) * 0.002f;
        }
        else
        {
            _currentGain = _targetGain;
        }
    }

    /// <summary>
    /// Converts a normalized float sample [-1.0, 1.0] to a 16-bit PCM short [-32768, 32767],
    /// applying software gain and a smooth soft-knee limiter (hyperbolic tangent).
    /// Clamps output peaks to a safe true-peak ceiling of 0.85 (-1.41 dBFS / ~27850 PCM)
    /// to physically prevent SBC polyphase synthesis filter overshoot and controller DAC clipping on high frequencies.
    /// </summary>
    public static short FloatToPcm16(float value, float gain = 1.0f)
    {
        float boosted = value * gain;
        float limited;

        // True-peak headroom ceiling for SBC polyphase filter and DS4 DAC:
        // 0.85f * 32767 = 27852 (-1.41 dBFS) ensures the controller's internal
        // reconstruction filter will never exceed 32767 even on sharp high-frequency transients.
        const float SafeCeiling = 0.85f;
        const float LinearKnee = 0.60f;
        const float Headroom = SafeCeiling - LinearKnee; // 0.25f

        if (boosted > LinearKnee)
        {
            limited = LinearKnee + Headroom * (float)Math.Tanh((boosted - LinearKnee) / Headroom);
        }
        else if (boosted < -LinearKnee)
        {
            limited = -LinearKnee - Headroom * (float)Math.Tanh((-boosted - LinearKnee) / Headroom);
        }
        else
        {
            limited = boosted;
        }

        float clamped = Math.Clamp(limited, -SafeCeiling, SafeCeiling);
        int sample = clamped < 0 
            ? (int)Math.Round(clamped * 32768.0f) 
            : (int)Math.Round(clamped * 32767.0f);
        return (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
    }

    public void Reset()
    {
        _phase = 0.0;
        _carryLeft = 0.0f;
        _carryRight = 0.0f;
        _hasCarry = false;
        _currentGain = _targetGain;
    }
}
