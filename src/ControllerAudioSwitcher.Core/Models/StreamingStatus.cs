namespace ControllerAudioSwitcher.Core.Models;

public enum StreamingState
{
    Disconnected,
    ConnectedUsbOnly,
    ConnectedBluetoothReady,
    Starting,
    Streaming,
    Stopping,
    Reconnecting,
    Failed
}

public sealed class StreamingStatus
{
    public StreamingState State { get; set; } = StreamingState.Disconnected;
    public string Message { get; set; } = "No controller detected.";
    public ControllerInfo? ActiveController { get; set; }
    public long PacketsSent { get; set; }
    public long BytesSent { get; set; }
    public long FramesEncoded { get; set; }
    public int BufferLevelFrames { get; set; }
    public long UnderrunCount { get; set; }
    public long DroppedFramesCount { get; set; }
    public long EncoderErrorsCount { get; set; }
    public long TransmissionFailuresCount { get; set; }
    public double EstimatedLatencyMs { get; set; }
    public string AudioFormatInfo { get; set; } = "32.0 kHz Stereo (SBC 16 blk / 8 sb / Bitpool 48)";
    public DateTime? StreamStartTime { get; set; }
    public int ControllerBatteryPercent { get; set; } = -1;
    public bool IsControllerCharging { get; set; }
    public AudioOutputRoute ActiveOutputRoute { get; set; } = AudioOutputRoute.Unknown;
}
