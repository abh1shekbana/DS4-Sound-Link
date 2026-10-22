using System.Collections.Concurrent;
using System.Diagnostics;
using ControllerAudioSwitcher.Core.Interfaces;
using ControllerAudioSwitcher.Core.Models;
using ControllerAudioSwitcher.Core.Native;
using ControllerAudioSwitcher.Core.Protocol;

namespace ControllerAudioSwitcher.Core.Services;

/// <summary>
/// Core audio streaming coordinator between WASAPI Loopback capture,
/// SBC frame encoder, and DualShock 4 Bluetooth HID transport.
/// Implements sub-millisecond timer resolution, double-buffered jitter absorption,
/// and underrun hysteresis for jitter-free real-time audio playback.
/// </summary>
public sealed class AudioStreamingController : IAudioStreamingController
{
    private readonly IControllerDiscoveryService _discoveryService;
    private readonly IAudioCaptureService _captureService;
    private readonly ISbcAudioEncoder _encoder;
    private readonly IBluetoothAudioTransport _transport;
    private readonly ISettingsService _settingsService;
    private readonly IDiagnosticService _diagnosticService;

    private readonly object _stateLock = new();
    private readonly StreamingStatus _status = new();
    private CancellationTokenSource? _streamCts;
    private Thread? _transmissionThread;

    private readonly ConcurrentQueue<byte[]> _encodedFrameQueue = new();
    private const int MaxQueuedFrames = 96;       // ~384 ms buffer ceiling to keep latency low
    private const int PrimeTargetFrames = 20;     // ~80 ms initial prebuffer to absorb jitter
    private const int RePrimeTargetFrames = 12;   // ~48 ms prebuffer after an underrun

    // Intermediate PCM accumulation buffer
    private readonly object _pcmLock = new();
    private readonly List<short> _pendingLeft = new(4096);
    private readonly List<short> _pendingRight = new(4096);

    // Precomputed and cached silence frame (Joint stereo, SNR, 32kHz, bitpool 48 = 109 bytes)
    private static readonly byte[] StaticSilenceFrame = PrecomputeSilenceFrame();

    public event EventHandler<StreamingStatus>? StatusChanged;

    public StreamingStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                return _status;
            }
        }
    }

    public bool IsStreaming
    {
        get
        {
            lock (_stateLock)
            {
                return _status.State == StreamingState.Streaming;
            }
        }
    }

    public AudioStreamingController(
        IControllerDiscoveryService discoveryService,
        IAudioCaptureService captureService,
        ISbcAudioEncoder encoder,
        IBluetoothAudioTransport transport,
        ISettingsService settingsService,
        IDiagnosticService diagnosticService)
    {
        _discoveryService = discoveryService;
        _captureService = captureService;
        _encoder = encoder;
        _transport = transport;
        _settingsService = settingsService;
        _diagnosticService = diagnosticService;

        _discoveryService.ControllerConnected += OnControllerConnected;
        _discoveryService.ControllerDisconnected += OnControllerDisconnected;
        _captureService.SamplesAvailable += OnCaptureSamplesAvailable;
        _captureService.CaptureError += OnCaptureError;
    }

    private void UpdateStatus(Action<StreamingStatus> updateAction)
    {
        lock (_stateLock)
        {
            updateAction(_status);
        }
        StatusChanged?.Invoke(this, Status);
    }

    private void OnControllerConnected(object? sender, ControllerInfo controller)
    {
        _diagnosticService.Info($"Controller detected: {controller.DisplayName} via {controller.ConnectionType}");

        if (controller.IsCompatible)
        {
            UpdateStatus(s =>
            {
                s.ActiveController = controller;
                if (s.State != StreamingState.Streaming)
                {
                    s.State = StreamingState.ConnectedBluetoothReady;
                    s.Message = "DualShock 4 ready for Bluetooth audio streaming.";
                }
            });

            if (_settingsService.CurrentSettings.AutoStartOnConnect && !IsStreaming)
            {
                _diagnosticService.Info("Auto-starting audio stream for connected DualShock 4...");
                _ = StartStreamingAsync(controller);
            }
        }
        else
        {
            UpdateStatus(s =>
            {
                s.ActiveController = controller;
                if (controller.ConnectionType == ControllerConnectionType.Usb)
                {
                    s.State = StreamingState.ConnectedUsbOnly;
                    s.Message = "Connected via USB. For wireless streaming, connect via Windows Bluetooth.";
                }
                else
                {
                    s.State = StreamingState.Disconnected;
                    s.Message = $"Controller connected ({controller.ConnectionType}), but does not support Bluetooth audio.";
                }
            });
        }
    }

    private void OnControllerDisconnected(object? sender, ControllerInfo controller)
    {
        _diagnosticService.Warn($"Controller disconnected: {controller.DisplayName}");

        if (ReferenceEquals(_status.ActiveController, controller) || 
            string.Equals(_status.ActiveController?.DevicePath, controller.DevicePath, StringComparison.OrdinalIgnoreCase))
        {
            _ = StopStreamingAsync();

            UpdateStatus(s =>
            {
                s.ActiveController = null;
                s.State = StreamingState.Disconnected;
                s.Message = "DualShock 4 disconnected.";
            });
        }
    }

    public async Task<bool> StartStreamingAsync(ControllerInfo? targetController = null)
    {
        targetController ??= _discoveryService.GetActiveBluetoothController();
        if (targetController == null || !targetController.IsCompatible)
        {
            UpdateStatus(s =>
            {
                s.State = StreamingState.Disconnected;
                s.Message = "No compatible Bluetooth DualShock 4 available.";
            });
            _diagnosticService.Warn("Cannot start streaming: no compatible Bluetooth controller found.");
            return false;
        }

        await StopStreamingAsync();

        UpdateStatus(s =>
        {
            s.State = StreamingState.Starting;
            s.Message = "Initializing transport and arming controller audio...";
            s.ActiveController = targetController;
            s.PacketsSent = 0;
            s.BytesSent = 0;
            s.FramesEncoded = 0;
            s.UnderrunCount = 0;
            s.StreamStartTime = DateTime.Now;
        });

        // Open HID transport
        if (!_transport.Open(targetController))
        {
            UpdateStatus(s =>
            {
                s.State = StreamingState.Failed;
                s.Message = "Failed to open HID communication handle to controller.";
            });
            _diagnosticService.Error("Failed to open HID transport.");
            return false;
        }

        // Arm audio plane with Report 0x11
        byte volume = _settingsService.CurrentSettings.HeadphoneVolume;
        if (!_transport.ArmAudio(volume))
        {
            _transport.Close();
            UpdateStatus(s =>
            {
                s.State = StreamingState.Failed;
                s.Message = "Failed to arm DualShock 4 audio hardware.";
            });
            _diagnosticService.Error("ArmAudio failed.");
            return false;
        }

        // Clear buffers
        lock (_pcmLock)
        {
            _pendingLeft.Clear();
            _pendingRight.Clear();
        }
        _encodedFrameQueue.Clear();
        _encoder.Reset();

        // Start WASAPI Capture (with optional speaker muting for headphone isolation)
        try
        {
            string? deviceId = string.IsNullOrEmpty(_settingsService.CurrentSettings.SelectedAudioDeviceId)
                ? null
                : _settingsService.CurrentSettings.SelectedAudioDeviceId;

            bool muteSpeakers = _settingsService.CurrentSettings.MuteSpeakersWhileStreaming;
            _captureService.StartCapture(deviceId, muteSpeakers);
            if (muteSpeakers)
            {
                _diagnosticService.Info("PC speakers muted for headphone isolation (loopback capture active).");
            }
        }
        catch (Exception ex)
        {
            _transport.DisarmAudio();
            _transport.Close();
            UpdateStatus(s =>
            {
                s.State = StreamingState.Failed;
                s.Message = $"Audio capture failed: {ex.Message}";
            });
            _diagnosticService.Error("Failed to start WASAPI audio capture.", ex);
            return false;
        }

        // Start dedicated real-time transmission thread
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        _transmissionThread = new Thread(() => TransmissionLoop(ct))
        {
            IsBackground = true,
            Name = "DualShock4-Audio-Transmitter",
            Priority = ThreadPriority.AboveNormal
        };
        _transmissionThread.Start();

        UpdateStatus(s =>
        {
            s.State = StreamingState.Streaming;
            s.Message = "Streaming audio to 3.5 mm headphone jack active.";
        });
        _diagnosticService.Info("DualShock 4 audio streaming started successfully.");
        return true;
    }

    public Task StopStreamingAsync()
    {
        if (_streamCts != null)
        {
            _streamCts.Cancel();
            _streamCts.Dispose();
            _streamCts = null;
        }

        if (_transmissionThread != null)
        {
            _transmissionThread.Join(500);
            _transmissionThread = null;
        }

        _captureService.StopCapture();

        if (_transport.IsOpen)
        {
            _transport.DisarmAudio();
            _transport.Close();
        }

        lock (_pcmLock)
        {
            _pendingLeft.Clear();
            _pendingRight.Clear();
        }
        _encodedFrameQueue.Clear();

        UpdateStatus(s =>
        {
            if (s.State == StreamingState.Streaming)
            {
                s.State = s.ActiveController?.IsCompatible == true 
                    ? StreamingState.ConnectedBluetoothReady 
                    : StreamingState.Disconnected;
                s.Message = "Audio streaming stopped.";
            }
        });

        _diagnosticService.Info("DualShock 4 audio streaming stopped.");
        return Task.CompletedTask;
    }

    public void SetVolume(byte volume)
    {
        _settingsService.CurrentSettings.HeadphoneVolume = volume;
        _settingsService.Save();

        if (IsStreaming)
        {
            _transport.ArmAudio(volume);
            _diagnosticService.Debug($"Volume updated to {volume}");
        }
    }

    private void OnCaptureSamplesAvailable(object? sender, AudioSamplesEventArgs e)
    {
        if (!IsStreaming) return;

        lock (_pcmLock)
        {
            for (int i = 0; i < e.SampleCount; i++)
            {
                _pendingLeft.Add(e.LeftSamples[i]);
                _pendingRight.Add(e.RightSamples[i]);
            }

            int frameSize = _encoder.SamplesPerFrame; // 128
            int availableFrames = _pendingLeft.Count / frameSize;
            if (availableFrames == 0) return;

            var leftChunk = new short[frameSize];
            var rightChunk = new short[frameSize];
            int readOffset = 0;

            for (int f = 0; f < availableFrames; f++)
            {
                _pendingLeft.CopyTo(readOffset, leftChunk, 0, frameSize);
                _pendingRight.CopyTo(readOffset, rightChunk, 0, frameSize);
                readOffset += frameSize;

                var frameBuffer = new byte[_encoder.EncodedFrameLength]; // 109 bytes
                if (_encoder.EncodeFrame(leftChunk, rightChunk, frameBuffer))
                {
                    if (_encodedFrameQueue.Count < MaxQueuedFrames)
                    {
                        _encodedFrameQueue.Enqueue(frameBuffer);
                        UpdateStatus(s => s.FramesEncoded++);
                    }
                    else
                    {
                        // Queue saturated: drop oldest frame to maintain low latency bounded
                        _encodedFrameQueue.TryDequeue(out _);
                        _encodedFrameQueue.Enqueue(frameBuffer);
                        UpdateStatus(s => s.UnderrunCount++);
                    }
                }
            }

            _pendingLeft.RemoveRange(0, readOffset);
            _pendingRight.RemoveRange(0, readOffset);
        }
    }

    private void TransmissionLoop(CancellationToken ct)
    {
        ushort frameNumber = 0;
        var batch = new byte[DualShock4BluetoothAudioProtocol.FramesPerStandardReport][];
        bool primed = false;

        var cadenceMs = DualShock4BluetoothAudioProtocol.StandardReportCadenceMs; // exactly 16 ms
        long frequency = Stopwatch.Frequency;
        long ticksPerMs = frequency / 1000;
        long cadenceTicks = (frequency * cadenceMs) / 1000;

        // Activate 1.0 ms Windows timer resolution for the duration of the stream
        using var timerScope = new WindowsMultimediaTimerScope(1);

        long nextSendTicks = Stopwatch.GetTimestamp() + cadenceTicks;

        while (!ct.IsCancellationRequested)
        {
            // High-precision hybrid sleep + spin wait
            long nowTicks = Stopwatch.GetTimestamp();
            long waitTicks = nextSendTicks - nowTicks;

            if (waitTicks > 0)
            {
                long waitMs = waitTicks / ticksPerMs;
                if (waitMs > 2)
                {
                    Thread.Sleep((int)(waitMs - 1));
                }

                while (Stopwatch.GetTimestamp() < nextSendTicks)
                {
                    Thread.SpinWait(20);
                }
            }

            nextSendTicks += cadenceTicks;

            nowTicks = Stopwatch.GetTimestamp();
            if (nowTicks > nextSendTicks + (2 * cadenceTicks))
            {
                // Drift detected; resynchronize deadline
                nextSendTicks = nowTicks + cadenceTicks;
            }

            // Jitter buffer priming and underrun hysteresis check
            int targetPrime = (Status.PacketsSent == 0) ? PrimeTargetFrames : RePrimeTargetFrames;
            if (!primed)
            {
                if (_encodedFrameQueue.Count >= targetPrime)
                {
                    primed = true;
                    _diagnosticService.Debug($"Jitter buffer primed ({_encodedFrameQueue.Count} frames). Resuming audio transmission.");
                }
                else
                {
                    // While waiting for buffer to fill, send silence to keep the DS4 audio DAC active
                    batch[0] = StaticSilenceFrame;
                    batch[1] = StaticSilenceFrame;
                    batch[2] = StaticSilenceFrame;
                    batch[3] = StaticSilenceFrame;

                    bool ok = _transport.SendAudioFrames(frameNumber, batch, 4, _settingsService.CurrentSettings.HeadsetMode);
                    if (ok)
                    {
                        frameNumber += (ushort)DualShock4BluetoothAudioProtocol.FramesPerStandardReport;
                        UpdateStatus(s =>
                        {
                            s.PacketsSent++;
                            s.BytesSent += DualShock4BluetoothAudioProtocol.AudioStreamReportLength;
                            s.BufferLevelFrames = _encodedFrameQueue.Count;
                            s.EstimatedLatencyMs = (_encodedFrameQueue.Count * 4.0) + 16.0;
                        });
                    }
                    continue;
                }
            }

            // Dequeue 4 audio frames
            int dequeued = 0;
            while (dequeued < DualShock4BluetoothAudioProtocol.FramesPerStandardReport)
            {
                if (_encodedFrameQueue.TryDequeue(out var frame))
                {
                    batch[dequeued++] = frame;
                }
                else
                {
                    break;
                }
            }

            if (dequeued < DualShock4BluetoothAudioProtocol.FramesPerStandardReport)
            {
                // Underrun occurred! Fill missing slots with silence and enter re-priming
                UpdateStatus(s => s.UnderrunCount++);
                _diagnosticService.Debug($"Buffer underrun: only {dequeued}/4 frames available. Re-priming jitter buffer.");

                while (dequeued < DualShock4BluetoothAudioProtocol.FramesPerStandardReport)
                {
                    batch[dequeued++] = StaticSilenceFrame;
                }

                primed = false;
            }

            bool headsetMode = _settingsService.CurrentSettings.HeadsetMode;
            bool success = _transport.SendAudioFrames(frameNumber, batch, 4, headsetMode);
            if (success)
            {
                frameNumber += (ushort)DualShock4BluetoothAudioProtocol.FramesPerStandardReport;
                UpdateStatus(s =>
                {
                    s.PacketsSent++;
                    s.BytesSent += DualShock4BluetoothAudioProtocol.AudioStreamReportLength;
                    s.BufferLevelFrames = _encodedFrameQueue.Count;
                    s.EstimatedLatencyMs = (_encodedFrameQueue.Count * 4.0) + 16.0;
                });
            }
            else
            {
                _diagnosticService.Warn("Transmission write failed; controller connection dropped or busy.");
                if (!_transport.IsOpen)
                {
                    _diagnosticService.Error("Transport closed unexpectedly during streaming.");
                    break;
                }
            }
        }
    }

    private static byte[] PrecomputeSilenceFrame()
    {
        var frame = new byte[DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength];
        var encoder = new Codec.DualShock4SbcEncoder(32000);
        var zero = new short[128];
        encoder.EncodeFrame(zero, zero, frame);
        return frame;
    }

    private void OnCaptureError(object? sender, string errorMessage)
    {
        _diagnosticService.Error($"Capture error: {errorMessage}");
        UpdateStatus(s => s.Message = errorMessage);
    }

    public void Dispose()
    {
        _ = StopStreamingAsync();
        _discoveryService.ControllerConnected -= OnControllerConnected;
        _discoveryService.ControllerDisconnected -= OnControllerDisconnected;
        _captureService.SamplesAvailable -= OnCaptureSamplesAvailable;
        _captureService.CaptureError -= OnCaptureError;
    }
}
