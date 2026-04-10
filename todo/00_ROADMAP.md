# WKVRCProxy Advanced Relay Roadmap

This roadmap outlines the evolution of WKVRCProxy from a simple redirector to a sophisticated, transparent relay proxy capable of bypassing VRChat's domain whitelists and website anti-bot measures.

## Phase 1: Infrastructure & Hosts Bypass
*   **01_Hosts_Management.md**: Implement the one-time UAC-elevated hosts file modification for `localhost.youtube.com`.
*   **02_Dynamic_Port_Orchestration.md**: Enhance the `IPCServer` or create a new service to manage the relay's dynamic port and export it to the Redirector.

## Phase 2: The Local Relay (The Middleman)
*   **03_HTTP_Relay_Foundation.md**: Implement a high-performance `HttpListener` or Kestrel-based relay within the Core.
*   **04_Redirector_Upgrade.md**: Update the `Redirector` to return `localhost.youtube.com` URLs instead of direct CDN links.

## Phase 3: Player Compatibility & Streaming
*   **05_Transparent_Header_Passthrough.md**: Implement the whitelist-based header forwarding (Range, Accept, etc.) to ensure AVPro/Unity stability.
*   **06_Chunked_Streaming_Implementation.md**: Ensure the relay uses non-buffering streams to prevent memory exhaustion and playback lag.

## Phase 4: Intelligence & Rules
*   **07_JSON_Domain_Policies.md**: Implement the `proxy-rules.json` system for per-domain header and UA overrides.
*   **08_Telemetry_and_Debug_Tools.md**: Add UI views for monitoring relay traffic and header debugging.

## Phase 5: Advanced Bypasses (The "Perfect" Mimic)
*   **09_TLS_Impersonation_Integration.md**: Integrate `curl-impersonate` as an outbound client to solve JA3/JA4 fingerprint blocking.
*   **10_POT_Token_Service.md**: Implement a background service to fetch and attach YouTube Proof of Origin tokens to GVS requests.

## Phase 6: Polish & Security
*   **11_Junk_and_Integrity_Checks.md**: Update the cleanup logic to handle the new relay artifacts.
*   **12_Edge_Case_Hardening.md**: Handle port conflicts, firewall prompts, and DNS caching issues.
