namespace AvataRunway.Models
{
    public enum AppLogLevel
    {
        All,
        WarnError,
        Error
    }

    public enum AppLanguage
    {
        Japanese,
        English,
        Chinese,
        Korean
    }

    public enum PreviewColorScheme
    {
        Default,
        Inverted,
        Black,
        White
    }

    public sealed class AppSettingsDocument
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        public AppSettings Settings { get; set; } = new();
    }

    public sealed class AppSettings
    {
        public bool AutoLoadLastVrm { get; set; }

        public bool RestoreWindowPlacement { get; set; } = true;

        public AppLogLevel LogLevel { get; set; } = AppLogLevel.All;

        public AppLanguage Language { get; set; } = AppLanguage.Japanese;

        [System.Text.Json.Serialization.JsonConverter(typeof(PreviewColorSchemeJsonConverter))]
        public PreviewColorScheme PreviewColorScheme { get; set; } = PreviewColorScheme.Default;

        public string LastVrmFilePath { get; set; } = string.Empty;

        public WindowPlacementSettings? WindowPlacement { get; set; }
    }

    public sealed class WindowPlacementSettings
    {
        public int X { get; set; }

        public int Y { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }
    }
}
