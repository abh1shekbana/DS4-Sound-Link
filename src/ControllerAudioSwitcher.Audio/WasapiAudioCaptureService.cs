using System.Runtime.InteropServices;
using ControllerAudioSwitcher.Audio.Resampling;
using ControllerAudioSwitcher.Core.Interfaces;
using ControllerAudioSwitcher.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ControllerAudioSwitcher.Audio;

public sealed class WasapiAudioCaptureService : IAudioCaptureService
{
    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;
    public event EventHandler<string>? CaptureError;

    private readonly object _syncRoot = new();
    private WasapiLoopbackCapture? _capture;
    private MMDevice? _activeRenderDevice;
    private bool _mutedByUs;
    private AudioResampler? _resampler;
    private bool _isCapturing;

    private short[] _resampledLeft = new short[4096];
    private short[] _resampledRight = new short[4096];
    private float[] _floatBuffer = new float[8192];

    public bool IsCapturing
    {
        get
        {
            lock (_syncRoot)
            {
                return _isCapturing;
            }
        }
    }

    public bool IsEndpointMuted
    {
        get
        {
            lock (_syncRoot)
            {
                if (_activeRenderDevice != null)
                {
                    try { return _activeRenderDevice.AudioEndpointVolume.Mute; } catch { }
                }

                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    return dev.AudioEndpointVolume.Mute;
                }
                catch
                {
                    return false;
                }
            }
        }
        set
        {
            SetEndpointMute(value);
        }
    }

    public void SetEndpointMute(bool mute, string? deviceId = null)
    {
        lock (_syncRoot)
        {
            try
            {
                if (_activeRenderDevice != null && string.IsNullOrEmpty(deviceId))
                {
                    _activeRenderDevice.AudioEndpointVolume.Mute = mute;
                }
                else
                {
                    using var enumerator = new MMDeviceEnumerator();
                    MMDevice dev = !string.IsNullOrEmpty(deviceId)
                        ? enumerator.GetDevice(deviceId)
                        : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    dev.AudioEndpointVolume.Mute = mute;
                }
            }
            catch (Exception ex)
            {
                CaptureError?.Invoke(this, $"Failed to set mute: {ex.Message}");
            }
        }
    }

    public IReadOnlyList<AudioDeviceChoice> EnumerateRenderDevices()
    {
        var list = new List<AudioDeviceChoice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            MMDevice? defaultDevice = null;
            try
            {
                defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch { }

            foreach (var dev in devices)
            {
                bool isDefault = defaultDevice != null && dev.ID == defaultDevice.ID;
                list.Add(new AudioDeviceChoice
                {
                    Id = dev.ID,
                    Name = dev.FriendlyName,
                    IsDefault = isDefault
                });
            }
        }
        catch (Exception ex)
        {
            CaptureError?.Invoke(this, $"Failed to enumerate audio devices: {ex.Message}");
        }

        return list;
    }

    public void StartCapture(string? deviceId = null, bool muteSpeakers = false)
    {
        lock (_syncRoot)
        {
            if (_isCapturing)
                StopCapture();

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice? targetDevice = null;

                if (!string.IsNullOrEmpty(deviceId))
                {
                    try
                    {
                        targetDevice = enumerator.GetDevice(deviceId);
                    }
                    catch
                    {
                        targetDevice = null;
                    }
                }

                targetDevice ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _activeRenderDevice = targetDevice;

                // If headphone isolation is requested, mute physical PC speakers while capturing
                if (muteSpeakers && _activeRenderDevice != null)
                {
                    try
                    {
                        if (!_activeRenderDevice.AudioEndpointVolume.Mute)
                        {
                            _activeRenderDevice.AudioEndpointVolume.Mute = true;
                            _mutedByUs = true;
                        }
                    }
                    catch { }
                }

                _capture = new WasapiLoopbackCapture(targetDevice);
                int sourceSampleRate = _capture.WaveFormat.SampleRate;

                _resampler = new AudioResampler(sourceSampleRate, 32000);
                _capture.DataAvailable += OnCaptureDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;

                _capture.StartRecording();
                _isCapturing = true;
            }
            catch (Exception ex)
            {
                _isCapturing = false;
                CaptureError?.Invoke(this, $"Failed to start WASAPI loopback capture: {ex.Message}");
                throw;
            }
        }
    }

    public void StopCapture()
    {
        lock (_syncRoot)
        {
            try
            {
                // Restore speaker mute state if we muted it
                if (_mutedByUs && _activeRenderDevice != null)
                {
                    try
                    {
                        _activeRenderDevice.AudioEndpointVolume.Mute = false;
                    }
                    catch { }
                    _mutedByUs = false;
                }

                if (_capture != null)
                {
                    _capture.DataAvailable -= OnCaptureDataAvailable;
                    _capture.RecordingStopped -= OnRecordingStopped;
                    _capture.StopRecording();
                    _capture.Dispose();
                }
            }
            catch (Exception ex)
            {
                CaptureError?.Invoke(this, $"Error stopping capture: {ex.Message}");
            }
            finally
            {
                _capture = null;
                _activeRenderDevice = null;
                _resampler = null;
                _isCapturing = false;
            }
        }
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _resampler == null || _capture == null)
            return;

        try
        {
            var format = _capture.WaveFormat;
            int bytesPerSample = format.BitsPerSample / 8;
            int channels = format.Channels;
            int frameCount = e.BytesRecorded / (bytesPerSample * channels);

            if (_floatBuffer.Length < frameCount * 2)
            {
                _floatBuffer = new float[frameCount * 2 * 2];
            }

            if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                var spanFloats = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
                if (channels == 2)
                {
                    spanFloats.CopyTo(_floatBuffer.AsSpan(0, frameCount * 2));
                }
                else if (channels == 1)
                {
                    for (int i = 0; i < frameCount; i++)
                    {
                        float mono = spanFloats[i];
                        _floatBuffer[i * 2] = mono;
                        _floatBuffer[i * 2 + 1] = mono;
                    }
                }
                else
                {
                    // Multi-channel downmix to stereo (take front left and front right)
                    for (int i = 0; i < frameCount; i++)
                    {
                        _floatBuffer[i * 2] = spanFloats[i * channels];
                        _floatBuffer[i * 2 + 1] = spanFloats[i * channels + 1];
                    }
                }
            }
            else if (format.BitsPerSample == 16)
            {
                var spanShorts = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded));
                for (int i = 0; i < frameCount; i++)
                {
                    if (channels >= 2)
                    {
                        _floatBuffer[i * 2] = spanShorts[i * channels] / 32768.0f;
                        _floatBuffer[i * 2 + 1] = spanShorts[i * channels + 1] / 32768.0f;
                    }
                    else
                    {
                        float val = spanShorts[i] / 32768.0f;
                        _floatBuffer[i * 2] = val;
                        _floatBuffer[i * 2 + 1] = val;
                    }
                }
            }

            int estimatedOutputFrames = (int)Math.Ceiling(frameCount * (32000.0 / format.SampleRate)) + 32;
            if (_resampledLeft.Length < estimatedOutputFrames)
            {
                _resampledLeft = new short[estimatedOutputFrames * 2];
                _resampledRight = new short[estimatedOutputFrames * 2];
            }

            int resampledCount = _resampler.ResampleStereoFloat(
                _floatBuffer.AsSpan(0, frameCount * 2),
                _resampledLeft.AsSpan(),
                _resampledRight.AsSpan());

            if (resampledCount > 0)
            {
                SamplesAvailable?.Invoke(this, new AudioSamplesEventArgs(_resampledLeft, _resampledRight, resampledCount));
            }
        }
        catch (Exception ex)
        {
            CaptureError?.Invoke(this, $"Error processing captured audio: {ex.Message}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            CaptureError?.Invoke(this, $"Audio capture stopped unexpectedly: {e.Exception.Message}");
        }
    }

    public void Dispose()
    {
        StopCapture();
    }
}
