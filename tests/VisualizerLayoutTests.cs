using System;
using System.Linq;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

// The renderer only draws what this mapping returns, so the pill geometry is
// pinned here: nothing may leave the drawable box, and a level must map
// monotonically to a bar height.
public class VisualizerLayoutTests
{
    const double Width = 260;
    const double Height = 58;

    static float[] Levels(params double[] values) =>
        values.Select(value => (float)value).ToArray();

    [Fact]
    public void BarsFillTheDrawableWidthInAscendingOrder()
    {
        var bars = VisualizerLayout.BuildBars(Width, Height, Levels(0.5));

        Assert.Equal(VisualizerLayout.BarCount, bars.Count);
        Assert.True(bars[0].X >= 0);
        Assert.True(bars[^1].X + bars[^1].Width <= Width);
        Assert.True(bars.Select(bar => bar.X).SequenceEqual(bars.Select(bar => bar.X).Order()));
        Assert.True(bars.All(bar => Math.Abs(bar.Width - bars[0].Width) < 0.01f));
    }

    [Fact]
    public void LouderLevelsDrawTallerBarsInsideTheBox()
    {
        var levels = Enumerable.Range(0, VisualizerLayout.BarCount)
            .Select(index => (float)index / VisualizerLayout.BarCount)
            .ToArray();

        var bars = VisualizerLayout.BuildBars(Width, Height, levels);

        Assert.True(bars[^1].Height > bars[1].Height);
        Assert.All(bars, bar =>
        {
            Assert.True(bar.Y >= 0);
            Assert.True(bar.Y + bar.Height <= Height);
        });
    }

    [Fact]
    public void SilenceDrawsNoBarGeometry()
    {
        var bars = VisualizerLayout.BuildBars(Width, Height, Levels(new double[VisualizerLayout.BarCount]));

        Assert.All(bars, bar => Assert.Equal(0f, bar.Height));
    }

    [Fact]
    public void OversizedLevelsAreClampedNotOverflowing()
    {
        var bars = VisualizerLayout.BuildBars(Width, Height, Levels(1, 4, 0.5, -1));

        Assert.All(bars, bar => Assert.True(bar.Y + bar.Height <= Height));
        Assert.Equal(bars[0].Height, bars[1].Height);
    }

    [Fact]
    public void ZeroSizedControlProducesNoGeometry()
    {
        Assert.Empty(VisualizerLayout.BuildBars(0, 0, Levels(0.5)));
        Assert.Empty(VisualizerLayout.BuildEnvelope(0, 0, Levels(0.5)));
        Assert.Empty(VisualizerLayout.BuildDotGrid(0, 0));
    }

    [Fact]
    public void DotGridMirrorsEachBandColumnAroundTheMidline()
    {
        var dots = VisualizerLayout.BuildDotGrid(Width, Height);

        Assert.Equal(VisualizerLayout.BarCount * 2 * VisualizerLayout.DotRowsPerSide, dots.Count);
        Assert.Equal(VisualizerLayout.BarCount, dots.Select(dot => dot.X).Distinct().Count());

        var midline = VisualizerLayout.InsetY + (Height - 2 * VisualizerLayout.InsetY) / 2;
        foreach (var dot in dots)
        {
            Assert.True(dot.Y > midline || dot.Y < midline, "a dot on the midline has no mirror pair");

            var mirrorY = 2 * midline - dot.Y;
            Assert.Contains(dots, other =>
                other != dot &&
                other.Column == dot.Column &&
                other.Row == dot.Row &&
                Math.Abs(other.X - dot.X) < 0.01f &&
                Math.Abs(other.Y - mirrorY) < 0.01f);

            Assert.True(dot.X - dot.Radius >= 0 && dot.X + dot.Radius <= Width);
            Assert.True(dot.Y - dot.Radius >= 0 && dot.Y + dot.Radius <= Height);
        }
    }

    [Fact]
    public void EnvelopeIsMirroredAroundTheVerticalCentre()
    {
        var points = VisualizerLayout.BuildEnvelope(
            Width, Height, Levels(Enumerable.Range(0, SpectrumAnalyzer.WaveformPoints).Select(index => 0.5).ToArray()));

        Assert.Equal(SpectrumAnalyzer.WaveformPoints, points.Count);
        Assert.All(points, point =>
        {
            Assert.True(point.X >= 0 && point.X <= Width);
            Assert.True(point.HalfHeight <= (Height - 2 * VisualizerLayout.InsetY) / 2);
        });
        Assert.True(points[0].X < points[^1].X);
    }

    [Fact]
    public void FrequencyRingBulgesOnlyWhereLevelsAreLoud()
    {
        var flat = VisualizerLayout.BuildFrequencyRing(
            100, 30, 20f, 10f, Levels(Enumerable.Repeat(0.5, VisualizerLayout.BarCount).ToArray()));

        Assert.Equal(VisualizerLayout.BarCount, flat.Count);
        // Flat levels make a circle: every vertex sits at base + amp * level.
        Assert.All(flat, vertex => Assert.Equal(25.0, DistanceToCentre(vertex), 2));

        var loud = new float[VisualizerLayout.BarCount];
        loud[3] = 1f;
        loud[7] = 0.5f;
        var ring = VisualizerLayout.BuildFrequencyRing(100, 30, 20f, 10f, loud);

        Assert.Equal(30.0, DistanceToCentre(ring[3]), 2);
        Assert.Equal(25.0, DistanceToCentre(ring[7]), 2);
        Assert.Equal(20.0, DistanceToCentre(ring[0]), 2);

        var clamped = VisualizerLayout.BuildFrequencyRing(100, 30, 20f, 10f, Levels(4, -2));
        Assert.Equal(30.0, DistanceToCentre(clamped[0]), 2);
        Assert.Equal(20.0, DistanceToCentre(clamped[1]), 2);
    }

    [Fact]
    public void MeterFillIsProportionalToTheLevel()
    {
        var half = VisualizerLayout.BuildMeter(Width, Height, 0.5, 0.75);
        var quiet = VisualizerLayout.BuildMeter(Width, Height, 0, 0);
        var loud = VisualizerLayout.BuildMeter(Width, Height, 1, 1);

        Assert.Equal(half.Track.Width / 2, half.Fill.Width, 2);
        Assert.Equal(0f, quiet.Fill.Width);
        Assert.Equal(loud.Track.Width, loud.Fill.Width, 2);
        Assert.True(half.PeakX <= half.Track.X + half.Track.Width);
        Assert.Equal(VisualizerLayout.InsetX, quiet.PeakX);
        Assert.True(half.PeakX > quiet.PeakX);
    }

    static double DistanceToCentre(VisualizerVertex vertex) =>
        Math.Sqrt((vertex.X - 100) * (vertex.X - 100) + (vertex.Y - 30) * (vertex.Y - 30));
}
