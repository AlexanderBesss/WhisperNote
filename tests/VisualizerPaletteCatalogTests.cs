using System;
using System.Linq;
using WhisperNote.Config;
using Xunit;

namespace WhisperNote.Tests;

// The saved scheme name is free text like the style name, and the gradient stops
// are computed from the hues: a preset that produced an unsorted or transparent
// ramp would paint an invisible pill rather than a different colour.
public class VisualizerPaletteCatalogTests
{
    [Fact]
    public void PresetsAreNamedAndDistinct()
    {
        var names = VisualizerPaletteCatalog.Options.Select(palette => palette.Name).ToList();

        Assert.True(names.Count >= 12, $"only {names.Count} presets to choose from");
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(VisualizerPaletteCatalog.DefaultPaletteName, names);

        // Two presets with the same hues are a duplicate choice, not a variant.
        var signatures = VisualizerPaletteCatalog.Options
            .Select(palette => string.Join(",", palette.Hues))
            .ToList();
        Assert.Equal(signatures.Count, signatures.Distinct().Count());
    }

    [Fact]
    public void UnknownNamesFallBackToTheDefaultScheme()
    {
        Assert.Same(VisualizerPaletteCatalog.Default, VisualizerPaletteCatalog.Resolve(null));
        Assert.Same(VisualizerPaletteCatalog.Default, VisualizerPaletteCatalog.Resolve(""));
        Assert.Same(VisualizerPaletteCatalog.Default, VisualizerPaletteCatalog.Resolve("Not A Scheme"));

        var named = VisualizerPaletteCatalog.Options[2];
        Assert.Same(named, VisualizerPaletteCatalog.Resolve(named.Name));
        Assert.Same(named, VisualizerPaletteCatalog.Resolve(named.Name.ToUpperInvariant()));
        Assert.Equal(named.Name, VisualizerPaletteCatalog.NameOf(named));
    }

    [Fact]
    public void EverySchemePaintsAnUplightingGradient()
    {
        foreach (var palette in VisualizerPaletteCatalog.Options)
        {
            Assert.NotEmpty(palette.Hues);

            var stops = palette.BarStops();
            Assert.Equal(palette.Hues.Count, stops.Count);
            Assert.Equal(0.0, stops[0].Offset, 3);
            Assert.Equal(palette.Hues.Count == 1 ? 0.0 : 1.0, stops[^1].Offset, 3);

            for (var i = 1; i < stops.Count; i++)
            {
                Assert.True(stops[i].Offset > stops[i - 1].Offset,
                    $"{palette.Name}: stop {i} is out of order");
                Assert.True(stops[i].Argb >> 24 <= stops[i - 1].Argb >> 24,
                    $"{palette.Name}: the gradient does not fade towards the tip");
            }

            // Bars and meter need opaque paint; a fully transparent stop would draw nothing.
            Assert.All(stops, stop => Assert.True(stop.Argb >> 24 >= 0x30, $"{palette.Name} fades out entirely"));
            Assert.All(palette.FillStops(), stop => Assert.True(stop.Argb >> 24 >= 0xA0, $"{palette.Name} meter fill is see-through"));
            Assert.True(palette.GlowStops()[0].Argb >> 24 >= 0x60, $"{palette.Name} glow is invisible");
            Assert.Equal(0u, palette.GlowStops()[1].Argb >> 24);
            Assert.Equal(0xFFu, palette.PeakArgb >> 24);
            Assert.True(palette.TrackArgb >> 24 >= 0x20, $"{palette.Name} track is invisible");
        }
    }

    [Fact]
    public void PresetsSpanSeveralColourFamilies()
    {
        // The complaint these presets answer is "right now it's kinda only red", so
        // the list has to span visibly different families, not shades of one hue.
        var families = VisualizerPaletteCatalog.Options
            .Select(palette => Family(palette.Hues[0]))
            .Distinct()
            .OrderBy(family => family, StringComparer.Ordinal)
            .ToList();

        Assert.True(families.Count >= 5, $"presets only cover {string.Join(", ", families)}");
    }

    // A solid one-colour preset is a legitimate choice, and a lone hue has no span to
    // spread across: the missing last index would hand GradientStop NaN offsets, which
    // it accepts and then paints unpredictably.
    [Fact]
    public void SchemeWithoutSeveralHuesKeepsFiniteStops()
    {
        var solid = new VisualizerPalette("Solid", 0x22CC66, 0x22CC66, 0x22CC66);
        var empty = new VisualizerPalette("Empty", 0x22CC66, 0x22CC66);

        Assert.Single(solid.Hues);
        Assert.Single(empty.Hues);

        foreach (var stops in new[] { solid.BarStops(), solid.FillStops(), empty.BarStops() })
        {
            var (argb, offset) = stops[0];
            Assert.Equal(0.0, offset);
            Assert.False(double.IsNaN(offset));
            Assert.True(argb != 0, "a fully transparent stop paints nothing");
        }
    }

    static string Family(uint rgb)
    {
        var r = (int)(rgb >> 16 & 0xFF);
        var g = (int)(rgb >> 8 & 0xFF);
        var b = (int)(rgb & 0xFF);

        if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 40)
            return "neutral";
        if (g > r && g > b)
            return "green";
        if (b > r && b > g)
            return "blue";
        if (r > g && b > g)
            return "purple";

        return "red";
    }
}
