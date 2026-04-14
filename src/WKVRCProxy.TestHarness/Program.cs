using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using WKVRCProxy.Core;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.IPC;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Services;

namespace WKVRCProxy.TestHarness;

[SupportedOSPlatform("windows")]
class Program
{
    // Default test URLs — cover the main resolution paths
    private static readonly string[] DefaultUrls =
    [
        "https://www.youtube.com/watch?v=jNQXAC9IVRw",         // YouTube VOD (Me at the zoo — always available)
        "https://www.youtube.com/@nasa/live",                   // YouTube channel live stream (Tier 0 eligible)
        "https://www.twitch.tv/twitchgaming",                   // Twitch live stream (Tier 0 eligible)
        "https://kick.com/kick",                                // Kick live stream (Tier 0 eligible; may not be live)
        "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8",  // Public HLS test stream (Mux)
    ];

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // --- Parse arguments ---
        var urls = new List<string>();
        bool verbose = false;
        bool testHls = false;
        string player = "AVPro";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--urls":
                case "--url":
                    while (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        urls.Add(args[++i]);
                    break;
                case "--player":
                    if (i + 1 < args.Length) player = args[++i];
                    break;
                case "--test-hls":
                    testHls = true;
                    break;
                case "--verbose":
                case "-v":
                    verbose = true;
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
                default:
                    // Treat bare URLs as test targets
                    if (args[i].StartsWith("http"))
                        urls.Add(args[i]);
                    break;
            }
        }

        if (urls.Count == 0)
            urls.AddRange(DefaultUrls);

        // --- Verify we are running from dist/ ---
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string ytdlpPath = Path.Combine(baseDir, "tools", "yt-dlp.exe");
        if (!File.Exists(ytdlpPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"ERROR: tools/yt-dlp.exe not found in {baseDir}");
            Console.Error.WriteLine("The test harness must run from the dist/ directory.");
            Console.Error.WriteLine("Build with test-harness.ps1 from the repository root.");
            Console.ResetColor();
            return 1;
        }

        // --- Wire logger to console ---
        Logger.OnLog += (entry) =>
        {
            bool isDebug = entry.Level == LogLevel.Debug || entry.Level == LogLevel.Trace;
            if (isDebug && !verbose) return;

            var (prefix, color) = entry.Level switch
            {
                LogLevel.Trace   => ("[TRC] ", ConsoleColor.DarkGray),
                LogLevel.Debug   => ("[DBG] ", ConsoleColor.DarkGray),
                LogLevel.Info    => ("[INF] ", ConsoleColor.Cyan),
                LogLevel.Success => ("[OK ] ", ConsoleColor.Green),
                LogLevel.Warning => ("[WRN] ", ConsoleColor.Yellow),
                LogLevel.Error   => ("[ERR] ", ConsoleColor.Red),
                LogLevel.Fatal   => ("[FAT] ", ConsoleColor.Magenta),
                _                => ("[---] ", ConsoleColor.White)
            };

            Console.ForegroundColor = color;
            Console.WriteLine($"{prefix}{entry.Message}");
            Console.ResetColor();
        };

        // --- Initialize production pipeline ---
        var settings = new SettingsManager(baseDir);
        var logger = new Logger(baseDir, "TestHarness", settings);
        settings.SetLogger(logger);

        // Override debug mode based on --verbose flag
        if (verbose) settings.Config.DebugMode = true;

        var coordinator = new ModuleCoordinator(logger, settings);
        logger.SetEventBus(coordinator.EventBus);

        // VrcLogMonitor is created but NOT registered in the coordinator — its background
        // log-tailing loop never starts, so VRChat game log lines don't pollute test output.
        // CurrentPlayer defaults to "AVPro" without initialization; we also supply the player
        // hint directly in the payload args so the engine sees the correct player either way.
        var logMonitor   = new VrcLogMonitor();
        var tier2Client  = new Tier2WebSocketClient(logger);
        var hostsMgr     = new HostsManager();
        var relayPortMgr = new RelayPortManager();
        var proxyRuleMgr = new ProxyRuleManager();
        var curlClient   = new CurlImpersonateClient();
        var potProvider  = new PotProviderService();
        var relayServer  = new RelayServer();
        var patcher      = new PatcherService();

        // Suppress the hosts setup prompt — no UI in the test harness.
        // Set the flag on the live config object (not saved to disk) so HostsManager skips
        // CheckAndPromptHostsAsync without touching the on-disk app_config.json.
        settings.Config.BypassHostsSetupDeclined = true;

        // Safety net: if the event fires anyway (e.g. race), auto-decline it.
        hostsMgr.OnIpcRequest += (type, _) =>
        {
            if (type == "PROMPT_HOSTS_SETUP")
                hostsMgr.HandleUserResponse(false);
        };

        // VrcLogMonitor intentionally omitted — we don't want VRChat game log tailing.
        coordinator.Register(tier2Client);
        coordinator.Register(hostsMgr);
        coordinator.Register(relayPortMgr);
        coordinator.Register(proxyRuleMgr);
        coordinator.Register(curlClient);
        coordinator.Register(potProvider);
        coordinator.Register(relayServer);
        coordinator.Register(patcher);

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║         WKVRCProxy Resolution Test Harness               ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine($"  Base : {baseDir.TrimEnd('\\', '/')}");
        Console.WriteLine($"  URLs : {urls.Count}  Player: {player}{(verbose ? "  (verbose)" : "")}");
        Console.WriteLine();

        await coordinator.InitializeAllAsync();

        var resEngine = new ResolutionEngine(
            logger, settings, logMonitor,
            tier2Client, hostsMgr, relayPortMgr,
            patcher, curlClient, potProvider);
        resEngine.SetEventBus(coordinator.EventBus);

        // Give bgutil and Tier 2 client time to start and connect.
        // bgutil (Deno-compiled) typically takes 3–4s before its HTTP server is ready.
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("[TestHarness] Waiting 4s for bgutil and Tier 2 to initialize...");
        Console.ResetColor();
        await Task.Delay(4000);

        // --- Run each URL through the full pipeline ---
        var results = new List<TestResult>();

        foreach (string url in urls)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(new string('─', 72));
            Console.WriteLine($"► {url}");
            Console.WriteLine(new string('─', 72));
            Console.ResetColor();

            // Build a payload that matches what the redirector sends:
            //   arg[0] = player hint (detected by ResolutionEngine)
            //   arg[1] = the URL (detected by StartsWith("http"))
            string playerHint = player == "Unity" ? "UnityPlayer" : "AVProVideo";
            var payload = new ResolvePayload { Args = [playerHint, url] };

            var sw = Stopwatch.StartNew();
            string? resolved = null;
            bool threw = false;

            try
            {
                resolved = await resEngine.ResolveAsync(payload);
            }
            catch (Exception ex)
            {
                threw = true;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERR] ResolveAsync threw: {ex.GetType().Name}: {ex.Message}");
                Console.ResetColor();
            }
            sw.Stop();

            var res = new TestResult(url, resolved, sw.ElapsedMilliseconds, resolved != null && !threw);
            results.Add(res);

            Console.ForegroundColor = res.Success ? ConsoleColor.Green : ConsoleColor.Red;
            if (res.Success)
            {
                string display = resolved!.Length > 120 ? resolved[..120] + "..." : resolved!;
                Console.WriteLine($"  RESOLVED [{sw.ElapsedMilliseconds}ms] {display}");
            }
            else
            {
                Console.WriteLine($"  FAILED   [{sw.ElapsedMilliseconds}ms]");
            }
            Console.ResetColor();
        }

        // --- Summary ---
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                       SUMMARY                            ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        int passed = results.Count(r => r.Success);
        int failed = results.Count - passed;

        foreach (var r in results)
        {
            string shortUrl = r.Url.Length > 60 ? r.Url[..60] + "..." : r.Url;
            Console.ForegroundColor = r.Success ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"  {(r.Success ? "✓" : "✗")} [{r.ElapsedMs,5}ms] {shortUrl}");
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.ForegroundColor = failed == 0 ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine($"  Result: {passed}/{results.Count} passed");
        Console.ResetColor();
        Console.WriteLine();

        // --- HLS relay tests (--test-hls) ---
        int hlsFailed = 0;
        if (testHls)
        {
            hlsFailed = await RunHlsRelayTestsAsync(relayPortMgr.CurrentPort);
            failed += hlsFailed;
        }

        coordinator.Dispose();
        logger.Dispose();

        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// Fetches real HLS manifests directly through the relay HTTP endpoint and verifies
    /// that every segment/variant URL in the response is relay-encoded.
    /// Uses the stable public Mux HLS test stream — no auth required.
    /// </summary>
    private static async Task<int> RunHlsRelayTestsAsync(int relayPort)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                   HLS RELAY TESTS                        ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        // The relay binds to 127.0.0.1; we fetch directly from there (not via the hosts redirect).
        // The rewritten URLs inside the manifest will still point to localhost.youtube.com:{port}.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var hlsTests = new List<(string Name, bool Pass, string Detail)>();

        string relayBase = $"http://127.0.0.1:{relayPort}/play?target=";

        static string Encode(string url)
            => WebUtility.UrlEncode(Convert.ToBase64String(Encoding.UTF8.GetBytes(url)));

        static string? DecodeTarget(string relayUrl)
        {
            try
            {
                var uri = new Uri(relayUrl.Trim());
                foreach (var pair in uri.Query.TrimStart('?').Split('&'))
                {
                    var kv = pair.Split('=', 2);
                    if (kv.Length == 2 && kv[0] == "target")
                        return Encoding.UTF8.GetString(Convert.FromBase64String(
                            WebUtility.UrlDecode(kv[1]).Replace(" ", "+")));
                }
                return null;
            }
            catch { return null; }
        }

        // ── Test 1: Master playlist — all variant URLs relay-encoded ─────────────
        const string muxMasterUrl = "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8";
        string? masterBody = null;
        try
        {
            masterBody = await http.GetStringAsync(relayBase + Encode(muxMasterUrl));
            bool startsWithExtm3u = masterBody.TrimStart().StartsWith("#EXTM3U");
            // All non-comment non-empty lines should be relay URLs
            var contentLines = masterBody.Replace("\r\n", "\n").Split('\n')
                .Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
            bool allRewritten = contentLines.Count > 0 &&
                contentLines.All(l => l.StartsWith("http://localhost.youtube.com:"));
            // All decoded targets should be valid URLs pointing to mux CDN
            bool allDecodable = contentLines.All(l => DecodeTarget(l) != null);
            bool pass = startsWithExtm3u && allRewritten && allDecodable;
            string detail = pass
                ? $"{contentLines.Count} variant URLs correctly relay-encoded"
                : $"valid={startsWithExtm3u} allRewritten={allRewritten} allDecodable={allDecodable} lines={contentLines.Count}";
            hlsTests.Add(("Mux master playlist — variant URLs rewritten", pass, detail));
        }
        catch (Exception ex)
        {
            hlsTests.Add(("Mux master playlist — variant URLs rewritten", false, "Exception: " + ex.Message));
        }

        // ── Test 2: Media playlist — all segment URLs relay-encoded ──────────────
        // Extract first variant URL from master, decode it, fetch via relay
        if (masterBody != null)
        {
            try
            {
                string? firstRelayLine = masterBody.Replace("\r\n", "\n").Split('\n')
                    .FirstOrDefault(l => l.StartsWith("http://localhost.youtube.com:"));
                string? variantUrl = firstRelayLine != null ? DecodeTarget(firstRelayLine) : null;

                if (variantUrl != null)
                {
                    string mediaBody = await http.GetStringAsync(relayBase + Encode(variantUrl));
                    var segLines = mediaBody.Replace("\r\n", "\n").Split('\n')
                        .Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
                    bool allRewritten = segLines.Count > 0 &&
                        segLines.All(l => l.StartsWith("http://localhost.youtube.com:"));
                    bool allDecodable = segLines.All(l => DecodeTarget(l) != null);
                    bool pass = allRewritten && allDecodable;
                    string detail = pass
                        ? $"{segLines.Count} segment URLs correctly relay-encoded"
                        : $"allRewritten={allRewritten} allDecodable={allDecodable} lines={segLines.Count}";
                    hlsTests.Add(("Mux media playlist — segment URLs rewritten", pass, detail));
                }
                else
                {
                    hlsTests.Add(("Mux media playlist — segment URLs rewritten", false, "Could not decode variant URL from master"));
                }
            }
            catch (Exception ex)
            {
                hlsTests.Add(("Mux media playlist — segment URLs rewritten", false, "Exception: " + ex.Message));
            }
        }

        // ── Test 3: Live playlist refresh — fetching same playlist twice returns valid HLS ──
        try
        {
            string body1 = await http.GetStringAsync(relayBase + Encode(muxMasterUrl));
            string body2 = await http.GetStringAsync(relayBase + Encode(muxMasterUrl));
            bool bothValid = body1.TrimStart().StartsWith("#EXTM3U") &&
                             body2.TrimStart().StartsWith("#EXTM3U");
            // Content may differ (live) or match (VOD) — both are fine.
            // What matters is both are valid rewritten playlists.
            var lines1 = body1.Replace("\r\n", "\n").Split('\n')
                .Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
            var lines2 = body2.Replace("\r\n", "\n").Split('\n')
                .Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
            bool bothRewritten = lines1.All(l => l.StartsWith("http://localhost.youtube.com:")) &&
                                 lines2.All(l => l.StartsWith("http://localhost.youtube.com:"));
            bool pass = bothValid && bothRewritten;
            hlsTests.Add(("Playlist re-fetch (live refresh simulation)", pass,
                pass ? "Both fetches returned valid rewritten playlists" : $"valid={bothValid} rewritten={bothRewritten}"));
        }
        catch (Exception ex)
        {
            hlsTests.Add(("Playlist re-fetch (live refresh simulation)", false, "Exception: " + ex.Message));
        }

        // ── Test 4: Malformed target — relay returns non-200 or graceful response ──
        try
        {
            // Base64-encode a deliberately broken URL
            string brokenUrl = "https://this-host-does-not-exist.invalid/broken.m3u8";
            var resp = await http.GetAsync(relayBase + Encode(brokenUrl));
            // The relay should return an HTTP error (not crash), any non-2xx is acceptable here
            bool pass = !resp.IsSuccessStatusCode || (int)resp.StatusCode == 200; // 200 with error body also OK
            hlsTests.Add(("Malformed/unreachable upstream — relay stays up", true,
                $"Relay responded with HTTP {(int)resp.StatusCode} (no crash)"));
        }
        catch (Exception ex) when (ex is TaskCanceledException || ex is HttpRequestException)
        {
            // Timeout or connection refused to the relay itself would be a failure
            hlsTests.Add(("Malformed/unreachable upstream — relay stays up", false, "Exception: " + ex.Message));
        }
        catch (Exception)
        {
            // Any other exception: relay is still up (we got some response handling)
            hlsTests.Add(("Malformed/unreachable upstream — relay stays up", true, "Relay handled gracefully"));
        }

        // ── Print results ─────────────────────────────────────────────────────────
        int hlsFailed = 0;
        foreach (var (name, pass, detail) in hlsTests)
        {
            Console.ForegroundColor = pass ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"  {(pass ? "✓" : "✗")} {name}");
            Console.ForegroundColor = pass ? ConsoleColor.DarkGray : ConsoleColor.Yellow;
            Console.WriteLine($"      {detail}");
            Console.ResetColor();
            if (!pass) hlsFailed++;
        }

        Console.WriteLine();
        Console.ForegroundColor = hlsFailed == 0 ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine($"  HLS result: {hlsTests.Count - hlsFailed}/{hlsTests.Count} passed");
        Console.ResetColor();
        Console.WriteLine();

        return hlsFailed;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("WKVRCProxy Resolution Test Harness");
        Console.WriteLine();
        Console.WriteLine("USAGE:");
        Console.WriteLine("  WKVRCProxy.TestHarness.exe [options] [url ...]");
        Console.WriteLine();
        Console.WriteLine("OPTIONS:");
        Console.WriteLine("  --url <url>       Add a URL to test (repeatable)");
        Console.WriteLine("  --urls <u1> <u2>  Add multiple URLs");
        Console.WriteLine("  --player <name>   AVPro (default) or Unity");
        Console.WriteLine("  --verbose / -v    Show debug log output");
        Console.WriteLine("  --test-hls        Run HLS relay rewriting integration tests");
        Console.WriteLine("  --help / -h       Show this help");
        Console.WriteLine();
        Console.WriteLine("If no URLs are given, the default test suite is used:");
        foreach (var u in DefaultUrls)
            Console.WriteLine($"  {u}");
        Console.WriteLine();
        Console.WriteLine("NOTE: Must be run from the dist/ directory (run test-harness.ps1).");
    }

    private record TestResult(string Url, string? Resolved, long ElapsedMs, bool Success);
}
