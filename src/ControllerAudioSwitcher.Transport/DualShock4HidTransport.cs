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

                _currentBufferIndex = 0;
                return true;
            }
            catch
            {
                Close();
                return false;
            }
        }
    }

    public bool ArmAudio(byte headphoneVolume = 110, byte speakerVolume = 0)
    {
        lock (_syncRoot)
        {
            if (!IsOpen || _deviceHandle == null || _deviceHandle.IsInvalid)
                return false;

            DualShock4BluetoothAudioProtocol.WriteAudioControlReport(
                _controlReportBuffer,
                speakerEnabled: true,
                headphoneVolume: headphoneVolume,
                speakerVolume: speakerVolume);

            return WriteControlDirect();
        }
    }

    public bool DisarmAudio()
    {
        lock (_syncRoot)
        {
            if (!IsOpen || _deviceHandle == null || _deviceHandle.IsInvalid)
                return false;

            DualShock4BluetoothAudioProtocol.WriteAudioControlReport(
                _controlReportBuffer,
                speakerEnabled: false,
                headphoneVolume: 0,
                speakerVolume: 0);

            return WriteControlDirect();
        }
    }

    private bool WriteControlDirect()
    {
        if (_deviceHandle == null || _deviceHandle.IsInvalid)
            return false;

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
                uint wait = HidNativeMethods.WaitForSingleObject(_controlWriteEvent, 100);
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
    }
}
