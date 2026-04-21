using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

// Per-host/per-strategy learning memory. Supersedes the old TierMemory (which stored one tier per
// host). StrategyMemory tracks every strategy we've ever tried for a host, with success and failure
// counts, so the dispatcher can rank and retry in priority order — and decay entries when a
// previously-working bypass starts failing (sites patch their detection and we need to adapt).
//
// Persisted to strategy_memory.json. On first run we migrate from tier_memory.json by expanding
// each legacy tier entry into the canonical strategy for that tier group.

// Failure classification. Lets demotion react differently to transient vs. terminal vs. block
// failures, and lets the dispatcher promote specific strategies in response (e.g. JsChallenge →
// try browser-extract next time).
public enum StrategyFailureKind
{
    Unknown = 0,
    NetworkError,   // DNS, connection refused, DNS-resolution, etc. Transient.
    Timeout,        // request or subprocess timed out. Transient.
    Blocked403,     // 401/403/429. Strong block signal, specific to this IP/fingerprint.
    NotFound404,    // 404/410/451. URL is gone; retrying won't help.
    JsChallenge,    // site served a JS/captcha challenge page instead of the expected media.
    LowQuality      // resolved a URL but its height was below the acceptable floor.
}

public class StrategyMemoryEntry
{
    public string StrategyName { get; set; } = "";
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public DateTime LastSuccess { get; set; }
    public DateTime? LastFailure { get; set; }
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;

    // Consecutive failures since last success. Compared against DemoteThresholdFor(LastFailureKind)
    // — different failure kinds trigger demotion at different counts (404 demotes immediately, a
    // transient timeout takes 5). A success resets this to 0.
    public int ConsecutiveFailures { get; set; }

    // Height of the last successful resolution. 0 = unknown (treated as neutral by the ranker).
    public int LastResolvedHeight { get; set; }

    // Running mean of successful resolution heights. Used as a tiebreaker during ranking so that a
    // strategy that consistently returns 1080p outranks one that returns 360p at equal W/L.
    // Computed as an exponential moving average: new = 0.7 * old + 0.3 * observed (fast to converge
    // when a site upgrades formats, slow enough that a single odd sample doesn't dominate).
    public double AverageResolvedHeight { get; set; }

    // Reason for the most recent failure. Lets the dispatcher react — e.g. if any strategy for a
    // host has LastFailureKind==JsChallenge, promote browser-extract in the cold race.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public StrategyFailureKind LastFailureKind { get; set; }

    public int NetScore => SuccessCount - FailureCount;
}

public class StrategyMemory
{
    private readonly Logger? _logger;
    private readonly string _path;

    // key: "host:streamType" (same shape as the old TierMemoryKey)
    private readonly ConcurrentDictionary<string, List<StrategyMemoryEntry>> _entries = new();
    private readonly object _saveLock = new();

    // Default demote threshold for failure kinds not in the switch (Unknown, etc).
    public const int DefaultConsecutiveFailureDemoteThreshold = 3;
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(30);

    // Asymmetric demote thresholds — a strategy stops being "preferred" once this many consecutive
    // failures of the given kind hit without an intervening success.
    //   404 → 1 (URL is gone; retrying doesn't help)
    //   403 → 2 (ban signal; give it one retry in case of transient IP reputation)
    //   JsChallenge → 2 (strategy can't pass the gate; browser-extract should take over)
    //   LowQuality → 3 (give the strategy a chance to roll a better format)
    //   Timeout / NetworkError → 5 (transient, let it retry)
    public static int DemoteThresholdFor(StrategyFailureKind kind) => kind switch
    {
        StrategyFailureKind.NotFound404 => 1,
        StrategyFailureKind.Blocked403 => 2,
        StrategyFailureKind.JsChallenge => 2,
        StrategyFailureKind.LowQuality => 3,
        StrategyFailureKind.Timeout => 5,
        StrategyFailureKind.NetworkError => 5,
        _ => DefaultConsecutiveFailureDemoteThreshold
    };

    public StrategyMemory(Logger? logger, string basePath)
    {
        _logger = logger;
        _path = Path.Combine(basePath, "strategy_memory.json");
    }

    public int EntryCount => _entries.Count;

    // Returns the entry the dispatcher should try first, or null to force a cold race.
    // Ranking order:
    //   1. NetScore (successes - failures)
    //   2. AverageResolvedHeight (tiebreaker: prefer the strategy that gets better quality)
    //   3. LastSuccess recency
    // Entries past their kind-specific demote threshold or older than StaleThreshold are excluded.
    public StrategyMemoryEntry? GetPreferred(string memKey)
    {
        if (!_entries.TryGetValue(memKey, out var list)) return null;
        var now = DateTime.UtcNow;
        StrategyMemoryEntry? best = null;
        lock (list)
        {
            foreach (var e in list)
            {
                if (e.ConsecutiveFailures >= DemoteThresholdFor(e.LastFailureKind)) continue;
                if (now - e.LastSuccess > StaleThreshold) continue;
                if (best == null || IsBetter(e, best)) best = e;
            }
        }
        return best;
    }

    private static bool IsBetter(StrategyMemoryEntry candidate, StrategyMemoryEntry current)
    {
        if (candidate.NetScore != current.NetScore) return candidate.NetScore > current.NetScore;
        if (Math.Abs(candidate.AverageResolvedHeight - current.AverageResolvedHeight) > 1e-3)
            return candidate.AverageResolvedHeight > current.AverageResolvedHeight;
        return candidate.LastSuccess > current.LastSuccess;
    }

    // For diagnostic/UI purposes: full ranked view. Does not filter out demoted/stale entries so
    // the user can see why something was demoted.
    public IReadOnlyList<StrategyMemoryEntry> GetAll(string memKey)
    {
        if (!_entries.TryGetValue(memKey, out var list)) return Array.Empty<StrategyMemoryEntry>();
        lock (list) { return list.OrderByDescending(e => e.NetScore).ThenByDescending(e => e.LastSuccess).ToList(); }
    }

    // Signals the dispatcher that this host's last failure was a JS challenge — any strategy that
    // needs a browser-rendered page should be promoted in the next cold race.
    public bool HostWantsBrowser(string memKey)
    {
        if (!_entries.TryGetValue(memKey, out var list)) return false;
        lock (list)
        {
            foreach (var e in list)
                if (e.LastFailureKind == StrategyFailureKind.JsChallenge && e.ConsecutiveFailures > 0)
                    return true;
        }
        return false;
    }

    public void RecordSuccess(string memKey, string strategyName) => RecordSuccess(memKey, strategyName, null);

    public void RecordSuccess(string memKey, string strategyName, int? resolvedHeight)
    {
        if (string.IsNullOrEmpty(memKey) || string.IsNullOrEmpty(strategyName)) return;
        // Tier 4 passthrough is a failure-mode fallback, not something we want the fast-path to pick.
        if (strategyName.StartsWith("tier4:", StringComparison.OrdinalIgnoreCase)) return;

        var now = DateTime.UtcNow;
        var list = _entries.GetOrAdd(memKey, _ => new List<StrategyMemoryEntry>());
        lock (list)
        {
            var entry = list.FirstOrDefault(e => string.Equals(e.StrategyName, strategyName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new StrategyMemoryEntry { StrategyName = strategyName, FirstSeen = now };
                list.Add(entry);
            }
            entry.SuccessCount++;
            if (entry.FailureCount > 0) entry.FailureCount = Math.Max(0, entry.FailureCount - 1);
            entry.ConsecutiveFailures = 0;
            entry.LastFailureKind = StrategyFailureKind.Unknown;
            entry.LastSuccess = now;
            if (resolvedHeight is int h && h > 0)
            {
                entry.LastResolvedHeight = h;
                entry.AverageResolvedHeight = entry.AverageResolvedHeight <= 0
                    ? h
                    : (0.7 * entry.AverageResolvedHeight) + (0.3 * h);
            }
        }
        EnforceCap();
        SaveAsync();
    }

    public void RecordFailure(string memKey, string strategyName) => RecordFailure(memKey, strategyName, StrategyFailureKind.Unknown);

    public void RecordFailure(string memKey, string strategyName, StrategyFailureKind kind)
    {
        if (string.IsNullOrEmpty(memKey) || string.IsNullOrEmpty(strategyName)) return;

        var now = DateTime.UtcNow;
        var list = _entries.GetOrAdd(memKey, _ => new List<StrategyMemoryEntry>());
        bool demoted = false;
        int threshold = DemoteThresholdFor(kind);
        lock (list)
        {
            var entry = list.FirstOrDefault(e => string.Equals(e.StrategyName, strategyName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new StrategyMemoryEntry { StrategyName = strategyName, FirstSeen = now, LastSuccess = DateTime.MinValue };
                list.Add(entry);
            }
            entry.FailureCount++;
            entry.ConsecutiveFailures++;
            entry.LastFailure = now;
            entry.LastFailureKind = kind;
            demoted = entry.ConsecutiveFailures == threshold;
        }
        if (demoted)
            _logger?.Info("[StrategyMemory] Strategy '" + strategyName + "' for " + memKey + " demoted after " + threshold + " consecutive " + kind + " failure(s) — next request will re-cascade.");
        SaveAsync();
    }

    // Forget all memory for a host. UI can call this when the user manually clicks "forget".
    public void ForgetKey(string memKey)
    {
        _entries.TryRemove(memKey, out _);
        SaveAsync();
    }

    // Snapshot of the whole memory, keyed by host+streamType. Used by the UI's bypass-health view.
    public IReadOnlyDictionary<string, IReadOnlyList<StrategyMemoryEntry>> Snapshot()
    {
        var result = new Dictionary<string, IReadOnlyList<StrategyMemoryEntry>>();
        foreach (var kvp in _entries)
        {
            lock (kvp.Value) result[kvp.Key] = kvp.Value.ToList();
        }
        return result;
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, List<StrategyMemoryEntry>>>(json);
                if (loaded != null)
                {
                    foreach (var kvp in loaded)
                        _entries[kvp.Key] = kvp.Value ?? new List<StrategyMemoryEntry>();
                    _logger?.Debug("[StrategyMemory] Loaded " + _entries.Count + " host entries.");
                    return;
                }
            }
            // No strategy_memory.json — attempt migration from legacy tier_memory.json so existing
            // users don't lose their learned tier rankings on upgrade.
            MigrateFromLegacyTierMemory();
        }
        catch (Exception ex)
        {
            _logger?.Warning("[StrategyMemory] Failed to load " + Path.GetFileName(_path) + " — starting fresh: " + ex.Message);
        }
    }

    public void MigrateFromLegacyTierMemory()
    {
        string legacyPath = Path.Combine(Path.GetDirectoryName(_path) ?? "", "tier_memory.json");
        if (!File.Exists(legacyPath)) return;
        try
        {
            string json = File.ReadAllText(legacyPath);
            var legacy = JsonSerializer.Deserialize<Dictionary<string, LegacyTierMemoryEntry>>(json);
            if (legacy == null || legacy.Count == 0) return;

            foreach (var kvp in legacy)
            {
                string? strategy = TierGroupToCanonicalStrategy(kvp.Value.Tier);
                if (strategy == null) continue;
                var entry = new StrategyMemoryEntry
                {
                    StrategyName = strategy,
                    SuccessCount = kvp.Value.SuccessCount,
                    LastSuccess = kvp.Value.LastSuccess == default ? DateTime.UtcNow : kvp.Value.LastSuccess.ToUniversalTime(),
                    FirstSeen = kvp.Value.LastSuccess == default ? DateTime.UtcNow : kvp.Value.LastSuccess.ToUniversalTime(),
                };
                _entries[kvp.Key] = new List<StrategyMemoryEntry> { entry };
            }
            _logger?.Info("[StrategyMemory] Migrated " + legacy.Count + " entries from tier_memory.json.");
            Save();
        }
        catch (Exception ex)
        {
            _logger?.Warning("[StrategyMemory] Legacy tier_memory.json migration failed: " + ex.Message);
        }
    }

    private static string? TierGroupToCanonicalStrategy(string tier)
    {
        return tier switch
        {
            "tier0" or "tier0-streamlink" => "tier0:streamlink-native",
            "tier1" => "tier1:po+impersonate",
            "tier2" => "tier2:cloud-whyknot",
            "tier3" => "tier3:plain",
            _ => null
        };
    }

    public void Save()
    {
        try
        {
            lock (_saveLock)
            {
                var snapshot = _entries.ToDictionary(kvp => kvp.Key, kvp => {
                    lock (kvp.Value) return kvp.Value.ToList();
                });
                string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
            }
        }
        catch (Exception ex) { _logger?.Debug("[StrategyMemory] Save failed: " + ex.Message); }
    }

    private System.Threading.Tasks.Task? _pendingSave;
    private readonly object _pendingSaveLock = new();

    // Coalesce saves: multiple recordSuccess/recordFailure calls in quick succession produce one
    // disk write. Keeps per-request latency low without losing data.
    private void SaveAsync()
    {
        lock (_pendingSaveLock)
        {
            if (_pendingSave != null && !_pendingSave.IsCompleted) return;
            _pendingSave = System.Threading.Tasks.Task.Run(async () => {
                await System.Threading.Tasks.Task.Delay(250);
                Save();
            });
        }
    }

    private void EnforceCap()
    {
        // Cap at 200 hosts (double the old tier-memory cap; we now hold lists). Evict the host whose
        // latest entry is oldest — that's the one we've touched least recently.
        if (_entries.Count <= 200) return;
        var oldestHost = _entries
            .Select(kvp => {
                DateTime latest;
                lock (kvp.Value) latest = kvp.Value.Count == 0 ? DateTime.MinValue : kvp.Value.Max(e => e.LastSuccess);
                return (kvp.Key, latest);
            })
            .OrderBy(t => t.latest)
            .FirstOrDefault();
        if (oldestHost.Key != null)
            _entries.TryRemove(oldestHost.Key, out _);
    }

    public static string KeyFor(string url, bool isLive)
    {
        try
        {
            string host = new Uri(url).Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host.Substring(4);
            return host + (isLive ? ":live" : ":vod");
        }
        catch { return ""; }
    }

    // Heuristic classifier. Maps raw error signals (exception + optional stderr/process exit
    // context) to a StrategyFailureKind. Keeps dispatcher call sites short and consistent.
    public static StrategyFailureKind ClassifyFailure(Exception? ex, string? stderr, bool timedOut = false)
    {
        if (timedOut) return StrategyFailureKind.Timeout;
        if (ex is OperationCanceledException) return StrategyFailureKind.Timeout;

        string blob = (stderr ?? "") + " " + (ex?.Message ?? "");
        if (string.IsNullOrWhiteSpace(blob)) return StrategyFailureKind.Unknown;
        string b = blob.ToLowerInvariant();

        // Block / ban signals
        if (b.Contains("http error 403") || b.Contains(" 403 ") || b.Contains("forbidden")) return StrategyFailureKind.Blocked403;
        if (b.Contains("http error 401") || b.Contains(" 401 ") || b.Contains("unauthorized")) return StrategyFailureKind.Blocked403;
        if (b.Contains("http error 429") || b.Contains(" 429 ") || b.Contains("too many requests")) return StrategyFailureKind.Blocked403;
        if (b.Contains("sign in to confirm") || b.Contains("confirm you're not a bot")) return StrategyFailureKind.JsChallenge;
        if (b.Contains("cloudflare") && (b.Contains("challenge") || b.Contains("just a moment"))) return StrategyFailureKind.JsChallenge;

        // Terminal signals
        if (b.Contains("http error 404") || b.Contains(" 404 ") || b.Contains("not found")) return StrategyFailureKind.NotFound404;
        if (b.Contains("http error 410") || b.Contains(" 410 ") || b.Contains("gone")) return StrategyFailureKind.NotFound404;
        if (b.Contains("http error 451") || b.Contains(" 451 ")) return StrategyFailureKind.NotFound404;
        if (b.Contains("video unavailable") || b.Contains("this video is not available")) return StrategyFailureKind.NotFound404;
        if (b.Contains("private video") || b.Contains("removed by the uploader")) return StrategyFailureKind.NotFound404;

        // Network signals
        if (b.Contains("timed out") || b.Contains("timeout")) return StrategyFailureKind.Timeout;
        if (b.Contains("name or service not known") || b.Contains("no address associated")) return StrategyFailureKind.NetworkError;
        if (b.Contains("connection refused") || b.Contains("connection reset")) return StrategyFailureKind.NetworkError;
        if (b.Contains("network is unreachable") || b.Contains("no route to host")) return StrategyFailureKind.NetworkError;
        if (b.Contains("ssl") && (b.Contains("handshake") || b.Contains("tlsv"))) return StrategyFailureKind.NetworkError;

        return StrategyFailureKind.Unknown;
    }

    private class LegacyTierMemoryEntry
    {
        public string Tier { get; set; } = "";
        public int SuccessCount { get; set; }
        public DateTime LastSuccess { get; set; }
    }
}
