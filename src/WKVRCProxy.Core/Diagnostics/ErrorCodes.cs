namespace WKVRCProxy.Core.Diagnostics;

public static class ErrorCodes
{
    // Network
    public const string IPC_PORT_EXHAUSTED = "IPC_PORT_EXHAUSTED";
    public const string RELAY_PORT_BIND_FAILED = "RELAY_PORT_BIND_FAILED";
    public const string TIER2_NODES_UNREACHABLE = "TIER2_NODES_UNREACHABLE";
    public const string DNS_BYPASS_BROKEN = "DNS_BYPASS_BROKEN";

    // FileSystem
    public const string REDIRECTOR_MISSING = "REDIRECTOR_MISSING";
    public const string YTDLP_MISSING = "YTDLP_MISSING";
    public const string CURL_IMPERSONATE_MISSING = "CURL_IMPERSONATE_MISSING";
    public const string VRC_TOOLS_NOT_FOUND = "VRC_TOOLS_NOT_FOUND";
    public const string HOSTS_FILE_UNREADABLE = "HOSTS_FILE_UNREADABLE";

    // ChildProcess
    public const string YTDLP_TIMEOUT = "YTDLP_TIMEOUT";
    public const string YTDLP_EXECUTION_ERROR = "YTDLP_EXECUTION_ERROR";
    public const string POT_PROVIDER_CRASHED = "POT_PROVIDER_CRASHED";
    public const string POT_PROVIDER_MISSING = "POT_PROVIDER_MISSING";

    // Configuration
    public const string CONFIG_LOAD_FAILED = "CONFIG_LOAD_FAILED";
    public const string PROXY_RULES_LOAD_FAILED = "PROXY_RULES_LOAD_FAILED";

    // Protocol
    public const string IPC_PAYLOAD_NULL = "IPC_PAYLOAD_NULL";
    public const string WEBSOCKET_SESSION_ERROR = "WEBSOCKET_SESSION_ERROR";

    // Resolution
    public const string ALL_TIERS_FAILED = "ALL_TIERS_FAILED";
    public const string URL_UNREACHABLE = "URL_UNREACHABLE";
}
