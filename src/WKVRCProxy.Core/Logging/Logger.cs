using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WKVRCProxy.Core.Logging;

public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Success,
    Warning,
    Error,
    Fatal
}

public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

public class Logger : IDisposable
{
    private readonly string _logFile;
    private readonly SettingsManager _settings;
    private readonly BlockingCollection<LogEntry> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;
    private readonly string _source;
    private static readonly ConcurrentQueue<LogEntry> _history = new();

    public static event Action<LogEntry>? OnLog;

    public static IEnumerable<LogEntry> GetHistory() => _history.ToArray();

    public Logger(string baseDir, string source, SettingsManager settings)
    {
        _logFile = Path.Combine(baseDir, "wkvrcproxy.log");
        _source = source;
        _settings = settings;

        // Auto-wipe log if larger than 5MB to prevent growth issues
        try
        {
            if (File.Exists(_logFile) && new FileInfo(_logFile).Length > 5 * 1024 * 1024)
            {
                File.Delete(_logFile);
            }
        }
        catch { }

        _writerTask = Task.Run(ProcessQueue);
    }

    private async Task ProcessQueue()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var entry = _queue.Take(_cts.Token);
                string line = "[" + entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] [" + entry.Level.ToString() + "] [" + entry.Source + "] " + entry.Message;
                
                File.AppendAllText(_logFile, line + Environment.NewLine);
                
                _history.Enqueue(entry);
                while (_history.Count > 1000) _history.TryDequeue(out _);

                OnLog?.Invoke(entry);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    public void Log(LogLevel level, string message)
    {
        if (!_settings.Config.DebugMode && (level == LogLevel.Trace || level == LogLevel.Debug))
        {
            return;
        }

        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Message = message,
            Source = _source
        };

        if (!_queue.IsAddingCompleted)
        {
            _queue.Add(entry);
        }
    }

    public void Trace(string msg) => Log(LogLevel.Trace, msg);
    public void Debug(string msg) => Log(LogLevel.Debug, msg);
    public void Info(string msg) => Log(LogLevel.Info, msg);
    public void Success(string msg) => Log(LogLevel.Success, msg);
    public void Warning(string msg) => Log(LogLevel.Warning, msg);
    public void Error(string msg) => Log(LogLevel.Error, msg);
    public void Fatal(string msg) => Log(LogLevel.Fatal, msg);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _cts.Cancel();
        try { _writerTask.Wait(1000); } catch { }
        _queue.Dispose();
        _cts.Dispose();
    }
}
