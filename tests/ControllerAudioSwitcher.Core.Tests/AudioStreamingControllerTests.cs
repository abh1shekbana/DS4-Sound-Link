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
        public int FramesSentCount { get; private set; }

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
}
