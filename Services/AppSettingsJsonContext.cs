using AvataRunway.Models;
using System.Text.Json.Serialization;

namespace AvataRunway.Services
{
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        UseStringEnumConverter = true)]
    [JsonSerializable(typeof(AppSettingsDocument))]
    internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
    {
    }
}
