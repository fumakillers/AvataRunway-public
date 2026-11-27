using AvataRunway.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace AvataRunway
{
    public sealed class PreviewColorSchemeOption
    {
        public PreviewColorSchemeOption(
            PreviewColorScheme scheme,
            Windows.UI.Color backgroundColor,
            Windows.UI.Color gridColor)
        {
            Scheme = scheme;
            Name = scheme.ToString();
            BackgroundBrush = new SolidColorBrush(backgroundColor);
            GridBrush = new SolidColorBrush(gridColor);
        }

        public PreviewColorScheme Scheme { get; }

        public string Name { get; }

        public Brush BackgroundBrush { get; }

        public Brush GridBrush { get; }

        public static PreviewColorSchemeOption[] CreateAll() =>
        [
            new(PreviewColorScheme.Default, ColorHelper.FromArgb(255, 48, 50, 54), ColorHelper.FromArgb(255, 77, 79, 82)),
            new(PreviewColorScheme.Inverted, ColorHelper.FromArgb(255, 255, 255, 255), ColorHelper.FromArgb(255, 48, 50, 54)),
            new(PreviewColorScheme.Black, ColorHelper.FromArgb(255, 0, 0, 0), ColorHelper.FromArgb(255, 0, 0, 0)),
            new(PreviewColorScheme.White, ColorHelper.FromArgb(255, 255, 255, 255), ColorHelper.FromArgb(255, 255, 255, 255))
        ];
    }
}
