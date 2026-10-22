using ControllerAudioSwitcher.Controller;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ControllerAudioSwitcher.Core.Tests;

[TestClass]
public class LocalDiscoveryDiagnostics
{
    [TestMethod]
    public void RunLocalDiscoveryScan()
    {
        var discovery = new ControllerDiscoveryService();
        discovery.RefreshControllers();
        var controllers = discovery.GetConnectedControllers();

        Console.WriteLine($"Discovered count: {controllers.Count}");
        foreach (var c in controllers)
        {
            Console.WriteLine($"Controller: {c.DisplayName}, Connection: {c.ConnectionType}, VID: 0x{c.VendorId:X4}, PID: 0x{c.ProductId:X4}, InputReportLen: {c.InputReportByteLength}, Compatible: {c.IsCompatible}, Path: {c.DevicePath}");
        }

        Assert.IsNotNull(controllers);
    }

    [TestMethod]
    public void InspectAudioDevices()
    {
        var audioService = new ControllerAudioSwitcher.Audio.WasapiAudioCaptureService();
        var devices = audioService.EnumerateRenderDevices();
        Console.WriteLine($"Render devices count: {devices.Count}");
        foreach (var d in devices)
        {
            Console.WriteLine($"Device: '{d.Name}', ID: {d.Id}, IsDefault: {d.IsDefault}");
        }
    }

    [TestMethod]
    public void TestEndpointVolumeAndMute()
    {
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var defaultDev = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        Console.WriteLine($"Default Device: {defaultDev.FriendlyName}, Master Volume: {defaultDev.AudioEndpointVolume.MasterVolumeLevelScalar:P0}, Muted: {defaultDev.AudioEndpointVolume.Mute}");

        bool originalMute = defaultDev.AudioEndpointVolume.Mute;
        try
        {
            // Test toggling mute
            defaultDev.AudioEndpointVolume.Mute = true;
            Console.WriteLine($"Endpoint Muted: {defaultDev.AudioEndpointVolume.Mute}");
            defaultDev.AudioEndpointVolume.Mute = false;
            Console.WriteLine($"Endpoint Restored: {!defaultDev.AudioEndpointVolume.Mute}");
        }
        finally
        {
            defaultDev.AudioEndpointVolume.Mute = originalMute;
        }
    }
}
