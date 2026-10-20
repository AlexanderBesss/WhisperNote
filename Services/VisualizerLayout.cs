using System;
using System.Collections.Generic;

namespace WhisperNote.Services;

public readonly record struct VisualizerBar(float X, float Y, float Width, float Height);

public readonly record struct VisualizerPoint(float X, float HalfHeight);

public readonly record struct VisualizerMeter(VisualizerBar Track, VisualizerBar Fill, float PeakX);

// Pure geometry for the overlay visualizer: pixels in, rectangles out, no WPF
// types, so the mapping is covered by unit tests.
public static class VisualizerLayout
{
    public const int BarCount = SpectrumAnalyzer.BandCount;
    public const float InsetX = 6f;
    public const float InsetY = 4f;
    public const float BarGap = 4f;
    public const float MinBarHeight = 1.5f;
    public const float MeterHeight = 5f;
    public const float GlowHeight = 10f;

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

    public static VisualizerBar BuildGlow(double width, double height, double level)
    {
        var drawableW = Math.Max(0f, (float)width - 2 * InsetX);
        var fillWidth = (float)Math.Clamp(level, 0, 1) * drawableW;
        var bottom = InsetY + Math.Max(0f, (float)height - 2 * InsetY);

        return new VisualizerBar(
            InsetX,
            bottom - GlowHeight,
            Math.Max(fillWidth, GlowHeight),
            GlowHeight);
    }
}
