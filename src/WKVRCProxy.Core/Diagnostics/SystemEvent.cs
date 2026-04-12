using System;
using System.Text.Json.Serialization;

namespace WKVRCProxy.Core.Diagnostics;

public enum SystemEventType
{
    Log,
    Status,
    Relay,
    Prompt,
    Health,
    Error
}

public class SystemEvent
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter<SystemEventType>))]
    public SystemEventType Type { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("sourceModule")]
    public string SourceModule { get; set; } = "";

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("payload")]
    public object? Payload { get; set; }
}
