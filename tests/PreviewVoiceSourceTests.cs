using System.Linq;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

// The settings preview is the only place a style can be seen before recording, so
// its synthetic signal has to light up several bands and carry a real level.
public class PreviewVoiceSourceTests
{
    [Fact]
    public void ThePreviewSignalCarriesSpeechLikeEnergy()
    {
        var source = new PreviewVoiceSource();

        for (var frame = 0; frame < 40; frame++)
            source.Pump();

        var bands = new float[SpectrumAnalyzer.BandCount];
        source.Spectrum.ReadLevels(bands);
        var lit = bands.Count(level => level > 0.05f);
        var picture = string.Join(" ", bands.Select(level => level.ToString("0.00")));

        Assert.True(lit >= 6, $"only {lit} bands lit: {picture}");
        Assert.True(source.Spectrum.Level > 0.05, $"level={source.Spectrum.Level:0.000}");

        var waveform = new float[SpectrumAnalyzer.WaveformPoints];
        source.Spectrum.ReadWaveform(waveform);
        Assert.True(waveform.Max() > 0.05f, $"waveform peak={waveform.Max():0.000}");

        source.Dispose();
    }

    // The settings window keeps this source alive for its whole life; a source that
    // started itself would keep pumping the analyser for tabs that show no visualizer.
    [Fact]
    public void SourceIsIdleUntilThePreviewIsOnScreen()
    {
        var source = new PreviewVoiceSource();

        Assert.False(source.Active);
        source.Active = true;
        Assert.True(source.Active);
        source.Active = false;
        Assert.False(source.Active);

        source.Dispose();
    }
}
