using ControllerAudioSwitcher.Core.Models;

namespace ControllerAudioSwitcher.Core.Interfaces;

public interface IAudioStreamingController : IDisposable
{
    event EventHandler<StreamingStatus>? StatusChanged;

    StreamingStatus Status { get; }
    bool IsStreaming { get; }

    Task<bool> StartStreamingAsync(ControllerInfo? targetController = null);
    Task StopStreamingAsync();
    void SetVolume(byte volume);
}
