# Step 9: TLS Impersonation Integration

This step upgrades the relay's outbound client to optionally use `curl-impersonate`. This matches a real browser's TLS signature (JA3/JA4) and HTTP/2 behavior, avoiding detection by advanced anti-bot systems like Cloudflare or YouTube's edge nodes.

## Objective
Implement `curl-impersonate` as a per-domain outbound handler for the `RelayServer`, replacing the standard .NET `HttpClient` when required.

## Technical Details
- **Tool**: `curl-impersonate-win.exe` (Chrome profile).
- **Execution Model**: The C# server spawns `curl-impersonate` as a hidden background process and pipes its `StandardOutput` directly back to the VRChat player.
- **Trigger**: `ProxyRule.UseCurlImpersonate == true`.

## Implementation Checklist

### 1. Vendor & Build Integration
Update `build.ps1`.
- [ ] Add a step to download the latest `curl-impersonate-win` release to the `vendor/` directory. *(No upstream project ships a Windows CLI exe — lexiforest only ships libcurl-impersonate.dll; custom build pipeline or wrapper needed)*
- [x] Ensure it's copied into `dist/tools/` during the build process alongside `yt-dlp.exe`.

### 2. `CurlImpersonateClient` Service (C#)
Create `CurlImpersonateClient.cs` in `src/WKVRCProxy.Core/Services/`.
- [x] Interface implementation: `public class CurlImpersonateClient : IProxyModule`
- [x] Method `public Task<Stream> SendRequestAsync(string method, string url, Dictionary<string, string> headers)`:
    - [x] Create `ProcessStartInfo`:
        ```csharp
        var psi = new ProcessStartInfo {
            FileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "curl-impersonate-win.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true, // Log errors but don't stream them to the player
            CreateNoWindow = true
        };
        ```
    - [x] Build arguments:
        ```csharp
        psi.ArgumentList.Add("--impersonate");
        psi.ArgumentList.Add("chrome116"); // Or latest stable profile
        psi.ArgumentList.Add("-s"); // Silent mode, no progress meter
        psi.ArgumentList.Add("-X");
        psi.ArgumentList.Add(method);
        foreach (var header in headers) {
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add($"{header.Key}: {header.Value}");
        }
        psi.ArgumentList.Add(url);
        ```
    - [x] Start the process and return `process.StandardOutput.BaseStream`.

### 3. Relay Integration (C#)
Update `RelayServer.cs`.
- [x] Check `if (rule.UseCurlImpersonate)`:
    - [x] Await `CurlImpersonateClient.SendRequestAsync(...)`.
    - [x] Note: Curl standard output will contain the raw body bytes, but by default `curl` does not output HTTP headers to stdout (unless `-i` is used).
    - [x] **Crucial Detail**: To support AVPro seeking (Range requests), we need the HTTP response headers (like `Content-Range`).
    - [x] **Fix**: Pass `-i` (include headers) to `curl-impersonate`. In C#, read the stream line-by-line until you hit a blank line `
` (parsing the response headers), write those parsed headers to `context.Response`, and then copy the remainder of the stream to `context.Response.OutputStream`.

## Verification
1. [ ] Set `UseCurlImpersonate: true` for a test domain in `proxy-rules.json`.
2. [ ] Use a TLS fingerprint checker service (e.g., `https://tls.browserleaks.com/json`) as the target URL through the relay.
3. [ ] Confirm the JA3 hash in the output matches a modern Chrome browser instead of the generic .NET signature.
