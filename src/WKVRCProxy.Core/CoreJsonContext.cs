using System.Text.Json.Serialization;

namespace WKVRCProxy.Core;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Models.AppConfig))]
[JsonSerializable(typeof(IPC.ResolvePayload))]
[JsonSerializable(typeof(Diagnostics.SystemEvent))]
[JsonSerializable(typeof(Diagnostics.ModuleHealthReport))]
[JsonSerializable(typeof(Diagnostics.ModuleHealthReport[]))]
[JsonSerializable(typeof(Diagnostics.ErrorContext))]
public partial class CoreJsonContext : JsonSerializerContext
{
}
