using ControllerAudioSwitcher.Core.Models;

namespace ControllerAudioSwitcher.Core.Interfaces;

public sealed class AudioSamplesEventArgs : EventArgs
{
    public short[] LeftSamples { get; }
    public short[] RightSamples { get; }
    public int SampleCount { get; }

    public AudioSamplesEventArgs(short[] left, short[] right, int count)
    {
        LeftSamples = left;
        RightSamples = right;
        SampleCount = count;
    }
}

public interface IAudioCaptureService : IDisposable
{
    event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;
    event EventHandler<string>? CaptureError;

    bool IsCapturing { get; }
    bool IsEndpointMuted { get; set; }
    IReadOnlyList<AudioDeviceChoice> EnumerateRenderDevices();
    void StartCapture(string? deviceId = null, bool muteSpeakers = false);
    void StopCapture();
    void SetEndpointMute(bool mute, string? deviceId = null);
}
