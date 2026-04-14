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
    private readonly PotProviderService? _potProvider;
    private SystemEventBus? _eventBus;

    public event Action<string, object>? OnStatusUpdate;
    private int _activeResolutions = 0;
    private readonly Dictionary<string, int> _tierCounts = new() {
        { "tier1", 0 }, { "tier2", 0 }, { "tier3", 0 }, { "tier4", 0 }
    };

    public ResolutionEngine(Logger logger, SettingsManager settings, VrcLogMonitor monitor, Tier2WebSocketClient tier2Client, HostsManager hostsManager, RelayPortManager relayPortManager, PatcherService patcher, CurlImpersonateClient? curlClient = null, PotProviderService? potProvider = null)
    {
        _logger = logger;
        _settings = settings;
        _monitor = monitor;
        _tier2Client = tier2Client;
        _hostsManager = hostsManager;
        _relayPortManager = relayPortManager;
        _patcher = patcher;
        _curlClient = curlClient;
        _potProvider = potProvider;
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

    // Headers for reachability checks on binary stream URLs (MP4, DASH, etc.) — Range probe returns 206.
    private static readonly Dictionary<string, string> _reachabilityHeaders = new()
    {
        ["Accept"] = "*/*",
        ["Accept-Language"] = "en-us,en;q=0.5",
        ["Range"] = "bytes=0-0"
    };

    // HLS manifest URLs do not support Range requests — a plain GET returning 200 is the correct probe.
    private static readonly Dictionary<string, string> _hlsReachabilityHeaders = new()
    {
        ["Accept"] = "*/*",
        ["Accept-Language"] = "en-us,en;q=0.5"
    };

    // Verify a resolved URL is reachable before accepting it.
    // Uses Range: bytes=0-0 for binary stream URLs (expects 206 or 416), and a plain GET for HLS
    // manifests since Range is not valid on .m3u8 files (they'd return 400, a false negative).
    // Prefers curl-impersonate (Chrome TLS fingerprint) so CDNs that reject plain .NET HttpClient
    // TLS handshakes are handled correctly. Falls back to plain HttpClient when unavailable.
    private async Task<bool> CheckUrlReachable(string url, RequestContext ctx)
    {
        string shortUrl = url.Length > 100 ? url.Substring(0, 100) + "..." : url;
        bool isHls = url.Contains(".m3u8") || url.Contains("m3u8");
        var headers = isHls ? _hlsReachabilityHeaders : _reachabilityHeaders;
        string probeMode = isHls ? "GET (HLS, no Range)" : "GET Range:bytes=0-0";

        if (_curlClient?.IsAvailable == true)
        {
            _logger.Debug("[" + ctx.CorrelationId + "] Probing via curl-impersonate [" + probeMode + "]: " + shortUrl);
            int status = await _curlClient.CheckReachabilityAsync(url, headers);
            if (status == -1)
            {
                // Probe timed out or process error — cannot confirm reachability, but do not reject.
                // Streaming servers (e.g. private HLS, proxy URLs) often do not respond to probe
                // requests within 5s. Rejecting on timeout causes valid URLs to cascade needlessly.
                _logger.Warning("[" + ctx.CorrelationId + "] Reachability check: curl-impersonate probe timed out for " + shortUrl + " — accepting URL (benefit of the doubt).");
                return true;
            }
            bool reachable = status is (>= 200 and < 400) or 416;
            if (!reachable)
                _logger.Warning("[" + ctx.CorrelationId + "] Reachability check: curl-impersonate returned HTTP " + status + " [" + probeMode + "] for " + shortUrl);
            else
                _logger.Debug("[" + ctx.CorrelationId + "] Reachability check: curl-impersonate HTTP " + status + " — OK for " + shortUrl);
            return reachable;
        }

        // Fallback: plain HttpClient with yt-dlp-matching headers
        _logger.Debug("[" + ctx.CorrelationId + "] Probing via HttpClient [" + probeMode + "] (curl-impersonate unavailable): " + shortUrl);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!isHls) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            req.Headers.TryAddWithoutValidation("Accept", "*/*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-us,en;q=0.5");
            var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            int status = (int)resp.StatusCode;
            bool reachable = status < 400 || status == 416;
            if (!reachable)
                _logger.Warning("[" + ctx.CorrelationId + "] Reachability check: HttpClient returned HTTP " + status + " [" + probeMode + "] for " + shortUrl);
            else
                _logger.Debug("[" + ctx.CorrelationId + "] Reachability check: HttpClient HTTP " + status + " — OK for " + shortUrl);
            return reachable;
        }
        catch (OperationCanceledException)
        {
            // Probe timed out — accept with warning rather than rejecting a potentially valid URL.
            _logger.Warning("[" + ctx.CorrelationId + "] Reachability check: HttpClient probe timed out for " + shortUrl + " — accepting URL (benefit of the doubt).");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning("[" + ctx.CorrelationId + "] Reachability check error (" + ex.GetType().Name + ") [" + probeMode + "] for " + shortUrl + " — " + ex.Message);
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
        Interlocked.Increment(ref _activeResolutions);
        var resolutionSw = Stopwatch.StartNew();

        string? targetUrl = payload.Args.FirstOrDefault(a => a.StartsWith("http"));
        if (string.IsNullOrEmpty(targetUrl))
        {
            Interlocked.Decrement(ref _activeResolutions);
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
                if (startIdx < 0)
                {
                    _logger.Warning("[" + ctx.CorrelationId + "] Unknown preferred tier '" + activeTier + "', defaulting to tier1.");
                    startIdx = 0;
                }
                var cascade = allTiers.Skip(startIdx).Where(t => !disabled.Contains(t)).ToList();

                _logger.Info("[" + ctx.CorrelationId + "] Cascade: " + string.Join(" → ", cascade.Select(t => t.ToUpper())) +
                    (disabled.Count > 0 ? " (disabled: " + string.Join(", ", disabled) + ")" : ""));

                foreach (var tier in cascade)
                {
                    if (tier == "tier1")
                    {
                        var (url, ms) = await TimedResolve(() => ResolveTier1(targetUrl, player, ctx));
                        if (url == null)
                        {
                            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1] yt-dlp returned no URL after " + ms + "ms — check stderr above for cause.");
                        }
                        else if (!await CheckUrlReachable(url, ctx))
                        {
                            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1] URL resolved in " + ms + "ms but failed reachability check — cascading to next tier.");
                        }
                        else
                        {
                            _logger.Info("[" + ctx.CorrelationId + "] [Tier 1] Success in" + ms + "ms.");
                            result = url; activeTier = "tier1"; break;
                        }
                    }
                    else if (tier == "tier2")
                    {
                        var (url, ms) = await TimedResolve(() => ResolveTier2(targetUrl, player, ctx));
                        if (url == null)
                        {
                            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 2] Cloud resolver returned no URL after " + ms + "ms.");
                        }
                        else
                        {
                            // Tier 2 returns freshly-generated URLs — skip reachability check.
                            // The cloud resolver's proxy URLs consistently time out the probe (curl exit 28)
                            // causing valid resolved URLs to cascade needlessly to slower tiers.
                            _logger.Info("[" + ctx.CorrelationId + "] [Tier 2] Success in " + ms + "ms.");
                            result = url; activeTier = "tier2"; break;
                        }
                    }
                    else if (tier == "tier3")
                    {
                        var (url, ms) = await TimedResolve(() => ResolveTier3(payload.Args, ctx));
                        if (url == null)
                        {
                            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 3] yt-dlp-og returned no URL after " + ms + "ms.");
                        }
                        else
                        {
                            _logger.Info("[" + ctx.CorrelationId + "] [Tier 3] Success in" + ms + "ms.");
                            result = url; activeTier = "tier3"; break;
                        }
                    }
                }

                // Passthrough fallback — returns the original URL unmodified (if not disabled)
                if (result == null && !disabled.Contains("tier4"))
                {
                    _logger.Warning("[" + ctx.CorrelationId + "] All active tiers exhausted — falling back to original URL (passthrough). Video may not play correctly.");
                    result = targetUrl;
                    activeTier = "tier4";
                    _eventBus?.PublishError("ResolutionEngine", new ErrorContext {
                        Category = ErrorCategory.Network,
                        Code = ErrorCodes.ALL_TIERS_FAILED,
                        Summary = "All resolution tiers failed",
                        Detail = "Tried: " + string.Join(", ", cascade) + ". All failed or returned unreachable URLs for: " + targetUrl,
                        ActionHint = "Check your internet connection. The video URL may be geo-restricted or require authentication.",
                        IsRecoverable = true
                    }, ctx.CorrelationId);
                }
                else if (result == null)
                {
                    // Tier 4 passthrough is disabled and all active tiers failed — resolution is a hard failure.
                    _logger.Error("[" + ctx.CorrelationId + "] Resolution failed: all tiers exhausted and Tier 4 passthrough is disabled. No URL will be returned.");
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
                    _logger.Info("[" + ctx.CorrelationId + "] URL relay-wrapped on port " + port + ".");
                }
                else
                {
                    _logger.Warning("[" + ctx.CorrelationId + "] Relay bypass is enabled but relay port is 0 — wrapping skipped. Video may fail to play.");
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
        try { _settings.Save(); }
        catch (Exception ex) { _logger.Warning("[" + ctx.CorrelationId + "] Failed to persist history after resolution: " + ex.Message); }

        resolutionSw.Stop();
        Interlocked.Decrement(ref _activeResolutions);
        UpdateStatus("Resolution completed via " + activeTier.ToUpper(), ctx);
        _logger.Success("[" + ctx.CorrelationId + "] Final Resolution [" + activeTier + "] [" + streamType + "] in " + resolutionSw.ElapsedMilliseconds + "ms: " + (result != null && result.Length > 100 ? result.Substring(0, 100) + "..." : result));
        return result;
    }

    private async Task<string?> ResolveTier1(string url, string player, RequestContext ctx)
    {
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1] Attempting native yt-dlp resolution...");

        var args = new List<string> { "--get-url", "--no-warnings", "--playlist-items", "1" };
        if (_settings.Config.ForceIPv4) args.Add("--force-ipv4");

        // Inject PO token for YouTube URLs so yt-dlp can pass bot detection.
        // PotProviderService runs bgutil-ytdlp-pot-provider as a local sidecar; without this
        // YouTube returns a bot-detection error and yt-dlp exits with no URL on stdout.
        bool isYouTube = url.Contains("youtube.com") || url.Contains("youtu.be");
        if (isYouTube)
        {
            if (_potProvider == null)
            {
                _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1] PotProviderService not wired — PO token will not be injected. yt-dlp may fail YouTube bot detection.");
            }
            else
            {
                string videoId = ExtractYouTubeVideoId(url) ?? "unknown";
                string? token = await _potProvider.GetPotTokenAsync("wkvrcproxy", videoId);
                if (!string.IsNullOrEmpty(token))
                {
                    args.Add("--extractor-args");
                    args.Add("youtube:po_token=web.gvs+" + token);
                    _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1] PO token injected for video: " + videoId);
                }
                else
                {
                    _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1] PO token fetch returned null for video: " + videoId + " — yt-dlp may fail YouTube bot detection.");
                }
            }
        }

        string formatStr;
        if (player == "AVPro")
        {
            // AVPro supports HLS, DASH, and MP4. Prefer HLS first (works for both live and VOD).
            formatStr = "best[protocol^=m3u8_native]/best[protocol^=http_dash_segments]/best[ext=mp4]/bestaudio/best";
        }
        else
        {
            // Unity player: prefer MP4 for VODs (better seeking), fall back to HLS for live streams,
            // and explicitly avoid raw DASH which Unity cannot decode.
            string res = _settings.Config.PreferredResolution.Replace("p", "");
            formatStr = "best[ext=mp4][height<=" + res + "]/best[ext=mp4]/best[protocol^=m3u8_native]/bestaudio/best[protocol!=http_dash_segments]/best";
        }
        args.Add("-f");
        args.Add(formatStr);
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1] Player=" + player + " Format=" + formatStr);

        // Inject generic:impersonate when curl-impersonate is available.
        // Required for CDN URLs protected by Cloudflare anti-bot (e.g. imvrcdn.com) — without this
        // yt-dlp's generic extractor gets HTTP 403 and fails. The youtube extractor ignores this arg.
        if (_curlClient?.IsAvailable == true)
        {
            args.Add("--extractor-args");
            args.Add("generic:impersonate");
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1] Injecting generic:impersonate (curl-impersonate available).");
        }

        args.Add(url);
        return await RunYtDlp("yt-dlp.exe", args, ctx);
    }

    // Extract a stable cache key from a YouTube URL.
    // Returns the video ID for watch/short/youtu.be URLs, or a channel/handle identifier for live channel URLs.
    // Channel live patterns: /channel/UCxxx/live, /c/Name/live, /user/Name/live, /@handle/live.
    private static string? ExtractYouTubeVideoId(string url)
    {
        try
        {
            var uri = new Uri(url);
            string path = uri.AbsolutePath;

            // Standard watch URL: youtube.com/watch?v=ID
            if (path == "/watch")
            {
                foreach (string part in uri.Query.TrimStart('?').Split('&'))
                {
                    if (part.StartsWith("v=")) return part.Substring(2);
                }
            }

            // Short URL: youtu.be/ID
            if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
                return path.TrimStart('/').Split('?')[0];

            // Shorts: /shorts/ID
            if (path.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase))
                return path.Substring("/shorts/".Length).Split('/')[0].Split('?')[0];

            // Channel live streams — return a stable identifier for the PO token cache key
            // /channel/UCxxx/live  →  "channel:UCxxx"
            if (path.StartsWith("/channel/", StringComparison.OrdinalIgnoreCase))
            {
                string segment = path.Substring("/channel/".Length).Split('/')[0];
                if (!string.IsNullOrEmpty(segment)) return "channel:" + segment;
            }

            // /c/Name/live  →  "c:Name"
            if (path.StartsWith("/c/", StringComparison.OrdinalIgnoreCase))
            {
                string segment = path.Substring("/c/".Length).Split('/')[0];
                if (!string.IsNullOrEmpty(segment)) return "c:" + segment;
            }

            // /user/Name/live  →  "user:Name"
            if (path.StartsWith("/user/", StringComparison.OrdinalIgnoreCase))
            {
                string segment = path.Substring("/user/".Length).Split('/')[0];
                if (!string.IsNullOrEmpty(segment)) return "user:" + segment;
            }

            // /@handle/live  →  "@handle"
            if (path.StartsWith("/@", StringComparison.OrdinalIgnoreCase))
            {
                string segment = path.Substring(1).Split('/')[0]; // keeps the @
                if (!string.IsNullOrEmpty(segment)) return segment;
            }
        }
        catch { }
        return null;
    }

    private async Task<string?> ResolveTier2(string url, string player, RequestContext ctx)
    {
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 2] Calling WhyKnot.dev via WebSocket...");
        int maxHeight = 1080;
        try {
            string res = _settings.Config.PreferredResolution.Replace("p", "");
            if (int.TryParse(res, out var parsed)) maxHeight = parsed;
        } catch (Exception ex) { _logger.Debug("[" + ctx.CorrelationId + "] Failed to parse PreferredResolution: " + ex.Message); }

        return await _tier2Client.ResolveUrlAsync(url, player, maxHeight, ctx.CorrelationId);
    }

    private async Task<string?> ResolveTier3(string[] originalArgs, RequestContext ctx)
    {
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 3] Attempting VRChat's original yt-dlp-og.exe (no PO token, no format override — uses VRChat's original args).");
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

        // Sanitize args for logging — mask PO token value (it's long and security-sensitive)
        string loggableArgs = string.Join(" ", args.Select(a => a.StartsWith("youtube:po_token=") ? "youtube:po_token=[REDACTED]" : a));
        _logger.Debug("[" + ctx.CorrelationId + "] Executing: " + binary + " " + loggableArgs);

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

            process.OutputDataReceived += (s, e) => {
                if (!string.IsNullOrWhiteSpace(e.Data) && e.Data.StartsWith("http"))
                {
                    string captured = e.Data.Trim();
                    _logger.Debug("[" + ctx.CorrelationId + "] [" + binary + "] stdout URL: " + (captured.Length > 100 ? captured.Substring(0, 100) + "..." : captured));
                    tcs.TrySetResult(captured);
                }
            };

            // Capture stderr so errors from yt-dlp are visible in the log instead of silently discarded.
            var stderrLines = new StringBuilder();
            process.ErrorDataReceived += (s, e) => {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    stderrLines.AppendLine(e.Data.Trim());
            };

            process.Start();
            ProcessGuard.Register(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Complete TCS with null after WaitForExit() (no-arg) so the pipe buffer is guaranteed
            // to be fully drained before null can win. The Exited event fires at OS process exit,
            // BEFORE async OutputDataReceived callbacks finish — which caused the URL to be silently
            // lost when yt-dlp exited immediately after writing the URL to stdout.
            // Catch ObjectDisposedException: on the timeout path the `using` disposes the process
            // before this Task.Run completes, which would otherwise be an unobserved exception.
            _ = Task.Run(() => {
                try { process.WaitForExit(); }
                catch (ObjectDisposedException) { /* process disposed on timeout path — expected */ }
                catch (InvalidOperationException) { /* process never started or already cleaned up */ }
                tcs.TrySetResult(null);
            });

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

            // Non-zero exit codes are almost always the reason yt-dlp returned no URL
            if (process.HasExited && process.ExitCode != 0)
                _logger.Warning("[" + ctx.CorrelationId + "] " + binary + " exited with non-zero code " + process.ExitCode + ".");

            string? resolvedUrl = await tcs.Task;
            if (resolvedUrl == null)
                _logger.Warning("[" + ctx.CorrelationId + "] [" + binary + "] Process exited without outputting a URL (check stderr above).");
            return resolvedUrl;
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
