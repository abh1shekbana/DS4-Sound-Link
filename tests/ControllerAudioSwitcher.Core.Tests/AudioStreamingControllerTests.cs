using ControllerAudioSwitcher.Core.Interfaces;
using ControllerAudioSwitcher.Core.Models;
using ControllerAudioSwitcher.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ControllerAudioSwitcher.Core.Tests;

[TestClass]
public class AudioStreamingControllerTests
{
    private sealed class MockDiscoveryService : IControllerDiscoveryService
    {
        public ControllerInfo? ActiveBtController { get; set; }
        public event EventHandler<ControllerInfo>? ControllerConnected;
        public event EventHandler<ControllerInfo>? ControllerDisconnected;

        public IReadOnlyList<ControllerInfo> GetConnectedControllers() =>
            ActiveBtController != null ? new[] { ActiveBtController } : Array.Empty<ControllerInfo>();

        public ControllerInfo? GetActiveBluetoothController() => ActiveBtController;

        public void TriggerConnect(ControllerInfo info) => ControllerConnected?.Invoke(this, info);
        public void TriggerDisconnect(ControllerInfo info) => ControllerDisconnected?.Invoke(this, info);
        public void StartDiscovery() { }
        public void StopDiscovery() { }
        public void Dispose() { }
    }

    private sealed class MockCaptureService : IAudioCaptureService
    {
        public bool IsCapturing { get; private set; }
        public bool IsEndpointMuted { get; set; }
#pragma warning disable CS0067
        public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;
        public event EventHandler<string>? CaptureError;
#pragma warning restore CS0067

        public IReadOnlyList<AudioDeviceChoice> EnumerateRenderDevices() =>
            new[] { new AudioDeviceChoice { Id = "test-dev", Name = "Speakers", IsDefault = true } };

        public void StartCapture(string? deviceId = null, bool muteSpeakers = false)
        {
            IsCapturing = true;
            if (muteSpeakers) IsEndpointMuted = true;
        }

        public void StopCapture() => IsCapturing = false;
        public void SetEndpointMute(bool mute, string? deviceId = null) => IsEndpointMuted = mute;
        public void SetSoftwareGain(float gain) { }
        public void Dispose() => StopCapture();
    }

    private sealed class MockSbcEncoder : ISbcAudioEncoder
    {
        public int SamplesPerFrame => 128;
        public int EncodedFrameLength => 109;
        public bool EncodeFrame(short[] left, short[] right, byte[] output)
        {
            Array.Fill<byte>(output, 0x11);
            return true;
        }
        public void Reset() { }
        public void Dispose() { }
    }

    private sealed class MockTransport : IBluetoothAudioTransport
    {
        public bool IsOpen { get; private set; }
        public bool Armed { get; private set; }
        public byte LastVolume { get; private set; }
        public byte LastHeadphoneVolume { get; private set; }
        public byte LastSpeakerVolume { get; private set; }
        public bool LastHeadsetMode { get; private set; } = true;
        public int FramesSentCount { get; private set; }
        public event EventHandler<(int batteryPercent, bool isCharging)>? BatteryReportReceived
        {
            add { }
            remove { }
        }
        public int CurrentBatteryPercent { get; set; } = 85;
        public bool IsCharging { get; set; }

        public bool Open(ControllerInfo controller)
        {
            IsOpen = true;
            return true;
        }

        public void Close()
        {
            IsOpen = false;
            Armed = false;
        }

        public bool ArmAudio(byte headphoneVolume = 110, byte speakerVolume = 0)
        {
            Armed = true;
            LastVolume = headphoneVolume;
            LastHeadphoneVolume = headphoneVolume;
            LastSpeakerVolume = speakerVolume;
            return true;
        }

        public bool DisarmAudio()
        {
            Armed = false;
            return true;
        }

        public bool SendAudioFrames(ushort frameNumber, byte[][] frames, int frameCount, bool headsetMode = true)
        {
            FramesSentCount += frameCount;
            LastHeadsetMode = headsetMode;
            return true;
        }

        public void Dispose() => Close();
    }

    private MockDiscoveryService _discovery = null!;
    private MockCaptureService _capture = null!;
    private MockSbcEncoder _encoder = null!;
    private MockTransport _transport = null!;
    private DiagnosticService _diag = null!;
    private SettingsService _settings = null!;
    private string _tempSettingsFile = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempSettingsFile = Path.Combine(Path.GetTempPath(), $"stream_test_{Guid.NewGuid():N}.json");
        _discovery = new MockDiscoveryService();
        _capture = new MockCaptureService();
        _encoder = new MockSbcEncoder();
        _transport = new MockTransport();
        _diag = new DiagnosticService();
        _settings = new SettingsService(_diag, _tempSettingsFile);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (File.Exists(_tempSettingsFile))
        {
            try { File.Delete(_tempSettingsFile); } catch { }
        }
    }

    [TestMethod]
    public async Task StartStreamingAsync_WithoutCompatibleController_FailsGracefully()
    {
        using var controller = new AudioStreamingController(_discovery, _capture, _encoder, _transport, _settings, _diag);

        _discovery.ActiveBtController = null;
        bool result = await controller.StartStreamingAsync();

        Assert.IsFalse(result);
        Assert.IsFalse(controller.IsStreaming);
        Assert.AreEqual(StreamingState.Disconnected, controller.Status.State);
    }

    [TestMethod]
    public async Task StartStreamingAsync_WithCompatibleController_StartsStreaming()
    {
        using var controller = new AudioStreamingController(_discovery, _capture, _encoder, _transport, _settings, _diag);

        var ds4 = new ControllerInfo
        {
            VendorId = 0x054C,
            ProductId = 0x09CC,
            ConnectionType = ControllerConnectionType.Bluetooth,
            DisplayName = "Sony DualShock 4"
        };
        _discovery.ActiveBtController = ds4;

        bool result = await controller.StartStreamingAsync();

        Assert.IsTrue(result);
        Assert.IsTrue(controller.IsStreaming);
        Assert.IsTrue(_transport.IsOpen);
        Assert.IsTrue(_transport.Armed);
        Assert.IsTrue(_capture.IsCapturing);
        Assert.AreEqual(StreamingState.Streaming, controller.Status.State);

        await controller.StopStreamingAsync();
        Assert.IsFalse(controller.IsStreaming);
        Assert.IsFalse(_capture.IsCapturing);
        Assert.IsFalse(_transport.IsOpen);
    }

    [TestMethod]
    public async Task DisconnectEvent_WhileStreaming_StopsStreamGracefully()
    {
        using var controller = new AudioStreamingController(_discovery, _capture, _encoder, _transport, _settings, _diag);

        var ds4 = new ControllerInfo
        {
            VendorId = 0x054C,
            ProductId = 0x09CC,
            ConnectionType = ControllerConnectionType.Bluetooth,
            DevicePath = @"\\?\hid#vid_054c&pid_09cc#1"
        };
        _discovery.ActiveBtController = ds4;

        await controller.StartStreamingAsync();
        Assert.IsTrue(controller.IsStreaming);

        // Simulate physical disconnect
        _discovery.TriggerDisconnect(ds4);

        // Allow disconnect teardown to complete
        await Task.Delay(50);

        Assert.IsFalse(controller.IsStreaming);
        Assert.IsFalse(_capture.IsCapturing);
        Assert.IsFalse(_transport.IsOpen);
        Assert.AreEqual(StreamingState.Disconnected, controller.Status.State);
    }

    [TestMethod]
    public void SetVolume_UpdatesSettingsAndArmedTransport()
    {
        using var controller = new AudioStreamingController(_discovery, _capture, _encoder, _transport, _settings, _diag);

        controller.SetVolume(95);

        Assert.AreEqual((byte)95, _settings.CurrentSettings.HeadphoneVolume);
    }

    [TestMethod]
    public async Task StopStreamingAsync_ShutsDownCleanlyAndCanRestartRepeatedly()
    {
        using var controller = new AudioStreamingController(_discovery, _capture, _encoder, _transport, _settings, _diag);

        var ds4 = new ControllerInfo
        {
            VendorId = 0x054C,
            ProductId = 0x09CC,
            ConnectionType = ControllerConnectionType.Bluetooth,
            DisplayName = "Sony DualShock 4"
        };
        _discovery.ActiveBtController = ds4;

        // Perform multiple consecutive start/stop cycles
        for (int cycle = 0; cycle < 3; cycle++)
        {
            bool started = await controller.StartStreamingAsync(ds4);
            Assert.IsTrue(started, $"StartStreamingAsync failed on cycle {cycle}");
            Assert.IsTrue(controller.IsStreaming);
            Assert.IsTrue(_transport.IsOpen);

            await controller.StopStreamingAsync();
            Assert.IsFalse(controller.IsStreaming, $"StopStreamingAsync failed on cycle {cycle}");
            Assert.IsFalse(_transport.IsOpen);
            Assert.AreEqual(StreamingState.ConnectedBluetoothReady, controller.Status.State);
        }
    }

    [TestMethod]
    public async Task StopStreamingAsync_ConcurrentInvocations_DoNotDeadlock()
    {
        using var controller = new AudioStreamingController(_discovery, _capture, _encoder, _transport, _settings, _diag);

        var ds4 = new ControllerInfo
        {
            VendorId = 0x054C,
            ProductId = 0x09CC,
            ConnectionType = ControllerConnectionType.Bluetooth,
            DisplayName = "Sony DualShock 4"
        };
        _discovery.ActiveBtController = ds4;

        await controller.StartStreamingAsync(ds4);
        Assert.IsTrue(controller.IsStreaming);

        // Trigger multiple concurrent stop requests
        var stopTask1 = controller.StopStreamingAsync();
        var stopTask2 = controller.StopStreamingAsync();
        var stopTask3 = controller.StopStreamingAsync();

        await Task.WhenAll(stopTask1, stopTask2, stopTask3);

        Assert.IsFalse(controller.IsStreaming);
        Assert.AreEqual(StreamingState.ConnectedBluetoothReady, controller.Status.State);
    }

    [TestMethod]
    public async Task SetOutputDestination_ToSpeaker_MutesHeadphonesAndEnablesSpeaker()
    {
        using var controller = new AudioStreamingController(_discovery, _capture, _encoder, _transport, _settings, _diag);

        var ds4 = new ControllerInfo
        {
            VendorId = 0x054C,
            ProductId = 0x09CC,
            ConnectionType = ControllerConnectionType.Bluetooth,
            DisplayName = "Sony DualShock 4"
        };
        _discovery.ActiveBtController = ds4;
        _settings.CurrentSettings.HeadphoneVolume = 105;

        // Start in speaker mode
        _settings.CurrentSettings.HeadsetMode = false;
        await controller.StartStreamingAsync(ds4);

        // Verify Report 0x11 arming parameters: headphoneVolume = 0, speakerVolume = 105
        Assert.AreEqual((byte)0, _transport.LastHeadphoneVolume, "Headphones must be muted (0) in speaker mode.");
        Assert.AreEqual((byte)105, _transport.LastSpeakerVolume, "Speaker must receive volume in speaker mode.");

        await controller.StopStreamingAsync();
    }

    [TestMethod]
    public async Task SetOutputDestination_ToHeadphones_MutesSpeakerAndEnablesHeadphones()
    {
        using var controller = new AudioStreamingController(_discovery, _capture, _encoder, _transport, _settings, _diag);

        var ds4 = new ControllerInfo
        {
            VendorId = 0x054C,
            ProductId = 0x09CC,
            ConnectionType = ControllerConnectionType.Bluetooth,
            DisplayName = "Sony DualShock 4"
        };
        _discovery.ActiveBtController = ds4;
        _settings.CurrentSettings.HeadphoneVolume = 112;

        // Start in headset mode
        _settings.CurrentSettings.HeadsetMode = true;
        await controller.StartStreamingAsync(ds4);

        // Verify Report 0x11 arming parameters: headphoneVolume = 112, speakerVolume = 0
        Assert.AreEqual((byte)112, _transport.LastHeadphoneVolume, "Headphones must receive volume in headset mode.");
        Assert.AreEqual((byte)0, _transport.LastSpeakerVolume, "Speaker must be muted (0) in headset mode.");

        await controller.StopStreamingAsync();
    }

    [TestMethod]
    public async Task SetOutputDestination_LiveSwitchWhileStreaming_UpdatesTransportWithoutStoppingStream()
    {
        using var controller = new AudioStreamingController(_discovery, _capture, _encoder, _transport, _settings, _diag);

        var ds4 = new ControllerInfo
        {
            VendorId = 0x054C,
            ProductId = 0x09CC,
            ConnectionType = ControllerConnectionType.Bluetooth,
            DisplayName = "Sony DualShock 4"
        };
        _discovery.ActiveBtController = ds4;
        _settings.CurrentSettings.HeadphoneVolume = 98;
        _settings.CurrentSettings.HeadsetMode = true;

        await controller.StartStreamingAsync(ds4);
        Assert.IsTrue(controller.IsStreaming);
        Assert.AreEqual((byte)98, _transport.LastHeadphoneVolume);
        Assert.AreEqual((byte)0, _transport.LastSpeakerVolume);

        // Live switch to speaker while streaming
        await controller.SetOutputDestinationAsync(false);
        Assert.IsTrue(controller.IsStreaming, "Stream must remain active during live switch.");
        Assert.IsFalse(_settings.CurrentSettings.HeadsetMode);
        Assert.AreEqual((byte)0, _transport.LastHeadphoneVolume, "Headphones must be muted after live switch to speaker.");
        Assert.AreEqual((byte)98, _transport.LastSpeakerVolume, "Speaker must be unmuted after live switch to speaker.");

        // Live switch back to headphones
        await controller.SetOutputDestinationAsync(true);
        Assert.IsTrue(controller.IsStreaming, "Stream must remain active during second live switch.");
        Assert.IsTrue(_settings.CurrentSettings.HeadsetMode);
        Assert.AreEqual((byte)98, _transport.LastHeadphoneVolume, "Headphones must be unmuted after live switch back.");
        Assert.AreEqual((byte)0, _transport.LastSpeakerVolume, "Speaker must be muted after live switch back.");
        Assert.AreEqual(AudioOutputRoute.Headphones, controller.Status.ActiveOutputRoute);

        await controller.StopStreamingAsync();
        Assert.IsFalse(controller.IsStreaming);
        Assert.AreEqual(AudioOutputRoute.Disconnected, controller.Status.ActiveOutputRoute);
    }

    [TestMethod]
    public async Task SetOutputDestination_RejectsUnsupportedRouting_WhenCapabilityIsUnsupported()
    {
        using var controller = new AudioStreamingController(_discovery, _capture, _encoder, _transport, _settings, _diag);

        var limitedPad = new ControllerInfo
        {
            VendorId = 0x054C,
            ProductId = 0x09CC,
            ConnectionType = ControllerConnectionType.Bluetooth,
            DisplayName = "Limited Pad",
            Capabilities = new ControllerCapabilities
            {
                BtAudioStreaming = ControllerCapabilityState.Supported,
                InternalSpeakerRouting = ControllerCapabilityState.Unsupported, // Has no internal speaker!
                HeadphoneRouting = ControllerCapabilityState.Supported
            }
        };
        _discovery.ActiveBtController = limitedPad;
        _settings.CurrentSettings.HeadsetMode = true;

        await controller.StartStreamingAsync(limitedPad);
        Assert.IsTrue(controller.IsStreaming);
        Assert.AreEqual(AudioOutputRoute.Headphones, controller.Status.ActiveOutputRoute);

        // Attempt to switch to internal speaker, which is unsupported on this controller
        await controller.SetOutputDestinationAsync(false);

        // Must reject switch and retain headphones as confirmed route
        Assert.AreEqual(AudioOutputRoute.Headphones, controller.Status.ActiveOutputRoute);
        Assert.IsTrue(_settings.CurrentSettings.HeadsetMode);

        await controller.StopStreamingAsync();
    }

    [TestMethod]
    public async Task StartStreamingAsync_WithDualSense_ArmsAndStreamsSuccessfully()
    {
        using var controller = new AudioStreamingController(_discovery, _capture, _encoder, _transport, _settings, _diag);

        var dualSense = new ControllerInfo
        {
            VendorId = 0x054C,
            ProductId = 0x0CE6,
            Model = ControllerModel.DualSense,
            ConnectionType = ControllerConnectionType.Bluetooth,
            DisplayName = "Sony DualSense (PS5)",
            Capabilities = ControllerCapabilities.Create(ControllerModel.DualSense, ControllerConnectionType.Bluetooth)
        };
        _discovery.ActiveBtController = dualSense;
        _settings.CurrentSettings.HeadsetMode = true;

        bool started = await controller.StartStreamingAsync(dualSense);
        Assert.IsTrue(started);
        Assert.IsTrue(controller.IsStreaming);
        Assert.AreEqual(AudioOutputRoute.Headphones, controller.Status.ActiveOutputRoute);

        await controller.StopStreamingAsync();
        Assert.IsFalse(controller.IsStreaming);
        Assert.AreEqual(AudioOutputRoute.Disconnected, controller.Status.ActiveOutputRoute);
    }
}
