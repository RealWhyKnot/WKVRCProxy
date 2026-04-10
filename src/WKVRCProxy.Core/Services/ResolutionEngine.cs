using System;
using System.Net;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.IPC;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Models;

namespace WKVRCProxy.Core.Services;

public class ResolutionEngine
{
    private readonly Logger _logger;
    private readonly SettingsManager _settings;
    private readonly VrcLogMonitor _monitor;
    private readonly HttpClient _httpClient;
    private readonly Tier2WebSocketClient _tier2Client;
    private readonly HostsManager _hostsManager;
    private readonly RelayPortManager _relayPortManager;

    public event Action<string, object>? OnStatusUpdate;
    private int _activeResolutions = 0;
    private readonly Dictionary<string, int> _tierCounts = new() {
        { "tier1", 0 }, { "tier2", 0 }, { "tier3", 0 }, { "tier4", 0 }
    };

    public ResolutionEngine(Logger logger, SettingsManager settings, VrcLogMonitor monitor, Tier2WebSocketClient tier2Client, HostsManager hostsManager, RelayPortManager relayPortManager)
    {
        _logger = logger;
        _settings = settings;
        _monitor = monitor;
        _tier2Client = tier2Client;
        _hostsManager = hostsManager;
        _relayPortManager = relayPortManager;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(_settings.Config.UserAgent);

        // Initialize counts from history
        foreach (var entry in _settings.Config.History)
        {
            var tierKey = entry.Tier.Split('-')[0];
            if (_tierCounts.ContainsKey(tierKey)) _tierCounts[tierKey]++;
        }
    }

    private void UpdateStatus(string message)
    {
        OnStatusUpdate?.Invoke(message, new {
            activeCount = _activeResolutions,
            tierStats = _tierCounts,
            node = _tier2Client.ActiveNode,
            player = _monitor.CurrentPlayer
        });
    }

    // Quick HEAD request to verify a resolved URL is actually reachable before accepting it.
    // A 3-second timeout is used to not delay resolution significantly.
    private async Task<bool> CheckUrlReachable(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return (int)resp.StatusCode < 400;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> ResolveAsync(ResolvePayload payload)
    {
        _activeResolutions++;
        string? targetUrl = payload.Args.FirstOrDefault(a => a.StartsWith("http"));
        if (string.IsNullOrEmpty(targetUrl))
        {
            _activeResolutions--;
            _logger.Warning("No valid URL found in resolution payload.");
            return null;
        }

        string player = _monitor.CurrentPlayer;
        if (payload.Args.Any(a => a.Contains("AVProVideo"))) player = "AVPro";
        if (payload.Args.Any(a => a.Contains("UnityPlayer"))) player = "Unity";

        _logger.Info("Starting resolution for: " + targetUrl + " [" + player + "]");
        UpdateStatus("Intercepted " + player + " request...");

        string? result = null;
        string activeTier = _settings.Config.PreferredTier;

        try
        {
            if (activeTier == "tier1")
            {
                result = await ResolveTier1(targetUrl, player);
                if (result != null && !await CheckUrlReachable(result))
                {
                    _logger.Warning("Tier 1 URL failed reachability check. Falling back to Tier 2.");
                    result = null;
                }
                if (result == null) {
                    _logger.Warning("Tier 1 failed. Falling back to Tier 2.");
                    result = await ResolveTier2(targetUrl, player);
                    activeTier = "tier2";
                    if (result != null && !await CheckUrlReachable(result))
                    {
                        _logger.Warning("Tier 2 URL failed reachability check. Falling back to Tier 3.");
                        result = null;
                    }
                    if (result == null) {
                        _logger.Warning("Tier 2 failed. Falling back to Tier 3.");
                        result = await ResolveTier3(targetUrl, payload.Args);
                        activeTier = "tier3";
                    }
                }
            }
            else if (activeTier == "tier2")
            {
                result = await ResolveTier2(targetUrl, player);
                if (result != null && !await CheckUrlReachable(result))
                {
                    _logger.Warning("Tier 2 URL failed reachability check. Falling back to Tier 3.");
                    result = null;
                }
                if (result == null)
                {
                    _logger.Warning("Tier 2 failed. Falling back to Tier 3.");
                    result = await ResolveTier3(targetUrl, payload.Args);
                    activeTier = "tier3";
                }
            }
            else if (activeTier == "tier3")
            {
                result = await ResolveTier3(targetUrl, payload.Args);
            }
            else if (activeTier == "tier4")
            {
                _logger.Info("Tier 4 active: Returning original URL (Passthrough)");
                result = targetUrl;
            }

            if (result == null && activeTier != "tier4")
            {
                _logger.Warning("All active tiers failed. Attempting Tier 4 fallback.");
                result = targetUrl;
                activeTier = "tier4";
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Resolution loop fatal error: " + ex.Message);
            result = targetUrl;
            activeTier = "tier4-error";
        }

        // Detect HLS / live stream from resolved URL before relay wrapping
        bool isLive = result != null && (result.Contains(".m3u8") || result.Contains("m3u8"));
        string streamType = isLive ? "live" : (!string.IsNullOrEmpty(result) && result != "FAILED" ? "vod" : "unknown");

        if (isLive)
            _logger.Info("Detected HLS/live stream. Stream type: " + streamType);

        if (_settings.Config.EnableRelayBypass && _hostsManager.IsBypassActive() && !string.IsNullOrEmpty(result) && result != "FAILED")
        {
            try
            {
                string encodedUrl = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(result));
                int port = _relayPortManager.CurrentPort;
                if (port > 0)
                {
                    string relayUrl = "http://localhost.youtube.com:" + port + "/play?target=" + WebUtility.UrlEncode(encodedUrl);
                    result = relayUrl;
                    _logger.Info("URL wrapped for localhost relay proxy bypass.");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to wrap URL for relay: " + ex.Message);
            }
        }

        var tierKey = activeTier.Split('-')[0];
        if (_tierCounts.ContainsKey(tierKey)) _tierCounts[tierKey]++;

        var entry = new HistoryEntry {
            Timestamp = DateTime.Now,
            OriginalUrl = targetUrl,
            ResolvedUrl = result ?? "FAILED",
            Tier = activeTier,
            Player = player,
            Success = !string.IsNullOrEmpty(result),
            IsLive = isLive,
            StreamType = streamType
        };

        _settings.Config.History.Insert(0, entry);
        if (_settings.Config.History.Count > 100) _settings.Config.History.RemoveAt(100);
        _settings.Save();

        _activeResolutions--;
        UpdateStatus("Resolution completed via " + activeTier.ToUpper());
        _logger.Success("Final Resolution [" + activeTier + "] [" + streamType + "]: " + (result != null && result.Length > 100 ? result.Substring(0, 100) + "..." : result));
        return result;
    }

    private async Task<string?> ResolveTier1(string url, string player)
    {
        _logger.Debug("[Tier 1] Attempting native yt-dlp resolution...");

        var args = new List<string> { "--get-url", "--no-warnings" };
        if (_settings.Config.ForceIPv4) args.Add("--force-ipv4");

        if (player == "AVPro")
        {
            // AVPro supports HLS, DASH, and MP4. Prefer HLS first (works for both live and VOD).
            args.Add("-f");
            args.Add("best[protocol^=m3u8_native]/best[protocol^=http_dash_segments]/best[ext=mp4]/best");
        }
        else
        {
            // Unity player: prefer MP4 for VODs (better seeking), fall back to HLS for live streams,
            // and explicitly avoid raw DASH which Unity cannot decode.
            args.Add("-f");
            string res = _settings.Config.PreferredResolution.Replace("p", "");
            args.Add("best[ext=mp4][height<=" + res + "]/best[ext=mp4]/best[protocol^=m3u8_native]/best[protocol!=http_dash_segments]/best");
        }

        args.Add(url);
        return await RunYtDlp("yt-dlp.exe", args);
    }

    private async Task<string?> ResolveTier2(string url, string player)
    {
        _logger.Debug("[Tier 2] Calling WhyKnot.dev via WebSocket...");
        int maxHeight = 1080;
        try {
            string res = _settings.Config.PreferredResolution.Replace("p", "");
            if (int.TryParse(res, out var parsed)) maxHeight = parsed;
        } catch { }

        return await _tier2Client.ResolveUrlAsync(url, player, maxHeight);
    }

    private async Task<string?> ResolveTier3(string url, string[] originalArgs)
    {
        _logger.Debug("[Tier 3] Attempting VRChat's original yt-dlp...");
        var args = originalArgs.ToList();
        return await RunYtDlp("yt-dlp-og.exe", args);
    }

    private async Task<string?> RunYtDlp(string binary, List<string> args)
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", binary);
        if (!File.Exists(path))
        {
            _logger.Error(binary + " not found at: " + path);
            return null;
        }

        _logger.Trace("Executing: " + binary + " " + string.Join(" ", args));

        try
        {
            var tcs = new TaskCompletionSource<string?>();
            using var process = new Process();
            process.StartInfo.FileName = path;
            process.StartInfo.Arguments = string.Join(" ", args.Select(a => "\"" + a + "\""));
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            // Enable events so the Exited handler fires immediately when the process ends.
            // Without this, a failed yt-dlp (no URL on stdout) would hold the caller
            // until the full 15-second timeout fires.
            process.EnableRaisingEvents = true;

            process.OutputDataReceived += (s, e) => {
                if (!string.IsNullOrWhiteSpace(e.Data) && e.Data.StartsWith("http"))
                {
                    tcs.TrySetResult(e.Data.Trim());
                }
            };

            // When the process exits without printing a URL, complete the TCS with null immediately
            // so the caller does not wait for the full timeout.
            process.Exited += (s, e) => { tcs.TrySetResult(null); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeoutTask = Task.Delay(15000);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completed == timeoutTask)
            {
                _logger.Warning(binary + " timed out.");
                try { process.Kill(); } catch { }
                return null;
            }

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            _logger.Trace(binary + " execution error: " + ex.Message);
            return null;
        }
    }
}
