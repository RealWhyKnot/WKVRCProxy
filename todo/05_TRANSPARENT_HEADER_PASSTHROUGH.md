# Step 5: Transparent Header Passthrough

This step ensures that the `RelayServer` accurately forwards the player's (AVPro/Unity) requests to the target CDN, including critical headers like `Range` for seeking and buffering.

## Objective
Implement a whitelist-based header forwarding system that maintains player stability and prevents detection.

## Technical Details
- **Player-to-CDN (Outbound)**: Whitelist-only (Range, Accept, etc.) to avoid fingerprint leakage.
- **CDN-to-Player (Inbound)**: All metadata headers (Content-Type, Content-Length, Content-Range, Accept-Ranges) must be mirrored exactly.

## Implementation Checklist

### 1. Outbound Header Whitelist (C#)
Update the `RelayServer.cs` request handler (`HandleRequestAsync`).
- [x] Initialize the outbound request: `var outboundRequest = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), targetUrl);`
- [x] Create a `HashSet<string>` of "Safe Headers":
    ```csharp
    var safeHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "Range", "Accept", "Accept-Language", "Accept-Encoding", "Referer", "Connection", "Keep-Alive"
    };
    ```
- [x] Iterate through `context.Request.Headers`:
    ```csharp
    foreach (string key in context.Request.Headers.AllKeys) {
        if (safeHeaders.Contains(key)) {
            outboundRequest.Headers.TryAddWithoutValidation(key, context.Request.Headers[key]);
        }
    }
    ```
- [x] **Special Handling**:
    - [x] Do not forward `Host`. `HttpClient` sets this automatically to the target domain.
    - [x] Do not forward `User-Agent`. Set a generic browser UA or leave it empty if TLS impersonation (Step 9) is going to handle it later. For now, set `outboundRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");`

### 2. Inbound Header Transparency (C#)
Update the response handling phase in `RelayServer.cs`.
- [x] After calling `await httpClient.SendAsync(...)`, copy the origin's status code:
    `context.Response.StatusCode = (int)response.StatusCode;`
- [x] Copy headers from both `response.Headers` and `response.Content.Headers`:
    ```csharp
    foreach (var header in response.Headers.Concat(response.Content.Headers)) {
        string key = header.Key;
        string value = string.Join(", ", header.Value);
        
        // Skip Transfer-Encoding as HttpListener manages chunked delivery automatically
        if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
        
        // HttpListener requires ContentLength64 to be set via property, not header array
        if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) {
            if (long.TryParse(value, out long len)) context.Response.ContentLength64 = len;
            continue;
        }

        context.Response.Headers.Add(key, value);
    }
    ```
- [x] CRITICAL: Ensure `Content-Range` and `Accept-Ranges: bytes` are being properly copied by the above loop.

## Verification
1. [x] Use a tool like `Postman` or `curl` to send a request to the relay:
    ```bash
    curl -H "Range: bytes=0-100" -i http://127.0.0.1:52341/play?target=<base64_url>
    ```
2. [x] Confirm the relay returns a `206 Partial Content` status.
3. [x] Verify the response headers contain `Content-Range: bytes 0-100/...`
4. [x] Verify the body contains exactly 101 bytes.