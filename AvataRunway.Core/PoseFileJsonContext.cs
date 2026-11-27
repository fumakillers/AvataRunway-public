using System.Text.Json.Serialization;

namespace AvataRunway.Core;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(PoseFile))]
internal sealed partial class PoseFileJsonContext : JsonSerializerContext;
