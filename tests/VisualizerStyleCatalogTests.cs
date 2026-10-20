using System;
using System.Linq;
using WhisperNote.Config;
using Xunit;

namespace WhisperNote.Tests;

// whispernote.json carries the style as free text: an older build must fall back
// instead of crashing, and the combo's display name must resolve back to the
// enum value the overlay draws.
public class VisualizerStyleCatalogTests
{
    [Fact]
    public void EveryStyleHasExactlyOneSettingOption()
    {
        var styles = Enum.GetValues<VisualizerStyle>();

        Assert.Equal(styles.Length, VisualizerCatalog.Options.Count);
        Assert.Equal(styles.Length, VisualizerCatalog.Options.Select(option => option.Value).Distinct().Count());
        Assert.All(VisualizerCatalog.Options, option => Assert.False(string.IsNullOrWhiteSpace(option.Name)));
    }

    [Fact]
    public void UnknownStoredNamesFallBackToSpectrum()
    {
        Assert.Equal(VisualizerStyle.Spectrum, VisualizerCatalog.Resolve(null));
        Assert.Equal(VisualizerStyle.Spectrum, VisualizerCatalog.Resolve(""));
        Assert.Equal(VisualizerStyle.Spectrum, VisualizerCatalog.Resolve("Waveformz"));
        Assert.Equal(VisualizerStyle.Spectrum, VisualizerCatalog.Resolve(VisualizerCatalog.DefaultStyleName));
    }

    [Fact]
    public void DisplayNamesAndEnumNamesBothResolve()
    {
        foreach (var option in VisualizerCatalog.Options)
        {
            Assert.Equal(option.Value, VisualizerCatalog.Resolve(option.Name));
            Assert.Equal(option.Value, VisualizerCatalog.Resolve(option.Value.ToString()));
            Assert.Equal(option.Name, VisualizerCatalog.NameOf(option.Value));
        }

        Assert.Equal(VisualizerStyle.Level, VisualizerCatalog.Resolve("level meter"));
    }
}
