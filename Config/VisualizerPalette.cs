using System;
using System.Collections.Generic;
using System.Linq;

namespace WhisperNote.Config;

// A visualizer colour scheme: one gradient drawn bottom-up, plus the accents the
// glow and meter styles need. Stops are RGB only, because the alpha ramp is applied
// uniformly so every preset keeps the same "strong at the baseline, faint at the
// tip" read no matter how many hues it walks through.
public sealed class VisualizerPalette
{
    const double BaseAlpha = 0xD0;
    const double TipAlpha = 0x3A;
    const uint FillAlpha = 0xC8;
    const uint GlowAlpha = 0xA8;
    const uint TrackAlpha = 0x30;

    public string Name { get; }

    // Bottom stop first; a single hue paints a flat gradient, several hues blend.
    public IReadOnlyList<uint> Hues { get; }

    public uint Glow { get; }

    public uint Peak { get; }

    public VisualizerPalette(string name, uint glow, uint peak, params uint[] hues)
    {
        Name = name;
        Glow = glow;
        Peak = peak;
        Hues = hues.Length == 0 ? new uint[] { 0xE5484D } : hues;
    }

    public override string ToString() => Name;

    public IReadOnlyList<(uint Argb, double Offset)> BarStops()
    {
        var last = Hues.Count - 1;
        var stops = new List<(uint, double)>(Hues.Count);
        for (var i = 0; i < Hues.Count; i++)
        {
            var alpha = last == 0 ? BaseAlpha : BaseAlpha + (TipAlpha - BaseAlpha) * i / last;
            // A lone hue has no span to spread across: dividing by last would yield NaN
            // offsets, which GradientStop accepts and then paints unpredictably.
            stops.Add((Argb((uint)Math.Round(alpha), Hues[i]), last == 0 ? 0 : (double)i / last));
        }

        return stops;
    }

    // The meter fill reads across the pill at one strength, so it keeps a flat alpha.
    public IReadOnlyList<(uint Argb, double Offset)> FillStops()
    {
        var last = Hues.Count - 1;
        var stops = new List<(uint, double)>(Hues.Count);
        for (var i = 0; i < Hues.Count; i++)
            stops.Add((Argb(FillAlpha, Hues[i]), last == 0 ? 0 : (double)i / last));

        return stops;
    }

    public IReadOnlyList<(uint Argb, double Offset)> GlowStops() =>
        new[] { (Argb(GlowAlpha, Glow), 0.0), (Argb(0, Glow), 1.0) };

    // Samples the hue ramp at a position, for styles that spread colours over
    // something other than the bottom-up gradient (e.g. over frequency rows).
    public uint HueAt(double t)
    {
        if (Hues.Count == 1)
            return Mask(Hues[0]);

        var scaled = Math.Clamp(t, 0.0, 1.0) * (Hues.Count - 1);
        var lower = Math.Min(Hues.Count - 2, (int)scaled);
        var mix = scaled - lower;

        var a = Mask(Hues[lower]);
        var b = Mask(Hues[lower + 1]);
        // Channels go through double before the subtraction: uint - uint wraps
        // instead of going negative, which a darker-to-brighter stop would corrupt.
        var red = (uint)Math.Round(LerpChannel(a >> 16, b >> 16, mix));
        var green = (uint)Math.Round(LerpChannel(a >> 8, b >> 8, mix));
        var blue = (uint)Math.Round(LerpChannel(a, b, mix));

        return red << 16 | green << 8 | blue;
    }

    static double LerpChannel(uint fromShifted, uint toShifted, double t)
    {
        double from = fromShifted & 0xFF;
        double to = toShifted & 0xFF;
        return from + (to - from) * t;
    }

    public uint TrackArgb => TrackAlpha << 24 | Mask(Hues[0]);

    public uint PeakArgb => 0xFFu << 24 | Mask(Peak);

    static uint Argb(uint alpha, uint rgb) => alpha << 24 | Mask(rgb);

    // The mask is a uint literal: against an int literal, & would widen to long.
    static uint Mask(uint rgb) => rgb & 0x00FFFFFFu;
}

public static class VisualizerPaletteCatalog
{
    public const string DefaultPaletteName = "Signal Red";

    public static readonly IReadOnlyList<VisualizerPalette> Options = new[]
    {
        new VisualizerPalette("Signal Red", 0xE5484D, 0xFF8A8E, 0xE5484D, 0xFF7A7A),
        new VisualizerPalette("Ember Sunset", 0xFF6B35, 0xFFC79A, 0xFF6B35, 0xFFB84D, 0xFF3D6E),
        new VisualizerPalette("Molten Gold", 0xFFB84D, 0xFFF0B8, 0xFFA62B, 0xFFE066),
        new VisualizerPalette("Crimson Fade", 0xC81E1E, 0xFFA5A5, 0xD62D2B, 0xFF6B6B),
        new VisualizerPalette("Cherry Cola", 0xD61E4E, 0xFF9FB3, 0xB3123C, 0xFF4D6D),
        new VisualizerPalette("Rose Water", 0xFF5C9E, 0xFFD0E3, 0xFF4D8D, 0xFFA0C8),
        new VisualizerPalette("Magenta Punch", 0xE63FA0, 0xFFB8E4, 0xFF2E88, 0xFF6BD6),
        new VisualizerPalette("Orchid", 0xD24BE0, 0xEBB6F5, 0xC039E8, 0xFF5CB8),
        new VisualizerPalette("Violet Bloom", 0xA855F7, 0xE9B8FA, 0x8B5CF6, 0xD946EF, 0xFF6BA8),
        new VisualizerPalette("Ultraviolet", 0x7C4CF2, 0xC6A9FB, 0x5B3BE8, 0x9B5CF6, 0xD46BF0),
        new VisualizerPalette("Indigo Sky", 0x6366F1, 0xBBD8FB, 0x4F46E5, 0x60A5FA),
        new VisualizerPalette("Ocean Depth", 0x2E6FE8, 0x9BE7F5, 0x1D4ED8, 0x0EA5E9, 0x5EEAD4),
        new VisualizerPalette("Sea Glass", 0x0EA5E9, 0xA5F0E8, 0x0891B2, 0x5EEAD4),
        new VisualizerPalette("Teal Sunrise", 0x14B8A6, 0xFBF0B0, 0x0D9488, 0x34D399, 0xFDE68A),
        new VisualizerPalette("Emerald Ice", 0x1FA75E, 0xBFE6FA, 0x0F9D58, 0x34D399, 0x93C5FD),
        new VisualizerPalette("Mint Lime", 0x22C55E, 0xE4F7B0, 0x10B981, 0xA3E635),
        new VisualizerPalette("Electric Lime", 0x84CC16, 0xF2FAC8, 0x65A30D, 0xBEF264),
        new VisualizerPalette("Citrus Pop", 0xFBBF24, 0xC8F3FA, 0xF59E0B, 0x84CC16, 0x22D3EE),
        new VisualizerPalette("Neon Nightfall", 0x38BDF8, 0xFBB6D6, 0x22D3EE, 0x6366F1, 0xEC4899),
        new VisualizerPalette("Aurora", 0x2DD4BF, 0xE0C4FB, 0x22C55E, 0x22D3EE, 0xA855F7),
        new VisualizerPalette("Synthwave", 0xFF5C93, 0xB8E6FB, 0xFF3D81, 0x8B5CF6, 0x22D3EE),
        new VisualizerPalette("Ice Blue", 0x7FB4F0, 0xE6F1FD, 0x60A5FA, 0xBFDFFF),
        new VisualizerPalette("Graphite", 0xB4BCC4, 0xFAFBFC, 0x9AA3AD, 0xE2E6EA),
    };

    public static VisualizerPalette Default => Options[0];

    // The saved name is free text so an older build that lacks a newer preset keeps working.
    public static VisualizerPalette Resolve(string? paletteName) =>
        Options.FirstOrDefault(palette =>
            string.Equals(palette.Name, paletteName, StringComparison.OrdinalIgnoreCase))
        ?? Default;

    public static string NameOf(VisualizerPalette palette) => palette.Name;
}
