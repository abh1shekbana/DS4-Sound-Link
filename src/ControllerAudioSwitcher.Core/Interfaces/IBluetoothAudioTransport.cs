using ControllerAudioSwitcher.Core.Models;

namespace ControllerAudioSwitcher.Core.Interfaces;

public interface IBluetoothAudioTransport : IDisposable
{
    bool IsOpen { get; }
    bool Open(ControllerInfo controller);
    void Close();
    bool ArmAudio(byte headphoneVolume = 110, byte speakerVolume = 0);
    bool DisarmAudio();
    bool SendAudioFrames(ushort frameNumber, byte[][] frames, int frameCount = 4, bool headsetMode = true);

    event EventHandler<(int batteryPercent, bool isCharging)>? BatteryReportReceived;
    int CurrentBatteryPercent { get; }
    bool IsCharging { get; }
}
