using System;
using System.Linq;
using WhisperNote.Config;
using Xunit;

namespace WhisperNote.Tests;

public class ProcessingCatalogTests
{
    [Fact]
    public void EveryStyleHasExactlyOneNamedOption()
    {
        var styles = Enum.GetValues<ProcessingStyle>();
        Assert.Equal(styles.Length, ProcessingCatalog.Options.Count);
        Assert.Equal(styles.Length, ProcessingCatalog.Options.Select(option => option.Value).Distinct().Count());
        foreach (var style in styles)
            Assert.Equal(style, ProcessingCatalog.Resolve(ProcessingCatalog.NameOf(style)));
    }

    [Fact]
    public void UnknownSavedNamesFallBackToTheDefault()
    {
        foreach (var name in new string?[] { null, "", "   ", "not an animation" })
            Assert.Equal(ProcessingStyle.WaveSweep, ProcessingCatalog.Resolve(name));
    }

    [Fact]
    public void DisplayNamesAndEnumSpellingsBothResolveCaseInsensitively()
    {
        Assert.Equal(ProcessingStyle.ShimmerBar, ProcessingCatalog.Resolve("shimmer bar"));
        Assert.Equal(ProcessingStyle.CometOrbit, ProcessingCatalog.Resolve("CometOrbit"));
    }
}
