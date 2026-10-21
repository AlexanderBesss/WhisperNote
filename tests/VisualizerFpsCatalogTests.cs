using System.Linq;
using WhisperNote.Config;
using Xunit;

// The frame-rate setting reaches the render loop as an int; a hand-edited or
// older config value must clamp to a ladder value instead of unlocking arbitrary
// rates, and the offered options must stay capped to the attached display.
public class VisualizerFpsCatalogTests
{
    [Fact]
    public void LadderValuesResolveAndUnknownValuesClampToDefault()
    {
        foreach (var fps in new[] { 30, 60, 120, 144, 240, 360 })
            Assert.Equal(fps, VisualizerFpsCatalog.Resolve(fps));

        Assert.Equal(VisualizerFpsCatalog.DefaultFps, VisualizerFpsCatalog.Resolve(0));
        Assert.Equal(VisualizerFpsCatalog.DefaultFps, VisualizerFpsCatalog.Resolve(-30));
        Assert.Equal(VisualizerFpsCatalog.DefaultFps, VisualizerFpsCatalog.Resolve(45));
        Assert.Equal(VisualizerFpsCatalog.DefaultFps, VisualizerFpsCatalog.Resolve(1000));
    }

    [Fact]
    public void OptionsAreCappedToTheAttachedRefreshRate()
    {
        var max = VisualizerFpsCatalog.MaxRefreshHz();
        Assert.InRange(max, 30, 1000);

        var options = VisualizerFpsCatalog.OptionsForMonitor(30);
        Assert.Contains(options, option => option.Fps == VisualizerFpsCatalog.DefaultFps);
        Assert.All(options, option => Assert.InRange(option.Fps, 30, max));
        Assert.Equal(options.Count, options.Select(option => option.Fps).Distinct().Count());

        // A saved value from a faster screen stays listed so the combo never
        // opens blank.
        var stale = VisualizerFpsCatalog.OptionsForMonitor(360);
        Assert.Contains(stale, option => option.Fps == 360);
    }

    [Fact]
    public void DefaultIsTheNominalRateAllStepSizesAreAuthoredAgainst()
    {
        Assert.Equal(VisualizerFpsCatalog.NominalFps, VisualizerFpsCatalog.DefaultFps);
        Assert.Contains(
            VisualizerFpsCatalog.OptionsForMonitor(VisualizerFpsCatalog.DefaultFps),
            option => option.Fps == VisualizerFpsCatalog.DefaultFps);
    }
}
