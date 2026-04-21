using System.Collections.Generic;

namespace WKVRCProxy.Core.Models;

public class ProxyRule
{
    public List<string> ForwardHeaders { get; set; } = new() {
        "Range", "Accept", "Accept-Language", "Accept-Encoding", "Referer", "Connection", "Keep-Alive", "User-Agent"
    };
    public string ForwardReferer { get; set; } = "same-origin"; // never, always, same-origin
    public string? OverrideUserAgent { get; set; } = null;
    public bool UseCurlImpersonate { get; set; } = false;
    public bool UsePoTokenProvider { get; set; } = false;

    // Additional static headers injected onto every relayed request for this rule. Applied AFTER
    // header forwarding and UA override, so values here take precedence. Use an empty-string value
    // to strip a header that was forwarded (e.g. "Origin": "" to remove the browser-origin).
    // Typical use cases:
    //   "Referer": "https://vrchat.com/"
    //   "Origin":  "https://vrchat.com"
    //   "Sec-CH-UA": "\"Chromium\";v=\"120\", \"Not:A-Brand\";v=\"24\""
    //   "X-Custom-Token": "<per-host shared secret>"
    // Keys are case-insensitive. Values are sent verbatim — no interpolation.
    public Dictionary<string, string> InjectHeaders { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
}

public class ProxyRulesConfig
{
    public ProxyRule Default { get; set; } = new();
    public Dictionary<string, ProxyRule> Domains { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
}
