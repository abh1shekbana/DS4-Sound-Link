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
/// underrun hysteresis, and thread-safe non-blocking shutdown coordination.
/// </summary>
public sealed class AudioStreamingController : IAudioStreamingController
{
    private readonly IControllerDiscoveryService _discoveryService;
    private readonly IAudioCaptureService _captureService;
    private readonly ISbcAudioEncoder _encoder;
    private readonly IBluetoothAudioTransport _transport;
    private readonly ISettingsService _settingsService;
    private readonly IDiagnosticService _diagnosticService;

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly StreamingStatus _status = new();
    private CancellationTokenSource? _streamCts;
    private Thread? _transmissionThread;
    private volatile bool _isStopping;
    private volatile bool _activeHeadsetMode = true;

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

        _activeHeadsetMode = _settingsService.CurrentSettings.HeadsetMode;

        _discoveryService.ControllerConnected += OnControllerConnected;
        _discoveryService.ControllerDisconnected += OnControllerDisconnected;
        _captureService.SamplesAvailable += OnCaptureSamplesAvailable;
        _captureService.CaptureError += OnCaptureError;
        _transport.BatteryReportReceived += OnTransportBatteryReportReceived;
    }

    private void OnTransportBatteryReportReceived(object? sender, (int batteryPercent, bool isCharging) e)
    {
        UpdateStatus(s =>
        {
            s.ControllerBatteryPercent = e.batteryPercent;
            s.IsControllerCharging = e.isCharging;
            if (s.ActiveController != null)
            {
                s.ActiveController.BatteryPercent = e.batteryPercent;
                s.ActiveController.IsCharging = e.isCharging;
            }
        });
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
                if (controller.BatteryPercent >= 0)
                {
                    s.ControllerBatteryPercent = controller.BatteryPercent;
                    s.IsControllerCharging = controller.IsCharging;
                }
                if (s.State != StreamingState.Streaming && s.State != StreamingState.Stopping)
                {
                    s.State = StreamingState.ConnectedBluetoothReady;
                    s.Message = "DualShock 4 ready for Bluetooth audio streaming.";
                }
            });

            if (_settingsService.CurrentSettings.AutoStartOnConnect && !IsStreaming && !_isStopping)
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
        await _lifecycleLock.WaitAsync();
        try
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

            if (IsStreaming)
            {
                return true;
            }

            UpdateStatus(s =>
            {
                s.State = StreamingState.Starting;
                s.Message = "Initializing transport and arming controller audio...";
                s.ActiveController = targetController;
                s.PacketsSent = 0;
                s.BytesSent = 0;
                s.FramesEncoded = 0;
                s.UnderrunCount = 0;
                s.DroppedFramesCount = 0;
                s.EncoderErrorsCount = 0;
                s.TransmissionFailuresCount = 0;
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
            bool headsetMode = _settingsService.CurrentSettings.HeadsetMode;
            _activeHeadsetMode = headsetMode;

            // Dual-layer isolation: mute inactive output destination
            byte hpVol = headsetMode ? volume : (byte)0;
            byte spkVol = headsetMode ? (byte)0 : volume;

            if (!_transport.ArmAudio(hpVol, spkVol))
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
            while (_encodedFrameQueue.TryDequeue(out _)) { }
            _encoder.Reset();

            // Set initial software gain
            float gain = Math.Clamp(_settingsService.CurrentSettings.SoftwareGainPercent / 100.0f, 0.5f, 2.5f);
            _captureService.SetSoftwareGain(gain);

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
                s.Message = $"Streaming audio to {(headsetMode ? "AUX / Headphone Jack" : "Internal Speaker")} active.";
            });
            _diagnosticService.Info($"DualShock 4 audio streaming started successfully (Destination: {(headsetMode ? "AUX / Headphone Jack" : "Internal Speaker")}).");
            return true;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopStreamingAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            lock (_stateLock)
            {
                if (_status.State == StreamingState.Stopping ||
                    (_status.State != StreamingState.Streaming && _status.State != StreamingState.Starting))
                {
                    return;
                }
            }

            UpdateStatus(s =>
            {
                s.State = StreamingState.Stopping;
                s.Message = "Stopping audio streaming and releasing pipeline...";
            });

            _isStopping = true;

            // 1. Signal cancellation to the transmission loop immediately
            _streamCts?.Cancel();

            // 2. Stop WASAPI capture asynchronously
            await Task.Run(() => _captureService.StopCapture());

            // 3. Await transmission worker thread completion asynchronously without blocking UI thread
            if (_transmissionThread != null)
            {
                var worker = _transmissionThread;
                _transmissionThread = null;
                await Task.Run(() =>
                {
                    if (!worker.Join(1500))
                    {
                        _diagnosticService.Warn("Transmission thread did not terminate within timeout.");
                    }
                });
            }

            _streamCts?.Dispose();
            _streamCts = null;

            // 4. Disarm audio hardware and close HID transport
            await Task.Run(() =>
            {
                if (_transport.IsOpen)
                {
                    _transport.DisarmAudio();
                    _transport.Close();
                }
            });

            // 5. Cleanly drain audio buffers
            lock (_pcmLock)
            {
                _pendingLeft.Clear();
                _pendingRight.Clear();
            }
            while (_encodedFrameQueue.TryDequeue(out _)) { }
            _encoder.Reset();

            _isStopping = false;

            UpdateStatus(s =>
            {
                s.State = s.ActiveController?.IsCompatible == true 
                    ? StreamingState.ConnectedBluetoothReady 
                    : StreamingState.Disconnected;
                s.Message = "Audio streaming stopped.";
                s.BufferLevelFrames = 0;
                s.EstimatedLatencyMs = 0;
            });

            _diagnosticService.Info("DualShock 4 audio streaming stopped successfully.");
        }
        catch (Exception ex)
        {
            _diagnosticService.Error($"Error in StopStreamingAsync: {ex.Message}", ex);
            UpdateStatus(s =>
            {
                s.State = s.ActiveController?.IsCompatible == true 
                    ? StreamingState.ConnectedBluetoothReady 
                    : StreamingState.Disconnected;
                s.Message = "Audio streaming stopped with warnings.";
            });
        }
        finally
        {
            _isStopping = false;
            _lifecycleLock.Release();
        }
    }

    public void SetVolume(byte volume)
    {
        _settingsService.CurrentSettings.HeadphoneVolume = volume;
        _settingsService.Save();

        if (IsStreaming && _transport.IsOpen)
        {
            bool headset = _activeHeadsetMode;
            byte hpVol = headset ? volume : (byte)0;
            byte spkVol = headset ? (byte)0 : volume;

            Task.Run(() =>
            {
                lock (_stateLock)
                {
                    if (_transport.IsOpen)
                    {
                        _transport.ArmAudio(hpVol, spkVol);
                    }
                }
            });
            _diagnosticService.Debug($"Volume updated to {volume} (Headset={headset})");
        }
    }

    public async Task SetOutputDestinationAsync(bool headsetMode)
    {
        _settingsService.CurrentSettings.HeadsetMode = headsetMode;
        _settingsService.Save();

        _activeHeadsetMode = headsetMode;

        if (IsStreaming && _transport.IsOpen)
        {
            byte volume = _settingsService.CurrentSettings.HeadphoneVolume;
            byte hpVol = headsetMode ? volume : (byte)0;
            byte spkVol = headsetMode ? (byte)0 : volume;

            await Task.Run(() =>
            {
                lock (_stateLock)
                {
                    if (_transport.IsOpen)
                    {
                        _transport.ArmAudio(hpVol, spkVol);
                    }
                }
            });

            _diagnosticService.Info($"Output destination switched live to: {(headsetMode ? "AUX / Headphone Jack (0x24)" : "Internal Speaker (0x02)")}");
        }
    }

    public void SetSoftwareGain(float gain)
    {
        _settingsService.CurrentSettings.SoftwareGainPercent = (int)Math.Round(gain * 100);
        _settingsService.Save();
        _captureService.SetSoftwareGain(gain);
        _diagnosticService.Debug($"Software gain updated to {gain:F2}x");
    }

    private void OnCaptureSamplesAvailable(object? sender, AudioSamplesEventArgs e)
    {
        if (!IsStreaming || _isStopping) return;

        lock (_pcmLock)
        {
            bool isSpeaker = !_activeHeadsetMode;

            for (int i = 0; i < e.SampleCount; i++)
            {
                short left = e.LeftSamples[i];
                short right = e.RightSamples[i];

                if (isSpeaker)
                {
                    // Clean mono downmix for internal controller speaker
                    short mono = (short)((left + right) / 2);
                    _pendingLeft.Add(mono);
                    _pendingRight.Add(mono);
                }
                else
                {
                    _pendingLeft.Add(left);
                    _pendingRight.Add(right);
                }
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
                        lock (_stateLock)
                        {
                            _status.FramesEncoded++;
                        }
                    }
                    else
                    {
                        // Queue saturated: drop oldest frame to maintain bounded low latency
                        _encodedFrameQueue.TryDequeue(out _);
                        _encodedFrameQueue.Enqueue(frameBuffer);
                        lock (_stateLock)
                        {
                            _status.DroppedFramesCount++;
                        }
                    }
                }
                else
                {
                    lock (_stateLock)
                    {
                        _status.EncoderErrorsCount++;
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
                    // Instant wake-up upon cancellation token signaled
                    if (ct.WaitHandle.WaitOne((int)(waitMs - 1)))
                    {
                        break;
                    }
                }

                while (Stopwatch.GetTimestamp() < nextSendTicks)
                {
                    if (ct.IsCancellationRequested) break;
                    Thread.SpinWait(20);
                }
            }

            if (ct.IsCancellationRequested) break;

            nextSendTicks += cadenceTicks;

            nowTicks = Stopwatch.GetTimestamp();
            if (nowTicks > nextSendTicks + (2 * cadenceTicks))
            {
                // Drift detected; resynchronize deadline
                nextSendTicks = nowTicks + cadenceTicks;
            }

            // Jitter buffer priming and underrun hysteresis check
            int targetPrime = (_status.PacketsSent == 0) ? PrimeTargetFrames : RePrimeTargetFrames;
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

                    bool ok = _transport.SendAudioFrames(frameNumber, batch, 4, _activeHeadsetMode);
                    if (ok)
                    {
                        frameNumber += (ushort)DualShock4BluetoothAudioProtocol.FramesPerStandardReport;
                        lock (_stateLock)
                        {
                            _status.PacketsSent++;
                            _status.BytesSent += DualShock4BluetoothAudioProtocol.AudioStreamReportLength;
                            _status.BufferLevelFrames = _encodedFrameQueue.Count;
                            _status.EstimatedLatencyMs = (_encodedFrameQueue.Count * 4.0) + 16.0;
                        }
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
                lock (_stateLock)
                {
                    _status.UnderrunCount++;
                }
                _diagnosticService.Debug($"Buffer underrun: only {dequeued}/4 frames available. Re-priming jitter buffer.");

                while (dequeued < DualShock4BluetoothAudioProtocol.FramesPerStandardReport)
                {
                    batch[dequeued++] = StaticSilenceFrame;
                }

                primed = false;
            }

            bool headsetMode = _activeHeadsetMode;
            bool success = _transport.SendAudioFrames(frameNumber, batch, 4, headsetMode);
            if (success)
            {
                frameNumber += (ushort)DualShock4BluetoothAudioProtocol.FramesPerStandardReport;
                lock (_stateLock)
                {
                    _status.PacketsSent++;
                    _status.BytesSent += DualShock4BluetoothAudioProtocol.AudioStreamReportLength;
                    _status.BufferLevelFrames = _encodedFrameQueue.Count;
                    _status.EstimatedLatencyMs = (_encodedFrameQueue.Count * 4.0) + 16.0;
                }
            }
            else
            {
                lock (_stateLock)
                {
                    _status.TransmissionFailuresCount++;
                }
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
        _lifecycleLock.Dispose();
    }
}
