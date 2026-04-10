# Step 10: POT Token Service

This step implements a system for fetching and attaching YouTube Proof of Origin (POT) tokens, which are increasingly required for GVS (Google Video Server) requests, especially for SABR formats and Web clients.

## Objective
Integrate a local POT provider tool (like `bgutil-ytdlp-pot-provider`) to generate valid tokens on the fly and attach them to the relay's outbound YouTube requests.

## Technical Details
- **Tool**: We will bundle the compiled binary of `bgutil-ytdlp-pot-provider` or run it via Deno (since Deno is already in our `tools/` folder).
- **Trigger**: `ProxyRule.UsePoTokenProvider == true` in `proxy-rules.json`.
- **Target**: `googlevideo.com` (GVS) requests.

## Implementation Checklist

### 1. `PotProviderService` Module (C#)
Create `PotProviderService.cs` in `src/WKVRCProxy.Core/Services/`.
- [x] Interface implementation: `public class PotProviderService : IProxyModule, IDisposable`
- [x] `InitializeAsync`:
    - [x] Find a free internal port (e.g., via `TcpListener` on port 0).
    - [x] Spawn the provider tool as a hidden child process (e.g., `deno run --allow-net ...` or a compiled exe).
    - [x] Wait for the tool's HTTP server to become responsive.
- [x] Method `public async Task<string?> GetPotTokenAsync(string visitorData, string videoId)`:
    - [x] Make an HTTP POST request to `http://127.0.0.1:<provider_port>/get_pot`.
    - [x] Payload: `{"client": "web.gvs", "visitorData": visitorData, "dataSyncId": videoId}`.
    - [x] Parse and return the `poToken` from the JSON response.
- [x] Implement a basic Memory Cache `Dictionary<string, (string token, DateTime expires)>` to avoid hammering the provider for every single chunk requested by AVPro.

### 2. Relay Integration (C#)
Update `RelayServer.cs`.
- [x] When a request for `googlevideo.com` arrives and the rule requires POT:
    - [x] Extract `video_id` from the URL query string (if present in the yt-dlp generated URL, usually it's in the signature or as `id=`).
    - [x] Extract `visitor_id` (often passed in the yt-dlp cookie string or URL parameters).
    - [x] Call `_potProvider.GetPotTokenAsync(...)`.
    - [x] Append the returned token to the `targetUrl` before making the outbound request: `targetUrl += $"&pot={token}"` (Google Video Servers accept it as a URL parameter).

### 3. Graceful Shutdown
Update `PotProviderService.cs` `Dispose()`.
- [x] Ensure the child process is aggressively killed `process.Kill(true)` so we don't leave orphaned node/deno/exe processes lingering in the background after the user closes WKVRCProxy.

## Verification
1. [x] Configure `googlevideo.com` to use `UsePoTokenProvider: true`.
2. [x] Play a YouTube video that enforces POT checks.
3. [x] Observe the Traffic View (from Step 8) or console logs.
4. [x] Confirm the relay pauses to acquire a POT token (first request might take ~1s) and successfully plays the video.