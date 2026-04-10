# Step 4: Redirector Upgrade

This step connects the `Redirector` (the `yt-dlp.exe` wrapper) to the new relay system. When `yt-dlp` resolves a media URL, instead of giving VRChat the raw YouTube CDN link, we will wrap it in our `localhost.youtube.com` proxy format.

## Objective
Update the Core's `ResolutionEngine` and the `Redirector` to seamlessly substitute the resolved URLs with our local relay proxy URLs, triggering the whitelist bypass in VRChat.

## Technical Details
- **Final Target URL Structure**: `http://localhost.youtube.com:<relay_port>/play?target=<Base64_Encoded_Real_URL>`
- **Condition**: We should only use the relay if the `HostsManager` reports the bypass is active (the user allowed it), otherwise we fall back to raw URLs.

## Implementation Checklist

### 1. `ResolutionEngine` Awareness (C#)
Update `src/WKVRCProxy.Core/Services/ResolutionEngine.cs`.
- [x] Inject `HostsManager` and `RelayPortManager` into the constructor.
- [x] At the end of `ResolveAsync`, right before returning the `result` string:
    - [x] Check `if (hostsManager.IsBypassActive() && !string.IsNullOrEmpty(result))`
    - [x] If true, construct the relay URL:
        ```csharp
        string encodedUrl = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(result));
        int port = relayPortManager.CurrentPort;
        string relayUrl = $"http://localhost.youtube.com:{port}/play?target={encodedUrl}";
        result = relayUrl;
        ```
    - [x] Log that the URL was spoofed for the relay.

### 2. Bypass Toggle in UI Settings (Optional but recommended)
- [x] **AppConfig.cs**: Add `public bool EnableRelayBypass { get; set; } = true;`
- [x] **appStore.ts**: Add `enableRelayBypass` to the config interface.
- [x] **SettingsView.vue**: Add a toggle switch: "Enable Localhost Relay Bypass".
- [x] Update `ResolutionEngine` to check `_settings.Config.EnableRelayBypass` as well.

### 3. Redirector Logging Polish (C#)
Update `src/WKVRCProxy.Redirector/Program.cs`.
- [x] Because the `Redirector` is simply a dumb pipe that gets a string back from the Core IPC, it doesn't actually need to format the URL itself. The Core is doing the formatting (see Checklist item 1).
- [x] However, we should ensure the `Redirector` trims any whitespace and accurately outputs the string via `Console.OpenStandardOutput()`. (This logic already exists, just verify it doesn't strip query parameters).

## Verification
1. [x] Ensure your hosts file has the `127.0.0.1 localhost.youtube.com` entry.
2. [x] Start the UI.
3. [x] Open a command prompt and manually run the redirector against a YouTube URL:
    ```bash
    .\tools\redirector.exe "https://www.youtube.com/watch?v=dQw4w9WgXcQ"
    ```
4. [x] Wait for resolution. The console output should be a `localhost.youtube.com` URL.
5. [x] Copy that exact URL into a web browser.
6. [x] You should hit the dummy text from Step 3: `Relay Active. Target was: https://...googlevideo.com/...`
