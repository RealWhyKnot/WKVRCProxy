using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

// Cloudflare WARP integration. Uses wgcf (account registration → WireGuard config) and wireproxy
// (user-space WG peer that exposes a SOCKS5 proxy). Nothing on the host machine is modified:
//   - No TUN device created (wireproxy is pure user-space).
//   - No admin rights needed (just a loopback TCP listener).
//   - No system routing changes. WARP only affects our own subprocesses that explicitly opt in
//     via --proxy socks5://127.0.0.1:40000.
//
// Files live under tools/warp/:
//   - wireproxy.exe    (pure user-space WG→SOCKS5 bridge)
//   - wgcf.exe         (account registration + config generator)
//   - wgcf-account.toml  (created by `wgcf register`; persists the WARP account)
//   - wgcf-profile.conf  (created by `wgcf generate`; wg-quick format)
//   - wireproxy.conf     (derived from wgcf-profile.conf + [Socks5] section we append)
//
// Gated by AppConfig.EnableWarp. If disabled, this module is a no-op at Init time.
[SupportedOSPlatform("windows")]
public class WarpService : IProxyModule, IDisposable
{
    public string Name => "WarpService";

    private Logger? _logger;
    private SettingsManager? _settings;
    private Process? _wireproxyProcess;
    private string _warpDir = "";
    private string _wireproxyExe = "";
    private string _wgcfExe = "";

    // Fixed SOCKS5 port; yt-dlp strategies reference this via --proxy. If a collision occurs we log
    // and disable WARP rather than relocate — resolvers would need a way to learn the dynamic port.
    public const int SocksPort = 40000;
    public string SocksProxyUrl => "socks5://127.0.0.1:" + SocksPort;

    // Tri-state lifecycle so other modules / UI can see whether WARP is usable.
    public WarpStatus Status { get; private set; } = WarpStatus.Disabled;
    public string? StatusDetail { get; private set; }

    public bool IsActive => Status == WarpStatus.Running;

    public async Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;
        _settings = context.Settings;

        if (!_settings.Config.EnableWarp)
        {
            Status = WarpStatus.Disabled;
            _logger.Debug("[Warp] EnableWarp=false — skipping init.");
            return;
        }

        string toolsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");
        _warpDir = Path.Combine(toolsDir, "warp");
        _wireproxyExe = Path.Combine(_warpDir, "wireproxy.exe");
        _wgcfExe = Path.Combine(_warpDir, "wgcf.exe");

        if (!File.Exists(_wireproxyExe) || !File.Exists(_wgcfExe))
        {
            Status = WarpStatus.BinariesMissing;
            StatusDetail = "tools/warp/wireproxy.exe and/or wgcf.exe are missing. WARP disabled.";
            _logger.Warning("[Warp] " + StatusDetail + " Download wireproxy and wgcf and drop them in tools/warp/ to enable.");
            return;
        }

        try
        {
            Directory.CreateDirectory(_warpDir);

            string accountToml = Path.Combine(_warpDir, "wgcf-account.toml");
            if (!File.Exists(accountToml))
            {
                _logger.Info("[Warp] No wgcf-account.toml — registering new WARP account (one-time).");
                bool ok = await RunWgcfAsync("register --accept-tos");
                if (!ok || !File.Exists(accountToml))
                {
                    Status = WarpStatus.Failed;
                    StatusDetail = "wgcf register failed — see logs. WARP disabled.";
                    _logger.Warning("[Warp] " + StatusDetail);
                    return;
                }
                _logger.Success("[Warp] Registered WARP account.");
            }

            string profileConf = Path.Combine(_warpDir, "wgcf-profile.conf");
            bool profileFresh = File.Exists(profileConf) &&
                (DateTime.UtcNow - File.GetLastWriteTimeUtc(profileConf)) < TimeSpan.FromDays(7);
            if (!profileFresh)
            {
                _logger.Debug("[Warp] Generating fresh wgcf-profile.conf.");
                bool ok = await RunWgcfAsync("generate");
                if (!ok || !File.Exists(profileConf))
                {
                    Status = WarpStatus.Failed;
                    StatusDetail = "wgcf generate failed — see logs. WARP disabled.";
                    _logger.Warning("[Warp] " + StatusDetail);
                    return;
                }
            }

            string wireproxyConf = Path.Combine(_warpDir, "wireproxy.conf");
            WriteWireproxyConfig(profileConf, wireproxyConf);

            if (IsLocalPortOpen(SocksPort))
            {
                Status = WarpStatus.Failed;
                StatusDetail = "Port " + SocksPort + " already in use — another WARP instance? WARP disabled.";
                _logger.Warning("[Warp] " + StatusDetail);
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = _wireproxyExe,
                Arguments = "-c \"" + wireproxyConf + "\"",
                WorkingDirectory = _warpDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _wireproxyProcess = Process.Start(psi);
            if (_wireproxyProcess == null)
            {
                Status = WarpStatus.Failed;
                StatusDetail = "Process.Start returned null for wireproxy.";
                _logger.Error("[Warp] " + StatusDetail);
                return;
            }

            ProcessGuard.Register(_wireproxyProcess);
            PipeStreamToLogger(_wireproxyProcess.StandardOutput, isError: false);
            PipeStreamToLogger(_wireproxyProcess.StandardError, isError: true);

            // Probe the SOCKS port for up to 5s before declaring Running. Wireproxy needs a moment
            // to hand-shake with the WARP endpoint; skipping this yields spurious first-request failures.
            for (int i = 0; i < 25; i++)
            {
                await Task.Delay(200);
                if (_wireproxyProcess.HasExited)
                {
                    Status = WarpStatus.Failed;
                    StatusDetail = "wireproxy exited early (code " + _wireproxyProcess.ExitCode + "). WARP disabled.";
                    _logger.Warning("[Warp] " + StatusDetail);
                    return;
                }
                if (IsLocalPortOpen(SocksPort))
                {
                    Status = WarpStatus.Running;
                    StatusDetail = "SOCKS5 listening on 127.0.0.1:" + SocksPort + ".";
                    _logger.Success("[Warp] WARP active — " + StatusDetail);
                    return;
                }
            }

            Status = WarpStatus.Failed;
            StatusDetail = "SOCKS port " + SocksPort + " never became reachable. WARP disabled.";
            _logger.Warning("[Warp] " + StatusDetail);
        }
        catch (Exception ex)
        {
            Status = WarpStatus.Failed;
            StatusDetail = "Init failed: " + ex.Message;
            _logger.Error("[Warp] " + StatusDetail, ex);
        }
    }

    private async Task<bool> RunWgcfAsync(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _wgcfExe,
            Arguments = args,
            WorkingDirectory = _warpDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null)
            {
                _logger?.Error("[Warp] wgcf start failed — Process.Start returned null.");
                return false;
            }
            ProcessGuard.Register(p);
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000))
            {
                try { p.Kill(true); } catch { }
                _logger?.Warning("[Warp] wgcf '" + args + "' timed out after 30s.");
                return false;
            }
            string outText = (await stdout).Trim();
            string errText = (await stderr).Trim();
            if (!string.IsNullOrEmpty(outText)) _logger?.Debug("[Warp] wgcf: " + outText);
            if (!string.IsNullOrEmpty(errText)) _logger?.Warning("[Warp] wgcf(err): " + errText);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger?.Error("[Warp] wgcf '" + args + "' threw: " + ex.Message);
            return false;
        }
    }

    // Convert wgcf's wg-quick config into a wireproxy config by appending a [Socks5] section.
    // wg-quick format is already close — we just strip any comment lines wgcf adds and append the
    // proxy listener. Any existing wireproxy.conf is overwritten on each run so rotated keys apply.
    private void WriteWireproxyConfig(string wgQuickConfPath, string destPath)
    {
        var sb = new StringBuilder();
        foreach (var line in File.ReadAllLines(wgQuickConfPath))
        {
            string t = line.TrimStart();
            // wg-quick sometimes carries Windows-specific directives that wireproxy doesn't grok.
            if (t.StartsWith("PostUp", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.StartsWith("PostDown", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.StartsWith("Table", StringComparison.OrdinalIgnoreCase)) continue;
            sb.AppendLine(line);
        }
        sb.AppendLine();
        sb.AppendLine("[Socks5]");
        sb.AppendLine("BindAddress = 127.0.0.1:" + SocksPort);
        File.WriteAllText(destPath, sb.ToString());
    }

    private static bool IsLocalPortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync("127.0.0.1", port);
            return task.Wait(250) && client.Connected;
        }
        catch { return false; }
    }

    private void PipeStreamToLogger(StreamReader reader, bool isError)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    if (isError) _logger?.Warning("[Warp] " + line);
                    else _logger?.Debug("[Warp] " + line);
                }
            }
            catch { /* Process exited or stream closed */ }
        });
    }

    public ModuleHealthReport GetHealthReport()
    {
        HealthStatus hs = Status switch
        {
            WarpStatus.Running => HealthStatus.Healthy,
            WarpStatus.Disabled => HealthStatus.Healthy,   // Intentionally off — not a failure.
            WarpStatus.BinariesMissing => HealthStatus.Degraded,
            _ => HealthStatus.Failed
        };
        return new ModuleHealthReport
        {
            ModuleName = Name,
            Status = hs,
            Reason = StatusDetail ?? Status.ToString(),
            LastChecked = DateTime.Now
        };
    }

    public void Shutdown()
    {
        if (_wireproxyProcess != null && !_wireproxyProcess.HasExited)
        {
            try { _wireproxyProcess.Kill(true); }
            catch { /* Shutdown cleanup — failure is expected */ }
            try { _wireproxyProcess.Dispose(); }
            catch { /* Shutdown cleanup — failure is expected */ }
        }
        Status = WarpStatus.Disabled;
    }

    public void Dispose() => Shutdown();
}

public enum WarpStatus
{
    Disabled,         // EnableWarp=false; module is inert
    BinariesMissing,  // tools/warp/wireproxy.exe or wgcf.exe not found
    Running,          // wireproxy is up and SOCKS5 is reachable
    Failed            // Init attempted but failed somewhere
}
