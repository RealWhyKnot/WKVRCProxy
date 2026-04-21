using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

// Keeps tools/yt-dlp.exe current. Runs once per launch when AutoUpdateYtDlp is enabled:
// 1. Query GitHub's "latest release" endpoint for yt-dlp/yt-dlp.
// 2. Compare the tag to the local binary's --version output. yt-dlp versions are date-based
//    (e.g. 2026.03.27) so lexical comparison is sufficient.
// 3. If a newer version exists, download yt-dlp.exe to a shadow path, run --version on the
//    shadow copy to prove it's not corrupted, then atomic-swap over the live file.
//
// Never touches yt-dlp-og.exe — that's VRChat's pinned copy and Tier 3 must keep its current
// extractor behaviour (see CLAUDE / memory: VRChat movie worlds + Tier 3 fallback).
[SupportedOSPlatform("windows")]
public class YtDlpUpdater : IProxyModule
{
    public string Name => "YtDlpUpdater";

    private Logger? _logger;
    private SettingsManager? _settings;
    private SystemEventBus? _eventBus;
    private readonly HttpClient _http;

    private string _toolsDir = "";
    private string _livePath = "";
    private string _shadowPath = "";

    private string _localVersion = "";
    private string _remoteVersion = "";
    private UpdateStatus _lastStatus = UpdateStatus.Idle;
    private string _lastStatusDetail = "";

    public enum UpdateStatus { Idle, Checking, UpToDate, UpdateAvailable, Downloading, Updated, Failed, Disabled }

    public string LocalVersion => _localVersion;
    public string RemoteVersion => _remoteVersion;
    public UpdateStatus Status => _lastStatus;
    public string StatusDetail => _lastStatusDetail;

    // Fires after each status transition so the UI can reflect progress without polling.
    public event Action<UpdateStatus, string, string, string>? OnStatusChanged;

    private const string LatestReleaseUrl = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    private const string AssetName = "yt-dlp.exe";

    public YtDlpUpdater()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        // GitHub rejects unidentified user-agents with 403.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WKVRCProxy-YtDlpUpdater/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;
        _settings = context.Settings;
        _eventBus = context.EventBus;
        _toolsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");
        _livePath = Path.Combine(_toolsDir, "yt-dlp.exe");
        _shadowPath = Path.Combine(_toolsDir, "yt-dlp.new.exe");

        if (!_settings.Config.AutoUpdateYtDlp)
        {
            _lastStatus = UpdateStatus.Disabled;
            _lastStatusDetail = "Auto-update disabled in settings.";
            _logger.Debug("[YtDlpUpdater] AutoUpdateYtDlp is false — skipping update check.");
            return Task.CompletedTask;
        }

        // Fire-and-forget so startup isn't blocked on a network call. One attempt per launch; if it
        // fails we surface the failure via health report and the next launch tries again.
        _ = Task.Run(CheckAndUpdateAsync);
        return Task.CompletedTask;
    }

    private async Task CheckAndUpdateAsync()
    {
        try
        {
            if (!File.Exists(_livePath))
            {
                SetStatus(UpdateStatus.Failed, "yt-dlp.exe missing from tools/.");
                return;
            }

            SetStatus(UpdateStatus.Checking, "Checking for yt-dlp updates...");

            _localVersion = await ReadLocalVersionAsync(_livePath);
            if (string.IsNullOrEmpty(_localVersion))
            {
                SetStatus(UpdateStatus.Failed, "Failed to read local yt-dlp version.");
                return;
            }

            _logger?.Debug("[YtDlpUpdater] Local yt-dlp version: " + _localVersion);

            _remoteVersion = await FetchLatestTagAsync();
            if (string.IsNullOrEmpty(_remoteVersion))
            {
                SetStatus(UpdateStatus.Failed, "Failed to reach GitHub releases API.");
                return;
            }

            _logger?.Debug("[YtDlpUpdater] Latest yt-dlp release: " + _remoteVersion);

            if (string.Equals(_localVersion, _remoteVersion, StringComparison.OrdinalIgnoreCase)
                || string.Compare(_localVersion, _remoteVersion, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                SetStatus(UpdateStatus.UpToDate, "Local " + _localVersion + " is current.");
                return;
            }

            SetStatus(UpdateStatus.UpdateAvailable, "Update " + _localVersion + " → " + _remoteVersion + " available.");

            SetStatus(UpdateStatus.Downloading, "Downloading " + _remoteVersion + "...");
            string assetUrl = "https://github.com/yt-dlp/yt-dlp/releases/download/" + _remoteVersion + "/" + AssetName;
            bool downloaded = await DownloadToShadowAsync(assetUrl);
            if (!downloaded)
            {
                SetStatus(UpdateStatus.Failed, "Download failed.");
                return;
            }

            string shadowVersion = await ReadLocalVersionAsync(_shadowPath);
            if (string.IsNullOrEmpty(shadowVersion))
            {
                SetStatus(UpdateStatus.Failed, "Downloaded binary did not return a version — refusing to swap.");
                TryDelete(_shadowPath);
                return;
            }

            _logger?.Debug("[YtDlpUpdater] Shadow binary reports version: " + shadowVersion);

            if (!SwapShadowOverLive())
            {
                SetStatus(UpdateStatus.Failed, "Atomic swap failed — yt-dlp may be in use. Restart to retry.");
                return;
            }

            _localVersion = shadowVersion;
            SetStatus(UpdateStatus.Updated, "Updated to " + _remoteVersion + ".");
        }
        catch (Exception ex)
        {
            SetStatus(UpdateStatus.Failed, ex.Message);
        }
    }

    private void SetStatus(UpdateStatus status, string detail)
    {
        _lastStatus = status;
        _lastStatusDetail = detail;
        switch (status)
        {
            case UpdateStatus.Failed:
                _logger?.Warning("[YtDlpUpdater] " + detail);
                break;
            case UpdateStatus.Updated:
                _logger?.Success("[YtDlpUpdater] " + detail);
                break;
            default:
                _logger?.Info("[YtDlpUpdater] " + detail);
                break;
        }
        try { OnStatusChanged?.Invoke(status, detail, _localVersion, _remoteVersion); }
        catch { /* UI dispatch failures must not break the update pipeline */ }
    }

    private async Task<string> ReadLocalVersionAsync(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            string stdout = await p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(true); } catch { }
                return "";
            }
            return stdout.Trim();
        }
        catch { return ""; }
    }

    private async Task<string> FetchLatestTagAsync()
    {
        try
        {
            using var resp = await _http.GetAsync(LatestReleaseUrl);
            if (!resp.IsSuccessStatusCode) return "";
            string body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("tag_name", out var tag))
                return tag.GetString() ?? "";
        }
        catch { }
        return "";
    }

    private async Task<bool> DownloadToShadowAsync(string assetUrl)
    {
        try
        {
            TryDelete(_shadowPath);
            using var resp = await _http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return false;
            using var fs = File.Create(_shadowPath);
            await resp.Content.CopyToAsync(fs);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Debug("[YtDlpUpdater] Download error: " + ex.Message);
            TryDelete(_shadowPath);
            return false;
        }
    }

    private bool SwapShadowOverLive()
    {
        string backup = _livePath + ".old";
        try
        {
            TryDelete(backup);
            // File.Replace performs an atomic swap on NTFS and preserves the original as a backup.
            // If the process is running (unlikely on startup) Windows returns ERROR_SHARING_VIOLATION
            // and we leave things untouched.
            File.Replace(_shadowPath, _livePath, backup, ignoreMetadataErrors: true);
            TryDelete(backup);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Debug("[YtDlpUpdater] Swap error: " + ex.Message);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public ModuleHealthReport GetHealthReport()
    {
        return _lastStatus switch
        {
            UpdateStatus.Failed => new ModuleHealthReport {
                ModuleName = Name,
                Status = HealthStatus.Degraded,
                Reason = "yt-dlp update: " + _lastStatusDetail,
                LastChecked = DateTime.Now
            },
            UpdateStatus.UpdateAvailable => new ModuleHealthReport {
                ModuleName = Name,
                Status = HealthStatus.Degraded,
                Reason = "yt-dlp update pending (" + _localVersion + " → " + _remoteVersion + ")",
                LastChecked = DateTime.Now
            },
            _ => new ModuleHealthReport {
                ModuleName = Name,
                Status = HealthStatus.Healthy,
                Reason = _lastStatusDetail,
                LastChecked = DateTime.Now
            }
        };
    }

    public void Shutdown()
    {
        try { _http.Dispose(); } catch { }
    }
}
