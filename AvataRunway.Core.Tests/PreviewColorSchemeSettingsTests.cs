using AvataRunway.Models;
using System.Text.Json;
using Xunit;

namespace AvataRunway.Core.Tests;

public sealed class PreviewColorSchemeSettingsTests
{
    [Fact]
    public void MissingColorSchemeUsesDefault()
    {
        var settings = DeserializeSettings("""
            {
              "Version": 1,
              "Settings": {
                "Language": "Japanese"
              }
            }
            """);

        Assert.Equal(PreviewColorScheme.Default, settings.PreviewColorScheme);
    }

    [Theory]
    [InlineData("\"Unknown\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("99")]
    public void InvalidColorSchemeUsesDefault(string storedValue)
    {
        var settings = DeserializeSettings($$"""
            {
              "Version": 1,
              "Settings": {
                "PreviewColorScheme": {{storedValue}}
              }
            }
            """);

        Assert.Equal(PreviewColorScheme.Default, settings.PreviewColorScheme);
    }

    [Theory]
    [InlineData("Default", PreviewColorScheme.Default)]
    [InlineData("Inverted", PreviewColorScheme.Inverted)]
    [InlineData("Black", PreviewColorScheme.Black)]
    [InlineData("White", PreviewColorScheme.White)]
    public void SupportedColorSchemeIsRestored(string storedValue, PreviewColorScheme expected)
    {
        var settings = DeserializeSettings($$"""
            {
              "Version": 1,
              "Settings": {
                "PreviewColorScheme": "{{storedValue}}"
              }
            }
            """);

        Assert.Equal(expected, settings.PreviewColorScheme);
    }

    [Fact]
    public void ColorSchemeIsSavedAsItsStableName()
    {
        var document = new AppSettingsDocument
        {
            Settings = new AppSettings
            {
                PreviewColorScheme = PreviewColorScheme.Inverted
            }
        };

        var json = JsonSerializer.Serialize(document);

        Assert.Contains("\"PreviewColorScheme\":\"Inverted\"", json);
    }

    private static AppSettings DeserializeSettings(string json)
    {
        var document = JsonSerializer.Deserialize<AppSettingsDocument>(
            json,
            new JsonSerializerOptions
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });

        return Assert.IsType<AppSettings>(document?.Settings);
    }
}
