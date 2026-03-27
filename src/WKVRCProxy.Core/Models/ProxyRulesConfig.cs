using System.Collections.Generic;

namespace WKVRCProxy.Core.Models;

public class ProxyRule
{
    public List<string> ForwardHeaders { get; set; } = new() {
        "Range", "Accept", "Accept-Language", "Accept-Encoding", "Referer", "Connection", "Keep-Alive"
    };
    public string ForwardReferer { get; set; } = "same-origin"; // never, always, same-origin
    public string? OverrideUserAgent { get; set; } = null;
    public bool UseCurlImpersonate { get; set; } = false;
    public bool UsePoTokenProvider { get; set; } = false;
}

public class ProxyRulesConfig
{
    public ProxyRule Default { get; set; } = new();
    public Dictionary<string, ProxyRule> Domains { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
}
