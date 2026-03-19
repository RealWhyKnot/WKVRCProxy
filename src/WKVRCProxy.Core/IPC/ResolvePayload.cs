using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WKVRCProxy.Core.IPC;

public class ResolvePayload
{
    [JsonPropertyName("args")]
    public string[] Args { get; set; } = [];

    [JsonPropertyName("env")]
    public Dictionary<string, string> Env { get; set; } = new();
}
