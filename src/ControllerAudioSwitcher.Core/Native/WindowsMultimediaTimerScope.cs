using System.Runtime.InteropServices;

namespace ControllerAudioSwitcher.Core.Native;

/// <summary>
/// Scoped wrapper for Windows timeBeginPeriod / timeEndPeriod.
/// Sets the Windows NT scheduler interrupt resolution to 1.0 ms (down from default 15.6 ms),
/// guaranteeing sub-millisecond precision for Thread.Sleep and Stopwatch wait loops.
/// </summary>
public sealed class WindowsMultimediaTimerScope : IDisposable
{
    private static class NativeMethods
    {
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]
        public static extern uint TimeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]
        public static extern uint TimeEndPeriod(uint uMilliseconds);
    }

    private bool _disposed;
    private readonly bool _isWindows;

    public WindowsMultimediaTimerScope(uint resolutionMs = 1)
    {
        _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        if (_isWindows)
        {
            try
            {
                NativeMethods.TimeBeginPeriod(resolutionMs);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_isWindows)
            {
                try
                {
                    NativeMethods.TimeEndPeriod(1);
                }
                catch { }
            }
        }
    }
}
