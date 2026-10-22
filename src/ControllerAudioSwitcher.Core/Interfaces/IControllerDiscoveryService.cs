using ControllerAudioSwitcher.Core.Models;

namespace ControllerAudioSwitcher.Core.Interfaces;

public interface IControllerDiscoveryService : IDisposable
{
    event EventHandler<ControllerInfo>? ControllerConnected;
    event EventHandler<ControllerInfo>? ControllerDisconnected;

    IReadOnlyList<ControllerInfo> GetConnectedControllers();
    void StartDiscovery();
    void StopDiscovery();
    ControllerInfo? GetActiveBluetoothController();
}
