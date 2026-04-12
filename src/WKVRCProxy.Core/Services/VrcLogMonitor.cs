using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

public class VrcLogMonitor : IProxyModule, IDisposable
{
    public string Name => "LogMonitor";
    private Logger? _logger;
    private SettingsManager? _settings;
    private readonly CancellationTokenSource _cts = new();
    private Task? _monitorTask;
    private Task? _redirectorLogTask;
    private bool _vrcToolsDetected = false;

    public string CurrentPlayer { get; private set; } = "AVPro";
    public event Action<string>? OnVrcPathDetected;

    public Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;
        _settings = context.Settings;
        _logger.Info("Starting VRChat Log Monitor...");
        _monitorTask = Task.Run(MonitorLoop);
        _redirectorLogTask = Task.Run(TailRedirectorLog);
        return Task.CompletedTask;
    }

    public void Shutdown()
    {
        _cts.Cancel();
    }
    
    private async Task MonitorLoop()
    {
        string vrcDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low", "VRChat", "VRChat");
        string currentFile = "";
        long lastSize = 0;
        
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!Directory.Exists(vrcDir))
                {
                    await Task.Delay(5000, _cts.Token);
                    continue;
                }

                string localTools = Path.Combine(vrcDir, "Tools");
                if (Directory.Exists(localTools) && !_vrcToolsDetected)
                {
                    _vrcToolsDetected = true;
                    OnVrcPathDetected?.Invoke(localTools);
                }
                
                var latestLog = new DirectoryInfo(vrcDir)
                    .GetFiles("output_log*.txt")
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();
                    
                if (latestLog != null)
                {
                    if (latestLog.FullName != currentFile)
                    {
                        currentFile = latestLog.FullName;
                        lastSize = 0;
                        _logger?.Info("Tracking new VRChat log: " + currentFile);
                    }
                    
                    if (latestLog.Length > lastSize)
                    {
                        using (var fs = new FileStream(currentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            fs.Seek(lastSize, SeekOrigin.Begin);
                            using (var reader = new StreamReader(fs))
                            {
                                string newContent = await reader.ReadToEndAsync();
                                lastSize = fs.Position;
                                
                                if (newContent.Contains("Application Path:"))
                                {
                                    var match = Regex.Match(newContent, @"Application Path:\s*(.+)");
                                    if (match.Success)
                                    {
                                        string exePath = match.Groups[1].Value.Trim();
                                        _logger?.Debug("VRChat Application Path detected: " + exePath);
                                        string? toolsDir = DetectToolsFromExe(exePath);
                                        if (toolsDir != null) OnVrcPathDetected?.Invoke(toolsDir);
                                    }
                                }

                                if (newContent.Contains("Video component initialization: Unity") || newContent.Contains("UnityVideoPlayer"))
                                {
                                    if (CurrentPlayer != "Unity")
                                    {
                                        CurrentPlayer = "Unity";
                                        _logger?.Info("Player Engine Switch Detected: Unity Video Player");
                                    }
                                }
                                else if (newContent.Contains("Video component initialization: AVPro") || newContent.Contains("AVProVideo"))
                                {
                                    if (CurrentPlayer != "AVPro")
                                    {
                                        CurrentPlayer = "AVPro";
                                        _logger?.Info("Player Engine Switch Detected: AVPro Video Player");
                                    }
                                }

                                ForwardVrcLogLines(newContent);
                            }
                        }
                    }
                }
                
                await Task.Delay(1000, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.Warning("VrcLogMonitor Error: " + ex.Message, ex);
                await Task.Delay(5000, _cts.Token);
            }
        }
    }

    // Tail the Redirector's log file so child process connection failures
    // are visible in the main logger instead of siloed in a separate file.
    private async Task TailRedirectorLog()
    {
        string logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WKVRCProxy", "yt-dlp-wrapper.log");
        long lastSize = 0;

        // Start from current end so we don't replay old entries on app restart
        if (File.Exists(logPath))
        {
            try { lastSize = new FileInfo(logPath).Length; } catch { }
        }

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, _cts.Token);
                if (!File.Exists(logPath)) continue;

                long currentSize = new FileInfo(logPath).Length;
                if (currentSize <= lastSize)
                {
                    // File was truncated or unchanged
                    if (currentSize < lastSize) lastSize = 0;
                    continue;
                }

                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(lastSize, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);
                string newContent = await reader.ReadToEndAsync();
                lastSize = fs.Position;

                foreach (string rawLine in newContent.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    _logger?.LogWithSource(LogLevel.Debug, "Redirector", line);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* File locked or inaccessible — retry next cycle */ }
        }
    }

    private void ForwardVrcLogLines(string content)
    {
        if (_logger == null) return;

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // Only forward lines that directly help diagnose video resolution issues.
            // Excluded: AVPro init spam, VideoTXL play/stop/loop events, player registrations,
            // "No MediaReference" errors, JSON stats blobs, MovieCapture init, etc.
            bool isRelevant = line.Contains("NativeProcess.Start:")
                           || line.Contains("NativeProcess.HasExited:")
                           || line.Contains("loading URL by user")
                           || line.Contains("Load Url:")
                           || line.Contains("Now Playing:")
                           || line.Contains("[AVProVideo] Error:")
                           || line.Contains("[AVProVideo] Opening ")
                           || line.Contains("[VRC.SDK3.Video]")
                           || line.Contains("[VRC.SDK2.Video]");

            // Exclude known noise that matches the above patterns
            if (line.Contains("No MediaReference") || line.Contains("No file path specified"))
                isRelevant = false;

            if (isRelevant)
            {
                _logger.LogWithSource(LogLevel.Info, "VRChat", line);
            }
        }
    }

    private string? DetectToolsFromExe(string exePath)
    {
        try
        {
            string? root = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(root)) return null;
            string toolsDir = Path.Combine(root, "VRChat_Data", "StreamingAssets", "Tools");
            if (Directory.Exists(toolsDir)) return toolsDir;
        }
        catch (Exception ex) { _logger?.Debug("Failed to detect Tools dir from exe path: " + ex.Message); }
        return null;
    }
    
    public void Dispose()
    {
        _cts.Cancel();
        try { _monitorTask?.Wait(1000); }
        catch { /* Shutdown cleanup — failure is expected */ }
        _cts.Dispose();
    }
}
