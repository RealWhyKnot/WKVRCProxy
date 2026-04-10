# Step 1: Hosts File Bypass Management

This step implements the foundation for the `localhost.youtube.com` domain bypass. By routing this domain to `127.0.0.1`, we trick VRChat's whitelist into allowing local traffic, since `*.youtube.com` is strictly allowed in public worlds.

## Objective
Implement a check-and-modify routine for the Windows `hosts` file that handles UAC elevation gracefully, informs the user why it's needed, and degrades gracefully if refused.

## Technical Details
- **Hosts File Path**: `C:\Windows\System32\drivers\etc\hosts`
- **Required Entry**: `127.0.0.1 localhost.youtube.com`
- **Model Addition**: Update `AppConfig.cs` to include a boolean `BypassHostsSetupDeclined` to prevent bothering the user every launch.

## Implementation Checklist

### 1. `HostsManager` Service (C#)
Create a new service `HostsManager.cs` in `src/WKVRCProxy.Core/Services/`.
- [x] Interface implementation: `public class HostsManager : IProxyModule`
- [x] Method `public bool IsBypassActive()`:
    - [x] Safely open the hosts file using `FileShare.ReadWrite` (in case another process locks it).
    - [x] Parse lines to check if `127.0.0.1 localhost.youtube.com` exists (ignoring comments starting with `#`).
- [x] Method `public async Task<bool> RequestBypassAsync()`:
    - [x] Send IPC message `PROMPT_HOSTS_SETUP` to the Vue frontend.
    - [x] Await a response (`HOSTS_SETUP_ACCEPTED` or `HOSTS_SETUP_DECLINED`).
    - [x] If accepted, spawn a child process:
        ```csharp
        var procInfo = new ProcessStartInfo {
            FileName = Process.GetCurrentProcess().MainModule.FileName,
            Arguments = "--setup-hosts",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        ```
    - [x] Handle `System.ComponentModel.Win32Exception` (thrown if the user clicks "No" on the Windows UAC prompt).

### 2. Elevated Execution Path (C#)
Update `src/WKVRCProxy.UI/Program.cs` to handle the `--setup-hosts` argument before initializing Photino.
- [x] In `Main(string[] args)`, check for `--setup-hosts`.
- [x] Execute modification:
    - [x] `File.AppendAllText(hostsPath, "\r\n127.0.0.1 localhost.youtube.com\r\n");`
    - [x] Handle `UnauthorizedAccessException` (often caused by aggressive Antivirus like BitDefender locking the hosts file). If caught, write an error log.
- [x] Execute DNS Flush:
    - [x] `Process.Start(new ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();`
- [x] Call `Environment.Exit(0)` immediately after.

### 3. UI Integration (Vue 3 / TypeScript)
- [x] **appStore.ts**: Add `BypassHostsSetupDeclined` to `AppConfig` interface and default config.
- [x] **appStore.ts**: Implement message handlers for `PROMPT_HOSTS_SETUP` from backend.
- [x] **App.vue** or **DashboardView.vue**: 
    - [x] Create a modal/dialog component explaining: "To enable public world video proxying, we need to add a local route to your Windows hosts file. This requires Administrator privileges."
    - [x] Buttons: "Allow", "Not Now", "Don't Ask Again".
    - [x] If "Don't Ask Again", set `BypassHostsSetupDeclined = true` and send `SAVE_CONFIG` IPC message.
- [x] **SettingsView.vue**: Add a toggle/button to manually trigger the hosts setup if they previously declined it.

## Verification
1. [x] Delete `127.0.0.1 localhost.youtube.com` from your PC's hosts file.
2. [x] Run the app. Ensure the UI prompts for permission.
3. [x] Click "Allow". Confirm the UAC prompt appears.
4. [x] Confirm the hosts file is updated and DNS is flushed.
5. [x] Run `ping localhost.youtube.com` in CMD; it should resolve to `127.0.0.1`.
6. [x] Restart the app, verify it detects the entry and does NOT prompt again.
