namespace ControllerAudioSwitcher.Core.Models;

public sealed class AppSettings
{
    public bool AutoStartOnConnect { get; set; } = true;
    public byte HeadphoneVolume { get; set; } = 110; // Range 0 - 127 (approx 85%)
    public string SelectedAudioDeviceId { get; set; } = string.Empty;
    public bool MinimizeToTray { get; set; } = true;
    public int SampleRate { get; set; } = 32000;
    public bool HeadsetMode { get; set; } = true; // true = 3.5mm jack (0x24), false = speaker (0x02)
    public bool VerboseLogging { get; set; } = false;
    public bool MuteSpeakersWhileStreaming { get; set; } = true; // Mute physical PC speakers during streaming
    public int SoftwareGainPercent { get; set; } = 100; // Range 50 - 200% (software digital gain with soft-knee limiting)
}
