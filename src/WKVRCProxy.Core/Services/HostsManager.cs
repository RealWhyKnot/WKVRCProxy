using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

public class HostsManager : IProxyModule
{
    public string Name => "HostsManager";
    private Logger? _logger;
    private IModuleContext? _context;

    public event Action<string, object>? OnIpcRequest;
    private TaskCompletionSource<bool>? _hostsSetupTcs;

    public Task InitializeAsync(IModuleContext context)
    {
        _context = context;
        _logger = context.Logger;
        _logger.Trace("Initializing HostsManager...");

        // We run the check in the background so it doesn't block other modules
        _ = CheckAndPromptHostsAsync();
        
        return Task.CompletedTask;
    }

    private async Task CheckAndPromptHostsAsync()
    {
        if (IsBypassActive())
        {
            _logger?.Success("Hosts bypass is already active.");
            return;
        }

        if (_context?.Settings.Config.BypassHostsSetupDeclined == true)
        {
            _logger?.Info("Hosts bypass setup previously declined. Skipping prompt.");
            return;
        }

        bool accepted = await RequestBypassAsync();
        if (accepted)
        {
            _logger?.Info("User accepted hosts setup. Spawning elevated process...");
            try
            {
                var procInfo = new ProcessStartInfo {
                    FileName = Process.GetCurrentProcess().MainModule?.FileName ?? "",
                    Arguments = "--setup-hosts",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                var proc = Process.Start(procInfo);
                proc?.WaitForExit();
                
                if (IsBypassActive())
                {
                    _logger?.Success("Hosts bypass setup successful.");
                }
                else
                {
                    _logger?.Warning("Hosts bypass setup completed but the entry was not found.");
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                _logger?.Warning("User canceled UAC prompt for hosts setup.");
            }
            catch (Exception ex)
            {
                _logger?.Error("Failed to setup hosts file: " + ex.Message);
            }
        }
        else
        {
            _logger?.Info("User declined hosts setup.");
            if (_context != null)
            {
                _context.Settings.Config.BypassHostsSetupDeclined = true;
                _context.Settings.Save();
            }
        }
    }

    public bool IsBypassActive()
    {
        string hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        if (!File.Exists(hostsPath)) return false;

        try
        {
            using var fileStream = new FileStream(hostsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fileStream);
            string? originalLine;
            while ((originalLine = reader.ReadLine()) != null)
            {
                string line = originalLine.Trim();
                if (line.StartsWith("#")) continue;
                if (line.Contains("127.0.0.1") && line.Contains("localhost.youtube.com"))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning("Could not read hosts file: " + ex.Message);
        }

        return false;
    }

    public async Task<bool> RequestBypassAsync()
    {
        _hostsSetupTcs = new TaskCompletionSource<bool>();
        OnIpcRequest?.Invoke("PROMPT_HOSTS_SETUP", new { });
        return await _hostsSetupTcs.Task;
    }

    public void HandleUserResponse(bool accepted)
    {
        if (_hostsSetupTcs != null && !_hostsSetupTcs.Task.IsCompleted)
        {
            _hostsSetupTcs.SetResult(accepted);
        }
    }

    public void Shutdown()
    {
        // Cancel pending tcs if any
        if (_hostsSetupTcs != null && !_hostsSetupTcs.Task.IsCompleted)
        {
            _hostsSetupTcs.SetCanceled();
        }
    }
}
