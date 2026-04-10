# Step 8: Telemetry and Debug Tools

This step adds real-time traffic monitoring to the Vue UI, allowing users (and developers) to see exactly what the relay is doing in the background.

## Objective
Implement a "Traffic" view in the Vue frontend that displays active connections, requested URLs, and status codes.

## Technical Details
- **Data Flow**: `RelayServer` (Core) -> `Logger` or IPC -> `Photino` (UI) -> `appStore.ts` (Vue).

## Implementation Checklist

### 1. Relay Telemetry (Core C#)
Update `RelayServer.cs`.
- [x] We can leverage the existing IPC messaging system. Add an event to `WebSocketIpcServer` or direct `PhotinoWindow` sending if accessible, but the best pattern in this app is using `Logger.OnLog` or creating a dedicated event.
- [x] Create a model:
    ```csharp
    public class RelayEvent {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string TargetUrl { get; set; } = "";
        public string Method { get; set; } = "";
        public int StatusCode { get; set; }
        public long BytesTransferred { get; set; }
    }
    ```
- [x] Since `Logger` is already bridged, maybe we add a `Logger.Relay(...)` method that emits a specific structured log type, OR we add a new `SendWebMessage` type `"RELAY_EVENT"` in `Program.cs`.
- [x] In `RelayServer.cs`, track bytes transferred during `CopyToAsync` (might require a custom buffer loop to count bytes, or just report completion).

### 2. UI View (Vue 3 / Tailwind)
Create `src/WKVRCProxy.UI/ui/src/views/RelayView.vue`.
- [x] Implement a live-updating table. Columns: Time, Target (truncated), Status, Bytes.
- [x] Add a visual indicator if a request is "Pending" vs "Complete".
- [x] Implement a "Clear" button to wipe the list array.

### 3. Sidebar Integration (Vue 3)
- [x] Add a new "Traffic" or "Relay" icon (e.g., `bi-arrow-left-right`) to `Sidebar.vue`.
- [x] Update `appStore.ts` `activeTab` to support `'relay'`.
- [x] Add `relayEvents: ref<RelayEvent[]>([])` to `appStore.ts`.
- [x] Update `handleMessage` in `appStore.ts` to push `RELAY_EVENT` payloads to the array (max 100 items).

## Verification
1. [x] Start the relay and navigate to the new "Traffic" tab in the UI.
2. [x] Play a video in VRChat.
3. [x] Confirm that requests appear in real-time. AVPro typically makes several `Range` requests; you should see them all populate with `206` status codes.