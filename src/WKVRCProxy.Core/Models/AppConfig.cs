using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WKVRCProxy.Core.Models;

public class HistoryEntry
{
    public DateTime Timestamp { get; set; }
    public string OriginalUrl { get; set; } = string.Empty;
    public string ResolvedUrl { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
    public string Player { get; set; } = string.Empty;
    public bool Success { get; set; }

    // No [JsonPropertyName] — C# default serialization outputs PascalCase matching the TypeScript HistoryEntry interface.
    public bool IsLive { get; set; }

    public string StreamType { get; set; } = "unknown";

    public int? ResolutionHeight { get; set; }
    public int? ResolutionWidth { get; set; }
    public string? Vcodec { get; set; }
}

public class AppConfig
{
    [JsonPropertyName("debugMode")]
    public bool DebugMode { get; set; } = true;

    [JsonPropertyName("preferredResolution")]
    public string PreferredResolution { get; set; } = "1080p";

    [JsonPropertyName("forceIPv4")]
    public bool ForceIPv4 { get; set; } = false;

    [JsonPropertyName("autoPatchOnStart")]
    public bool AutoPatchOnStart { get; set; } = true;

    [JsonPropertyName("userAgent")]
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    [JsonPropertyName("history")]
    public List<HistoryEntry> History { get; set; } = new();

    [JsonPropertyName("preferredTier")]
    public string PreferredTier { get; set; } = "tier1";

    [JsonPropertyName("customVrcPath")]
    public string? CustomVrcPath { get; set; }

    [JsonPropertyName("bypassHostsSetupDeclined")]
    public bool BypassHostsSetupDeclined { get; set; } = false;

    [JsonPropertyName("enableRelayBypass")]
    public bool EnableRelayBypass { get; set; } = true;

    [JsonPropertyName("disabledTiers")]
    public List<string> DisabledTiers { get; set; } = new();

    [JsonPropertyName("enableTierMemory")]
    public bool EnableTierMemory { get; set; } = true;

    // Tier 2 (whyknot.dev cloud resolver) does PO-token / extractor orchestration and can
    // legitimately take 30-60s on cold cache. The old 10s default caused false timeouts.
    [JsonPropertyName("tier2TimeoutSeconds")]
    public int Tier2TimeoutSeconds { get; set; } = 60;

    // Check GitHub for a newer yt-dlp on every launch and swap the binary if one is available.
    // Never touches yt-dlp-og.exe (Tier 3 needs VRChat's pinned copy).
    [JsonPropertyName("autoUpdateYtDlp")]
    public bool AutoUpdateYtDlp { get; set; } = true;

    // Enable the browser-extract bypass strategy (headless Chromium/Edge/Chrome). Fires when
    // yt-dlp strategies can't crack a JS-gated site. Default on — system browsers are detected
    // first, no download unless DownloadBundledChromium is also true.
    [JsonPropertyName("enableBrowserExtract")]
    public bool EnableBrowserExtract { get; set; } = true;

    // When no Edge/Chrome/Brave is found on the system, opt in to downloading a ~180 MB bundled
    // Chromium via PuppeteerSharp's BrowserFetcher. Off by default to avoid surprise downloads;
    // users who lack a system browser and want browser-extract enable this once.
    [JsonPropertyName("downloadBundledChromium")]
    public bool DownloadBundledChromium { get; set; } = false;

    // Enable Cloudflare WARP. When on, we launch wireproxy+wgcf as child processes so yt-dlp/
    // browser-extract strategies can optionally route through WARP's SOCKS5 proxy on 127.0.0.1:40000.
    // Nothing on the host machine is modified; WARP only applies to our own subprocesses.
    [JsonPropertyName("enableWarp")]
    public bool EnableWarp { get; set; } = false;
}
