using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

public class CodecInstaller : IProxyModule
{
    public string Name => "CodecOptimizer";
    private Logger? _logger;

    public Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;
        _logger.Trace("Initializing CodecInstaller...");
        _ = RunOptimizationAsync();
        return Task.CompletedTask;
    }

    private async Task RunOptimizationAsync()
    {
        _logger?.Info("Starting silent hardware codec optimization...");
        
        await Task.Run(() => {
            _logger?.Trace("Checking for AV1 Video Extension...");
            TryInstallCodec("AV1 Video Extension", "9MVZQVXJBQ9V", "Microsoft.AV1VideoExtension");
            _logger?.Trace("Checking for HEVC Video Extension...");
            TryInstallCodec("HEVC Video Extension", "9NMZLZ57R3T7", "Microsoft.HEVCVideoExtension");
            _logger?.Trace("Checking for VP9 Video Extensions...");
            TryInstallCodec("VP9 Video Extensions", "9N4D0MSV0403", "Microsoft.VP9VideoExtensions");
        });

        _logger?.Success("Hardware codec check complete.");
    }

    public void Shutdown() { }

    private void TryInstallCodec(string name, string storeId, string packageFamilyName)
    {
        try
        {
            if (IsInstalled(packageFamilyName, storeId))
            {
                _logger?.Info(name + " is already installed.");
                return;
            }

            _logger?.Info("Installing " + name + " silently via winget...");

            string args = "install --id " + storeId + " --source msstore --accept-package-agreements --accept-source-agreements --silent";
            
            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                process.WaitForExit(60000); 
                if (process.ExitCode == 0)
                {
                    _logger?.Success(name + " installed successfully.");
                }
                else
                {
                    _logger?.Warning(name + " installation returned code: " + process.ExitCode);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("Failed to automate " + name + " install: " + ex.Message);
        }
    }

    public void InstallAV1() => OpenUri("ms-windows-store://pdp/?ProductId=9MVZQVXJBQ9V");
    public void InstallHEVC() => OpenUri("ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7");
    public void InstallVP9() => OpenUri("ms-windows-store://pdp/?ProductId=9N4D0MSV0403");

    private void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
        }
        catch (Exception ex) { _logger?.Error("Failed to open store: " + ex.Message); }
    }

    private bool IsInstalled(string packageFamilyName, string storeId)
    {
        try
        {
            string script = "Get-AppxPackage -Name '" + packageFamilyName + "'";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + script + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using (var process = Process.Start(psi))
            {
                if (process != null)
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    if (!string.IsNullOrWhiteSpace(output)) return true;
                }
            }

            var wpsi = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "list --id " + storeId + " --source msstore",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using (var wprocess = Process.Start(wpsi))
            {
                if (wprocess != null)
                {
                    string woutput = wprocess.StandardOutput.ReadToEnd();
                    wprocess.WaitForExit();
                    if (wprocess.ExitCode == 0 && !string.IsNullOrWhiteSpace(woutput) && !woutput.Contains("No installed package found")) return true;
                }
            }

            return false;
        }
        catch { return false; }
    }
}
