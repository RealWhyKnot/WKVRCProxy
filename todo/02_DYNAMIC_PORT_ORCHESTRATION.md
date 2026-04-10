# Step 2: Dynamic Port Orchestration

To ensure stability across different user environments and to avoid requiring the entire application to run as Administrator (which binding to port 80 requires), the relay must bind to a dynamic, available port and inform the Redirector exactly which port to use.

## Objective
Implement a reliable way to find an ephemeral port, bind the HTTP Relay to it, and share this port with the Redirector via a persistent link file in standard user space.

## Technical Details
- **Port Storage**: `AppData\Local\WKVRCProxy\relay_port.dat`
- **Port Selection**: Use `TcpListener` bound to port `0` to let the Windows OS assign an available high port (typically 49152–65535) avoiding conflicts with IIS, Skype, etc.

## Implementation Checklist

### 1. `RelayPortManager` Service (C#)
Create `RelayPortManager.cs` in `src/WKVRCProxy.Core/Services/`.
- [x] Interface implementation: `public class RelayPortManager : IProxyModule`
- [x] Properties: `public int CurrentPort { get; private set; }`
- [x] Method `public void InitializeAsync(IModuleContext context)`:
    - [x] Obtain port:
        ```csharp
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        CurrentPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        ```
    - [x] Export port to file securely:
        - [x] Ensure `AppData\Local\WKVRCProxy` directory exists.
        - [x] Use `FileShare.Read` when writing to prevent locks, or use a temporary file rename strategy.
        ```csharp
        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKVRCProxy");
        string portFile = Path.Combine(appData, "relay_port.dat");
        File.WriteAllText(portFile, CurrentPort.ToString());
        ```
    - [x] Log success: `context.Logger.Debug($"Relay port exported: {CurrentPort}");`

### 2. Integration with `ModuleCoordinator` (C#)
Update `src/WKVRCProxy.UI/Program.cs`.
- [x] Instantiate `RelayPortManager` and `coordinator.Register(relayPortManager)`.
- [x] Ensure `RelayPortManager` is registered *before* the new Relay server (Step 3) so the port is available during server initialization.

### 3. Redirector Link File Update (C#)
Update `src/WKVRCProxy.Redirector/Program.cs`.
The Redirector currently reads `ipc_port.dat` to communicate with the Core via WebSocket. It will need to read `relay_port.dat` to format the final URL.
- [x] Add logic in `Main()` to read `relay_port.dat` safely:
    ```csharp
    string relayPortFile = Path.Combine(appData, "relay_port.dat");
    int relayPort = 0;
    if (File.Exists(relayPortFile)) {
        string portStr = File.ReadAllText(relayPortFile).Trim();
        int.TryParse(portStr, out relayPort);
    }
    ```
- [x] Note: We won't use `relayPort` to change the output *yet* (that happens in Step 4), but getting the file read logic됩 is key here. Make sure it doesn't crash the Redirector if the file is missing or locked.

## Verification
1. [x] Launch the app.
2. [x] Open `C:\Users\%USERNAME%\AppData\Local\WKVRCProxy\relay_port.dat`.
3. [x] Verify it contains a high number (e.g., 54321).
4. [x] Close the app and re-launch it.
5. [x] Verify `relay_port.dat` is updated to a new number or properly maintained.
6. [x] Ensure no file-locking exceptions appear in `logs/` when the Redirector executes simultaneously.
