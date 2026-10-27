using System.Runtime.InteropServices;
using ControllerAudioSwitcher.Core.Interfaces;
using ControllerAudioSwitcher.Core.Models;
using ControllerAudioSwitcher.Core.Protocol;
using ControllerAudioSwitcher.Transport.Native;
using Microsoft.Win32.SafeHandles;

namespace ControllerAudioSwitcher.Transport;

/// <summary>
/// High-performance double-buffered Bluetooth HID Transport for DualShock 4 audio streaming.
/// Uses asynchronous overlapped I/O with double-buffered output reports to eliminate
/// transmission thread stalls and deliver rock-solid 16.0 ms audio packets.
/// </summary>
public sealed class DualShock4HidTransport : IBluetoothAudioTransport
{
    private readonly object _syncRoot = new();
    private SafeFileHandle? _deviceHandle;
    private ControllerInfo? _currentController;
    private bool _isDisposed;

    // Double-buffering for 0x17 streaming reports
    private const int BufferCount = 2;
    private readonly byte[][] _streamReportBuffers = new byte[BufferCount][]
    {
        new byte[DualShock4BluetoothAudioProtocol.AudioStreamReportLength],
        new byte[DualShock4BluetoothAudioProtocol.AudioStreamReportLength]
    };
    private readonly GCHandle[] _pinnedStreamBuffers = new GCHandle[BufferCount];
    private readonly IntPtr[] _overlappedPtrs = new IntPtr[BufferCount];
    private readonly IntPtr[] _writeEvents = new IntPtr[BufferCount];
    private readonly bool[] _ioPending = new bool[BufferCount];
    private int _currentBufferIndex;

    // Single buffer for 0x11 control reports
    private readonly byte[] _controlReportBuffer = new byte[DualShock4BluetoothAudioProtocol.AudioControlReportLength];
    private IntPtr _controlOverlappedPtr;
    private IntPtr _controlWriteEvent;
    private GCHandle _pinnedControlBuffer;

    // Battery telemetry reporting
    public event EventHandler<(int batteryPercent, bool isCharging)>? BatteryReportReceived;
    public int CurrentBatteryPercent { get; private set; } = -1;
    public bool IsCharging { get; private set; }

    // Input report reader for battery telemetry
    private readonly byte[] _inputReportBuffer = new byte[128];
    private GCHandle _pinnedInputBuffer;
    private IntPtr _readOverlappedPtr;
    private IntPtr _readEvent;
    private CancellationTokenSource? _readerCts;

    public bool IsOpen
    {
        get
        {
            lock (_syncRoot)
            {
                return _deviceHandle != null && !_deviceHandle.IsInvalid && !_deviceHandle.IsClosed;
            }
        }
    }

    public DualShock4HidTransport()
    {
        for (int i = 0; i < BufferCount; i++)
        {
            _writeEvents[i] = HidNativeMethods.CreateEvent(IntPtr.Zero, true, false, null);
            _overlappedPtrs[i] = Marshal.AllocHGlobal(Marshal.SizeOf<HidNativeMethods.OVERLAPPED>());
            ZeroOverlapped(_overlappedPtrs[i], _writeEvents[i]);
        }

        _controlWriteEvent = HidNativeMethods.CreateEvent(IntPtr.Zero, true, false, null);
        _controlOverlappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<HidNativeMethods.OVERLAPPED>());
        ZeroOverlapped(_controlOverlappedPtr, _controlWriteEvent);

        _readEvent = HidNativeMethods.CreateEvent(IntPtr.Zero, true, false, null);
        _readOverlappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<HidNativeMethods.OVERLAPPED>());
        ZeroOverlapped(_readOverlappedPtr, _readEvent);
    }

    private static void ZeroOverlapped(IntPtr overlappedPtr, IntPtr hEvent)
    {
        var overlapped = new HidNativeMethods.OVERLAPPED
        {
            hEvent = hEvent
        };
        Marshal.StructureToPtr(overlapped, overlappedPtr, false);
    }

    public bool Open(ControllerInfo controller)
    {
        if (controller == null || string.IsNullOrEmpty(controller.DevicePath))
            return false;

        lock (_syncRoot)
        {
            Close();

            try
            {
                uint access = HidNativeMethods.GenericRead | HidNativeMethods.GenericWrite;
                uint share = HidNativeMethods.FileShareRead | HidNativeMethods.FileShareWrite;
                uint flags = HidNativeMethods.FileFlagOverlapped | HidNativeMethods.FileFlagNoBuffering | HidNativeMethods.FileFlagWriteThrough;

                _deviceHandle = HidNativeMethods.CreateFile(
                    controller.DevicePath,
                    access,
                    share,
                    IntPtr.Zero,
                    HidNativeMethods.OpenExisting,
                    flags,
                    IntPtr.Zero);

                if (_deviceHandle.IsInvalid)
                {
                    _deviceHandle = null;
                    return false;
                }

                // Lower input buffer latency
                HidNativeMethods.HidD_SetNumInputBuffers(_deviceHandle, 2);

                _currentController = controller;

                for (int i = 0; i < BufferCount; i++)
                {
                    if (!_pinnedStreamBuffers[i].IsAllocated)
                    {
                        _pinnedStreamBuffers[i] = GCHandle.Alloc(_streamReportBuffers[i], GCHandleType.Pinned);
                    }
                    _ioPending[i] = false;
                }

                if (!_pinnedControlBuffer.IsAllocated)
                {
                    _pinnedControlBuffer = GCHandle.Alloc(_controlReportBuffer, GCHandleType.Pinned);
                }

                if (!_pinnedInputBuffer.IsAllocated)
                {
                    _pinnedInputBuffer = GCHandle.Alloc(_inputReportBuffer, GCHandleType.Pinned);
                }

                _currentBufferIndex = 0;

                // Start background reader for battery telemetry
                _readerCts = new CancellationTokenSource();
                var token = _readerCts.Token;
                Task.Run(() => InputReportReaderLoop(token), token);

                return true;
            }
            catch
            {
                Close();
                return false;
            }
        }
    }

    private void InputReportReaderLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && IsOpen)
        {
            SafeFileHandle? handle;
            lock (_syncRoot)
            {
                handle = _deviceHandle;
            }

            if (handle == null || handle.IsInvalid || handle.IsClosed)
                break;

            try
            {
                HidNativeMethods.ResetEvent(_readEvent);
                ZeroOverlapped(_readOverlappedPtr, _readEvent);

                IntPtr bufPtr = _pinnedInputBuffer.AddrOfPinnedObject();
                bool readSuccess = HidNativeMethods.ReadFile(
                    handle,
                    bufPtr,
                    (uint)_inputReportBuffer.Length,
                    out uint bytesRead,
                    _readOverlappedPtr);

                if (!readSuccess)
                {
                    int err = Marshal.GetLastWin32Error();
                    const int errorIoPending = 997;
                    if (err == errorIoPending)
                    {
                        uint wait = HidNativeMethods.WaitForSingleObject(_readEvent, 200);
                        if (wait == HidNativeMethods.WaitObject0)
                        {
                            readSuccess = HidNativeMethods.GetOverlappedResult(handle, _readOverlappedPtr, out bytesRead, false);
                        }
                        else
                        {
                            HidNativeMethods.CancelIoEx(handle, _readOverlappedPtr);
                            readSuccess = false;
                        }
                    }
                }

                if (readSuccess && bytesRead > 0)
                {
                    var span = _inputReportBuffer.AsSpan(0, (int)bytesRead);
                    int percent;
                    bool charging;

                    if (_currentController?.Model == ControllerModel.DualSense || _currentController?.Model == ControllerModel.DualSenseEdge)
                    {
                        if (DualSenseBluetoothAudioProtocol.TryParseInputReport(span, out percent, out charging, out bool jackConnected))
                        {
                            if (_currentController != null)
                            {
                                _currentController.IsHeadphoneJackInserted = jackConnected;
                            }
                            if (percent != CurrentBatteryPercent || charging != IsCharging)
                            {
                                CurrentBatteryPercent = percent;
                                IsCharging = charging;
                                if (_currentController != null)
                                {
                                    _currentController.BatteryPercent = percent;
                                    _currentController.IsCharging = charging;
                                }
                                BatteryReportReceived?.Invoke(this, (percent, charging));
                            }
                        }
                    }
                    else if (DualShock4BatteryParser.TryParse(span, out percent, out charging))
                    {
                        if (percent != CurrentBatteryPercent || charging != IsCharging)
                        {
                            CurrentBatteryPercent = percent;
                            IsCharging = charging;
                            if (_currentController != null)
                            {
                                _currentController.BatteryPercent = percent;
                                _currentController.IsCharging = charging;
                            }
                            BatteryReportReceived?.Invoke(this, (percent, charging));
                        }
                    }
                }
            }
            catch
            {
                // Ignore transient read errors during stream transitions
            }

            try
            {
                Task.Delay(1000, token).Wait(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                break;
            }
        }
    }

    public bool ArmAudio(byte headphoneVolume = 110, byte speakerVolume = 0)
    {
        lock (_syncRoot)
        {
            if (!IsOpen || _deviceHandle == null || _deviceHandle.IsInvalid)
                return false;

            if (_currentController?.Model == ControllerModel.DualSense || _currentController?.Model == ControllerModel.DualSenseEdge)
            {
                bool isHeadset = speakerVolume == 0;
                byte vol = isHeadset ? headphoneVolume : speakerVolume;
                DualSenseBluetoothAudioProtocol.WriteBluetoothControlReport(
                    _controlReportBuffer,
                    headsetMode: isHeadset,
                    volume: vol);
            }
            else
            {
                DualShock4BluetoothAudioProtocol.WriteAudioControlReport(
                    _controlReportBuffer,
                    speakerEnabled: true,
                    headphoneVolume: headphoneVolume,
                    speakerVolume: speakerVolume);
            }

            return WriteControlDirect();
        }
    }

    public bool DisarmAudio()
    {
        lock (_syncRoot)
        {
            if (!IsOpen || _deviceHandle == null || _deviceHandle.IsInvalid)
                return false;

            if (_currentController?.Model == ControllerModel.DualSense || _currentController?.Model == ControllerModel.DualSenseEdge)
            {
                DualSenseBluetoothAudioProtocol.WriteBluetoothControlReport(
                    _controlReportBuffer,
                    headsetMode: false,
                    volume: 0,
                    isMuted: true);
            }
            else
            {
                DualShock4BluetoothAudioProtocol.WriteAudioControlReport(
                    _controlReportBuffer,
                    speakerEnabled: false,
                    headphoneVolume: 0,
                    speakerVolume: 0);
            }

            return WriteControlDirect();
        }
    }

    private bool WriteControlDirect()
    {
        if (_deviceHandle == null || _deviceHandle.IsInvalid)
            return false;

        // Drain any pending streaming write on the same file handle before writing control report
        for (int i = 0; i < BufferCount; i++)
        {
            if (_ioPending[i])
            {
                uint waitResult = HidNativeMethods.WaitForSingleObject(_writeEvents[i], 40);
                if (waitResult == HidNativeMethods.WaitObject0)
                {
                    HidNativeMethods.GetOverlappedResult(_deviceHandle, _overlappedPtrs[i], out _, false);
                    _ioPending[i] = false;
                }
            }
        }

        HidNativeMethods.ResetEvent(_controlWriteEvent);
        ZeroOverlapped(_controlOverlappedPtr, _controlWriteEvent);

        IntPtr bufferPtr = _pinnedControlBuffer.AddrOfPinnedObject();
        bool success = HidNativeMethods.WriteFile(
            _deviceHandle,
            bufferPtr,
            (uint)DualShock4BluetoothAudioProtocol.AudioControlReportLength,
            out _,
            _controlOverlappedPtr);

        if (!success)
        {
            int error = Marshal.GetLastWin32Error();
            const int errorIoPending = 997; // ERROR_IO_PENDING

            if (error == errorIoPending)
            {
                uint wait = HidNativeMethods.WaitForSingleObject(_controlWriteEvent, 150);
                if (wait == HidNativeMethods.WaitObject0)
                {
                    return HidNativeMethods.GetOverlappedResult(_deviceHandle, _controlOverlappedPtr, out _, false);
                }
                else
                {
                    HidNativeMethods.CancelIoEx(_deviceHandle, _controlOverlappedPtr);
                    return false;
                }
            }

            return false;
        }

        return true;
    }

    public bool SendAudioFrames(ushort frameNumber, byte[][] frames, int frameCount = 4, bool headsetMode = true)
    {
        lock (_syncRoot)
        {
            if (!IsOpen || _deviceHandle == null || _deviceHandle.IsInvalid)
                return false;

            int idx = _currentBufferIndex;
            IntPtr writeEvent = _writeEvents[idx];
            IntPtr overlappedPtr = _overlappedPtrs[idx];

            // If the write on this buffer from 2 cycles ago is still pending, wait with bounded timeout
            if (_ioPending[idx])
            {
                uint wait = HidNativeMethods.WaitForSingleObject(writeEvent, 20);
                if (wait == HidNativeMethods.WaitObject0)
                {
                    HidNativeMethods.GetOverlappedResult(_deviceHandle, overlappedPtr, out _, false);
                    _ioPending[idx] = false;
                }
                else
                {
                    // Stalled or disconnected
                    HidNativeMethods.CancelIoEx(_deviceHandle, overlappedPtr);
                    _ioPending[idx] = false;
                    return false;
                }
            }

            // Populate the active stream buffer
            byte[] targetBuffer = _streamReportBuffers[idx];
            DualShock4BluetoothAudioProtocol.WriteStreamReport(
                targetBuffer,
                frameNumber,
                frames,
                headsetMode: headsetMode);

            HidNativeMethods.ResetEvent(writeEvent);
            ZeroOverlapped(overlappedPtr, writeEvent);

            IntPtr bufferPtr = _pinnedStreamBuffers[idx].AddrOfPinnedObject();
            bool success = HidNativeMethods.WriteFile(
                _deviceHandle,
                bufferPtr,
                (uint)DualShock4BluetoothAudioProtocol.AudioStreamReportLength,
                out _,
                overlappedPtr);

            if (!success)
            {
                int error = Marshal.GetLastWin32Error();
                const int errorIoPending = 997; // ERROR_IO_PENDING

                if (error == errorIoPending)
                {
                    _ioPending[idx] = true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                _ioPending[idx] = false;
            }

            // Advance to alternating buffer for next 16ms tick
            _currentBufferIndex = (idx + 1) % BufferCount;
            return true;
        }
    }

    public void Close()
    {
        CancellationTokenSource? cts;
        lock (_syncRoot)
        {
            cts = _readerCts;
            _readerCts = null;
        }

        if (cts != null)
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch { }
        }

        lock (_syncRoot)
        {
            if (_deviceHandle != null)
            {
                try
                {
                    if (!_deviceHandle.IsInvalid && !_deviceHandle.IsClosed)
                    {
                        for (int i = 0; i < BufferCount; i++)
                        {
                            if (_ioPending[i])
                            {
                                HidNativeMethods.CancelIoEx(_deviceHandle, _overlappedPtrs[i]);
                                _ioPending[i] = false;
                            }
                        }
                        HidNativeMethods.CancelIoEx(_deviceHandle, _controlOverlappedPtr);
                        if (_readOverlappedPtr != IntPtr.Zero)
                        {
                            HidNativeMethods.CancelIoEx(_deviceHandle, _readOverlappedPtr);
                        }
                        _deviceHandle.Close();
                    }
                }
                catch { }
                finally
                {
                    _deviceHandle = null;
                    _currentController = null;
                }
            }

            for (int i = 0; i < BufferCount; i++)
            {
                if (_pinnedStreamBuffers[i].IsAllocated)
                {
                    _pinnedStreamBuffers[i].Free();
                }
            }

            if (_pinnedControlBuffer.IsAllocated)
            {
                _pinnedControlBuffer.Free();
            }

            if (_pinnedInputBuffer.IsAllocated)
            {
                _pinnedInputBuffer.Free();
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Close();

        for (int i = 0; i < BufferCount; i++)
        {
            if (_writeEvents[i] != IntPtr.Zero)
            {
                HidNativeMethods.CloseHandle(_writeEvents[i]);
                _writeEvents[i] = IntPtr.Zero;
            }

            if (_overlappedPtrs[i] != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_overlappedPtrs[i]);
                _overlappedPtrs[i] = IntPtr.Zero;
            }
        }

        if (_controlWriteEvent != IntPtr.Zero)
        {
            HidNativeMethods.CloseHandle(_controlWriteEvent);
            _controlWriteEvent = IntPtr.Zero;
        }

        if (_controlOverlappedPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_controlOverlappedPtr);
            _controlOverlappedPtr = IntPtr.Zero;
        }

        if (_readEvent != IntPtr.Zero)
        {
            HidNativeMethods.CloseHandle(_readEvent);
            _readEvent = IntPtr.Zero;
        }

        if (_readOverlappedPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_readOverlappedPtr);
            _readOverlappedPtr = IntPtr.Zero;
        }
    }
}
