namespace ControllerAudioSwitcher.Core.Interfaces;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public sealed class LogEntry
{
    public DateTime Timestamp { get; } = DateTime.Now;
    public LogLevel Level { get; init; }
    public string Message { get; init; } = string.Empty;
    public Exception? Exception { get; init; }

    public override string ToString() => $"[{Timestamp:HH:mm:ss.fff}] [{Level}] {Message}";
}

public interface IDiagnosticService
{
    event EventHandler<LogEntry>? LogReceived;
    IReadOnlyList<LogEntry> RecentLogs { get; }

    void Log(LogLevel level, string message, Exception? ex = null);
    void Debug(string message) => Log(LogLevel.Debug, message);
    void Info(string message) => Log(LogLevel.Info, message);
    void Warn(string message, Exception? ex = null) => Log(LogLevel.Warning, message, ex);
    void Error(string message, Exception? ex = null) => Log(LogLevel.Error, message, ex);
    void Clear();
}
