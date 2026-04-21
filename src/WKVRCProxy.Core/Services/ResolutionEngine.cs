using System;
using System.Net;
using System.Collections.Concurrent;
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

public record YtDlpResult(string Url, int? Height, int? Width, string? Vcodec, string? FormatId, string? Protocol);

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
    private readonly BrowserExtractService? _browserExtractor;
    private readonly WarpService? _warp;
    private SystemEventBus? _eventBus;

    public event Action<string, object>? OnStatusUpdate;
    private int _activeResolutions = 0;
    private readonly ConcurrentDictionary<string, int> _tierCounts = new(
        new[] {
            new KeyValuePair<string, int>("tier0", 0),
            new KeyValuePair<string, int>("tier1", 0),
            new KeyValuePair<string, int>("tier2", 0),
            new KeyValuePair<string, int>("tier3", 0),
            new KeyValuePair<string, int>("tier4", 0)
        });

    // Per-host/per-strategy learning. See StrategyMemory.cs for semantics (success/failure decay,
    // demotion, migration from the old tier_memory.json).
    private readonly StrategyMemory _strategyMemory;
    public StrategyMemory StrategyMemory => _strategyMemory;

    // Well-known UA string that matches VRChat's Unity build so pre-flight probes (Tier 1 / Tier 3)
    // pass allowlists on "VRChat movie world" hosts (vr-m.net etc.) that reject anonymous clients.
    // Seed value only — Phase 3 will capture the actual UA from incoming AVPro relay requests.
    internal const string VrchatAvProUserAgent = "UnityPlayer/2022.3.22f1 (UnityWebRequest/1.0, libcurl/7.84.0-DEV)";
    internal const string VrchatReferer = "https://vrchat.com/";

    // Domain-level "requires PO token" flag. YouTube's bot-detection mode is not per-video — once it
    // flips on, the whole domain requires PO tokens for a window of time (~30 min in practice). We
    // flag the host on the first bot-check stderr, then every Tier 1 call to that host pays the PO
    // token cost upfront. When the flag expires, we try the fast-path (no PO) again on the next call.
    // Value = absolute expiry timestamp (UTC).
    private readonly ConcurrentDictionary<string, DateTime> _domainRequiresPot = new();
    private static readonly TimeSpan DomainRequiresPotTtl = TimeSpan.FromMinutes(30);

    // Per-host cache of Streamlink's "--can-handle-url" answer. Keyed by host (lowercase). Negative
    // answers expire after 24h (Streamlink plugin list rarely grows mid-session); positive answers
    // expire after 7d. Saves ~500ms per request for hosts Streamlink doesn't support (e.g. vr-m.net).
    private readonly ConcurrentDictionary<string, (bool CanHandle, DateTime Expiry)> _streamlinkCapabilityCache = new();
    private static readonly TimeSpan StreamlinkCacheTtlNegative = TimeSpan.FromHours(24);
    private static readonly TimeSpan StreamlinkCacheTtlPositive = TimeSpan.FromDays(7);

    // Positive resolve cache: short-TTL (URL, player) → resolved URL. VRChat calls yt-dlp multiple times
    // for the same video (thumbnail probe + duration probe + actual play). Without this, each call hits
    // whyknot.dev fresh, burning ~6s per trip. Cache TTL stays short to keep CDN URLs (which expire
    // server-side) fresh and lets transient failures self-heal on the next real play.
    private record ResolveCacheEntry(YtDlpResult Result, string Tier, DateTime Expires);
    private readonly ConcurrentDictionary<string, ResolveCacheEntry> _resolveCache = new();
    private static readonly TimeSpan ResolveCacheTtl = TimeSpan.FromSeconds(90);
    public static string ResolveCacheKey(string url, string player) => player + "|" + url;

    public ResolutionEngine(Logger logger, SettingsManager settings, VrcLogMonitor monitor, Tier2WebSocketClient tier2Client, HostsManager hostsManager, RelayPortManager relayPortManager, PatcherService patcher, CurlImpersonateClient? curlClient = null, PotProviderService? potProvider = null, BrowserExtractService? browserExtractor = null, WarpService? warp = null)
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
        _browserExtractor = browserExtractor;
        _warp = warp;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(_settings.Config.UserAgent);

        // Initialize counts from history
        foreach (var entry in _settings.Config.History)
        {
            var tierKey = entry.Tier.Split('-')[0];
            _tierCounts.AddOrUpdate(tierKey, 1, (_, v) => v + 1);
        }

        _strategyMemory = new StrategyMemory(_logger, AppDomain.CurrentDomain.BaseDirectory);
        _strategyMemory.Load();

        LogActiveTiers();
    }

    private void LogActiveTiers()
    {
        var all = new[] {
            ("tier0", "streamlink"),
            ("tier1", "yt-dlp"),
            ("tier2", "cloud"),
            ("tier3", "yt-dlp-og"),
            ("tier4", "passthrough"),
        };
        var disabled = _settings.Config.DisabledTiers ?? new List<string>();
        var active = all.Where(t => !disabled.Contains(t.Item1)).Select(t => t.Item2);
        var off = all.Where(t => disabled.Contains(t.Item1)).Select(t => t.Item2).ToList();
        string line = "Active tiers: " + string.Join(", ", active);
        if (off.Count > 0) line += " (disabled: " + string.Join(", ", off) + ")";
        _logger.Info(line);
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

    private void RecordStrategySuccess(string memKey, string strategyName, int? resolvedHeight = null)
    {
        if (string.IsNullOrEmpty(memKey) || !_settings.Config.EnableTierMemory) return;
        _strategyMemory.RecordSuccess(memKey, strategyName, resolvedHeight);
    }

    private void RecordStrategyFailure(string memKey, string strategyName, StrategyFailureKind kind = StrategyFailureKind.Unknown)
    {
        if (string.IsNullOrEmpty(memKey) || !_settings.Config.EnableTierMemory) return;
        _strategyMemory.RecordFailure(memKey, strategyName, kind);
    }

    // True if the host is currently in "needs PO token" mode due to a recent bot-detection failure.
    // Self-cleaning: expired entries are removed on lookup.
    private bool DomainRequiresPot(string host)
    {
        if (!_domainRequiresPot.TryGetValue(host, out var expires)) return false;
        if (DateTime.UtcNow >= expires)
        {
            _domainRequiresPot.TryRemove(host, out _);
            return false;
        }
        return true;
    }

    // Flag a host as requiring PO tokens for the next TTL window. Bounded cleanup runs inline.
    private void MarkDomainRequiresPot(string host)
    {
        _domainRequiresPot[host] = DateTime.UtcNow.Add(DomainRequiresPotTtl);
        if (_domainRequiresPot.Count > 32)
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _domainRequiresPot)
                if (kv.Value <= now) _domainRequiresPot.TryRemove(kv.Key, out _);
        }
    }

    // Normalize a URL's host for domain-key lookup ("www.youtube.com" → "youtube.com", "youtu.be" stays).
    // Returns the empty string if the URL is malformed.
    public static string ExtractHost(string url)
    {
        try
        {
            string host = new Uri(url).Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host.Substring(4);
            return host;
        }
        catch { return ""; }
    }

    // Scans yt-dlp stderr for well-known YouTube bot-detection phrases.
    public static bool IsBotDetectionStderr(string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        return stderr.Contains("Sign in to confirm you're not a bot", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Sign in to confirm you are not a bot", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("confirm you're not a bot", StringComparison.OrdinalIgnoreCase);
    }

    // Runs a tier resolver and measures how long it takes.
    private static async Task<(T Result, long ElapsedMs)> TimedResolve<T>(Func<Task<T>> resolver)
    {
        var sw = Stopwatch.StartNew();
        T result = await resolver();
        sw.Stop();
        return (result, sw.ElapsedMilliseconds);
    }

    // Map PreferredResolution ("1080p") to an integer height; defaults to 1080 when unparseable.
    private int ParsePreferredHeight()
    {
        string res = _settings.Config.PreferredResolution?.Replace("p", "") ?? "1080";
        return int.TryParse(res, out var h) ? h : 1080;
    }

    // Quality floor: accept a resolved stream whose height is at least 2/3 of preferred.
    // Pref=1080p → floor=720p ✓, pref=720p → floor=480p ✓, pref=480p → floor=320p.
    public static int ComputeQualityFloor(int preferredHeight) =>
        (int)(preferredHeight * 2.0 / 3.0);

    // A tier result is "good enough" if we don't know its height (trust-by-default) or it's ≥ floor.
    public static bool IsAcceptableQuality(int? resolvedHeight, int floorHeight) =>
        resolvedHeight == null || resolvedHeight >= floorHeight;

    // Hosts where the relay wrap is worth the cost: YouTube-family domains, where
    // the relay injects PO tokens / curl-impersonate / UA overrides that VRChat's
    // built-in yt-dlp can't do itself. Everything else — SoundCloud, Twitch, HLS
    // "movie worlds" like vr-m.net — is hurt by the wrap: our relay strips AVPro's
    // native headers and the origin 403s us. Keep this list narrow on purpose.
    private static bool IsRelayBeneficialDomain(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        string host = uri.Host.ToLowerInvariant();
        return host.EndsWith("youtube.com")
            || host.EndsWith("youtu.be")
            || host.EndsWith("googlevideo.com")
            || host.EndsWith("ytimg.com")
            || host.EndsWith("google.com");
    }

    // Wrap a pristine resolved URL in the localhost.youtube.com relay URL so VRChat's yt-dlp reaches
    // our on-host proxy instead of the public CDN. No-op if bypass is disabled, share mode, or
    // relay port unassigned. Called from both the normal resolve path and the cache-hit shortcut.
    // forceWrap: overrides the YouTube-family domain gate. Set by strategies that rely on relay-side
    // session replay (browser-extract) so the captured cookies/headers reach AVPro's requests.
    private string ApplyRelayWrap(string pristineUrl, bool skipRelayWrap, string correlationId, bool forceWrap = false)
    {
        if (skipRelayWrap || !_settings.Config.EnableRelayBypass || !_hostsManager.IsBypassActive())
            return pristineUrl;
        if (!forceWrap && !IsRelayBeneficialDomain(pristineUrl))
        {
            string host = Uri.TryCreate(pristineUrl, UriKind.Absolute, out var u) ? u.Host : "<unparseable>";
            _logger.Info("[" + correlationId + "] Relay wrap skipped for " + host + " — returning pristine URL to AVPro.");
            return pristineUrl;
        }
        try
        {
            int port = _relayPortManager.CurrentPort;
            if (port <= 0)
            {
                _logger.Warning("[" + correlationId + "] Relay bypass is enabled but relay port is 0 — wrapping skipped. Video may fail to play.");
                return pristineUrl;
            }
            string encodedUrl = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pristineUrl));
            string relayUrl = "http://localhost.youtube.com:" + port + "/play?target=" + WebUtility.UrlEncode(encodedUrl);
            _logger.Info("[" + correlationId + "] URL relay-wrapped on port " + port + ".");
            return relayUrl;
        }
        catch (Exception ex)
        {
            _logger.Warning("[" + correlationId + "] Failed to wrap URL for relay: " + ex.Message);
            return pristineUrl;
        }
    }

    public Task<string?> ResolveAsync(ResolvePayload payload) =>
        ResolveInternalAsync(payload, skipRelayWrap: false, playerOverride: null, historyPlayerLabel: null);

    // Resolve a URL for the Share panel: same cascade, but never wrap in localhost.youtube.com relay URL
    // (that URL only works inside the user's own VRChat). History entries tagged with `historyPlayerLabel`
    // ("CloudShare" / "P2PShare") so they show up in history but are distinguishable.
    public Task<string?> ResolveForShareAsync(string url, string player, string shareMode)
    {
        var payload = new ResolvePayload { Args = new[] { url, player == "AVPro" ? "AVProVideo" : "UnityPlayer" } };
        return ResolveInternalAsync(payload, skipRelayWrap: true, playerOverride: player, historyPlayerLabel: shareMode);
    }

    private async Task<string?> ResolveInternalAsync(ResolvePayload payload, bool skipRelayWrap, string? playerOverride, string? historyPlayerLabel)
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

        string player = playerOverride ?? _monitor.CurrentPlayer;
        if (playerOverride == null)
        {
            if (payload.Args.Any(a => a.Contains("AVProVideo"))) player = "AVPro";
            if (payload.Args.Any(a => a.Contains("UnityPlayer"))) player = "Unity";
        }

        _logger.Info("[" + ctx.CorrelationId + "] Starting resolution for: " + targetUrl + " [" + player + "]" + (skipRelayWrap ? " [share]" : ""));
        UpdateStatus("Intercepted " + player + " request...", ctx);

        // Positive resolve cache: collapse redundant calls from VRChat (which spawns yt-dlp 2-3x per
        // play event for thumbnail/duration/actual-play). A cache hit bypasses the full cascade and
        // returns the already-resolved URL, just re-applying the relay wrap (port may have changed).
        // History/stat writes are skipped on hit so the UI doesn't show duplicate rows.
        string cacheKey = ResolveCacheKey(targetUrl, player);
        if (_resolveCache.TryGetValue(cacheKey, out var cached))
        {
            if (DateTime.UtcNow < cached.Expires)
            {
                string cachedFinal = ApplyRelayWrap(cached.Result.Url, skipRelayWrap, ctx.CorrelationId);
                resolutionSw.Stop();
                Interlocked.Decrement(ref _activeResolutions);
                UpdateStatus("Cached resolution via " + cached.Tier.ToUpper(), ctx);
                string shortCached = cachedFinal.Length > 100 ? cachedFinal.Substring(0, 100) + "..." : cachedFinal;
                _logger.Success("[" + ctx.CorrelationId + "] Final Resolution [" + cached.Tier + "] [cache-hit] in " + resolutionSw.ElapsedMilliseconds + "ms: " + shortCached);
                return cachedFinal;
            }
            _resolveCache.TryRemove(cacheKey, out _);
        }

        YtDlpResult? winnerResult = null;
        YtDlpResult? bestSoFar = null;
        string bestSoFarTier = "";
        // Set by the browser-extract strategy. Tells ApplyRelayWrap to wrap even non-YouTube URLs
        // so the relay can replay the captured browser session (cookies + headers) to AVPro.
        bool winnerForcesRelayWrap = false;
        string activeTier = _settings.Config.PreferredTier;
        var disabled = _settings.Config.DisabledTiers ?? new List<string>();

        int preferredH = ParsePreferredHeight();
        int floorH = ComputeQualityFloor(preferredH);

        try
        {
            if (activeTier == "tier4")
            {
                _logger.Info("[" + ctx.CorrelationId + "] Tier 4 active: Returning original URL (Passthrough)");
                winnerResult = new YtDlpResult(targetUrl, null, null, null, null, null);
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

                bool isStreamlinkLive = !disabled.Contains("tier0") && await StreamlinkCanHandleUrlAsync(targetUrl, ctx);
                string memKey = StrategyMemory.KeyFor(targetUrl, isStreamlinkLive);

                StrategyMemoryEntry? remembered = null;
                string? rememberedGroup = null;
                if (_settings.Config.EnableTierMemory && !string.IsNullOrEmpty(memKey))
                {
                    remembered = _strategyMemory.GetPreferred(memKey);
                    if (remembered != null)
                    {
                        rememberedGroup = remembered.StrategyName.Split(':')[0];
                        _logger.Debug("[" + ctx.CorrelationId + "] [StrategyMemory] Preferred '" + remembered.StrategyName + "' for " + memKey + " (" + remembered.SuccessCount + "W/" + remembered.FailureCount + "L).");
                    }
                }

                _logger.Info("[" + ctx.CorrelationId + "] Cascade: " + string.Join(" → ", cascade.Select(t => t.ToUpper())) +
                    (disabled.Count > 0 ? " (disabled: " + string.Join(", ", disabled) + ")" : "") +
                    " (quality floor " + floorH + "p)");

                // activeStrategy is the specific variant label written to StrategyMemory on success.
                // activeTier is the tier-group (backwards compat for UI / HistoryEntry.Tier).
                string activeStrategy = "";

                // Tier 0: Streamlink — live-stream fast-path. Skipped when a faster (tier1/2/3)
                // winner is already remembered. Streamlink does not report resolution, so the
                // quality heuristic treats it as unknown → accepted.
                if (isStreamlinkLive && !disabled.Contains("tier0"))
                {
                    bool tryStreamlink = rememberedGroup == null || rememberedGroup == "tier0";
                    if (tryStreamlink)
                    {
                        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] Streamlink supports this URL — attempting resolution.");
                        var (slRes, slMs) = await TimedResolve(() => ResolveStreamlink(targetUrl, ctx));
                        if (slRes != null)
                        {
                            _logger.Info("[" + ctx.CorrelationId + "] [Tier 0] Streamlink success in " + slMs + "ms.");
                            winnerResult = slRes; activeTier = "tier0-streamlink"; activeStrategy = "tier0:streamlink-native";
                        }
                        else if (rememberedGroup == "tier0")
                        {
                            _logger.Warning("[" + ctx.CorrelationId + "] [StrategyMemory] Remembered tier0 strategy failed — demoting.");
                            RecordStrategyFailure(memKey, remembered!.StrategyName);
                            remembered = null; rememberedGroup = null;
                        }
                        else
                        {
                            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] Streamlink returned no URL after " + slMs + "ms — cascading.");
                        }
                    }
                }

                if (winnerResult == null)
                {
                    int cascadeStart = 0;
                    if (rememberedGroup != null && rememberedGroup != "tier0")
                    {
                        int ri = cascade.IndexOf(rememberedGroup);
                        if (ri > 0)
                        {
                            cascadeStart = ri;
                            _logger.Debug("[" + ctx.CorrelationId + "] [StrategyMemory] Jumping to remembered group '" + rememberedGroup + "' (skipping " + ri + " earlier tier(s)).");
                        }
                    }

                    // Specific-strategy fast-path. Memory tracks strategy NAMES (e.g.
                    // "tier1:browser-extract"), but the sequential cascade below only knows tier GROUPS
                    // — it would run the tier's default recipe (ResolveTier1 etc.), which is a
                    // completely different yt-dlp arg set from the remembered variant. That bug
                    // previously caused a remembered "tier1:browser-extract" winner on YouTube to run
                    // as "tier1:default", get bot-checked, and fall through to Tier 2 cloud. Look up
                    // the exact strategy in the catalog and run it first with a soft deadline. On
                    // failure, demote the specific entry and fall through to the cold race so the
                    // dispatcher can rediscover a working variant.
                    if (remembered != null && rememberedGroup != null && rememberedGroup != "tier0"
                        && !disabled.Contains(rememberedGroup))
                    {
                        var catalogForFast = BuildColdRaceStrategies(targetUrl, player, payload.Args, disabled);
                        var specific = catalogForFast.FirstOrDefault(s =>
                            string.Equals(s.Name, remembered.StrategyName, StringComparison.OrdinalIgnoreCase));
                        if (specific != null)
                        {
                            _logger.Info("[" + ctx.CorrelationId + "] [StrategyMemory] Fast-path: running remembered '"
                                + specific.Name + "' (" + remembered.SuccessCount + "W/" + remembered.FailureCount + "L).");
                            using var fastCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                            var fastSctx = new StrategyRunContext(targetUrl, player, payload.Args, ctx, floorH, fastCts.Token);
                            var fastSw = Stopwatch.StartNew();
                            YtDlpResult? fastRes = null;
                            try { fastRes = await specific.Executor(fastSctx); }
                            catch (Exception ex) { _logger.Debug("[" + ctx.CorrelationId + "] Fast-path '" + specific.Name + "' threw: " + ex.Message); }
                            fastSw.Stop();
                            if (fastRes != null && IsAcceptableQuality(fastRes.Height, floorH))
                            {
                                winnerResult = fastRes; activeTier = specific.Group; activeStrategy = specific.Name;
                                winnerForcesRelayWrap = specific.ForceRelayWrap;
                                _logger.Info("[" + ctx.CorrelationId + "] [StrategyMemory] Fast-path '" + specific.Name
                                    + "' won in " + fastSw.ElapsedMilliseconds + "ms"
                                    + (fastRes.Height is int fh ? " at " + fh + "p." : "."));
                            }
                            else
                            {
                                string reason = fastCts.IsCancellationRequested ? "timed out"
                                    : (fastRes == null ? "no result" : "below floor (" + fastRes.Height + "p)");
                                _logger.Warning("[" + ctx.CorrelationId + "] [StrategyMemory] Fast-path '" + specific.Name
                                    + "' failed (" + reason + ") in " + fastSw.ElapsedMilliseconds + "ms — demoting and cold-racing.");
                                RecordStrategyFailure(memKey, specific.Name);
                                if (fastRes != null && (bestSoFar == null || (fastRes.Height ?? 0) > (bestSoFar.Height ?? 0)))
                                { bestSoFar = fastRes; bestSoFarTier = specific.Name; }
                                remembered = null; rememberedGroup = null;
                                cascadeStart = 0; // re-enable cold race + full sequential cascade
                            }
                        }
                        else
                        {
                            _logger.Debug("[" + ctx.CorrelationId + "] [StrategyMemory] Remembered strategy '"
                                + remembered.StrategyName + "' not in current catalog — cold-racing instead.");
                        }
                    }

                    // Cold-start race: no memory, Tier 1 is first, Tier 2 is also active. Race every
                    // applicable Tier 1 variant plus Tier 2 in parallel — concurrency capped. First past
                    // the quality floor wins. Sub-floor results are kept as fallback. This is the
                    // "request spam, resolve fast, then remember" approach the user asked for.
                    bool coldStart = remembered == null;
                    int tier1Idx = cascade.IndexOf("tier1");
                    int tier2Idx = cascade.IndexOf("tier2");
                    bool canRace = coldStart && tier1Idx == cascadeStart && tier2Idx > tier1Idx;
                    if (canRace)
                    {
                        var strategies = BuildColdRaceStrategies(targetUrl, player, payload.Args, disabled);
                        _logger.Info("[" + ctx.CorrelationId + "] [Race] Cold-start across " + strategies.Count + " strategies: [" + string.Join(", ", strategies.Select(s => s.Name)) + "] (concurrency cap 5, first past " + floorH + "p floor wins).");
                        var raceSw = Stopwatch.StartNew();

                        using var raceCts = new System.Threading.CancellationTokenSource();
                        using var sem = new System.Threading.SemaphoreSlim(5);
                        var sctx = new StrategyRunContext(targetUrl, player, payload.Args, ctx, floorH, raceCts.Token);
                        var launches = strategies.Select(s => RunStrategySlot(s, sctx, sem, raceCts.Token)).ToList();

                        while (launches.Count > 0 && winnerResult == null)
                        {
                            var done = await Task.WhenAny(launches);
                            launches.Remove(done);
                            var (strat, res) = await done;
                            if (res == null)
                            {
                                if (!raceCts.IsCancellationRequested) RecordStrategyFailure(memKey, strat.Name);
                                continue;
                            }
                            if (IsAcceptableQuality(res.Height, floorH))
                            {
                                winnerResult = res; activeTier = strat.Group; activeStrategy = strat.Name;
                                winnerForcesRelayWrap = strat.ForceRelayWrap;
                                _logger.Info("[" + ctx.CorrelationId + "] [Race] '" + strat.Name + "' cleared floor in " + raceSw.ElapsedMilliseconds + "ms — winner.");
                                try { raceCts.Cancel(); } catch { }
                            }
                            else
                            {
                                _logger.Info("[" + ctx.CorrelationId + "] [Race] '" + strat.Name + "' returned " + res.Height + "p < " + floorH + "p floor — keeping as fallback.");
                                if (bestSoFar == null || (res.Height ?? 0) > (bestSoFar.Height ?? 0))
                                { bestSoFar = res; bestSoFarTier = strat.Name; }
                            }
                        }
                        raceSw.Stop();

                        // Skip past the groups we covered in the race; Tier 3 is the remaining sequential fallback.
                        if (winnerResult == null)
                        {
                            cascadeStart = Math.Max(cascadeStart, tier2Idx + 1);
                            _logger.Debug("[" + ctx.CorrelationId + "] [Race] No winner — continuing cascade at index " + cascadeStart + ".");
                        }
                    }

                    int i = cascadeStart;
                    bool cascadeRestarted = false;
                    while (i < cascade.Count && winnerResult == null)
                    {
                        string tier = cascade[i];
                        YtDlpResult? tierResult = null;
                        string tierStrategy = tier + ":default";

                        if (tier == "tier1") { tierResult = await AttemptTier1(targetUrl, player, ctx); tierStrategy = "tier1:default"; }
                        else if (tier == "tier2") { tierResult = await AttemptTier2(targetUrl, player, ctx); tierStrategy = "tier2:cloud-whyknot"; }
                        else if (tier == "tier3") { tierResult = await AttemptTier3(payload.Args, ctx); tierStrategy = "tier3:plain"; }

                        if (tierResult != null)
                        {
                            // Quality heuristic: accept immediately if height is unknown (tier 2/3/streamlink)
                            // or >= floor. Otherwise record as best-so-far and keep cascading.
                            if (IsAcceptableQuality(tierResult.Height, floorH))
                            {
                                winnerResult = tierResult; activeTier = tier; activeStrategy = tierStrategy;
                                break;
                            }

                            _logger.Info("[" + ctx.CorrelationId + "] [" + tier + "] returned " + tierResult.Height + "p < " + floorH + "p floor — cascading to next tier for better quality.");
                            if (bestSoFar == null || (tierResult.Height ?? 0) > (bestSoFar.Height ?? 0))
                            {
                                // Record the full strategy name (e.g. "tier2:cloud-whyknot"), not just "tier2".
                                // The best-of fallback at the end of the cascade feeds this name into StrategyMemory —
                                // using the group alone would synthesize a fake "tier2:default" entry that never ran.
                                bestSoFar = tierResult; bestSoFarTier = tierStrategy;
                            }
                        }
                        else if (remembered != null && tier == rememberedGroup)
                        {
                            RecordStrategyFailure(memKey, remembered.StrategyName);
                        }

                        if (!cascadeRestarted && remembered != null && tier == rememberedGroup)
                        {
                            _logger.Warning("[" + ctx.CorrelationId + "] [StrategyMemory] Remembered group '" + rememberedGroup + "' failed — retrying full cascade.");
                            remembered = null; rememberedGroup = null; cascadeRestarted = true;
                            i = 0; continue;
                        }

                        i++;
                    }

                    // All tiers finished below floor: fall back to the best sub-floor result we got.
                    if (winnerResult == null && bestSoFar != null)
                    {
                        _logger.Warning("[" + ctx.CorrelationId + "] All tiers returned below the " + floorH + "p floor; using best-of: [" + bestSoFarTier + "] at " + bestSoFar.Height + "p.");
                        winnerResult = bestSoFar; activeTier = bestSoFarTier.Split(':')[0]; activeStrategy = bestSoFarTier.Contains(":") ? bestSoFarTier : (bestSoFarTier + ":default");
                    }

                    if (winnerResult != null && !string.IsNullOrEmpty(activeStrategy))
                    {
                        RecordStrategySuccess(memKey, activeStrategy, winnerResult.Height);
                        _logger.Debug("[" + ctx.CorrelationId + "] [StrategyMemory] Recorded success for '" + activeStrategy + "' on " + memKey +
                            (winnerResult.Height is int h ? " at " + h + "p." : "."));
                    }
                }

                if (winnerResult == null && !disabled.Contains("tier4"))
                {
                    _logger.Warning("[" + ctx.CorrelationId + "] All active tiers exhausted — falling back to original URL (passthrough). Video may not play correctly.");
                    winnerResult = new YtDlpResult(targetUrl, null, null, null, null, null);
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
                else if (winnerResult == null)
                {
                    _logger.Error("[" + ctx.CorrelationId + "] Resolution failed: all tiers exhausted and Tier 4 passthrough is disabled. No URL will be returned.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("[" + ctx.CorrelationId + "] Resolution loop fatal error: " + ex.Message, ex);
            winnerResult = new YtDlpResult(targetUrl, null, null, null, null, null);
            activeTier = "tier4-error";
        }

        string? result = winnerResult?.Url;
        bool isLive = result != null && (result.Contains(".m3u8") || result.Contains("m3u8"));
        string streamType = isLive ? "live" : (!string.IsNullOrEmpty(result) && result != "FAILED" ? "vod" : "unknown");

        if (isLive)
            _logger.Info("[" + ctx.CorrelationId + "] Detected HLS/live stream. Stream type: " + streamType);

        if (!string.IsNullOrEmpty(result) && result != "FAILED")
            result = ApplyRelayWrap(result, skipRelayWrap, ctx.CorrelationId, forceWrap: winnerForcesRelayWrap);

        // Populate resolve cache on successful non-passthrough resolution. Skipping Tier 4 means
        // a fresh cascade attempt next time — we don't want to "remember" a failed cascade.
        if (winnerResult != null && !activeTier.StartsWith("tier4"))
        {
            _resolveCache[cacheKey] = new ResolveCacheEntry(winnerResult, activeTier, DateTime.UtcNow.Add(ResolveCacheTtl));
            if (_resolveCache.Count > 100)
            {
                var now = DateTime.UtcNow;
                foreach (var kv in _resolveCache)
                    if (kv.Value.Expires <= now) _resolveCache.TryRemove(kv.Key, out _);
            }
        }

        var tierKey = activeTier.Split('-')[0];
        _tierCounts.AddOrUpdate(tierKey, 1, (_, v) => v + 1);

        var entry = new HistoryEntry {
            Timestamp = DateTime.Now,
            OriginalUrl = targetUrl,
            ResolvedUrl = result ?? "FAILED",
            Tier = activeTier,
            Player = historyPlayerLabel ?? player,
            Success = !string.IsNullOrEmpty(result),
            IsLive = isLive,
            StreamType = streamType,
            ResolutionHeight = winnerResult?.Height,
            ResolutionWidth = winnerResult?.Width,
            Vcodec = winnerResult?.Vcodec
        };

        _settings.Config.History.Insert(0, entry);
        if (_settings.Config.History.Count > 100) _settings.Config.History.RemoveAt(100);
        try { _settings.Save(); }
        catch (Exception ex) { _logger.Warning("[" + ctx.CorrelationId + "] Failed to persist history after resolution: " + ex.Message); }

        resolutionSw.Stop();
        Interlocked.Decrement(ref _activeResolutions);
        UpdateStatus("Resolution completed via " + activeTier.ToUpper(), ctx);

        string resolutionLabel = "";
        if (winnerResult != null && winnerResult.Height.HasValue)
        {
            string w = winnerResult.Width?.ToString() ?? "?";
            string v = winnerResult.Vcodec != null ? " " + winnerResult.Vcodec : "";
            resolutionLabel = " [" + w + "x" + winnerResult.Height + v + "]";
        }
        _logger.Success("[" + ctx.CorrelationId + "] Final Resolution [" + activeTier + "] [" + streamType + "]" + resolutionLabel + " in " + resolutionSw.ElapsedMilliseconds + "ms: " + (result != null && result.Length > 100 ? result.Substring(0, 100) + "..." : result));
        return result;
    }

    private static string FormatMetaLog(YtDlpResult r)
    {
        if (r.Height == null && r.Vcodec == null) return "";
        string h = r.Height.HasValue ? r.Height + "p" : "?";
        string v = r.Vcodec != null ? " " + r.Vcodec : "";
        return " [" + h + v + "]";
    }

    // Per-tier attempt wrappers: call resolver with timing, run reachability check where applicable,
    // emit the success/failure log line. Return the YtDlpResult if successful, null otherwise.
    // Shared between sequential cascade and parallel race branch.
    private async Task<YtDlpResult?> AttemptTier1(string url, string player, RequestContext ctx)
    {
        var (res, ms) = await TimedResolve(() => ResolveTier1(url, player, ctx));
        if (res == null)
        {
            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1] yt-dlp returned no URL after " + ms + "ms — check stderr above for cause.");
            return null;
        }
        if (!await CheckUrlReachable(res.Url, ctx))
        {
            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1] URL resolved in " + ms + "ms but failed reachability check — cascading to next tier.");
            return null;
        }
        _logger.Info("[" + ctx.CorrelationId + "] [Tier 1] Success in " + ms + "ms" + FormatMetaLog(res) + ".");
        return res;
    }

    private async Task<YtDlpResult?> AttemptTier2(string url, string player, RequestContext ctx)
    {
        var (res, ms) = await TimedResolve(() => ResolveTier2(url, player, ctx));
        if (res == null)
        {
            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 2] Cloud resolver returned no URL after " + ms + "ms.");
            return null;
        }
        _logger.Info("[" + ctx.CorrelationId + "] [Tier 2] Success in " + ms + "ms" + FormatMetaLog(res) + ".");
        return res;
    }

    private async Task<YtDlpResult?> AttemptTier3(string[] originalArgs, RequestContext ctx)
    {
        var (res, ms) = await TimedResolve(() => ResolveTier3(originalArgs, ctx));
        if (res == null)
        {
            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 3] yt-dlp-og returned no URL after " + ms + "ms.");
            return null;
        }
        _logger.Info("[" + ctx.CorrelationId + "] [Tier 3] Success in " + ms + "ms" + FormatMetaLog(res) + ".");
        return res;
    }

    // Builds the set of strategies to race in parallel on a cold-start request (no StrategyMemory
    // hit). The catalog is request-aware: YouTube URLs get the PO-token variant; non-YouTube URLs
    // get the impersonate-only and vrchat-ua variants (aimed at movie-world hosts). Tier 2 is always
    // included because it runs on a WebSocket (no subprocess).
    private List<ResolveStrategy> BuildColdRaceStrategies(string url, string player, string[] originalArgs, List<string> disabled)
    {
        var list = new List<ResolveStrategy>();
        bool isYouTubeHost = url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
                          || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
        string? videoId = isYouTubeHost ? ExtractYouTubeVideoId(url) : null;

        if (!disabled.Contains("tier1"))
        {
            // Default variant: auto PO-token + auto impersonate (current live behaviour).
            list.Add(new ResolveStrategy("tier1:default", "tier1", 10, true,
                sctx => ResolveTier1(sctx.Url, sctx.Player, sctx.RequestContext)));

            // VRChat UA: for movie-world hosts that allowlist UnityPlayer. Tier 1 sees a successful
            // generic-extractor probe instead of the 403 it gets with the default UA.
            list.Add(new ResolveStrategy("tier1:vrchat-ua", "tier1", 20, true,
                sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                    injectPot: false, injectImpersonate: false,
                    userAgent: VrchatAvProUserAgent, referer: VrchatReferer,
                    videoId: videoId, variantLabel: "vrchat-ua")));

            // curl-impersonate without PO token: for sites where the PO-token request itself flags us.
            list.Add(new ResolveStrategy("tier1:impersonate-only", "tier1", 30, true,
                sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                    injectPot: false, injectImpersonate: true,
                    userAgent: null, referer: null,
                    videoId: videoId, variantLabel: "impersonate-only")));

            // Plain yt-dlp: last-resort bypass for hosts that work without any extras.
            list.Add(new ResolveStrategy("tier1:plain", "tier1", 40, true,
                sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                    injectPot: false, injectImpersonate: false,
                    userAgent: null, referer: null,
                    videoId: videoId, variantLabel: "plain")));

            // YouTube-only: PO token forced on (no impersonate). Useful when impersonate confuses youtube.com.
            if (isYouTubeHost)
            {
                list.Add(new ResolveStrategy("tier1:po-only", "tier1", 25, true,
                    sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                        injectPot: true, injectImpersonate: false,
                        userAgent: null, referer: null,
                        videoId: videoId, variantLabel: "po-only")));

                // Alternate YouTube player clients. Each yt-dlp YouTube "client" has its own bot-detection
                // profile and format set. Memory learns which one wins per video/host; if none of the
                // default/impersonate/po variants succeed (YouTube rolled a new bot-check signature),
                // one of these typically still works. Priority higher than 40 means they fire last in the
                // concurrent race — they're fallbacks, not primaries. Each carries its own memory entry
                // so we can see per-client W/L in the Bypass Health view.
                //   ios_music   — audio-focused, often passes when main clients are gated
                //   tv_embedded — TV cast client, historically bypasses age gates
                //   android_vr  — Oculus VR client, rarely gated, weaker format selection
                //   web_safari  — Safari variant, different fingerprint from plain 'web'
                //   mweb        — mobile web, lightweight format set
                list.Add(new ResolveStrategy("tier1:ios-music", "tier1", 50, true,
                    sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                        injectPot: false, injectImpersonate: false,
                        userAgent: null, referer: null,
                        videoId: videoId, variantLabel: "ios-music", playerClient: "ios_music")));

                list.Add(new ResolveStrategy("tier1:tv-embedded", "tier1", 55, true,
                    sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                        injectPot: false, injectImpersonate: false,
                        userAgent: null, referer: null,
                        videoId: videoId, variantLabel: "tv-embedded", playerClient: "tv_embedded")));

                list.Add(new ResolveStrategy("tier1:android-vr", "tier1", 60, true,
                    sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                        injectPot: false, injectImpersonate: false,
                        userAgent: null, referer: null,
                        videoId: videoId, variantLabel: "android-vr", playerClient: "android_vr")));

                list.Add(new ResolveStrategy("tier1:web-safari", "tier1", 65, true,
                    sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                        injectPot: false, injectImpersonate: false,
                        userAgent: null, referer: null,
                        videoId: videoId, variantLabel: "web-safari", playerClient: "web_safari")));

                list.Add(new ResolveStrategy("tier1:mweb", "tier1", 70, true,
                    sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                        injectPot: false, injectImpersonate: false,
                        userAgent: null, referer: null,
                        videoId: videoId, variantLabel: "mweb", playerClient: "mweb")));
            }
        }

        if (!disabled.Contains("tier2"))
        {
            list.Add(new ResolveStrategy("tier2:cloud-whyknot", "tier2", 10, false,
                sctx => AttemptTier2(sctx.Url, sctx.Player, sctx.RequestContext)));
        }

        // WARP variants: same yt-dlp recipes but egress via Cloudflare WARP (SOCKS5 loopback).
        // Useful for origins that geo-block or IP-flag the user's home ISP — WARP presents a
        // Cloudflare edge IP that many CDNs trust by default. Fires last in the race (priority 90+)
        // since most direct requests succeed; WARP is the "try a different network" retry.
        // Only added when tier1 isn't disabled AND WARP is actually up (no point queueing strategies
        // that will no-op — memory would learn a fake winner).
        if (!disabled.Contains("tier1") && _warp != null && _warp.IsActive)
        {
            list.Add(new ResolveStrategy("tier1:warp+default", "tier1", 90, true,
                sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                    injectPot: false, injectImpersonate: _curlClient?.IsAvailable == true,
                    userAgent: null, referer: null,
                    videoId: videoId, variantLabel: "warp+default", playerClient: null, useWarp: true)));

            list.Add(new ResolveStrategy("tier1:warp+vrchat-ua", "tier1", 95, true,
                sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                    injectPot: false, injectImpersonate: false,
                    userAgent: VrchatAvProUserAgent, referer: VrchatReferer,
                    videoId: videoId, variantLabel: "warp+vrchat-ua", playerClient: null, useWarp: true)));
        }

        // Browser-extract: last-resort bypass. A real headless browser visits the page, captures
        // the first media URL it sees, and (if the origin is gated) the captured session headers
        // are stashed in BrowserSessionCache so the relay replays them on AVPro's requests. Costs
        // a browser page load (~3–8s) so it fires last in the race — earlier strategies usually win.
        // ForceRelayWrap=true: even non-YouTube URLs need to flow through the relay so captured
        // cookies/headers reach AVPro's subsequent requests. Subprocess=false: runs in-proc via
        // PuppeteerSharp (the browser is itself a subprocess but not counted against the semaphore).
        if (!disabled.Contains("tier1") && _browserExtractor != null && _settings.Config.EnableBrowserExtract)
        {
            list.Add(new ResolveStrategy("tier1:browser-extract", "tier1", 80, false,
                sctx => RunBrowserExtract(sctx.Url, sctx.RequestContext, sctx.Cancellation),
                ForceRelayWrap: true));
        }
        return list;
    }

    // Strategy runner used by the cold-race dispatcher. Honours the shared semaphore and the
    // race-wide cancellation token. Returns (strategy, null) if cancelled or the executor failed.
    private async Task<(ResolveStrategy Strategy, YtDlpResult? Result)> RunStrategySlot(
        ResolveStrategy s, StrategyRunContext sctx,
        System.Threading.SemaphoreSlim sem, System.Threading.CancellationToken ct)
    {
        try { await sem.WaitAsync(ct); }
        catch (OperationCanceledException) { return (s, null); }
        try
        {
            if (ct.IsCancellationRequested) return (s, null);
            var r = await s.Executor(sctx);
            return (s, r);
        }
        catch (Exception ex)
        {
            _logger.Debug("[" + sctx.RequestContext.CorrelationId + "] Strategy '" + s.Name + "' threw: " + ex.Message);
            return (s, null);
        }
        finally { try { sem.Release(); } catch { } }
    }

    private async Task<YtDlpResult?> ResolveTier1(string url, string player, RequestContext ctx)
    {
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1] Attempting native yt-dlp resolution...");

        bool isYouTube = url.Contains("youtube.com") || url.Contains("youtu.be");
        string host = ExtractHost(url);
        string? videoId = isYouTube ? ExtractYouTubeVideoId(url) : null;

        // Decide whether to fetch a PO token up front. YouTube doesn't require PO on every request —
        // it flips into bot-detection mode domain-wide for a window of ~30 min. The fast-path (no PO)
        // completes in 2-3s when YouTube is happy; PO token fetch adds 5-15s. So: only pay the PO cost
        // when we've recently seen a bot-check for this host.
        bool needsPot = isYouTube && DomainRequiresPot(host);
        var result = await RunTier1Attempt(url, player, ctx, injectPot: needsPot, videoId);

        // Fast-path failure mode: bot-check stderr even though we didn't send a PO token. Flag the
        // domain so the next request uses PO upfront. Don't retry in-call — the cascade falls through
        // to Tier 2 for this request; next Tier 1 call will take the PO path and likely succeed.
        if (result == null && !needsPot && isYouTube && IsBotDetectionStderr(_lastTier1Stderr))
        {
            MarkDomainRequiresPot(host);
            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1] YouTube bot detection triggered on fast-path for '" + host + "' — flagging domain for PO token for " + DomainRequiresPotTtl.TotalMinutes + " min.");
        }
        // PO-path failure: PO token was injected but bot-check still fired. Refresh the flag so we keep
        // using PO, and log loudly — this usually means the bgutil sidecar's token is stale.
        else if (result == null && needsPot && isYouTube && IsBotDetectionStderr(_lastTier1Stderr))
        {
            MarkDomainRequiresPot(host);
            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1] YouTube bot detection triggered EVEN WITH PO token for '" + host + "' — check bgutil sidecar health.");
        }

        return result;
    }

    // Stashed stderr from the most recent Tier 1 attempt so the outer method can decide whether to
    // flag the domain. Avoids changing RunYtDlp's signature just to plumb stderr through one path.
    private string _lastTier1Stderr = "";

    // Browser-extract executor. Runs a headless browser, captures the first media URL it sees,
    // probes whether AVPro can reach it directly, and (if not) caches the session headers/cookies
    // in BrowserSessionCache for the relay to replay. Returns a YtDlpResult wrapping the media URL.
    // The strategy's ForceRelayWrap flag tells ApplyRelayWrap to wrap the URL even for non-YouTube
    // hosts when the browser session is required.
    private async Task<YtDlpResult?> RunBrowserExtract(string url, RequestContext ctx, CancellationToken ct)
    {
        if (_browserExtractor == null)
        {
            _logger.Debug("[" + ctx.CorrelationId + "] [browser-extract] Service not wired — strategy skipped.");
            return null;
        }
        if (!_settings.Config.EnableBrowserExtract)
        {
            _logger.Debug("[" + ctx.CorrelationId + "] [browser-extract] Disabled by config (EnableBrowserExtract=false).");
            return null;
        }

        // Deadline: 25s gives the browser enough time to load and intercept a first manifest while
        // still letting faster strategies win the race. Site load typically lands in 3–8s.
        var sw = Stopwatch.StartNew();
        var result = await _browserExtractor.ExtractMediaUrlAsync(url, TimeSpan.FromSeconds(25), ct);
        sw.Stop();

        if (result == null)
        {
            _logger.Debug("[" + ctx.CorrelationId + "] [browser-extract] No media URL captured in " + sw.ElapsedMilliseconds + "ms.");
            return null;
        }
        _logger.Info("[" + ctx.CorrelationId + "] [browser-extract] Captured media URL in " + result.ElapsedMs + "ms (" + result.RequestsLogged + " requests seen, sessionCached=" + result.SessionCached + ").");
        return new YtDlpResult(result.MediaUrl, result.Height, null, null, null, null);
    }

    private Task<YtDlpResult?> RunTier1Attempt(string url, string player, RequestContext ctx, bool injectPot, string? videoId)
        => RunTier1Attempt(url, player, ctx, injectPot, injectImpersonate: _curlClient?.IsAvailable == true, userAgent: null, referer: null, videoId: videoId, variantLabel: "default", playerClient: null);

    // Variant-aware Tier 1 yt-dlp invocation. Strategies in the catalog call through this with
    // different flag combinations so the dispatcher can race them in parallel. The variantLabel
    // shows up in log lines for diagnostic clarity.
    //
    // playerClient: when non-null, passes --extractor-args youtube:player_client=<value>. yt-dlp
    // supports 'web', 'mweb', 'ios', 'ios_music', 'android_vr', 'tv_embedded', 'web_safari', etc.
    // Different clients return different format sets and have different bot-detection profiles —
    // some survive restrictive-mode/age-gating where the default 'web' client fails. Combining
    // multiple clients in --extractor-args is legal (comma-separated); we keep one per strategy
    // so the memory ranker can learn which specific client wins per host.
    private async Task<YtDlpResult?> RunTier1Attempt(string url, string player, RequestContext ctx,
        bool injectPot, bool injectImpersonate, string? userAgent, string? referer, string? videoId, string variantLabel, string? playerClient = null, bool useWarp = false)
    {
        // --print replaces legacy --get-url and lets us capture format metadata (height/vcodec) on the side.
        // Two sentinel-prefixed lines are emitted so the parser can distinguish URL from meta line.
        var args = new List<string> {
            "--print", "url:%(url)s",
            "--print", "meta:%(height)s|%(width)s|%(vcodec)s|%(format_id)s|%(protocol)s",
            "--no-warnings", "--playlist-items", "1"
        };
        if (_settings.Config.ForceIPv4) args.Add("--force-ipv4");

        // Cloudflare WARP route-through: yt-dlp (and the generic extractor's HTTP probes) go out via
        // our on-host wireproxy SOCKS5 listener, which is user-space WG to the Cloudflare edge. Only
        // this specific yt-dlp subprocess is affected — nothing else on the host routes through WARP.
        // Strategies opt in via useWarp=true; WarpService.IsActive gates it (skip the flag if WARP
        // didn't come up so we don't leak --proxy to a dead listener).
        if (useWarp)
        {
            if (_warp != null && _warp.IsActive)
            {
                args.Add("--proxy");
                args.Add(_warp.SocksProxyUrl);
                _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] Routing yt-dlp through WARP SOCKS5 (" + _warp.SocksProxyUrl + ").");
            }
            else
            {
                _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] useWarp requested but WARP is not active — running direct.");
            }
        }

        if (injectPot)
        {
            if (_potProvider == null)
            {
                _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] PotProviderService not wired — PO token cannot be injected.");
            }
            else
            {
                string potCacheKey = videoId ?? "unknown";
                string? token = await _potProvider.GetPotTokenAsync("wkvrcproxy", potCacheKey);
                if (!string.IsNullOrEmpty(token))
                {
                    args.Add("--extractor-args");
                    args.Add("youtube:po_token=web.gvs+" + token);
                    _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] PO token injected for video: " + potCacheKey);
                }
                else
                {
                    _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] PO token fetch returned null for video: " + potCacheKey + " — attempting without token.");
                }
            }
        }
        else
        {
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] Skipping PO token fetch.");
        }

        if (!string.IsNullOrEmpty(userAgent))
        {
            args.Add("--user-agent");
            args.Add(userAgent);
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] User-Agent override: " + userAgent);
        }
        if (!string.IsNullOrEmpty(referer))
        {
            args.Add("--referer");
            args.Add(referer);
        }

        string formatStr;
        string res = _settings.Config.PreferredResolution.Replace("p", "");
        if (player == "AVPro")
        {
            // AVPro supports HLS, DASH, and MP4. Prefer HLS first (works for both live and VOD).
            // Height-capped branches are tried first so AVPro does not choke on 4K / HEVC it cannot decode;
            // unrestricted fallbacks keep us from ever returning nothing when only higher renditions exist.
            formatStr = "best[protocol^=m3u8_native][height<=" + res + "]/"
                      + "best[protocol^=http_dash_segments][height<=" + res + "]/"
                      + "best[ext=mp4][height<=" + res + "]/"
                      + "best[protocol^=m3u8_native]/"
                      + "best[ext=mp4]/bestaudio/best";
        }
        else
        {
            // Unity player: prefer MP4 for VODs (better seeking), fall back to HLS for live streams,
            // and explicitly avoid raw DASH which Unity cannot decode.
            formatStr = "best[ext=mp4][height<=" + res + "]/best[ext=mp4]/best[protocol^=m3u8_native]/bestaudio/best[protocol!=http_dash_segments]/best";
        }
        args.Add("-f");
        args.Add(formatStr);
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] Player=" + player + " Format=" + formatStr);

        // Inject generic:impersonate when curl-impersonate is available.
        // Required for CDN URLs protected by Cloudflare anti-bot (e.g. imvrcdn.com) — without this
        // yt-dlp's generic extractor gets HTTP 403 and fails. The youtube extractor ignores this arg.
        if (injectImpersonate && _curlClient?.IsAvailable == true)
        {
            args.Add("--extractor-args");
            args.Add("generic:impersonate");
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] Injecting generic:impersonate.");
        }

        // Per-strategy YouTube player_client override. yt-dlp accepts multiple --extractor-args for
        // the same extractor; they merge at parse time, so this is additive to any po_token flag above.
        if (!string.IsNullOrEmpty(playerClient))
        {
            args.Add("--extractor-args");
            args.Add("youtube:player_client=" + playerClient);
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] YouTube player_client=" + playerClient + ".");
        }

        args.Add(url);
        var (result, stderr) = await RunYtDlp("yt-dlp.exe", args, ctx);
        _lastTier1Stderr = stderr;
        return result;
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

    private async Task<YtDlpResult?> ResolveTier2(string url, string player, RequestContext ctx)
    {
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 2] Calling WhyKnot.dev via WebSocket...");
        int maxHeight = ParsePreferredHeight();
        string? resolved = await _tier2Client.ResolveUrlAsync(url, player, maxHeight, ctx.CorrelationId);
        // Tier 2 server currently returns only the stream URL. Height stays null until whyknot.dev
        // adds format metadata to the resolve_result message (see follow-up in plan).
        return resolved == null ? null : new YtDlpResult(resolved, null, null, null, null, null);
    }

    private Task<YtDlpResult?> ResolveTier3(string[] originalArgs, RequestContext ctx)
        => ResolveTier3(originalArgs, ctx, userAgent: null, referer: null, variantLabel: "plain");

    private async Task<YtDlpResult?> ResolveTier3(string[] originalArgs, RequestContext ctx,
        string? userAgent, string? referer, string variantLabel)
    {
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 3:" + variantLabel + "] Attempting VRChat's yt-dlp-og.exe.");
        var args = originalArgs.ToList();
        if (!string.IsNullOrEmpty(userAgent))
        {
            args.Add("--user-agent");
            args.Add(userAgent);
        }
        if (!string.IsNullOrEmpty(referer))
        {
            args.Add("--referer");
            args.Add(referer);
        }
        var (result, _) = await RunYtDlp("yt-dlp-og.exe", args, ctx);
        return result;
    }

    // Asks Streamlink whether it has a plugin that handles the given URL.
    // Uses `streamlink --can-handle-url <url>` which is a local plugin registry check —
    // no network call, completes in <500ms. Exit code 0 means Streamlink supports the URL;
    // non-zero means it doesn't. This is the authoritative gate for Tier 0: no hardcoded
    // domain lists, no URL pattern matching — Streamlink's own registry decides.
    //
    // Results are cached per-host to avoid paying ~500ms on every resolve for the same unsupported
    // domain (e.g. vr-m.net). Plugin list only changes across Streamlink upgrades, so 24h/7d TTLs
    // are plenty.
    private async Task<bool> StreamlinkCanHandleUrlAsync(string url, RequestContext ctx)
    {
        string path = GetBinaryPath("streamlink.exe");
        if (!File.Exists(path)) return false; // Not installed — skip silently

        string host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : url;
        if (_streamlinkCapabilityCache.TryGetValue(host, out var cached) && cached.Expiry > DateTime.UtcNow)
        {
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] Streamlink capability cache hit for " + host + " → " + cached.CanHandle + ".");
            if (!cached.CanHandle)
                _logger.Info("[" + ctx.CorrelationId + "] [Tier 0] Streamlink has no plugin for " + host + " — skipping (cached).");
            return cached.CanHandle;
        }
        bool result = await StreamlinkCanHandleUrlUncachedAsync(url, path, ctx);
        var ttl = result ? StreamlinkCacheTtlPositive : StreamlinkCacheTtlNegative;
        _streamlinkCapabilityCache[host] = (result, DateTime.UtcNow.Add(ttl));
        if (!result)
            _logger.Info("[" + ctx.CorrelationId + "] [Tier 0] Streamlink has no plugin for " + host + " — skipping.");
        return result;
    }

    private async Task<bool> StreamlinkCanHandleUrlUncachedAsync(string url, string path, RequestContext ctx)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = path;
            process.StartInfo.ArgumentList.Add("--can-handle-url");
            process.StartInfo.ArgumentList.Add(url);
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            process.Start();
            ProcessGuard.Register(process);

            // Drain stdout/stderr to prevent buffer deadlock — exit code is all we need.
            // Suppress ObjectDisposedException if the process is killed on the timeout path.
            _ = process.StandardOutput.ReadToEndAsync().ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
            _ = process.StandardError.ReadToEndAsync().ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);

            var tcs = new TaskCompletionSource<int>();
            _ = Task.Run(() => {
                try { process.WaitForExit(); tcs.TrySetResult(process.ExitCode); }
                catch (ObjectDisposedException) { tcs.TrySetResult(-1); }
                catch (InvalidOperationException) { tcs.TrySetResult(-1); }
            });

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            if (completed != tcs.Task)
            {
                try { process.Kill(); } catch { }
                _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] --can-handle-url timed out — skipping Streamlink.");
                return false;
            }

            return await tcs.Task == 0;
        }
        catch (Exception ex)
        {
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] --can-handle-url error: " + ex.Message);
            return false;
        }
    }

    private async Task<YtDlpResult?> ResolveStreamlink(string url, RequestContext ctx)
    {
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] Attempting Streamlink resolution...");
        var args = new List<string> { "--stream-url", "--quiet", url, "best" };
        var (result, _) = await RunYtDlp("streamlink.exe", args, ctx, timeoutMs: 9000);
        return result;
    }

    // yt-dlp-og.exe lives in the VRChat Tools folder (created by PatcherService as a backup).
    // streamlink.exe lives in tools/streamlink/bin/ (portable zip layout) or tools/streamlink/.
    // All other binaries (yt-dlp.exe, redirector.exe) live in dist/tools/.
    private string GetBinaryPath(string binary)
    {
        if (binary == "streamlink.exe")
        {
            string slBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "streamlink");
            string slBin = Path.Combine(slBase, "bin", binary);
            if (File.Exists(slBin)) return slBin;
            return Path.Combine(slBase, binary);
        }
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

    private async Task<(YtDlpResult? Result, string Stderr)> RunYtDlp(string binary, List<string> args, RequestContext ctx, int timeoutMs = 15000)
    {
        string path = GetBinaryPath(binary);
        if (!File.Exists(path))
        {
            _logger.Error("[" + ctx.CorrelationId + "] " + binary + " not found at: " + path);
            return (null, "");
        }

        // Sanitize args for logging — mask PO token value (it's long and security-sensitive)
        string loggableArgs = string.Join(" ", args.Select(a => a.StartsWith("youtube:po_token=") ? "youtube:po_token=[REDACTED]" : a));
        _logger.Debug("[" + ctx.CorrelationId + "] Executing: " + binary + " " + loggableArgs);

        try
        {
            var stdoutLines = new List<string>();
            var stdoutLock = new object();
            var urlSeenTcs = new TaskCompletionSource<bool>();

            using var process = new Process();
            process.StartInfo.FileName = path;
            process.StartInfo.Arguments = string.Join(" ", args.Select(a => "\"" + a + "\""));
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            process.OutputDataReceived += (s, e) => {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                string line = e.Data.Trim();
                lock (stdoutLock) stdoutLines.Add(line);
                // Signal early when a URL line appears, but don't complete — we still need the meta line.
                if (line.StartsWith("url:") || line.StartsWith("http"))
                    urlSeenTcs.TrySetResult(true);
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

            var exitTcs = new TaskCompletionSource<bool>();
            _ = Task.Run(() => {
                try { process.WaitForExit(); }
                catch (ObjectDisposedException) { /* process disposed on timeout path — expected */ }
                catch (InvalidOperationException) { /* process never started or already cleaned up */ }
                exitTcs.TrySetResult(true);
            });

            var timeoutTask = Task.Delay(timeoutMs);
            var completed = await Task.WhenAny(exitTcs.Task, timeoutTask);

            // Log any stderr output regardless of whether it timed out or resolved
            string stderrOutput = stderrLines.ToString().Trim();
            if (!string.IsNullOrEmpty(stderrOutput))
                _logger.Warning("[" + ctx.CorrelationId + "] [" + binary + "] stderr: " + stderrOutput);

            if (completed == timeoutTask)
            {
                _logger.Warning("[" + ctx.CorrelationId + "] " + binary + " timed out after " + (timeoutMs / 1000) + "s.");
                _eventBus?.PublishError("ResolutionEngine", new ErrorContext {
                    Category = ErrorCategory.ChildProcess,
                    Code = ErrorCodes.YTDLP_TIMEOUT,
                    Summary = binary + " timed out after " + (timeoutMs / 1000) + " seconds",
                    Detail = "The process did not produce a URL within the timeout window",
                    ActionHint = "The video source may be slow to respond. Try again or switch to a different tier.",
                    IsRecoverable = true
                }, ctx.CorrelationId);
                try { process.Kill(); } catch { /* Process may have already exited */ }
                return (null, stderrOutput);
            }

            // Non-zero exit codes are almost always the reason yt-dlp returned no URL
            if (process.HasExited && process.ExitCode != 0)
                _logger.Warning("[" + ctx.CorrelationId + "] " + binary + " exited with non-zero code " + process.ExitCode + ".");

            List<string> linesSnapshot;
            lock (stdoutLock) linesSnapshot = new List<string>(stdoutLines);

            var parsed = ParseYtDlpOutput(linesSnapshot);
            if (parsed == null)
            {
                _logger.Warning("[" + ctx.CorrelationId + "] [" + binary + "] Process exited without outputting a URL (check stderr above).");
                return (null, stderrOutput);
            }

            string shortUrl = parsed.Url.Length > 100 ? parsed.Url.Substring(0, 100) + "..." : parsed.Url;
            string metaSummary = parsed.Height.HasValue ? parsed.Height + "p " + (parsed.Vcodec ?? "?") : "(no metadata)";
            _logger.Debug("[" + ctx.CorrelationId + "] [" + binary + "] resolved: " + shortUrl + " [" + metaSummary + "]");
            return (parsed, stderrOutput);
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
            return (null, "");
        }
    }

    // Parses yt-dlp stdout. Expects either:
    //   url:<url>                                            (tier 1 with --print url:%(url)s)
    //   meta:<height>|<width>|<vcodec>|<format_id>|<protocol>  (tier 1 with --print meta:...)
    // or a plain first-line URL (yt-dlp-og, streamlink). `NA` or empty fields → null.
    public static YtDlpResult? ParseYtDlpOutput(List<string> lines)
    {
        string? url = null;
        int? height = null, width = null;
        string? vcodec = null, formatId = null, protocol = null;

        foreach (var line in lines)
        {
            if (url == null && line.StartsWith("url:"))
            {
                string rest = line.Substring(4).Trim();
                if (rest.StartsWith("http")) url = rest;
            }
            else if (url == null && line.StartsWith("http"))
            {
                url = line;
            }
            else if (line.StartsWith("meta:"))
            {
                var parts = line.Substring(5).Split('|');
                if (parts.Length >= 1) height = ParseNullableInt(parts[0]);
                if (parts.Length >= 2) width = ParseNullableInt(parts[1]);
                if (parts.Length >= 3) vcodec = NullIfEmpty(parts[2]);
                if (parts.Length >= 4) formatId = NullIfEmpty(parts[3]);
                if (parts.Length >= 5) protocol = NullIfEmpty(parts[4]);
            }
        }

        return url == null ? null : new YtDlpResult(url, height, width, vcodec, formatId, protocol);
    }

    private static int? ParseNullableInt(string s)
    {
        s = s.Trim();
        if (string.IsNullOrEmpty(s) || s == "NA" || s == "None") return null;
        return int.TryParse(s, out var v) ? v : null;
    }

    private static string? NullIfEmpty(string s)
    {
        s = s.Trim();
        return (string.IsNullOrEmpty(s) || s == "NA" || s == "None") ? null : s;
    }

}
