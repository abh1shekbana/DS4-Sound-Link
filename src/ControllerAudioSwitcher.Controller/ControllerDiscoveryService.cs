using System.Runtime.InteropServices;
using ControllerAudioSwitcher.Controller.Native;
using ControllerAudioSwitcher.Core.Interfaces;
using ControllerAudioSwitcher.Core.Models;
using ControllerAudioSwitcher.Core.Protocol;
using Microsoft.Win32.SafeHandles;

namespace ControllerAudioSwitcher.Controller;

public sealed class ControllerDiscoveryService : IControllerDiscoveryService
{
    public const int SonyVid = 0x054C;
    public const int Ds4V1Pid = 0x05C4;
    public const int Ds4V2Pid = 0x09CC;
    public const int Ds4VariantPid = 0x05C5;
    public const int SonyWaPid = 0x0BA0;
    public const int DualSensePid = 0x0CE6;
    public const int DualSenseEdgePid = 0x0DF2;

    public event EventHandler<ControllerInfo>? ControllerConnected;
    public event EventHandler<ControllerInfo>? ControllerDisconnected;

    private readonly IDiagnosticService? _diagnosticService;
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, ControllerInfo> _connectedControllers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _pollTimer;
    private int _isRefreshing;
    private bool _isDisposed;

    public ControllerDiscoveryService(IDiagnosticService? diagnosticService = null)
    {
        _diagnosticService = diagnosticService;
        _pollTimer = new Timer(OnPollTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void StartDiscovery()
    {
        _diagnosticService?.Info("Starting PlayStation controller discovery (DualShock 4 / DualSense / Edge)...");
        RefreshControllers();
        _pollTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void StopDiscovery()
    {
        _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _diagnosticService?.Debug("Stopped controller discovery polling.");
    }

    public IReadOnlyList<ControllerInfo> GetConnectedControllers()
    {
        lock (_syncRoot)
        {
            return _connectedControllers.Values.ToList();
        }
    }

    public ControllerInfo? GetActiveBluetoothController()
    {
        lock (_syncRoot)
        {
            return _connectedControllers.Values.FirstOrDefault(c => c.IsCompatible);
        }
    }

    private void OnPollTick(object? state)
    {
        if (Interlocked.CompareExchange(ref _isRefreshing, 1, 0) == 0)
        {
            try
            {
                RefreshControllers();
            }
            finally
            {
                Interlocked.Exchange(ref _isRefreshing, 0);
            }
        }
    }

    public void RefreshControllers()
    {
        var discovered = ScanControllers();

        List<ControllerInfo> connected = new();
        List<ControllerInfo> disconnected = new();
        List<ControllerInfo> batteryUpdated = new();

        lock (_syncRoot)
        {
            var discoveredMap = discovered.ToDictionary(c => c.DevicePath, StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in discoveredMap)
            {
                if (!_connectedControllers.ContainsKey(kvp.Key))
                {
                    _connectedControllers[kvp.Key] = kvp.Value;
                    connected.Add(kvp.Value);
                }
                else
                {
                    var existing = _connectedControllers[kvp.Key];
                    bool stateChanged = false;

                    if (kvp.Value.BatteryPercent >= 0 &&
                        (existing.BatteryPercent != kvp.Value.BatteryPercent || existing.IsCharging != kvp.Value.IsCharging))
                    {
                        existing.BatteryPercent = kvp.Value.BatteryPercent;
                        existing.IsCharging = kvp.Value.IsCharging;
                        stateChanged = true;
                    }

                    if (existing.IsHeadphoneJackInserted != kvp.Value.IsHeadphoneJackInserted)
                    {
                        existing.IsHeadphoneJackInserted = kvp.Value.IsHeadphoneJackInserted;
                        stateChanged = true;
                    }

                    if (stateChanged)
                    {
                        batteryUpdated.Add(existing);
                    }
                }
            }

            var removedPaths = _connectedControllers.Keys.Where(k => !discoveredMap.ContainsKey(k)).ToList();
            foreach (var path in removedPaths)
            {
                var removed = _connectedControllers[path];
                _connectedControllers.Remove(path);
                disconnected.Add(removed);
            }
        }

        foreach (var c in connected)
        {
            _diagnosticService?.Info($"Controller attached: {c.DisplayName} ({c.ConnectionType}) [Model: {c.Model}, Audio: {c.Capabilities.BtAudioStreaming}, Battery: {(c.BatteryPercent >= 0 ? c.BatteryPercent + "%" : "reading...")}]");
            ControllerConnected?.Invoke(this, c);
        }

        foreach (var c in batteryUpdated)
        {
            ControllerConnected?.Invoke(this, c);
        }

        foreach (var d in disconnected)
        {
            _diagnosticService?.Warn($"Controller removed: {d.DisplayName} ({d.ConnectionType})");
            ControllerDisconnected?.Invoke(this, d);
        }
    }

    private List<ControllerInfo> ScanControllers()
    {
        var results = new List<ControllerInfo>();

        DeviceSetupNativeMethods.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr devInfo = DeviceSetupNativeMethods.SetupDiGetClassDevs(
            ref hidGuid,
            IntPtr.Zero,
            IntPtr.Zero,
            DeviceSetupNativeMethods.DIGCF_PRESENT | DeviceSetupNativeMethods.DIGCF_DEVICEINTERFACE);

        if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1))
        {
            _diagnosticService?.Warn("SetupDiGetClassDevs failed to acquire HID device info set.");
            return results;
        }

        try
        {
            var interfaceData = new DeviceSetupNativeMethods.SP_DEVICE_INTERFACE_DATA();
            interfaceData.cbSize = Marshal.SizeOf<DeviceSetupNativeMethods.SP_DEVICE_INTERFACE_DATA>();

            int memberIndex = 0;
            while (DeviceSetupNativeMethods.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, memberIndex, ref interfaceData))
            {
                memberIndex++;

                DeviceSetupNativeMethods.SetupDiGetDeviceInterfaceDetail(
                    devInfo,
                    ref interfaceData,
                    IntPtr.Zero,
                    0,
                    out int requiredSize,
                    IntPtr.Zero);

                if (requiredSize <= 0) continue;

                IntPtr detailDataBuffer = Marshal.AllocHGlobal(requiredSize);
                try
                {
                    Marshal.WriteInt32(detailDataBuffer, IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize);

                    if (DeviceSetupNativeMethods.SetupDiGetDeviceInterfaceDetail(
                            devInfo,
                            ref interfaceData,
                            detailDataBuffer,
                            requiredSize,
                            out _,
                            IntPtr.Zero))
                    {
                        IntPtr pDevicePath = detailDataBuffer + 4;
                        string? devicePath = Marshal.PtrToStringAuto(pDevicePath);

                        if (!string.IsNullOrEmpty(devicePath))
                        {
                            var controller = ProbeController(devicePath);
                            if (controller != null)
                            {
                                results.Add(controller);
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailDataBuffer);
                }
            }
        }
        finally
        {
            DeviceSetupNativeMethods.SetupDiDestroyDeviceInfoList(devInfo);
        }

        return results;
    }

    private ControllerInfo? ProbeController(string devicePath)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = DeviceSetupNativeMethods.CreateFile(
                devicePath,
                0, // DesiredAccess = 0 for querying attributes without locking device
                DeviceSetupNativeMethods.FileShareRead | DeviceSetupNativeMethods.FileShareWrite | DeviceSetupNativeMethods.FileShareDelete,
                IntPtr.Zero,
                DeviceSetupNativeMethods.OpenExisting,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                _diagnosticService?.Debug($"Probe: Unable to open handle for {devicePath} (Win32Error={Marshal.GetLastWin32Error()})");
                return null;
            }

            var attributes = new DeviceSetupNativeMethods.HIDD_ATTRIBUTES();
            attributes.Size = Marshal.SizeOf<DeviceSetupNativeMethods.HIDD_ATTRIBUTES>();

            if (!DeviceSetupNativeMethods.HidD_GetAttributes(handle, ref attributes))
            {
                return null;
            }

            ControllerModel model;
            string name;

            if (attributes.VendorID == SonyVid)
            {
                switch (attributes.ProductID)
                {
                    case Ds4V1Pid:
                        model = ControllerModel.DualShock4V1;
                        name = "Sony DualShock 4 (v1)";
                        break;
                    case Ds4V2Pid:
                        model = ControllerModel.DualShock4V2;
                        name = "Sony DualShock 4 (v2)";
                        break;
                    case Ds4VariantPid:
                        model = ControllerModel.DualShock4V2;
                        name = "Sony DualShock 4";
                        break;
                    case SonyWaPid:
                        model = ControllerModel.DualShock4Dongle;
                        name = "Sony DualShock 4 Wireless Adapter";
                        break;
                    case DualSensePid:
                        model = ControllerModel.DualSense;
                        name = "Sony DualSense (PS5)";
                        break;
                    case DualSenseEdgePid:
                        model = ControllerModel.DualSenseEdge;
                        name = "Sony DualSense Edge";
                        break;
                    default:
                        model = ControllerModel.UnknownPlayStation;
                        name = $"Sony Controller (PID: 0x{attributes.ProductID:X4})";
                        break;
                }
            }
            else if (attributes.VendorID == 0x0C12 || attributes.VendorID == 0x0F0D || attributes.VendorID == 0x146B)
            {
                model = ControllerModel.DualShock4CompatibleClone;
                name = $"Compatible PlayStation Controller (VID: 0x{attributes.VendorID:X4})";
            }
            else
            {
                return null;
            }

            int inputReportLength = 0;
            int outputReportLength = 0;
            if (DeviceSetupNativeMethods.HidD_GetPreparsedData(handle, out IntPtr preparsedData))
            {
                try
                {
                    int status = DeviceSetupNativeMethods.HidP_GetCaps(preparsedData, out var caps);
                    if (status == DeviceSetupNativeMethods.HIDP_STATUS_SUCCESS || status == 0)
                    {
                        inputReportLength = caps.InputReportByteLength;
                        outputReportLength = caps.OutputReportByteLength;
                    }
                    else
                    {
                        _diagnosticService?.Debug($"HidP_GetCaps returned 0x{status:X8} for {devicePath}");
                    }
                }
                finally
                {
                    DeviceSetupNativeMethods.HidD_FreePreparsedData(preparsedData);
                }
            }

            // Determine connection type:
            // 1. Sony Wireless Adapter
            // 2. USB connection: path contains USB or report lengths match USB descriptors
            // 3. Bluetooth connection: path contains Bluetooth UUID 00001124, BTH, VID prefix 0002054C / 0001054C, or output report >= 78
            var connectionType = ControllerConnectionType.Unknown;
            if (attributes.ProductID == SonyWaPid)
            {
                connectionType = ControllerConnectionType.SonyWirelessAdapter;
            }
            else if (devicePath.Contains("USB#", StringComparison.OrdinalIgnoreCase) || 
                     devicePath.Contains("USB\\", StringComparison.OrdinalIgnoreCase) ||
                     (inputReportLength == 64 && outputReportLength <= 48 && !devicePath.Contains("00001124", StringComparison.OrdinalIgnoreCase)))
            {
                connectionType = ControllerConnectionType.Usb;
            }
            else if (devicePath.Contains("00001124", StringComparison.OrdinalIgnoreCase) ||
                     devicePath.Contains("BTH", StringComparison.OrdinalIgnoreCase) ||
                     devicePath.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                     devicePath.Contains("0002054c", StringComparison.OrdinalIgnoreCase) ||
                     devicePath.Contains("0001054c", StringComparison.OrdinalIgnoreCase) ||
                     outputReportLength >= 78 ||
                     inputReportLength >= 78 ||
                     !devicePath.Contains("USB", StringComparison.OrdinalIgnoreCase))
            {
                connectionType = ControllerConnectionType.Bluetooth;
            }

            var info = new ControllerInfo
            {
                DevicePath = devicePath,
                DisplayName = name,
                VendorId = attributes.VendorID,
                ProductId = attributes.ProductID,
                Model = model,
                ConnectionType = connectionType,
                Capabilities = ControllerCapabilities.Create(model, connectionType),
                InputReportByteLength = inputReportLength,
                OutputReportByteLength = outputReportLength
            };

            if (info.IsCompatible)
            {
                ProbeBattery(info);
            }

            _diagnosticService?.Debug($"Identified controller: {info.DisplayName} ({info.ConnectionType}), Model={info.Model}, InputLen={inputReportLength}, OutputLen={outputReportLength}, AudioCompatible={info.IsCompatible}, Battery={info.BatteryPercent}%");

            return info;
        }
        catch (Exception ex)
        {
            _diagnosticService?.Debug($"Error probing device {devicePath}: {ex.Message}");
            return null;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private void ProbeBattery(ControllerInfo info)
    {
        if (string.IsNullOrEmpty(info.DevicePath))
            return;

        SafeFileHandle? readHandle = null;
        IntPtr hEvent = IntPtr.Zero;
        IntPtr overlappedPtr = IntPtr.Zero;

        try
        {
            uint access = DeviceSetupNativeMethods.GenericRead;
            uint share = DeviceSetupNativeMethods.FileShareRead | DeviceSetupNativeMethods.FileShareWrite | DeviceSetupNativeMethods.FileShareDelete;
            uint flags = DeviceSetupNativeMethods.FileFlagOverlapped;

            readHandle = DeviceSetupNativeMethods.CreateFile(
                info.DevicePath,
                access,
                share,
                IntPtr.Zero,
                DeviceSetupNativeMethods.OpenExisting,
                flags,
                IntPtr.Zero);

            if (readHandle.IsInvalid)
                return;

            hEvent = DeviceSetupNativeMethods.CreateEvent(IntPtr.Zero, true, false, null);
            overlappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<DeviceSetupNativeMethods.OVERLAPPED>());
            var ov = new DeviceSetupNativeMethods.OVERLAPPED { hEvent = hEvent };
            Marshal.StructureToPtr(ov, overlappedPtr, false);

            int bufLen = Math.Max(78, info.InputReportByteLength > 0 ? info.InputReportByteLength : 78);
            byte[] buffer = new byte[bufLen];
            GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);

            try
            {
                bool success = DeviceSetupNativeMethods.ReadFile(
                    readHandle,
                    pinned.AddrOfPinnedObject(),
                    (uint)buffer.Length,
                    out uint bytesRead,
                    overlappedPtr);

                if (!success)
                {
                    int err = Marshal.GetLastWin32Error();
                    const int errorIoPending = 997;
                    if (err == errorIoPending)
                    {
                        uint wait = DeviceSetupNativeMethods.WaitForSingleObject(hEvent, 40);
                        if (wait == DeviceSetupNativeMethods.WaitObject0)
                        {
                            success = DeviceSetupNativeMethods.GetOverlappedResult(readHandle, overlappedPtr, out bytesRead, false);
                        }
                        else
                        {
                            DeviceSetupNativeMethods.CancelIoEx(readHandle, overlappedPtr);
                            success = false;
                        }
                    }
                }

                if (success && bytesRead > 0)
                {
                    var span = buffer.AsSpan(0, (int)bytesRead);
                    if (info.Model == ControllerModel.DualSense || info.Model == ControllerModel.DualSenseEdge)
                    {
                        if (DualSenseBluetoothAudioProtocol.TryParseInputReport(span, out int dsBattery, out bool dsCharging, out bool dsJack))
                        {
                            info.BatteryPercent = dsBattery;
                            info.IsCharging = dsCharging;
                            info.IsHeadphoneJackInserted = dsJack;
                            _diagnosticService?.Debug($"DualSense telemetry probe for {info.DisplayName}: {dsBattery}% (Charging={dsCharging}, JackConnected={dsJack})");
                        }
                    }
                    else if (DualShock4BatteryParser.TryParse(span, out int batteryPercent, out bool isCharging))
                    {
                        info.BatteryPercent = batteryPercent;
                        info.IsCharging = isCharging;
                        _diagnosticService?.Debug($"DualShock 4 battery probe for {info.DisplayName}: {batteryPercent}% (Charging={isCharging})");
                    }
                }
            }
            finally
            {
                if (pinned.IsAllocated) pinned.Free();
            }
        }
        catch
        {
            // Graceful fallback if reading battery is unsupported or device busy
        }
        finally
        {
            if (hEvent != IntPtr.Zero) DeviceSetupNativeMethods.CloseHandle(hEvent);
            if (overlappedPtr != IntPtr.Zero) Marshal.FreeHGlobal(overlappedPtr);
            readHandle?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        StopDiscovery();
        _pollTimer.Dispose();

        lock (_syncRoot)
        {
            _connectedControllers.Clear();
        }
    }
}
