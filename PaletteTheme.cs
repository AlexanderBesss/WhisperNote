using System;
using System.Windows;
using System.Windows.Media;
using WhisperNote.Config;

namespace WhisperNote;

// The palette chosen in settings re-tints every accent in the app through these
// resource keys; templates reference them with DynamicResource. The fallback
// values live in Styles/Theme.xaml and must match the default palette's average.
public static class PaletteTheme
{
    // The application dictionary freezes every brush inserted into it (XAML
    // fallbacks at load, later replacements on insertion), so entries can never
    // be mutated in place. These statics stay out of any dictionary and are the
    // live instances converters hand out: Apply mutates them, so values captured
    // earlier re-tint without re-evaluating.
    public static SolidColorBrush Accent => AccentLive;

    static readonly SolidColorBrush AccentLive = LiveBrush(1.0);
    static readonly SolidColorBrush AccentDeepLive = LiveBrush(0.72);
    static readonly SolidColorBrush AccentFillLive = LiveBrush(0.35);

    public static void Apply(VisualizerPalette palette)
    {
        var average = AverageHue(palette);
        var resources = Application.Current.Resources;
        Retint(resources, AccentLive, "AccentBrush", average, 1.0);
        Retint(resources, AccentDeepLive, "AccentDeepBrush", average, 0.72);
        Retint(resources, AccentFillLive, "AccentFillBrush", average, 0.35);
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

    static SolidColorBrush LiveBrush(double factor) =>
        new SolidColorBrush(ScaleColor(AverageHue(VisualizerPaletteCatalog.Default), factor));

    // Mutate the live instance for earlier converter captures, then post a
    // frozen snapshot under the key: the dictionary would freeze an unfrozen
    // insert anyway, and DynamicResource consumers re-resolve the replacement.
    static void Retint(ResourceDictionary resources, SolidColorBrush live, string key, uint rgb, double factor)
    {
        var color = ScaleColor(rgb, factor);
        live.Color = color;
        var snapshot = new SolidColorBrush(color);
        snapshot.Freeze();
        resources[key] = snapshot;
    }

    static Color ScaleColor(uint rgb, double factor) =>
        Color.FromRgb(
            (byte)((rgb >> 16 & 0xFF) * factor + 0.5),
            (byte)((rgb >> 8 & 0xFF) * factor + 0.5),
            (byte)((rgb & 0xFF) * factor + 0.5));
}
