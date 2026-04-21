using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WKVRCProxy.Core.Services;

// Captured browser session used to replay AVPro requests through our relay with the exact cookies
// and headers the real browser sent. Populated by BrowserExtractService when it successfully
// resolves a media URL; consulted by RelayServer per request.
//
// Host = bare lowercase hostname (no port, no path). Keyed this way so session survives URL
// mutation (path changes, query-string rotations) while the CDN origin is the same.
public record BrowserSession(
    string Host,
    string ResolvedUrl,
    IReadOnlyDictionary<string, string> Headers,
    string CookieHeader,       // Pre-serialized "name=value; name=value" string (already URL-safe)
    DateTime CapturedAt,
    DateTime Expires
);

// Registered as a lightweight IProxyModule so RelayServer, ResolutionEngine, and BrowserExtractService
// can GetModule<BrowserSessionCache>() it from IModuleContext.
public class BrowserSessionCache : IProxyModule
{
    public string Name => "BrowserSessionCache";

    private readonly ConcurrentDictionary<string, BrowserSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    // Default TTL for captured sessions. HLS manifests and signed URLs usually outlive this, but an
    // hour keeps memory footprint bounded. On a relay 403/401 we invalidate immediately anyway, so
    // TTL is the fallback expiration — not the primary one.
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);

    public Task InitializeAsync(IModuleContext context) => Task.CompletedTask;
    public void Shutdown() { _sessions.Clear(); }

    public BrowserSession? Get(string host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        if (!_sessions.TryGetValue(host, out var s)) return null;
        if (DateTime.UtcNow > s.Expires)
        {
            _sessions.TryRemove(host, out _);
            return null;
        }
        return s;
    }

    public void Put(BrowserSession session)
    {
        if (string.IsNullOrEmpty(session.Host)) return;
        _sessions[session.Host] = session;
    }

    // Called by the relay when an upstream request returns 401/403 — the captured session is no
    // longer valid (cookie expired, IP flagged, etc.). Next resolve will re-run the browser.
    public void Invalidate(string host)
    {
        if (string.IsNullOrEmpty(host)) return;
        _sessions.TryRemove(host, out _);
    }

    public IReadOnlyDictionary<string, BrowserSession> Snapshot()
    {
        return new Dictionary<string, BrowserSession>(_sessions, StringComparer.OrdinalIgnoreCase);
    }

    public static string HostFromUrl(string url)
    {
        try
        {
            string host = new Uri(url).Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host.Substring(4);
            return host;
        }
        catch { return ""; }
    }
}
