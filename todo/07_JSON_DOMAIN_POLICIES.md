# Step 7: JSON Domain Policies

This step implements a rule-based system for the `RelayServer` to handle per-domain header and User-Agent overrides. Different video hosts have different anti-bot measures; YouTube is strict, while a random CDN might require a specific `Referer`.

## Objective
Implement a `proxy-rules.json` file to manage these policies dynamically without recompiling.

## Technical Details
- **Rules File**: `proxy-rules.json` in the app's `AppDomain.CurrentDomain.BaseDirectory`.
- **Model**: `ProxyRules` containing a `Default` rule and a `Dictionary<string, ProxyRule> Domains`.

## Implementation Checklist

### 1. `ProxyRules` Models (C#)
Create `ProxyRuleManager.cs` and models in `src/WKVRCProxy.Core/Models/`.
- [x] Models:
    ```csharp
    public class ProxyRule {
        public List<string> ForwardHeaders { get; set; } = new();
        public string ForwardReferer { get; set; } = "same-origin"; // never, always, same-origin
        public string? OverrideUserAgent { get; set; } = null;
        public bool UseCurlImpersonate { get; set; } = false;
        public bool UsePoTokenProvider { get; set; } = false;
    }
    public class ProxyRulesConfig {
        public ProxyRule Default { get; set; } = new();
        public Dictionary<string, ProxyRule> Domains { get; set; } = new();
    }
    ```

### 2. `ProxyRuleManager` Service (C#)
Create the service to load and provide rules.
- [x] Interface implementation: `public class ProxyRuleManager : IProxyModule`
- [x] `InitializeAsync`: Check if `proxy-rules.json` exists. If not, generate a default one with rules for `youtube.com` and `googlevideo.com`. Load the JSON.
- [x] `GetRuleForDomain(string domain)`: Match the domain suffix (e.g., if domain is `rr3.sn-xg4-cges.googlevideo.com`, it should match the `googlevideo.com` rule).

### 3. Relay Rule Integration (C#)
Update `RelayServer.cs`.
- [x] Extract the target domain: `var uri = new Uri(targetUrl); string domain = uri.Host;`
- [x] Fetch the rule: `var rule = _ruleManager.GetRuleForDomain(domain);`
- [x] Use `rule.ForwardHeaders` instead of the hardcoded `HashSet` from Step 5.
- [x] Apply `rule.OverrideUserAgent` if it is not null.
- [x] Implement `ForwardReferer`:
    - [x] `never`: don't forward.
    - [x] `always`: forward exactly as AVPro sent it.
    - [x] `same-origin`: Only forward if the AVPro referer domain matches or is a subdomain of the target domain.

## Verification
1. [x] Check the `tools/proxy-rules.json` is generated.
2. [x] Add a rule for `test.com` with `OverrideUserAgent: "CustomBot/1.0"`.
3. [x] Request a `test.com` URL through the relay and capture it with a tool like `netcat` or `webhook.site`.
4. [x] Verify the User-Agent was overridden correctly.