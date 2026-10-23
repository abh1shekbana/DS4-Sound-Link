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
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

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

    private float _softwareGain = 1.20f;

    public void SetSoftwareGain(float gain)
    {
        lock (_syncRoot)
        {
            _softwareGain = Math.Clamp(gain, 0.5f, 2.5f);
            if (_resampler != null)
            {
                _resampler.SoftwareGain = _softwareGain;
            }
        }
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

                _resampler = new AudioResampler(sourceSampleRate, 32000, _softwareGain);
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
                    try { _capture.StopRecording(); } catch { }
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
        if (e.BytesRecorded == 0 || !_isCapturing)
            return;

        AudioResampler? resampler;
        WaveFormat? format;
        lock (_syncRoot)
        {
            resampler = _resampler;
            format = _capture?.WaveFormat;
        }

        if (resampler == null || format == null)
            return;

        try
        {
            int bytesPerSample = format.BitsPerSample / 8;
            if (bytesPerSample <= 0) return;
            int channels = format.Channels;
            if (channels <= 0) return;
            int frameCount = e.BytesRecorded / (bytesPerSample * channels);
            if (frameCount <= 0) return;

            if (_floatBuffer.Length < frameCount * 2)
            {
                _floatBuffer = new float[frameCount * 2 * 2];
            }

            bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (format is WaveFormatExtensible wfe && wfe.SubFormat == SubtypeIeeeFloat) ||
                           (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.Extensible);

            if (isFloat && format.BitsPerSample == 32)
            {
                var spanFloats = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
                if (channels == 2)
                {
                    spanFloats[..(frameCount * 2)].CopyTo(_floatBuffer.AsSpan(0, frameCount * 2));
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
                    // Multi-channel downmix (stereo fold-down with center and surround channels)
                    // Standard 5.1/7.1: 0=FL, 1=FR, 2=FC, 3=LFE, 4=BL/SL, 5=BR/SR
                    for (int i = 0; i < frameCount; i++)
                    {
                        int baseIdx = i * channels;
                        float fl = spanFloats[baseIdx];
                        float fr = spanFloats[baseIdx + 1];
                        float fc = channels > 2 ? spanFloats[baseIdx + 2] : 0.0f;
                        float sl = channels > 4 ? spanFloats[baseIdx + 4] : 0.0f;
                        float sr = channels > 5 ? spanFloats[baseIdx + 5] : 0.0f;

                        _floatBuffer[i * 2] = (fl + 0.7071f * fc + 0.7071f * sl) * 0.7f;
                        _floatBuffer[i * 2 + 1] = (fr + 0.7071f * fc + 0.7071f * sr) * 0.7f;
                    }
                }
            }
            else if (format.BitsPerSample == 16)
            {
                var spanShorts = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded));
                if (channels == 2)
                {
                    for (int i = 0; i < frameCount; i++)
                    {
                        _floatBuffer[i * 2] = spanShorts[i * 2] / 32768.0f;
                        _floatBuffer[i * 2 + 1] = spanShorts[i * 2 + 1] / 32768.0f;
                    }
                }
                else if (channels == 1)
                {
                    for (int i = 0; i < frameCount; i++)
                    {
                        float val = spanShorts[i] / 32768.0f;
                        _floatBuffer[i * 2] = val;
                        _floatBuffer[i * 2 + 1] = val;
                    }
                }
                else
                {
                    for (int i = 0; i < frameCount; i++)
                    {
                        int baseIdx = i * channels;
                        float fl = spanShorts[baseIdx] / 32768.0f;
                        float fr = spanShorts[baseIdx + 1] / 32768.0f;
                        float fc = channels > 2 ? spanShorts[baseIdx + 2] / 32768.0f : 0.0f;
                        _floatBuffer[i * 2] = (fl + 0.7071f * fc) * 0.75f;
                        _floatBuffer[i * 2 + 1] = (fr + 0.7071f * fc) * 0.75f;
                    }
                }
            }
            else if (format.BitsPerSample == 24)
            {
                var buf = e.Buffer;
                int bytesPerFrame = bytesPerSample * channels;
                for (int i = 0; i < frameCount; i++)
                {
                    int offset = i * bytesPerFrame;
                    int sL = buf[offset] | (buf[offset + 1] << 8) | ((sbyte)buf[offset + 2] << 16);
                    float left = sL / 8388608.0f;

                    float right;
                    if (channels >= 2)
                    {
                        int offsetR = offset + 3;
                        int sR = buf[offsetR] | (buf[offsetR + 1] << 8) | ((sbyte)buf[offsetR + 2] << 16);
                        right = sR / 8388608.0f;
                    }
                    else
                    {
                        right = left;
                    }

                    _floatBuffer[i * 2] = left;
                    _floatBuffer[i * 2 + 1] = right;
                }
            }
            else if (format.BitsPerSample == 32)
            {
                // 32-bit signed integer PCM
                var spanInts = MemoryMarshal.Cast<byte, int>(e.Buffer.AsSpan(0, e.BytesRecorded));
                for (int i = 0; i < frameCount; i++)
                {
                    if (channels >= 2)
                    {
                        _floatBuffer[i * 2] = spanInts[i * channels] / 2147483648.0f;
                        _floatBuffer[i * 2 + 1] = spanInts[i * channels + 1] / 2147483648.0f;
                    }
                    else
                    {
                        float val = spanInts[i] / 2147483648.0f;
                        _floatBuffer[i * 2] = val;
                        _floatBuffer[i * 2 + 1] = val;
                    }
                }
            }
            else
            {
                // Unsupported bit depth fallback: log error once
                return;
            }

            int estimatedOutputFrames = (int)Math.Ceiling(frameCount * (32000.0 / format.SampleRate)) + 32;
            if (_resampledLeft.Length < estimatedOutputFrames)
            {
                _resampledLeft = new short[estimatedOutputFrames * 2];
                _resampledRight = new short[estimatedOutputFrames * 2];
            }

            int resampledCount = resampler.ResampleStereoFloat(
                _floatBuffer.AsSpan(0, frameCount * 2),
                _resampledLeft.AsSpan(),
                _resampledRight.AsSpan());

            if (resampledCount > 0 && _isCapturing)
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
