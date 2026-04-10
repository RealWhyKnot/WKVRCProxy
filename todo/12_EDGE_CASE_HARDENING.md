# Step 12: Edge Case Hardening

This step addresses common failure points that occur on varied user PC environments, ensuring a seamless experience regardless of local network idiosyncrasies or strict firewalls.

## Objective
Implement robust error handling, automated retry logic, and user-facing troubleshooting tools.

## Technical Details
- **Conflicts**: Port exhaustion or immediate re-binding.
- **Network**: Windows Defender Firewall prompts blocking the Relay server.

## Implementation Checklist

### 1. Port Conflict Handling (C#)
Update `RelayPortManager.cs`.
- [x] Wrap the `TcpListener` port selection in a loop (max 5 retries).
- [x] If `HttpListener.Start()` throws a `HttpListenerException` (e.g., Error 32: The process cannot access the file because it is being used by another process):
    - [x] Catch it, grab a new port, and try again.
- [x] If 5 attempts fail, log a `FATAL` error and display an overlay in the Vue UI: "Critical: Unable to bind to any local port. Please check your firewall or restart your PC."

### 2. Firewall and AV Prompting (C# / Vue)
Because the `HttpListener` binds to `127.0.0.1`, it *usually* doesn't trigger the Windows Firewall popup. However, some strict AVs (like Norton) block it anyway.
- [x] Add a "Troubleshooting" tab in `SettingsView.vue`.
- [x] Add a button: "Add Firewall Exclusion".
- [x] Send IPC command `ADD_FIREWALL_RULE`.
- [x] In `Program.cs` or a new command handler, launch a UAC-elevated process:
    ```csharp
    var psi = new ProcessStartInfo {
        FileName = "netsh",
        Arguments = $"advfirewall firewall add rule name=\"WKVRCProxy Relay\" dir=in action=allow program=\"{Process.GetCurrentProcess().MainModule.FileName}\" enable=yes",
        Verb = "runas",
        UseShellExecute = true,
        WindowStyle = ProcessWindowStyle.Hidden
    };
    Process.Start(psi)?.WaitForExit();
    ```

### 3. Defensive Hosts file parsing
Update `HostsManager.cs`.
- [x] Some users have massively bloated hosts files (e.g., from Pi-hole or Spybot Anti-Beacon) that are megabytes in size.
- [x] Ensure the file reading uses `File.ReadLines` (which yields an `IEnumerable<string>`) instead of `File.ReadAllText` (which loads it all into memory) to maintain fast startup times.
- [x] Stop reading as soon as the `127.0.0.1 localhost.youtube.com` match is found.

## Verification
1. [x] Force a port conflict: Hardcode the Relay to port 8080, run a Python web server on 8080, and launch WKVRCProxy. Verify it catches the error, picks a new port, and starts successfully.
2. [x] Click "Add Firewall Exclusion" in the UI. Accept the UAC prompt.
3. [x] Open `wf.msc` (Windows Defender Firewall with Advanced Security), check Inbound Rules, and verify "WKVRCProxy Relay" exists.
