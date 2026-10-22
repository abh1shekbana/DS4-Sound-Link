namespace ControllerAudioSwitcher.Core.Interfaces;

public interface ISbcAudioEncoder : IDisposable
{
    int SamplesPerFrame { get; }
    int EncodedFrameLength { get; }
    bool EncodeFrame(short[] left, short[] right, byte[] output);
    void Reset();
}
