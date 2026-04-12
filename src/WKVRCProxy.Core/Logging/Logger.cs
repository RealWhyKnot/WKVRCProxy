using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;

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
    private readonly string _source;
    private SystemEventBus? _eventBus;

    private readonly BlockingCollection<QueueItem> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;

    private static readonly ConcurrentQueue<LogEntry> _history = new();

    public static event Action<LogEntry>? OnLog;

    public static IEnumerable<LogEntry> GetHistory() => _history.ToArray();

    // Private wrapper carries exception alongside the public LogEntry.
    // The file writer uses both; the UI broadcast uses only the LogEntry.
    private class QueueItem
    {
        public LogEntry Entry { get; init; } = null!;
        public Exception? Exception { get; init; }
    }

    public Logger(string baseDir, string source, SettingsManager settings)
    {
        _source = source;
        _settings = settings;

        // --- Log Rotation: keep last 5 session files ---
        // Delete legacy single-file log if present (upgrade migration)
        string legacyLog = Path.Combine(baseDir, "wkvrcproxy.log");
        try { if (File.Exists(legacyLog)) File.Delete(legacyLog); } catch { }

        // Collect existing rotated logs, oldest first
        var existing = Directory.GetFiles(baseDir, "wkvrcproxy_*.log")
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.CreationTimeUtc)
            .ToList();

        // Delete oldest until there is room for the new file (keep at most 4)
        while (existing.Count >= 5)
        {
            try { existing[0].Delete(); } catch { }
            existing.RemoveAt(0);
        }

        // Create new session log with timestamp in name
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        _logFile = Path.Combine(baseDir, $"wkvrcproxy_{stamp}.log");

        // Write session banner so the log is immediately readable in context
        try
        {
            var header = new StringBuilder();
            header.AppendLine("==========================================================");
            header.AppendLine($"  WKVRCProxy Session Started — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            header.AppendLine($"  Machine : {Environment.MachineName}");
            header.AppendLine($"  OS      : {RuntimeInformation.OSDescription}");
            header.AppendLine($"  Runtime : {RuntimeInformation.FrameworkDescription}");
            header.AppendLine("==========================================================");
            File.WriteAllText(_logFile, header.ToString());
        }
        catch { }

        _writerTask = Task.Run(ProcessQueue);
    }

    public void SetEventBus(SystemEventBus bus) { _eventBus = bus; }

    private async Task ProcessQueue()
    {
        while (!_cts.IsCancellationRequested)
        {
            QueueItem? item = null;
            try
            {
                item = _queue.Take(_cts.Token);
                var entry = item.Entry;

                // --- File write: full detail including stack trace ---
                var sb = new StringBuilder();
                sb.Append('[');
                sb.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.Append("] [");
                sb.Append(entry.Level.ToString().ToUpperInvariant());
                sb.Append("] [");
                sb.Append(entry.Source);
                sb.Append("] ");
                sb.AppendLine(entry.Message);

                if (item.Exception != null)
                {
                    sb.AppendLine($"  Exception: {item.Exception.GetType().FullName}: {item.Exception.Message}");
                    if (item.Exception.StackTrace != null)
                    {
                        foreach (string traceLine in item.Exception.StackTrace.Split('\n'))
                            sb.AppendLine("    " + traceLine.TrimEnd());
                    }
                    if (item.Exception.InnerException != null)
                    {
                        sb.AppendLine($"  InnerException: {item.Exception.InnerException.GetType().FullName}: {item.Exception.InnerException.Message}");
                        if (item.Exception.InnerException.StackTrace != null)
                        {
                            foreach (string traceLine in item.Exception.InnerException.StackTrace.Split('\n'))
                                sb.AppendLine("    " + traceLine.TrimEnd());
                        }
                    }
                }

                try { await File.AppendAllTextAsync(_logFile, sb.ToString()); }
                catch { /* disk write failed — continue so UI still gets the event */ }

                // --- In-memory history and UI broadcast: clean message only, never the stack trace ---
                _history.Enqueue(entry);
                while (_history.Count > 1000) _history.TryDequeue(out _);

                OnLog?.Invoke(entry);
                _eventBus?.PublishLog(entry);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // If an entry was dequeued, still try to fire UI event so it isn't silently lost
                if (item != null)
                    try { OnLog?.Invoke(item.Entry); } catch { }
            }
        }
    }

    public void Log(LogLevel level, string message, Exception? ex = null)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Message = message,
            Source = _source
        };

        if (!_queue.IsAddingCompleted)
            _queue.Add(new QueueItem { Entry = entry, Exception = ex });
    }

    public void LogWithSource(LogLevel level, string source, string message)
    {
        var entry = new LogEntry { Timestamp = DateTime.Now, Level = level, Message = message, Source = source };
        if (!_queue.IsAddingCompleted)
            _queue.Add(new QueueItem { Entry = entry });
    }

    public void Trace(string msg, Exception? ex = null)   => Log(LogLevel.Trace, msg, ex);
    public void Debug(string msg, Exception? ex = null)   => Log(LogLevel.Debug, msg, ex);
    public void Info(string msg, Exception? ex = null)    => Log(LogLevel.Info, msg, ex);
    public void Success(string msg, Exception? ex = null) => Log(LogLevel.Success, msg, ex);
    public void Warning(string msg, Exception? ex = null) => Log(LogLevel.Warning, msg, ex);
    public void Error(string msg, Exception? ex = null)   => Log(LogLevel.Error, msg, ex);
    public void Fatal(string msg, Exception? ex = null)   => Log(LogLevel.Fatal, msg, ex);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _cts.Cancel();
        try { _writerTask.Wait(2000); } catch { }
        _queue.Dispose();
        _cts.Dispose();
    }
}
