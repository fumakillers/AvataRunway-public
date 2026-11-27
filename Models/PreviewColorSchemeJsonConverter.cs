using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvataRunway.Models
{
    public sealed class PreviewColorSchemeJsonConverter : JsonConverter<PreviewColorScheme>
    {
        public override PreviewColorScheme Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String &&
                Enum.TryParse(reader.GetString(), ignoreCase: false, out PreviewColorScheme value) &&
                Enum.IsDefined(value))
            {
                return value;
            }

            if (reader.TokenType == JsonTokenType.Number &&
                reader.TryGetInt32(out var numericValue) &&
                Enum.IsDefined(typeof(PreviewColorScheme), numericValue))
            {
                return (PreviewColorScheme)numericValue;
            }

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                using var ignoredValue = JsonDocument.ParseValue(ref reader);
            }

            return PreviewColorScheme.Default;
        }

        public override void Write(
            Utf8JsonWriter writer,
            PreviewColorScheme value,
            JsonSerializerOptions options)
        {
            var safeValue = Enum.IsDefined(value) ? value : PreviewColorScheme.Default;
            writer.WriteStringValue(safeValue.ToString());
        }
    }
}
