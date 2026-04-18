using System.Text.Json.Serialization;

namespace MirrorGrab.Services;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<BatchStateData>))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
