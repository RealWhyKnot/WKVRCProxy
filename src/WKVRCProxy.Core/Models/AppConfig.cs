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
}
