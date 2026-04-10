# Step 6: Chunked Streaming Implementation

This step ensures that the `RelayServer` handles large video files efficiently without consuming excessive memory. Unity and AVPro expect true streaming, not buffered downloads.

## Objective
Implement a robust, non-buffering streaming loop between the CDN and the video player.

## Technical Details
- **Streaming Mode**: Pass-through via `Stream.CopyToAsync()`.
- **Completion Option**: `HttpCompletionOption.ResponseHeadersRead` to ensure headers are sent to the player immediately.

## Implementation Checklist

### 1. Non-Buffering Logic (C#)
Update `RelayServer.cs`.
- [x] Instantiate a shared `HttpClient` at the class level (don't create one per request to avoid socket exhaustion). Set `Timeout = Timeout.InfiniteTimeSpan` since videos are long.
- [x] Execute the outbound request:
    ```csharp
    using var response = await _httpClient.SendAsync(outboundRequest, HttpCompletionOption.ResponseHeadersRead, context.Request.Headers["Keep-Alive"] != null ? CancellationToken.None : cts.Token);
    ```
- [x] Read the response stream:
    ```csharp
    using var sourceStream = await response.Content.ReadAsStreamAsync();
    ```
- [x] Stream directly to the `HttpListenerResponse` output:
    ```csharp
    // Use a reasonable buffer size like 81920 (80KB)
    await sourceStream.CopyToAsync(context.Response.OutputStream, 81920);
    ```

### 2. Error and Cancellation Handling (C#)
- [x] AVPro frequently aborts connections to seek. You MUST handle `HttpListenerException` (e.g., "The specified network name is no longer available").
    ```csharp
    try {
        await sourceStream.CopyToAsync(context.Response.OutputStream, 81920);
    } 
    catch (HttpListenerException) { /* Player closed connection (e.g., seeking) - normal behavior */ }
    catch (IOException) { /* Socket closed */ }
    ```
- [x] Finally block:
    ```csharp
    finally {
        try { context.Response.Close(); } catch { }
    }
    ```

## Verification
1. [x] Play a large YouTube video (e.g., a 10-hour VOD) in VRChat via the relay.
2. [x] Monitor the `WKVRCProxy` application's memory usage in Task Manager.
3. [x] Confirm memory usage remains low and stable (< 100MB) during playback.
4. [x] Verify seeking works correctly by jumping to different parts of the video (this tests both Range headers from Step 5 and the abortion/re-connection handling in Step 6).