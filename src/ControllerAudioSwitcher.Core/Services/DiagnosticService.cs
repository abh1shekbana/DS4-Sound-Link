using ControllerAudioSwitcher.Core.Interfaces;

namespace ControllerAudioSwitcher.Core.Services;

public sealed class DiagnosticService : IDiagnosticService
{
    private readonly object _syncRoot = new();
    private readonly List<LogEntry> _recentLogs = new();
    private const int MaxLogEntries = 1000;

    public event EventHandler<LogEntry>? LogReceived;

    public IReadOnlyList<LogEntry> RecentLogs
    {
        get
        {
            lock (_syncRoot)
            {
                return _recentLogs.ToList();
            }
        }
    }

    public void Log(LogLevel level, string message, Exception? ex = null)
    {
        var entry = new LogEntry
        {
            Level = level,
            Message = message,
            Exception = ex
        };

        lock (_syncRoot)
        {
            _recentLogs.Add(entry);
            if (_recentLogs.Count > MaxLogEntries)
            {
                _recentLogs.RemoveAt(0);
            }
        }

        LogReceived?.Invoke(this, entry);
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _recentLogs.Clear();
        }
    }
}
