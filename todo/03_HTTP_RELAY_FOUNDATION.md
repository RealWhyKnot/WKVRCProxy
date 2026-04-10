# Step 3: HTTP Relay Foundation

This step implements the core "middleman" server. It acts as an HTTP proxy that receives requests from AVPro/Unity for `localhost.youtube.com` and prepares to forward them to the real video CDN.

## Objective
Implement a high-performance, non-buffering HTTP server inside the Core module using `HttpListener`.

## Technical Details
- **Server Type**: `System.Net.HttpListener` (Lightweight, no ASP.NET Core dependency required for Core).
- **Listening Host**: `http://127.0.0.1:<DynamicPort>/` (Bound to loopback to prevent Windows Firewall prompts).
- **URL Structure**: We expect requests looking like `http://localhost.youtube.com:<port>/play?target=<Base64_URL>` but the `HttpListener` will just see the path `/play?target=...`.

## Implementation Checklist

### 1. `RelayServer` Module (C#)
Create `RelayServer.cs` in `src/WKVRCProxy.Core/Services/`.
- [x] Interface implementation: `public class RelayServer : IProxyModule, IDisposable`
- [x] Dependencies: Needs `RelayPortManager` and `IModuleContext`.
- [x] `InitializeAsync`:
    - [x] Retrieve `CurrentPort` from `RelayPortManager`.
    - [x] Instantiate `HttpListener` and add prefix: `$"http://127.0.0.1:{port}/"`
    - [x] Start the listener and launch a background `Task.Run(ListenLoop)`.
- [x] `ListenLoop` Method:
    - [x] `while (listener.IsListening)` loop.
    - [x] `var context = await listener.GetContextAsync();`
    - [x] Fire and forget a worker task: `_ = Task.Run(() => HandleRequestAsync(context));`
- [x] `HandleRequestAsync` Method (Skeleton):
    - [x] Check if `context.Request.Url.AbsolutePath` starts with `/play`. If not, return 404.
    - [x] Parse query string: `string targetBase64 = context.Request.QueryString["target"];`
    - [x] Decode Base64 target:
        ```csharp
        string targetUrl = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(targetBase64));
        ```
    - [x] *For now, just return a dummy 200 OK text response to prove the server works.* (Steps 5 and 6 will implement the actual outbound `HttpClient` stream).
        ```csharp
        byte[] responseBytes = System.Text.Encoding.UTF8.GetBytes($"Relay Active. Target was: {targetUrl}");
        context.Response.StatusCode = 200;
        context.Response.ContentLength64 = responseBytes.Length;
        await context.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);
        context.Response.Close();
        ```

### 2. Startup Integration (C#)
Update `src/WKVRCProxy.UI/Program.cs`.
- [x] Instantiate `RelayServer` and `coordinator.Register(relayServer)`.
- [x] Add graceful shutdown to `OnShutdown()`.

### 3. Graceful Shutdown (C#)
- [x] Implement `public void Shutdown()` in `RelayServer` to call `listener.Stop(); listener.Close();`.

## Verification
1. [x] Run the application.
2. [x] Check the logs to find the dynamic port (e.g., `Relay listening on port 52341`).
3. [x] Base64 encode a test URL (e.g., `https://google.com` -> `aHR0cHM6Ly9nb29nbGUuY29t`).
4. [x] Open your browser and navigate to: `http://127.0.0.1:52341/play?target=aHR0cHM6Ly9nb29nbGUuY29t`.
5. [x] Verify the browser displays the dummy text: `Relay Active. Target was: https://google.com`.
