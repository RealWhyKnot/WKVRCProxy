using System;
using System.Text.Json.Serialization;

namespace WKVRCProxy.Core.Models;

public class RelayEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.Now;
    [JsonPropertyName("targetUrl")]
    public string TargetUrl { get; set; } = "";
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";
    [JsonPropertyName("statusCode")]
    public int StatusCode { get; set; }
    [JsonPropertyName("bytesTransferred")]
    public long BytesTransferred { get; set; }
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }
}
