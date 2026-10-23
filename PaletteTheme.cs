using System;
using System.Windows;
using System.Windows.Media;
using WhisperNote.Config;

namespace WhisperNote;

// The palette chosen in settings re-tints every accent in the app through these
// resource keys; templates reference them with DynamicResource. The fallback
// values live in Styles/Theme.xaml and must match the default palette's Glow.
public static class PaletteTheme
{
    public static void Apply(VisualizerPalette palette)
    {
        var average = AverageHue(palette);
        var resources = Application.Current.Resources;
        SetColor(resources, "AccentBrush", average, 1.0);
        SetColor(resources, "AccentDeepBrush", average, 0.72);
        SetColor(resources, "AccentFillBrush", average, 0.35);
    }

    // The glows paint the Hues gradient, so the solid accents use its average:
    // they sit in the middle of the sweep instead of on one stop.
    static uint AverageHue(VisualizerPalette palette)
    {
        double r = 0, g = 0, b = 0;
        foreach (var hue in palette.Hues)
        {
            r += hue >> 16 & 0xFF;
            g += hue >> 8 & 0xFF;
            b += hue & 0xFF;
        }
        var n = palette.Hues.Count;
        return (uint)((int)Math.Round(r / n) << 16
            | (int)Math.Round(g / n) << 8
            | (int)Math.Round(b / n));
    }

    // Mutates the shared brush instance instead of swapping it, so consumers
    // that captured the brush once (converter results) re-tint live too.
    static void SetColor(ResourceDictionary resources, string key, uint rgb, double factor)
    {
        var color = Color.FromRgb(
            (byte)((rgb >> 16 & 0xFF) * factor + 0.5),
            (byte)((rgb >> 8 & 0xFF) * factor + 0.5),
            (byte)((rgb & 0xFF) * factor + 0.5));
        if (resources[key] is SolidColorBrush brush && !brush.IsFrozen)
            brush.Color = color;
        else
            // Replacing a frozen brush orphans values converters captured earlier
            // (they never re-resolve); keep the XAML fallback brushes unfrozen.
            resources[key] = new SolidColorBrush(color);
    }
}
