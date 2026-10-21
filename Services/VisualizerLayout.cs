using System;
using System.Collections.Generic;

namespace WhisperNote.Services;

public readonly record struct VisualizerBar(float X, float Y, float Width, float Height);

public readonly record struct VisualizerPoint(float X, float HalfHeight);

public readonly record struct VisualizerMeter(VisualizerBar Track, VisualizerBar Fill, float PeakX);

public readonly record struct VisualizerDot(float X, float Y, float Radius, int Column, int Row);

public readonly record struct VisualizerVertex(float X, float Y);

// Pure geometry for the overlay visualizer: pixels in, rectangles out, no WPF
// types, so the mapping is covered by unit tests.
public static class VisualizerLayout
{
    public const int BarCount = SpectrumAnalyzer.BandCount;
    public const float InsetX = 6f;
    public const float InsetY = 4f;
    public const float BarGap = 4f;
    public const float MinBarHeight = 1.5f;
    public const float MeterHeight = 6f;

    // History length for the scrolling trace style, in frames (~33 ms each).
    public const int TraceColumns = 96;
    public const int DotRowsPerSide = 5;

    public static IReadOnlyList<VisualizerBar> BuildBars(double width, double height, ReadOnlySpan<float> levels)
    {
        var bars = new List<VisualizerBar>(BarCount);
        var drawableW = (float)width - 2 * InsetX;
        var drawableH = (float)height - 2 * InsetY;
        if (drawableW <= 0 || drawableH <= 0)
            return bars;

        var gap = BarGap;
        var barWidth = (drawableW - gap * (BarCount - 1)) / BarCount;
        if (barWidth < 1f)
        {
            gap = 1f;
            barWidth = Math.Max(1f, (drawableW - gap * (BarCount - 1)) / BarCount);
        }

        var total = barWidth * BarCount + gap * (BarCount - 1);
        var startX = InsetX + (drawableW - total) / 2f;

        for (int i = 0; i < BarCount; i++)
        {
            var level = i < levels.Length ? Math.Clamp(levels[i], 0f, 1f) : 0f;
            var barHeight = level <= 0f ? 0f : Math.Max(MinBarHeight, level * drawableH);
            bars.Add(new VisualizerBar(
                startX + i * (barWidth + gap),
                InsetY + drawableH - barHeight,
                barWidth,
                barHeight));
        }

        return bars;
    }

    public static IReadOnlyList<VisualizerPoint> BuildEnvelope(double width, double height, ReadOnlySpan<float> points)
    {
        var envelope = new List<VisualizerPoint>(points.Length);
        var drawableW = (float)width - 2 * InsetX;
        var drawableH = (float)height - 2 * InsetY;
        if (drawableW <= 0 || drawableH <= 0 || points.Length == 0)
            return envelope;

        var step = points.Length > 1 ? drawableW / (points.Length - 1) : 0f;
        var maxHalf = drawableH / 2f;

        for (int i = 0; i < points.Length; i++)
            envelope.Add(new VisualizerPoint(
                InsetX + i * step,
                Math.Clamp(points[i], 0f, 1f) * maxHalf));

        return envelope;
    }

    public static VisualizerMeter BuildMeter(double width, double height, double level, double peak)
    {
        var drawableW = Math.Max(0f, (float)width - 2 * InsetX);
        var top = InsetY + Math.Max(0f, (float)height - 2 * InsetY) - MeterHeight;
        var fillWidth = (float)Math.Clamp(level, 0, 1) * drawableW;
        var peakX = InsetX + (float)Math.Clamp(peak, 0, 1) * drawableW;

        return new VisualizerMeter(
            new VisualizerBar(InsetX, top, drawableW, MeterHeight),
            new VisualizerBar(InsetX, top, fillWidth, MeterHeight),
            peakX);
    }

    // Mirrored LED grid: one column per band, DotRowsPerSide dots above and below
    // the midline. Row 0 sits closest to the midline, so the renderer lights the
    // first `level * rows` rows of a column and a quiet band always keeps its
    // centre dots before losing the outer ones.
    public static IReadOnlyList<VisualizerDot> BuildDotGrid(double width, double height)
    {
        var dots = new List<VisualizerDot>(BarCount * 2 * DotRowsPerSide);
        var drawableW = (float)width - 2 * InsetX;
        var drawableH = (float)height - 2 * InsetY;
        if (drawableW <= 0 || drawableH <= 0)
            return dots;

        var cellWidth = drawableW / BarCount;
        var cellHeight = drawableH / (2 * DotRowsPerSide);
        var radius = Math.Min(cellWidth, cellHeight) * 0.36f;
        var centerY = InsetY + drawableH / 2f;

        for (int column = 0; column < BarCount; column++)
        {
            var x = InsetX + (column + 0.5f) * cellWidth;
            for (int row = 0; row < DotRowsPerSide; row++)
            {
                var offset = (row + 0.5f) * cellHeight;
                dots.Add(new VisualizerDot(x, centerY - offset, radius, column, row));
                dots.Add(new VisualizerDot(x, centerY + offset, radius, column, row));
            }
        }

        return dots;
    }

    // Frequency outline of a pulse ring: one vertex per band direction starting at
    // the top, radius pushed out by that band's level. Flat levels make a circle;
    // the live spectrum turns it into a splat that reads which bands were loud.
    public static IReadOnlyList<VisualizerVertex> BuildFrequencyRing(
        double centerX, double centerY, float baseRadius, float amplitude, ReadOnlySpan<float> levels)
    {
        var vertices = new List<VisualizerVertex>(BarCount);
        if (baseRadius <= 0f)
            return vertices;

        for (int i = 0; i < BarCount; i++)
        {
            var level = i < levels.Length ? Math.Clamp(levels[i], 0f, 1f) : 0f;
            var radius = baseRadius + amplitude * level;
            var angle = -MathF.PI / 2f + 2f * MathF.PI * i / BarCount;
            vertices.Add(new VisualizerVertex(
                (float)centerX + radius * MathF.Cos(angle),
                (float)centerY + radius * MathF.Sin(angle)));
        }

        return vertices;
    }
}
