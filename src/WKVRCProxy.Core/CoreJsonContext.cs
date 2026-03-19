using System.Text.Json.Serialization;

namespace WKVRCProxy.Core;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Models.AppConfig))]
[JsonSerializable(typeof(IPC.ResolvePayload))]
public partial class CoreJsonContext : JsonSerializerContext
{
}
