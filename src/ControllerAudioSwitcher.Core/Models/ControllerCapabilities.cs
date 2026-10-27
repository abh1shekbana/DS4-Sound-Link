namespace ControllerAudioSwitcher.Core.Models;

/// <summary>
/// Explicit capability states distinguishing verified functionality from unsupported or unverified functionality.
/// </summary>
public enum ControllerCapabilityState
{
    Unknown = 0,
    Supported = 1,
    Unsupported = 2,
    NotTested = 3
}

/// <summary>
/// Controller hardware families and models.
/// </summary>
public enum ControllerModel
{
    Unknown = 0,
    DualShock4V1 = 1,
    DualShock4V2 = 2,
    DualShock4Dongle = 3,
    DualShock4CompatibleClone = 4,
    DualSense = 5,
    DualSenseEdge = 6,
    UnknownPlayStation = 7
}

/// <summary>
/// Mutually exclusive output routing state.
/// </summary>
public enum AudioOutputRoute
{
    Unknown = 0,
    Disconnected = 1,
    InternalSpeaker = 2,
    Headphones = 3
}

/// <summary>
/// Fine-grained controller capability matrix.
/// Replaces monolithic boolean capability flags.
/// </summary>
public sealed class ControllerCapabilities
{
    public ControllerCapabilityState BtAudioStreaming { get; set; } = ControllerCapabilityState.Unknown;
    public ControllerCapabilityState UsbAudioStreaming { get; set; } = ControllerCapabilityState.Unknown;
    public ControllerCapabilityState InternalSpeakerRouting { get; set; } = ControllerCapabilityState.Unknown;
    public ControllerCapabilityState HeadphoneRouting { get; set; } = ControllerCapabilityState.Unknown;
    public ControllerCapabilityState JackDetection { get; set; } = ControllerCapabilityState.Unknown;
    public ControllerCapabilityState BatteryReporting { get; set; } = ControllerCapabilityState.Unknown;
    public ControllerCapabilityState HapticOutput { get; set; } = ControllerCapabilityState.Unknown;

    public bool CanStreamAudioWireless => BtAudioStreaming == ControllerCapabilityState.Supported;
    public bool CanStreamAudioWired => UsbAudioStreaming == ControllerCapabilityState.Supported;
    public bool SupportsExclusiveRouting => 
        InternalSpeakerRouting == ControllerCapabilityState.Supported && 
        HeadphoneRouting == ControllerCapabilityState.Supported;

    /// <summary>
    /// Creates capability profile based on verified controller model and transport.
    /// </summary>
    public static ControllerCapabilities Create(ControllerModel model, ControllerConnectionType connection)
    {
        var caps = new ControllerCapabilities();

        switch (model)
        {
            case ControllerModel.DualShock4V1:
            case ControllerModel.DualShock4V2:
                if (connection == ControllerConnectionType.Bluetooth)
                {
                    caps.BtAudioStreaming = ControllerCapabilityState.Supported;
                    caps.UsbAudioStreaming = ControllerCapabilityState.Unsupported;
                    caps.InternalSpeakerRouting = ControllerCapabilityState.Supported;
                    caps.HeadphoneRouting = ControllerCapabilityState.Supported;
                    caps.JackDetection = ControllerCapabilityState.Unsupported; // DS4 standard BT report does not expose reliable jack detect
                    caps.BatteryReporting = ControllerCapabilityState.Supported;
                    caps.HapticOutput = ControllerCapabilityState.Supported; // Standard classic rumble
                }
                else if (connection == ControllerConnectionType.Usb)
                {
                    caps.BtAudioStreaming = ControllerCapabilityState.Unsupported;
                    caps.UsbAudioStreaming = ControllerCapabilityState.Supported; // Exposed via standard Windows USB Audio endpoint
                    caps.InternalSpeakerRouting = ControllerCapabilityState.Unsupported; // Routed by Windows endpoint
                    caps.HeadphoneRouting = ControllerCapabilityState.Supported;
                    caps.JackDetection = ControllerCapabilityState.Unsupported;
                    caps.BatteryReporting = ControllerCapabilityState.Supported;
                    caps.HapticOutput = ControllerCapabilityState.Supported;
                }
                break;

            case ControllerModel.DualShock4Dongle:
                caps.BtAudioStreaming = ControllerCapabilityState.Unsupported;
                caps.UsbAudioStreaming = ControllerCapabilityState.Supported; // Wireless adapter presents as USB audio device
                caps.InternalSpeakerRouting = ControllerCapabilityState.Unsupported;
                caps.HeadphoneRouting = ControllerCapabilityState.Supported;
                caps.JackDetection = ControllerCapabilityState.Unsupported;
                caps.BatteryReporting = ControllerCapabilityState.Supported;
                caps.HapticOutput = ControllerCapabilityState.Supported;
                break;

            case ControllerModel.DualShock4CompatibleClone:
                if (connection == ControllerConnectionType.Bluetooth)
                {
                    // Clones generally fail vendor SBC audio arming commands (Report 0x11/0x17)
                    caps.BtAudioStreaming = ControllerCapabilityState.NotTested;
                    caps.UsbAudioStreaming = ControllerCapabilityState.Unsupported;
                    caps.InternalSpeakerRouting = ControllerCapabilityState.NotTested;
                    caps.HeadphoneRouting = ControllerCapabilityState.NotTested;
                    caps.JackDetection = ControllerCapabilityState.Unsupported;
                    caps.BatteryReporting = ControllerCapabilityState.NotTested;
                    caps.HapticOutput = ControllerCapabilityState.Supported;
                }
                else
                {
                    caps.BtAudioStreaming = ControllerCapabilityState.Unsupported;
                    caps.UsbAudioStreaming = ControllerCapabilityState.NotTested;
                    caps.InternalSpeakerRouting = ControllerCapabilityState.Unsupported;
                    caps.HeadphoneRouting = ControllerCapabilityState.NotTested;
                    caps.JackDetection = ControllerCapabilityState.Unsupported;
                    caps.BatteryReporting = ControllerCapabilityState.NotTested;
                    caps.HapticOutput = ControllerCapabilityState.Supported;
                }
                break;

            case ControllerModel.DualSense:
            case ControllerModel.DualSenseEdge:
                if (connection == ControllerConnectionType.Bluetooth)
                {
                    caps.BtAudioStreaming = ControllerCapabilityState.Supported; // Opus Report 0x39 / Report 0x31
                    caps.UsbAudioStreaming = ControllerCapabilityState.Unsupported;
                    caps.InternalSpeakerRouting = ControllerCapabilityState.Supported;
                    caps.HeadphoneRouting = ControllerCapabilityState.Supported;
                    caps.JackDetection = ControllerCapabilityState.Supported; // DS_STATUS1_HP_DETECT bit 0
                    caps.BatteryReporting = ControllerCapabilityState.Supported;
                    caps.HapticOutput = ControllerCapabilityState.Supported; // Voice-coil haptics
                }
                else if (connection == ControllerConnectionType.Usb)
                {
                    caps.BtAudioStreaming = ControllerCapabilityState.Unsupported;
                    caps.UsbAudioStreaming = ControllerCapabilityState.Supported; // 4-channel 48kHz USB Audio
                    caps.InternalSpeakerRouting = ControllerCapabilityState.Supported;
                    caps.HeadphoneRouting = ControllerCapabilityState.Supported;
                    caps.JackDetection = ControllerCapabilityState.Supported;
                    caps.BatteryReporting = ControllerCapabilityState.Supported;
                    caps.HapticOutput = ControllerCapabilityState.Supported;
                }
                break;

            case ControllerModel.UnknownPlayStation:
            default:
                caps.BtAudioStreaming = ControllerCapabilityState.Unknown;
                caps.UsbAudioStreaming = ControllerCapabilityState.Unknown;
                caps.InternalSpeakerRouting = ControllerCapabilityState.Unknown;
                caps.HeadphoneRouting = ControllerCapabilityState.Unknown;
                caps.JackDetection = ControllerCapabilityState.Unknown;
                caps.BatteryReporting = ControllerCapabilityState.Unknown;
                caps.HapticOutput = ControllerCapabilityState.Unknown;
                break;
        }

        return caps;
    }
}
