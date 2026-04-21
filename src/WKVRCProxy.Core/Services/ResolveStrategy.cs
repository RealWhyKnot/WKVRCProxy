using System;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;

namespace WKVRCProxy.Core.Services;

// A single bypass variant. The dispatcher picks strategies from the catalog based on the request
// (URL, player, memory) and runs them. Each strategy carries its own logic — the catalog just
// assembles the menu.
public record ResolveStrategy(
    string Name,          // e.g. "tier1:po+impersonate", "tier3:vrchat-ua"
    string Group,         // "tier0" | "tier1" | "tier2" | "tier3" | "tier4"
    int Priority,         // Lower = try first within a group. Used to order concurrent launches.
    bool UsesSubprocess,  // Subprocess strategies contribute to the concurrency budget.
    Func<StrategyRunContext, Task<YtDlpResult?>> Executor,
    // When true, force relay-wrap on the resolved URL even for non-YouTube hosts. Used by
    // browser-extract so the captured browser session (cookies + headers) can be replayed on
    // AVPro's requests. The per-host session cache in BrowserSessionCache provides the headers.
    bool ForceRelayWrap = false
);

public record StrategyRunContext(
    string Url,
    string Player,
    string[] OriginalArgs,      // forwarded for tier 3 (yt-dlp-og with VRChat's own args)
    RequestContext RequestContext,
    int QualityFloor,
    CancellationToken Cancellation
);
