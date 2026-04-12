using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

[SupportedOSPlatform("windows")]
public class PatcherService : IProxyModule, IDisposable
{
    public string Name => "Patcher";
    public string? VrcToolsDir => _vrcToolsDir;

    private Logger? _logger;
    private IModuleContext? _context;
    private string? _vrcToolsDir;
    private string? _wrapperPath;
    private bool _isPatchDesired = false;
    private readonly CancellationTokenSource _cts = new();
    private Task? _monitorTask;
    private DateTime _lastPatchTime = DateTime.MinValue;

    public Task InitializeAsync(IModuleContext context)
    {
        _context = context;
        _logger = context.Logger;
        DetectVrcPath();
        return Task.CompletedTask;
    }

    private void DetectVrcPath()
    {
        try
        {
            if (_context != null && !string.IsNullOrEmpty(_context.Settings.Config.CustomVrcPath))
            {
                if (Directory.Exists(_context.Settings.Config.CustomVrcPath))
                {
                    _vrcToolsDir = _context.Settings.Config.CustomVrcPath;
                    _logger?.Success("Using Custom VRChat Tools path: " + _vrcToolsDir);
                    return;
                }
                else
                {
                    _logger?.Warning("Custom VRChat path configured but does not exist: " + _context.Settings.Config.CustomVrcPath);
                }
            }

            string localLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low", "VRChat", "VRChat", "Tools");
            if (Directory.Exists(localLow))
            {
                _vrcToolsDir = localLow;
                _logger?.Success("VRChat Tools found: " + _vrcToolsDir);
                return;
            }
            _logger?.Warning("VRChat Tools folder missing at default location.");
        }
        catch (Exception ex) { _logger?.Error("Path Detection Error: " + ex.Message); }
    }

    public void UpdateToolsDir(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        if (_vrcToolsDir == path) return;

        _vrcToolsDir = path;
        _logger?.Success("Tools path updated: " + _vrcToolsDir);
    }

    public void WipeToolsFolder()
    {
        if (string.IsNullOrEmpty(_vrcToolsDir) || !Directory.Exists(_vrcToolsDir)) return;

        _logger?.Info("Wiping VRChat Tools folder for clean state...");
        try
        {
            Shutdown();
            foreach (var f in Directory.GetFiles(_vrcToolsDir))
            {
                try { File.Delete(f); }
                catch (Exception ex) { _logger?.Debug("Failed to delete file during wipe (" + Path.GetFileName(f) + "): " + ex.Message); }
            }
            foreach (var d in Directory.GetDirectories(_vrcToolsDir))
            {
                try { Directory.Delete(d, true); }
                catch (Exception ex) { _logger?.Debug("Failed to delete directory during wipe (" + Path.GetFileName(d) + "): " + ex.Message); }
            }
            _logger?.Success("Tools folder wiped.");
        }
        catch (Exception ex) { _logger?.Error("Wipe failed: " + ex.Message); }
    }

    public void StartMonitoring(string wrapperPath)
    {
        _wrapperPath = wrapperPath;
        _isPatchDesired = true;
        if (_monitorTask == null) _monitorTask = Task.Run(MonitorLoop);
    }

    public List<string> GetJunkItems()
    {
        var junk = new List<string>();
        if (string.IsNullOrEmpty(_vrcToolsDir) || !Directory.Exists(_vrcToolsDir)) return junk;

        try
        {
            foreach (var f in Directory.GetFiles(_vrcToolsDir))
            {
                string name = Path.GetFileName(f);
                if (name.Equals("yt-dlp.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("yt-dlp-og.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("relay_port.dat", StringComparison.OrdinalIgnoreCase)) continue;
                junk.Add(f);
            }
            foreach (var d in Directory.GetDirectories(_vrcToolsDir))
            {
                junk.Add(d);
            }
        }
        catch (Exception ex) { _logger?.Warning("Failed to enumerate junk items in Tools folder: " + ex.Message); }
        return junk;
    }

    public void CleanupJunk()
    {
        var items = GetJunkItems();
        if (items.Count == 0) return;

        _logger?.Info("Cleaning up " + items.Count + " junk items from Tools folder...");
        foreach (var item in items)
        {
            try
            {
                if (File.Exists(item)) File.Delete(item);
                else if (Directory.Exists(item)) Directory.Delete(item, true);
            }
            catch (Exception ex) { _logger?.Debug("Cleanup failed for " + item + ": " + ex.Message); }
        }
        _logger?.Success("Tools folder cleaned.");
    }

    private async Task MonitorLoop()
    {
        _logger?.Info("Patch monitor active.");
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_isPatchDesired && !string.IsNullOrEmpty(_vrcToolsDir) && !string.IsNullOrEmpty(_wrapperPath))
                {
                    await EnsurePatchApplied();
                }
            }
            catch (Exception ex) { _logger?.Warning("Patch monitor error: " + ex.Message, ex); }
            await Task.Delay(3000, _cts.Token);
        }
    }

    private async Task EnsurePatchApplied()
    {
        string targetPath = Path.Combine(_vrcToolsDir!, "yt-dlp.exe");
        string backupPath = Path.Combine(_vrcToolsDir!, "yt-dlp-og.exe");

        if (!File.Exists(targetPath)) 
        {
            try {
                File.Copy(_wrapperPath!, targetPath, true);
                _lastPatchTime = DateTime.Now;
                _logger?.Success("Patch applied (yt-dlp.exe created).");
            } catch (Exception ex) { _logger?.Error("Failed to apply patch (copy yt-dlp.exe): " + ex.Message, ex); }
            return;
        }

        if (!File.Exists(backupPath))
        {
            try {
                File.Move(targetPath, backupPath);
                File.Copy(_wrapperPath!, targetPath, true);
                _lastPatchTime = DateTime.Now;
                _logger?.Info("Patch initialized (Backup created).");
            } catch (Exception ex) { _logger?.Error("Failed to initialize patch (backup/copy): " + ex.Message, ex); }
            return;
        }

        if (IsFileSame(targetPath, _wrapperPath!)) return;
        if ((DateTime.Now - _lastPatchTime).TotalSeconds < 3) return;

        try
        {
            File.Copy(_wrapperPath!, targetPath, true);
            _lastPatchTime = DateTime.Now;
            _logger?.Warning("Patch integrity restored (yt-dlp.exe was modified or replaced).");
        }
        catch (Exception ex) { _logger?.Warning("Failed to restore patch integrity (file in use, will retry): " + ex.Message); }
    }

    private bool IsFileSame(string path1, string path2)
    {
        string hash1 = HashUtils.GetFileHash(path1);
        string hash2 = HashUtils.GetFileHash(path2);
        if (string.IsNullOrEmpty(hash1) || string.IsNullOrEmpty(hash2)) return false;
        return hash1 == hash2;
    }

    public ModuleHealthReport GetHealthReport()
    {
        bool hasToolsDir = !string.IsNullOrEmpty(_vrcToolsDir) && Directory.Exists(_vrcToolsDir);
        return new ModuleHealthReport
        {
            ModuleName = Name,
            Status = hasToolsDir ? HealthStatus.Healthy : HealthStatus.Degraded,
            Reason = hasToolsDir ? "" : "VRChat Tools folder not found",
            LastChecked = DateTime.Now
        };
    }

    public void Shutdown()
    {
        _isPatchDesired = false;
        _cts.Cancel();

        // Restore yt-dlp.exe — only possible if we know the tools dir.
        if (!string.IsNullOrEmpty(_vrcToolsDir))
        {
            try
            {
                string targetPath = Path.Combine(_vrcToolsDir, "yt-dlp.exe");
                string backupPath = Path.Combine(_vrcToolsDir, "yt-dlp-og.exe");

                if (File.Exists(backupPath))
                {
                    _logger?.Info("Restoring original yt-dlp.exe...");
                    if (File.Exists(targetPath)) File.Delete(targetPath);
                    File.Move(backupPath, targetPath);
                    _logger?.Success("Original state restored.");
                }
            }
            catch (Exception ex)
            {
                _logger?.Error("Shutdown Restore Error: " + ex.Message);
            }
        }

        // Process cleanup — always runs regardless of tools dir or restore outcome.
        // ProcessGuard (Job Object) handles the primary kill; this is a belt-and-suspenders
        // fallback for any stray processes that were started outside the job.
        foreach (var proc in Process.GetProcessesByName("curl-impersonate-win"))
        {
            try { proc.Kill(); }
            catch { /* Shutdown cleanup — failure is expected */ }
        }
        foreach (var proc in Process.GetProcessesByName("bgutil-ytdlp-pot-provider"))
        {
            try { proc.Kill(); }
            catch { /* Shutdown cleanup — failure is expected */ }
        }

        string relayPortFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKVRCProxy", "relay_port.dat");
        if (File.Exists(relayPortFile))
        {
            try { File.Delete(relayPortFile); }
            catch { /* Shutdown cleanup — failure is expected */ }
        }
    }

    public void Dispose()
    {
        Shutdown();
        _cts.Dispose();
    }
}
