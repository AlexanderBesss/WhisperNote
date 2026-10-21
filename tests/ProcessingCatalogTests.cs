using System;
using System.Linq;
using WhisperNote.Config;
using Xunit;

namespace WhisperNote.Tests;

public class ProcessingCatalogTests
{
    [Fact]
    public void OfferedOptionsAreUniqueAndRoundTrip()
    {
        // Styles without an option (the CounterArcs/VoiceRipples fallback
        // looks) are intentional; every GPU twin must still be offered and
        // must fall back to a real CPU style.
        var offered = ProcessingCatalog.Options.Select(option => option.Value).ToList();
        Assert.Equal(offered.Count, offered.Distinct().Count());
        foreach (var style in offered)
            Assert.Equal(style, ProcessingCatalog.Resolve(ProcessingCatalog.NameOf(style)));
        foreach (var style in Enum.GetValues<ProcessingStyle>())
        {
            if (!ProcessingCatalog.IsGpu(style))
                continue;
            Assert.Contains(style, offered);
            Assert.False(ProcessingCatalog.IsGpu(ProcessingCatalog.CpuTwin(style)));
        }
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
