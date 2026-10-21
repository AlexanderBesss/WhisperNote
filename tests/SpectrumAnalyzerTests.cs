using System;
using System.Linq;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

public class SpectrumAnalyzerTests
{
    const int SampleRate = 48000;
    const int ChunkSamples = 512;

    static byte[] ToneFloat(double hz, double amplitude, int samples, int channels = 1)
    {
        var buffer = new byte[samples * channels * 4];
        for (int sample = 0; sample < samples; sample++)
        {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * hz * sample / SampleRate));
            for (int ch = 0; ch < channels; ch++)
                BitConverter.GetBytes(value).CopyTo(buffer, (sample * channels + ch) * 4);
        }

        return buffer;
    }

    static byte[] ToneInt16(double hz, double amplitude, int samples)
    {
        var buffer = new byte[samples * 2];
        for (int sample = 0; sample < samples; sample++)
        {
            var value = (short)Math.Round(amplitude * 32768 * Math.Sin(2 * Math.PI * hz * sample / SampleRate));
            BitConverter.GetBytes(value).CopyTo(buffer, sample * 2);
        }

        return buffer;
    }

    static void Feed(SpectrumAnalyzer analyzer, byte[] buffer, int samples, int channels = 1)
    {
        var chunkBytes = ChunkSamples * channels * 4;
        for (int offset = 0; offset + chunkBytes <= buffer.Length; offset += chunkBytes)
            analyzer.AddBytes(buffer.AsSpan(offset, chunkBytes).ToArray(), chunkBytes, SampleRate, channels, 32, true);
    }

    static void FeedInt16(SpectrumAnalyzer analyzer, byte[] buffer, int samples)
    {
        var chunkBytes = ChunkSamples * 2;
        for (int offset = 0; offset + chunkBytes <= buffer.Length; offset += chunkBytes)
            analyzer.AddBytes(buffer.AsSpan(offset, chunkBytes).ToArray(), chunkBytes, SampleRate, 1, 16, false);
    }

    static float[] LevelsAfter(SpectrumAnalyzer analyzer, double hz, double amplitude, int seconds = 1, int channels = 1)
    {
        analyzer.Reset();
        Feed(analyzer, ToneFloat(hz, amplitude, SampleRate * seconds, channels), SampleRate * seconds, channels);
        var levels = new float[SpectrumAnalyzer.BandCount];
        analyzer.ReadLevels(levels);
        return levels;
    }

    // Mirrors the analyzer's band-edge formula; keep it in sync when that formula changes.
    static int BandOf(double hz)
    {
        for (int band = 0; band < SpectrumAnalyzer.BandCount; band++)
        {
            var low = 60 * Math.Pow(8000 / 60.0, band / (double)SpectrumAnalyzer.BandCount);
            var high = 60 * Math.Pow(8000 / 60.0, (band + 1) / (double)SpectrumAnalyzer.BandCount);
            if (hz >= low && hz < high)
                return band;
        }

        return SpectrumAnalyzer.BandCount - 1;
    }

    [Fact]
    public void ToneLightsTheBandOfItsOwnFrequency()
    {
        var levels = LevelsAfter(new SpectrumAnalyzer(), 440, 0.2);

        Assert.Equal(BandOf(440), Array.IndexOf(levels, levels.Max()));
        Assert.True(levels[BandOf(440)] > 0.5f);
        Assert.True(levels[0] < 0.2f, "bass stays down when only 440 Hz is present");
    }

    [Fact]
    public void HighToneLightsTheTopBandsNotTheBass()
    {
        var levels = LevelsAfter(new SpectrumAnalyzer(), 5000, 0.2);
        var loudest = Array.IndexOf(levels, levels.Max());

        Assert.True(loudest >= 15, $"5 kHz belongs in the top bands, got band {loudest}");
        Assert.True(levels.Take(5).All(level => level < 0.2f));
    }

    [Fact]
    public void DcContentLightsTheLowestBand()
    {
        var analyzer = new SpectrumAnalyzer();
        var samples = 93 * ChunkSamples;
        var full = new byte[samples * 4];
        for (int i = 0; i < samples; i++)
            BitConverter.GetBytes(0.2f).CopyTo(full, i * 4);

        Feed(analyzer, full, samples);
        var levels = new float[SpectrumAnalyzer.BandCount];
        analyzer.ReadLevels(levels);

        Assert.True(levels[0] > 0.5f);
        Assert.True(levels.Skip(10).All(level => level < 0.2f));
    }

    [Fact]
    public void QuietSpeechIsLiftedIntoViewByAutoGain()
    {
        var loud = LevelsAfter(new SpectrumAnalyzer(), 440, 0.2);
        var whisper = LevelsAfter(new SpectrumAnalyzer(), 440, 0.005);

        // The auto-gain rides the frame peak.
        Assert.True(whisper.Max() > 0.5f);
        Assert.True(loud.Max() <= 1.0f, "a loud voice must not overflow the meter");
    }

    [Fact]
    public void NothingMovesWhenTheEndpointIsSilent()
    {
        var analyzer = new SpectrumAnalyzer();
        Feed(analyzer, new byte[SampleRate * 4], SampleRate);

        var levels = new float[SpectrumAnalyzer.BandCount];
        analyzer.ReadLevels(levels);

        Assert.All(levels, level => Assert.Equal(0f, level));
        Assert.True(analyzer.Level < 0.01);
    }

    [Fact]
    public void LevelsDecaySmoothlyToZeroAfterTheVoiceStops()
    {
        var analyzer = new SpectrumAnalyzer();
        var levels = LevelsAfter(analyzer, 440, 0.4);
        var loudest = levels.Max();

        var previous = loudest;
        for (int round = 0; round < 10; round++)
        {
            Feed(analyzer, new byte[ChunkSamples * 4 * 10], ChunkSamples * 10);
            analyzer.ReadLevels(levels);
            var current = levels.Max();
            Assert.True(current <= previous + 1e-5, "decay must be monotonic");
            previous = current;
        }

        Feed(analyzer, new byte[SampleRate * 4], SampleRate);
        analyzer.ReadLevels(levels);
        Assert.True(levels.Max() < 0.05f);
        Assert.True(analyzer.Level < 0.05f);
    }

    [Fact]
    public void FloatAndPcm16FormsOfTheSameToneAgree()
    {
        var floatLevels = LevelsAfter(new SpectrumAnalyzer(), 440, 0.2);
        var analyzer16 = new SpectrumAnalyzer();
        analyzer16.Reset();
        FeedInt16(analyzer16, ToneInt16(440, 0.2, SampleRate), SampleRate);
        var pcm16 = new float[SpectrumAnalyzer.BandCount];
        analyzer16.ReadLevels(pcm16);

        Assert.Equal(Array.IndexOf(floatLevels, floatLevels.Max()), Array.IndexOf(pcm16, pcm16.Max()));
        Assert.True(Math.Abs(floatLevels.Max() - pcm16.Max()) < 0.05f);
    }

    [Fact]
    public void StereoIsDownmixedToTheMonoSpectrum()
    {
        var mono = LevelsAfter(new SpectrumAnalyzer(), 440, 0.2, channels: 1);
        var stereo = LevelsAfter(new SpectrumAnalyzer(), 440, 0.2, channels: 2);

        Assert.Equal(Array.IndexOf(mono, mono.Max()), Array.IndexOf(stereo, stereo.Max()));
        Assert.True(Math.Abs(mono.Max() - stereo.Max()) < 0.05f);
    }

    [Fact]
    public void OneHugeBufferMatchesTheSameAudioFedInChunks()
    {
        var tone = ToneFloat(440, 0.2, SampleRate * 4);

        var chunked = LevelsAfter(new SpectrumAnalyzer(), 440, 0.2, seconds: 4);

        var single = new SpectrumAnalyzer();
        single.Reset();
        single.AddBytes(tone, tone.Length, SampleRate, 1, 32, true);
        var singleLevels = new float[SpectrumAnalyzer.BandCount];
        single.ReadLevels(singleLevels);

        Assert.Equal(Array.IndexOf(chunked, chunked.Max()), Array.IndexOf(singleLevels, singleLevels.Max()));
    }

    [Fact]
    public void WaveformTracesEveryGroupOfTheWindow()
    {
        var analyzer = new SpectrumAnalyzer();
        analyzer.Reset();
        Feed(analyzer, ToneFloat(440, 0.2, SampleRate), SampleRate);

        var wave = new float[SpectrumAnalyzer.WaveformPoints];
        analyzer.ReadWaveform(wave);

        Assert.Equal(1f, wave.Max(), 2);
        Assert.True(wave.Min() > 0.05f, "no gap may collapse to zero inside a steady tone");
    }

    [Fact]
    public void ResetClearsBandsLevelAndPeak()
    {
        var analyzer = new SpectrumAnalyzer();
        Feed(analyzer, ToneFloat(440, 0.4, SampleRate), SampleRate);
        Assert.True(analyzer.Level > 0.1);

        analyzer.Reset();

        var levels = new float[SpectrumAnalyzer.BandCount];
        var wave = new float[SpectrumAnalyzer.WaveformPoints];
        analyzer.ReadLevels(levels);
        analyzer.ReadWaveform(wave);

        Assert.All(levels, level => Assert.Equal(0f, level));
        Assert.All(wave, point => Assert.Equal(0f, point));
        Assert.Equal(0.0, analyzer.Level);
    }

    [Fact]
    public void ReadsNeverWritePastTheCallersArray()
    {
        var analyzer = new SpectrumAnalyzer();
        Feed(analyzer, ToneFloat(440, 0.2, SampleRate), SampleRate);

        var full = new float[SpectrumAnalyzer.BandCount];
        var shortBuffer = new float[5];
        analyzer.ReadLevels(full);
        analyzer.ReadLevels(shortBuffer);

        Assert.Equal(full.Take(5), shortBuffer);
    }

    [Fact]
    public void TruncatedAndEmptyBuffersAreIgnored()
    {
        var analyzer = new SpectrumAnalyzer();
        var tone = ToneFloat(440, 0.2, SampleRate);

        analyzer.AddBytes(tone, 0, SampleRate, 1, 32, true);
        analyzer.AddBytes(tone, 3, SampleRate, 1, 4, true);
        analyzer.AddBytes(tone, tone.Length, 0, 1, 32, true);
        analyzer.AddBytes(tone, tone.Length, SampleRate, 0, 32, true);

        var levels = new float[SpectrumAnalyzer.BandCount];
        analyzer.ReadLevels(levels);

        Assert.All(levels, level => Assert.Equal(0f, level));
        Assert.True(analyzer.Level < 0.01);
    }
}
