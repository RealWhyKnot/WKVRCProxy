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
    private readonly CancellationTokenSource _cts = new();
    private Task? _monitorTask;
    private bool _vrcToolsDetected = false;
    
    public string CurrentPlayer { get; private set; } = "AVPro";
    public event Action<string>? OnVrcPathDetected;
    
    public Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;
        _logger.Trace("Initializing VrcLogMonitor...");
        _logger.Info("Starting VRChat Log Monitor...");
        _monitorTask = Task.Run(MonitorLoop);
        return Task.CompletedTask;
    }

    public void Shutdown()
    {
        _logger?.Trace("Shutting down VrcLogMonitor...");
        _cts.Cancel();
    }
    
    private async Task MonitorLoop()
    {
        string vrcDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low", "VRChat", "VRChat");
        _logger?.Trace("Monitoring VRChat logs at: " + vrcDir);
        string currentFile = "";
        long lastSize = 0;
        
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!Directory.Exists(vrcDir))
                {
                    _logger?.Trace("VRChat directory not found, retrying in 5s...");
                    await Task.Delay(5000, _cts.Token);
                    continue;
                }

                string localTools = Path.Combine(vrcDir, "Tools");
                if (Directory.Exists(localTools)) 
                {
                    if (_vrcToolsDetected == false)
                    {
                        _logger?.Trace("Local Tools folder detected: " + localTools);
                        _vrcToolsDetected = true;
                    }
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
                        _logger?.Trace("Reading " + (latestLog.Length - lastSize) + " new bytes from log...");
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
                                        _logger?.Trace("VRChat Application Path detected: " + exePath);
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
                            }
                        }
                    }
                }
                
                await Task.Delay(1000, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.Trace("VrcLogMonitor Error: " + ex.Message);
                await Task.Delay(5000, _cts.Token);
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
        catch { }
        return null;
    }
    
    public void Dispose()
    {
        _cts.Cancel();
        try { _monitorTask?.Wait(1000); } catch { }
        _cts.Dispose();
    }
}
