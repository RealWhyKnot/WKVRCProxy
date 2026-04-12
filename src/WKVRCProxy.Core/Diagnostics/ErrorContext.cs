using System.Text.Json.Serialization;

namespace WKVRCProxy.Core.Diagnostics;

public enum ErrorCategory
{
    Network,
    FileSystem,
    ChildProcess,
    Configuration,
    Protocol,
    Internal
}

public class ErrorContext
{
    [JsonPropertyName("category")]
    [JsonConverter(typeof(JsonStringEnumConverter<ErrorCategory>))]
    public ErrorCategory Category { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "";

    [JsonPropertyName("actionHint")]
    public string ActionHint { get; set; } = "";

    [JsonPropertyName("isRecoverable")]
    public bool IsRecoverable { get; set; }
}
