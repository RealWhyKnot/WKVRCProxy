using System;
using System.Net;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.IPC;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Models;

namespace WKVRCProxy.Core.Services;

[SupportedOSPlatform("windows")]
public class ResolutionEngine
{
    private readonly Logger _logger;
    private readonly SettingsManager _settings;
    private readonly VrcLogMonitor _monitor;
    private readonly HttpClient _httpClient;
    private readonly Tier2WebSocketClient _tier2Client;
    private readonly HostsManager _hostsManager;
    private readonly RelayPortManager _relayPortManager;
    private readonly PatcherService _patcher;
    private readonly CurlImpersonateClient? _curlClient;
    private SystemEventBus? _eventBus;

    public event Action<string, object>? OnStatusUpdate;
    private int _activeResolutions = 0;
    private readonly Dictionary<string, int> _tierCounts = new() {
        { "tier1", 0 }, { "tier2", 0 }, { "tier3", 0 }, { "tier4", 0 }
    };

    public ResolutionEngine(Logger logger, SettingsManager settings, VrcLogMonitor monitor, Tier2WebSocketClient tier2Client, HostsManager hostsManager, RelayPortManager relayPortManager, PatcherService patcher, CurlImpersonateClient? curlClient = null)
    {
        _logger = logger;
        _settings = settings;
        _monitor = monitor;
        _tier2Client = tier2Client;
        _hostsManager = hostsManager;
        _relayPortManager = relayPortManager;
        _patcher = patcher;
        _curlClient = curlClient;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(_settings.Config.UserAgent);

        // Initialize counts from history
        foreach (var entry in _settings.Config.History)
        {
            var tierKey = entry.Tier.Split('-')[0];
            if (_tierCounts.ContainsKey(tierKey)) _tierCounts[tierKey]++;
        }
    }

    public void SetEventBus(SystemEventBus bus) { _eventBus = bus; }

    private void UpdateStatus(string message, RequestContext? ctx = null)
    {
        var stats = new {
            activeCount = _activeResolutions,
            tierStats = _tierCounts,
            node = _tier2Client.ActiveNode,
            player = _monitor.CurrentPlayer,
            correlationId = ctx?.CorrelationId
        };
        OnStatusUpdate?.Invoke(message, stats);
        _eventBus?.PublishStatus("ResolutionEngine", message, stats, ctx?.CorrelationId);
    }

    // Headers sent for reachability checks — mirrors what yt-dlp sends for stream URL validation.
    private static readonly Dictionary<string, string> _reachabilityHeaders = new()
    {
        ["Accept"] = "*/*",
        ["Accept-Language"] = "en-us,en;q=0.5",
        ["Range"] = "bytes=0-0"
    };

    // Verify a resolved URL is reachable before accepting it.
    // Prefers curl-impersonate (Chrome TLS fingerprint, yt-dlp-compatible headers) so CDNs that
    // reject plain .NET HttpClient TLS handshakes or HEAD requests are handled correctly.
    // Falls back to plain HttpClient if curl-impersonate is unavailable.
    private async Task<bool> CheckUrlReachable(string url, RequestContext ctx)
    {
        if (_curlClient?.IsAvailable == true)
        {
            int status = await _curlClient.CheckReachabilityAsync(url, _reachabilityHeaders);
            // 206 Partial Content, 200 OK (range ignored), 416 Range Not Satisfiable (URL exists)
            return status is (>= 200 and < 400) or 416;
        }

        // Fallback: plain HttpClient with yt-dlp-matching headers
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            req.Headers.TryAddWithoutValidation("Accept", "*/*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-us,en;q=0.5");
            var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            int status = (int)resp.StatusCode;
            return status < 400 || status == 416;
        }
        catch (Exception ex)
        {
            _logger.Debug("[" + ctx.CorrelationId + "] HttpClient reachability check failed for " + url + ": " + ex.Message);
            return false;
        }
    }

    // Runs a tier resolver and measures how long it takes.
    private static async Task<(string? Url, long ElapsedMs)> TimedResolve(Func<Task<string?>> resolver)
    {
        var sw = Stopwatch.StartNew();
        string? result = await resolver();
        sw.Stop();
        return (result, sw.ElapsedMilliseconds);
    }

    public async Task<string?> ResolveAsync(ResolvePayload payload)
    {
        _activeResolutions++;
        var resolutionSw = Stopwatch.StartNew();

        string? targetUrl = payload.Args.FirstOrDefault(a => a.StartsWith("http"));
        if (string.IsNullOrEmpty(targetUrl))
        {
            _activeResolutions--;
            _logger.Warning("No valid URL found in resolution payload.");
            return null;
        }

        var ctx = RequestContext.Create(targetUrl);

        string player = _monitor.CurrentPlayer;
        if (payload.Args.Any(a => a.Contains("AVProVideo"))) player = "AVPro";
        if (payload.Args.Any(a => a.Contains("UnityPlayer"))) player = "Unity";

        _logger.Info("[" + ctx.CorrelationId + "] Starting resolution for: " + targetUrl + " [" + player + "]");
        UpdateStatus("Intercepted " + player + " request...", ctx);

        string? result = null;
        string activeTier = _settings.Config.PreferredTier;
        var disabled = _settings.Config.DisabledTiers ?? new List<string>();

        try
        {
            if (activeTier == "tier4")
            {
                _logger.Info("[" + ctx.CorrelationId + "] Tier 4 active: Returning original URL (Passthrough)");
                result = targetUrl;
            }
            else
            {
                // Build ordered cascade starting from preferred tier, skipping disabled tiers
                var allTiers = new[] { "tier1", "tier2", "tier3" };
                int startIdx = Array.IndexOf(allTiers, activeTier);
                if (startIdx < 0) startIdx = 0;
                var cascade = allTiers.Skip(startIdx).Where(t => !disabled.Contains(t)).ToList();

                foreach (var tier in cascade)
                {
                    if (tier == "tier1")
                    {
                        var (url, ms) = await TimedResolve(() => ResolveTier1(targetUrl, player, ctx));
                        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1] resolved in " + ms + "ms");
                        if (url != null && await CheckUrlReachable(url, ctx)) { result = url; activeTier = "tier1"; break; }
                        _logger.Warning("[" + ctx.CorrelationId + "] Tier 1 failed/unreachable, trying next.");
                    }
                    else if (tier == "tier2")
                    {
                        var (url, ms) = await TimedResolve(() => ResolveTier2(targetUrl, player, ctx));
                        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 2] resolved in " + ms + "ms");
                        if (url != null && await CheckUrlReachable(url, ctx)) { result = url; activeTier = "tier2"; break; }
                        _logger.Warning("[" + ctx.CorrelationId + "] Tier 2 failed/unreachable, trying next.");
                    }
                    else if (tier == "tier3")
                    {
                        var (url, ms) = await TimedResolve(() => ResolveTier3(targetUrl, payload.Args, ctx));
                        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 3] resolved in " + ms + "ms");
                        if (url != null) { result = url; activeTier = "tier3"; break; }
                        _logger.Warning("[" + ctx.CorrelationId + "] Tier 3 failed.");
                    }
                }

                // Tier 4 passthrough fallback (if not disabled)
                if (result == null && !disabled.Contains("tier4"))
                {
                    _logger.Warning("[" + ctx.CorrelationId + "] All active tiers failed. Using Tier 4 passthrough.");
                    result = targetUrl;
                    activeTier = "tier4";
                    _eventBus?.PublishError("ResolutionEngine", new ErrorContext {
                        Category = ErrorCategory.Network,
                        Code = ErrorCodes.ALL_TIERS_FAILED,
                        Summary = "All resolution tiers failed",
                        Detail = "Tier 1 (yt-dlp), Tier 2 (cloud), and Tier 3 (original yt-dlp) all failed or returned unreachable URLs for: " + targetUrl,
                        ActionHint = "Check your internet connection. The video URL may be geo-restricted or require authentication.",
                        IsRecoverable = true
                    }, ctx.CorrelationId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("[" + ctx.CorrelationId + "] Resolution loop fatal error: " + ex.Message, ex);
            result = targetUrl;
            activeTier = "tier4-error";
        }

        // Detect HLS / live stream from resolved URL before relay wrapping
        bool isLive = result != null && (result.Contains(".m3u8") || result.Contains("m3u8"));
        string streamType = isLive ? "live" : (!string.IsNullOrEmpty(result) && result != "FAILED" ? "vod" : "unknown");

        if (isLive)
            _logger.Info("[" + ctx.CorrelationId + "] Detected HLS/live stream. Stream type: " + streamType);

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
                    _logger.Info("[" + ctx.CorrelationId + "] URL wrapped for localhost relay proxy bypass.");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("[" + ctx.CorrelationId + "] Failed to wrap URL for relay: " + ex.Message);
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

        resolutionSw.Stop();
        _activeResolutions--;
        UpdateStatus("Resolution completed via " + activeTier.ToUpper(), ctx);
        _logger.Success("[" + ctx.CorrelationId + "] Final Resolution [" + activeTier + "] [" + streamType + "] in " + resolutionSw.ElapsedMilliseconds + "ms: " + (result != null && result.Length > 100 ? result.Substring(0, 100) + "..." : result));
        return result;
    }

    private async Task<string?> ResolveTier1(string url, string player, RequestContext ctx)
    {
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1] Attempting native yt-dlp resolution...");

        var args = new List<string> { "--get-url", "--no-warnings", "--playlist-items", "1" };
        if (_settings.Config.ForceIPv4) args.Add("--force-ipv4");

        if (player == "AVPro")
        {
            // AVPro supports HLS, DASH, and MP4. Prefer HLS first (works for both live and VOD).
            args.Add("-f");
            args.Add("best[protocol^=m3u8_native]/best[protocol^=http_dash_segments]/best[ext=mp4]/bestaudio/best");
        }
        else
        {
            // Unity player: prefer MP4 for VODs (better seeking), fall back to HLS for live streams,
            // and explicitly avoid raw DASH which Unity cannot decode.
            args.Add("-f");
            string res = _settings.Config.PreferredResolution.Replace("p", "");
            args.Add("best[ext=mp4][height<=" + res + "]/best[ext=mp4]/best[protocol^=m3u8_native]/bestaudio/best[protocol!=http_dash_segments]/best");
        }

        args.Add(url);
        return await RunYtDlp("yt-dlp.exe", args, ctx);
    }

    private async Task<string?> ResolveTier2(string url, string player, RequestContext ctx)
    {
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 2] Calling WhyKnot.dev via WebSocket...");
        int maxHeight = 1080;
        try {
            string res = _settings.Config.PreferredResolution.Replace("p", "");
            if (int.TryParse(res, out var parsed)) maxHeight = parsed;
        } catch (Exception ex) { _logger.Debug("[" + ctx.CorrelationId + "] Failed to parse PreferredResolution: " + ex.Message); }

        return await _tier2Client.ResolveUrlAsync(url, player, maxHeight);
    }

    private async Task<string?> ResolveTier3(string url, string[] originalArgs, RequestContext ctx)
    {
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 3] Attempting VRChat's original yt-dlp...");
        var args = originalArgs.ToList();
        return await RunYtDlp("yt-dlp-og.exe", args, ctx);
    }

    // yt-dlp-og.exe lives in the VRChat Tools folder (created by PatcherService as a backup).
    // All other binaries (yt-dlp.exe, redirector.exe) live in dist/tools/.
    private string GetBinaryPath(string binary)
    {
        if (binary == "yt-dlp-og.exe")
        {
            string? toolsDir = _patcher.VrcToolsDir;
            if (!string.IsNullOrEmpty(toolsDir))
            {
                string vrcPath = Path.Combine(toolsDir, binary);
                if (File.Exists(vrcPath)) return vrcPath;
            }
        }
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", binary);
    }

    private async Task<string?> RunYtDlp(string binary, List<string> args, RequestContext ctx)
    {
        string path = GetBinaryPath(binary);
        if (!File.Exists(path))
        {
            _logger.Error("[" + ctx.CorrelationId + "] " + binary + " not found at: " + path);
            return null;
        }

        _logger.Debug("[" + ctx.CorrelationId + "] Executing: " + binary + " " + string.Join(" ", args));

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

            // Capture stderr so errors from yt-dlp are visible in the log instead of silently discarded.
            var stderrLines = new StringBuilder();
            process.ErrorDataReceived += (s, e) => {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    stderrLines.AppendLine(e.Data.Trim());
            };

            // When the process exits without printing a URL, complete the TCS with null immediately
            // so the caller does not wait for the full timeout.
            process.Exited += (s, e) => { tcs.TrySetResult(null); };

            process.Start();
            ProcessGuard.Register(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeoutTask = Task.Delay(15000);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask);

            // Log any stderr output regardless of whether it timed out or resolved
            string stderrOutput = stderrLines.ToString().Trim();
            if (!string.IsNullOrEmpty(stderrOutput))
                _logger.Warning("[" + ctx.CorrelationId + "] [" + binary + "] stderr: " + stderrOutput);

            if (completed == timeoutTask)
            {
                _logger.Warning("[" + ctx.CorrelationId + "] " + binary + " timed out after 15s.");
                _eventBus?.PublishError("ResolutionEngine", new ErrorContext {
                    Category = ErrorCategory.ChildProcess,
                    Code = ErrorCodes.YTDLP_TIMEOUT,
                    Summary = binary + " timed out after 15 seconds",
                    Detail = "The process did not produce a URL within the timeout window",
                    ActionHint = "The video source may be slow to respond. Try again or switch to a different tier.",
                    IsRecoverable = true
                }, ctx.CorrelationId);
                try { process.Kill(); } catch { /* Process may have already exited */ }
                return null;
            }

            // Log non-zero exit codes for debugging
            if (process.HasExited && process.ExitCode != 0)
                _logger.Debug("[" + ctx.CorrelationId + "] " + binary + " exited with code " + process.ExitCode);

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            _logger.Error("[" + ctx.CorrelationId + "] " + binary + " execution error: " + ex.Message, ex);
            _eventBus?.PublishError("ResolutionEngine", new ErrorContext {
                Category = ErrorCategory.ChildProcess,
                Code = ErrorCodes.YTDLP_EXECUTION_ERROR,
                Summary = binary + " failed to execute",
                Detail = ex.Message,
                ActionHint = "The binary may be corrupted or missing. Try reinstalling WKVRCProxy.",
                IsRecoverable = false
            }, ctx.CorrelationId);
            return null;
        }
    }
}
