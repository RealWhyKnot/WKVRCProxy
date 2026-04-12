using System;
using System.Text.Json.Serialization;

namespace WKVRCProxy.Core.Diagnostics;

public enum HealthStatus
{
    Healthy,
    Degraded,
    Failed
}

public class ModuleHealthReport
{
    [JsonPropertyName("moduleName")]
    public string ModuleName { get; set; } = "";

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter<HealthStatus>))]
    public HealthStatus Status { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("lastChecked")]
    public DateTime LastChecked { get; set; }
}
