# Step 11: Junk and Integrity Checks

This step ensures that WKVRCProxy remains a "good citizen" on the user's PC by cleaning up relay-related artifacts upon exit and actively verifying its own network integrity.

## Objective
Implement a robust cleanup routine for relay artifacts and verify that the `localhost.youtube.com` bypass is resolving correctly to avoid confusing user errors.

## Technical Details
- **Junk**: Temporary link files (`relay_port.dat`) and any orphan processes (`curl-impersonate`, POT provider).
- **Integrity**: `Dns.GetHostAddresses` to verify the hosts file bypass.

## Implementation Checklist

### 1. `RelayIntegrityManager` Module (C#)
Create `RelayIntegrityManager.cs` in `src/WKVRCProxy.Core/Services/`.
- [x] Interface implementation: `public class RelayIntegrityManager : IProxyModule`
- [x] Run a periodic background task (e.g., every 30 seconds):
    - [x] `var ips = await Dns.GetHostAddressesAsync("localhost.youtube.com");`
    - [x] Check if `ips` contains `127.0.0.1`.
    - [x] If it fails (e.g., a user manually edited the hosts file while the app was running), send a `LOG` event to the UI: `ERROR: DNS Bypass broken. Public worlds will fail.`

### 2. Deep Process Cleanup (C#)
Update `PatcherService.cs` or a centralized shutdown orchestrator.
- [x] Implement a system-wide search for orphaned child processes.
    ```csharp
    // Sometimes standard Dispose doesn't catch deeply nested children
    foreach (var proc in Process.GetProcessesByName("curl-impersonate-win")) {
        try { proc.Kill(); } catch { }
    }
    foreach (var proc in Process.GetProcessesByName("bgutil-ytdlp-pot-provider")) {
        try { proc.Kill(); } catch { }
    }
    ```
- [x] Ensure `relay_port.dat` is added to the deletion list in `Shutdown()`.

### 3. Junk Whitelisting (C#)
Update `PatcherService.cs` `GetJunkItems()`.
- [x] Currently, the patcher complains if it finds foreign files in the VRChat Tools folder.
- [x] Ensure `relay_port.dat` (if mistakenly placed there) or other temporary tools aren't flagged as malicious junk, or explicitly delete them before the scan runs.

## Verification
1. [x] Start the app and trigger all relay features (watch a video to spawn curl and pot provider).
2. [x] Force-close the app via Task Manager (simulating a crash).
3. [x] Open the app again and close it gracefully.
4. [x] Check Task Manager: No `curl-impersonate-win` should exist.
5. [x] Open the Hosts file while the app is running, remove the bypass line, and save. Wait 30 seconds and check the UI logs for the Integrity error.