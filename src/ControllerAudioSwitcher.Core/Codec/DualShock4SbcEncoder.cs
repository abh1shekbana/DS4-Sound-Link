using ControllerAudioSwitcher.Core.Interfaces;

namespace ControllerAudioSwitcher.Core.Codec;

/// <summary>
/// DualShock 4 SBC Audio Encoder.
/// Derived from Bluetooth A2DP SBC specification, Appendix B.
/// Configured specifically for DualShock 4: 32 kHz stereo, 8 subbands, 16 blocks, Joint Stereo, SNR bit allocation, bitpool 48.
/// Produces exactly 109 bytes per frame from 128 stereo PCM samples.
/// </summary>
public sealed class DualShock4SbcEncoder : ISbcAudioEncoder
{
    public const int SamplesPerChannel = 128;
    public const int FrameLength = 109;

    public int SamplesPerFrame => SamplesPerChannel;
    public int EncodedFrameLength => FrameLength;

    private const int Channels = 2;
    private const int Subbands = 8;
    private const int Blocks = 16;
    private const int Bitpool = 48;
    private const int HistoryLength = 80;
    private readonly byte _configuration; // 0x7F for 32kHz, 0x3F for 16kHz

    private readonly double[][] _history =
    {
        new double[HistoryLength],
        new double[HistoryLength]
    };

    private readonly double[][] _subbandSamples =
    {
        new double[SamplesPerChannel],
        new double[SamplesPerChannel]
    };

    private readonly int[][] _scaleFactors =
    {
        new int[Subbands],
        new int[Subbands]
    };

    private readonly int[][] _allocatedBits =
    {
        new int[Subbands],
        new int[Subbands]
    };

    private readonly bool[] _joined = new bool[Subbands];
    private readonly SbcBitStream _writer = new SbcBitStream(Array.Empty<byte>(), 0);

    public DualShock4SbcEncoder(int sampleRate = 32000)
    {
        _configuration = sampleRate switch
        {
            16000 => 0x3F,
            32000 => 0x7F,
            _ => throw new ArgumentOutOfRangeException(nameof(sampleRate), "Supported sample rates are 16000 and 32000 Hz.")
        };
    }

    public void Reset()
    {
        for (int channel = 0; channel < Channels; channel++)
        {
            Array.Clear(_history[channel], 0, _history[channel].Length);
            Array.Clear(_subbandSamples[channel], 0, _subbandSamples[channel].Length);
        }
    }

    public bool EncodeFrame(short[] left, short[] right, byte[] output)
    {
        if (left == null || right == null || output == null ||
            left.Length < SamplesPerChannel ||
            right.Length < SamplesPerChannel || 
            output.Length < FrameLength)
        {
            return false;
        }

        AnalyzeChannel(0, left);
        AnalyzeChannel(1, right);
        SelectJointStereoBands();
        CalculateScaleFactors();
        AllocateSnrBits();
        return WriteFrame(output);
    }

    private void AnalyzeChannel(int channel, short[] pcm)
    {
        double[] channelHistory = _history[channel];
        double[] channelSamples = _subbandSamples[channel];
        double[][] matrix = SbcEncoderTables.AnalysisMatrix8;

        for (int block = 0; block < Blocks; block++)
        {
            int inputOffset = block * Subbands;
            int outputOffset = block * Subbands;

            Array.Copy(channelHistory, 0, channelHistory, Subbands, HistoryLength - Subbands);
            for (int sample = 0; sample < Subbands; sample++)
            {
                channelHistory[Subbands - 1 - sample] = pcm[inputOffset + sample];
            }

            for (int band = 0; band < Subbands; band++)
            {
                double value = 0.0;
                double[] coefficients = matrix[band];
                for (int tap = 0; tap < HistoryLength; tap++)
                {
                    value += coefficients[tap] * channelHistory[tap];
                }
                channelSamples[outputOffset + band] = value;
            }
        }
    }

    private void SelectJointStereoBands()
    {
        for (int band = 0; band < Subbands - 1; band++)
        {
            double maximumLeft = 0.0;
            double maximumRight = 0.0;
            double maximumSum = 0.0;
            double maximumDifference = 0.0;

            for (int block = 0; block < Blocks; block++)
            {
                int index = block * Subbands + band;
                double left = _subbandSamples[0][index];
                double right = _subbandSamples[1][index];
                maximumLeft = Math.Max(maximumLeft, Math.Abs(left));
                maximumRight = Math.Max(maximumRight, Math.Abs(right));
                maximumSum = Math.Max(maximumSum, Math.Abs((left + right) * 0.5));
                maximumDifference = Math.Max(maximumDifference, Math.Abs((left - right) * 0.5));
            }

            bool useJoint = ScaleFactor(maximumSum) + ScaleFactor(maximumDifference) <
                            ScaleFactor(maximumLeft) + ScaleFactor(maximumRight);
            _joined[band] = useJoint;
            if (!useJoint) continue;

            for (int block = 0; block < Blocks; block++)
            {
                int index = block * Subbands + band;
                double left = _subbandSamples[0][index];
                double right = _subbandSamples[1][index];
                _subbandSamples[0][index] = (left + right) * 0.5;
                _subbandSamples[1][index] = (left - right) * 0.5;
            }
        }

        _joined[Subbands - 1] = false;
    }

    private void CalculateScaleFactors()
    {
        for (int channel = 0; channel < Channels; channel++)
        {
            for (int band = 0; band < Subbands; band++)
            {
                double maximum = 0.0;
                for (int block = 0; block < Blocks; block++)
                {
                    maximum = Math.Max(maximum, Math.Abs(_subbandSamples[channel][block * Subbands + band]));
                }
                _scaleFactors[channel][band] = ScaleFactor(maximum);
            }
        }
    }

    private static int ScaleFactor(double maximumMagnitude)
    {
        int factor = 0;
        while (factor < 15 && (1 << (factor + 1)) <= maximumMagnitude)
        {
            factor++;
        }
        return factor;
    }

    private void AllocateSnrBits()
    {
        int maximumNeed = 0;
        for (int channel = 0; channel < Channels; channel++)
        {
            for (int band = 0; band < Subbands; band++)
            {
                maximumNeed = Math.Max(maximumNeed, _scaleFactors[channel][band]);
                _allocatedBits[channel][band] = 0;
            }
        }

        int bitCount = 0;
        int sliceCount = 0;
        int bitSlice = maximumNeed + 1;
        do
        {
            bitSlice--;
            bitCount += sliceCount;
            sliceCount = 0;
            for (int channel = 0; channel < Channels; channel++)
            {
                for (int band = 0; band < Subbands; band++)
                {
                    int need = _scaleFactors[channel][band];
                    if (need > bitSlice + 1 && need < bitSlice + 16)
                    {
                        sliceCount++;
                    }
                    else if (need == bitSlice + 1)
                    {
                        sliceCount += 2;
                    }
                }
            }
        }
        while (bitCount + sliceCount < Bitpool);

        if (bitCount + sliceCount == Bitpool)
        {
            bitCount += sliceCount;
            bitSlice--;
        }

        for (int channel = 0; channel < Channels; channel++)
        {
            for (int band = 0; band < Subbands; band++)
            {
                int need = _scaleFactors[channel][band];
                _allocatedBits[channel][band] = need < bitSlice + 2 ? 0 : Math.Min(need - bitSlice, 16);
            }
        }

        int nextChannel = 0;
        int nextBand = 0;
        while (bitCount < Bitpool && nextBand < Subbands)
        {
            int bits = _allocatedBits[nextChannel][nextBand];
            if (bits >= 2 && bits < 16)
            {
                _allocatedBits[nextChannel][nextBand]++;
                bitCount++;
            }
            else if (_scaleFactors[nextChannel][nextBand] == bitSlice + 1 && bitCount + 1 < Bitpool)
            {
                _allocatedBits[nextChannel][nextBand] = 2;
                bitCount += 2;
            }
            AdvanceAllocationCursor(ref nextChannel, ref nextBand);
        }

        nextChannel = 0;
        nextBand = 0;
        while (bitCount < Bitpool && nextBand < Subbands)
        {
            if (_allocatedBits[nextChannel][nextBand] < 16)
            {
                _allocatedBits[nextChannel][nextBand]++;
                bitCount++;
            }
            AdvanceAllocationCursor(ref nextChannel, ref nextBand);
        }
    }

    private static void AdvanceAllocationCursor(ref int channel, ref int band)
    {
        if (++channel == Channels)
        {
            channel = 0;
            band++;
        }
    }

    private bool WriteFrame(byte[] output)
    {
        Array.Clear(output, 0, FrameLength);
        _writer.Reset(output, FrameLength);
        _writer.PutBits(0x9C, 8); // SBC Sync word
        _writer.PutBits(_configuration, 8);
        _writer.PutBits(Bitpool, 8);
        _writer.PutBits(0, 8); // CRC placeholder

        for (int band = 0; band < Subbands - 1; band++)
        {
            _writer.PutBits(_joined[band] ? 1u : 0u, 1);
        }
        _writer.PutBits(0, 1);

        for (int channel = 0; channel < Channels; channel++)
        {
            for (int band = 0; band < Subbands; band++)
            {
                _writer.PutBits((uint)_scaleFactors[channel][band], 4);
            }
        }

        for (int block = 0; block < Blocks; block++)
        {
            for (int channel = 0; channel < Channels; channel++)
            {
                for (int band = 0; band < Subbands; band++)
                {
                    int bits = _allocatedBits[channel][band];
                    if (bits == 0) continue;

                    uint levels = (1u << bits) - 1;
                    double limit = 1 << (_scaleFactors[channel][band] + 1);
                    double normalized = _subbandSamples[channel][block * Subbands + band] / limit;
                    long quantized = (long)((normalized + 1.0) * levels * 0.5);
                    quantized = Math.Max(0, Math.Min(quantized, levels));
                    _writer.PutBits((uint)quantized, bits);
                }
            }
        }

        _writer.Flush();
        if (_writer.HasError) return false;

        output[3] = CalculateCrc(output);
        return true;
    }

    private static byte CalculateCrc(byte[] frame)
    {
        byte crc = 0x0F;
        UpdateCrc(ref crc, frame[1]);
        UpdateCrc(ref crc, frame[2]);
        for (int index = 4; index <= 12; index++)
        {
            UpdateCrc(ref crc, frame[index]);
        }
        return crc;
    }

    private static void UpdateCrc(ref byte crc, byte value)
    {
        for (int shift = 7; shift >= 0; shift--)
        {
            bool highBit = (((value >> shift) & 1) ^ (crc >> 7)) != 0;
            crc <<= 1;
            if (highBit) crc ^= 0x1D;
        }
    }

    public void Dispose()
    {
        Reset();
    }
}
