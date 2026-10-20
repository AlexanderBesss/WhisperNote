using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using WhisperNote.Config;
using WhisperNote.Services;

namespace WhisperNote;

// Draws the live mic signal behind the recording pill. One control, four styles;
// it redraws at ~30 fps and only while a snapshot actually changed, so an idle
// overlay costs nothing and a hidden overlay redraws never.
public sealed class VoiceVisualizer : FrameworkElement
{
    const int FrameIntervalMs = 33;
    const double PeakDecay = 0.96;

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(
            nameof(Source),
            typeof(SpectrumAnalyzer),
            typeof(VoiceVisualizer),
            new PropertyMetadata(null, OnPresentationChanged));

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(
            nameof(Mode),
            typeof(VisualizerStyle),
            typeof(VoiceVisualizer),
            new PropertyMetadata(VisualizerStyle.Spectrum, OnPresentationChanged));

    public static readonly DependencyProperty PaletteProperty =
        DependencyProperty.Register(
            nameof(Palette),
            typeof(VisualizerPalette),
            typeof(VoiceVisualizer),
            new PropertyMetadata(VisualizerPaletteCatalog.Default, OnPaletteChanged));

    static void OnPaletteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var visualizer = (VoiceVisualizer)d;
        visualizer.BuildBrushes();
        OnPresentationChanged(d, e);
    }

    LinearGradientBrush _barBrush = null!;
    LinearGradientBrush _fillBrush = null!;
    LinearGradientBrush _glowBrush = null!;
    SolidColorBrush _trackBrush = null!;
    SolidColorBrush _peakBrush = null!;

    readonly float[] _drawnLevels = new float[SpectrumAnalyzer.BandCount];
    readonly float[] _drawnWave = new float[SpectrumAnalyzer.WaveformPoints];

    double _drawnLevel;
    double _drawnPeak;
    long _lastFrameTick;
    bool _subscribed;

    public SpectrumAnalyzer? Source
    {
        get => (SpectrumAnalyzer?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public VisualizerStyle Mode
    {
        get => (VisualizerStyle)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public VisualizerPalette Palette
    {
        get => (VisualizerPalette)GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    // WPF exposes Loaded/Unloaded/IsVisibleChanged only as events, so the render
    // hook follows visibility through them.
    public VoiceVisualizer()
    {
        BuildBrushes();
        IsVisibleChanged += (_, _) => UpdateSubscription();
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => Detach();
    }

    static void OnPresentationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var visualizer = (VoiceVisualizer)d;
        visualizer.ResetSnapshot();
        visualizer.UpdateSubscription();
        visualizer.InvalidateVisual();
    }

    void UpdateSubscription()
    {
        // CompositionTarget.Rendering is the app-wide static render hook; the
        // overlay only exists while recording, and visibility gates the redraws.
        // IsLoaded is deliberately not required: the pill can be visible before
        // its loaded pass settles, and a missed frame here would freeze the bars.
        var shouldRun = IsVisible && Source != null && PresentationSource.FromVisual(this) != null;
        if (shouldRun == _subscribed)
            return;

        _subscribed = shouldRun;
        if (shouldRun)
            CompositionTarget.Rendering += OnRendering;
        else
            CompositionTarget.Rendering -= OnRendering;
    }

    void Detach()
    {
        if (!_subscribed)
            return;

        _subscribed = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    void OnRendering(object? sender, EventArgs e)
    {
        var source = Source;
        if (!IsVisible || source == null)
        {
            Detach();
            return;
        }

        var tick = Environment.TickCount64;
        if (tick - _lastFrameTick < FrameIntervalMs)
            return;
        _lastFrameTick = tick;

        // The hook only nudges; OnRender pulls the snapshot, so a frame is drawn
        // from live data on any render pass, not only when this hook fires.
        InvalidateVisual();
    }

    // Pulling the levels here, rather than in the render-loop hook, is what keeps
    // the pill honest: a style change, a show, or any layout pass draws live
    // values. The analyzer's lock is held only for the array copies.
    void RefreshSnapshot()
    {
        var source = Source;
        if (source == null)
        {
            ResetSnapshot();
            return;
        }

        source.ReadLevels(_drawnLevels);
        source.ReadWaveform(_drawnWave);
        _drawnLevel = source.Level;
        // Peak-hold is a render-side VU decay: the analyzer reports the smoothed
        // level only, which is what the meter fill follows.
        _drawnPeak = Math.Max(_drawnLevel, _drawnPeak * PeakDecay);
        if (_drawnPeak < 0.01)
            _drawnPeak = 0;
    }

    void ResetSnapshot()
    {
        Array.Clear(_drawnLevels);
        Array.Clear(_drawnWave);
        _drawnLevel = _drawnPeak = 0;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = RenderSize.Width;
        var height = RenderSize.Height;
        if (width <= 0 || height <= 0)
            return;

        RefreshSnapshot();

        if (Mode == VisualizerStyle.SpectrumGlow)
        {
            DrawGlow(dc, width, height);
            DrawBars(dc, width, height);
            return;
        }

        switch (Mode)
        {
            case VisualizerStyle.Waveform:
                DrawWaveform(dc, width, height);
                break;
            case VisualizerStyle.Level:
                DrawMeter(dc, width, height);
                break;
            default:
                DrawBars(dc, width, height);
                break;
        }
    }

    void DrawBars(DrawingContext dc, double width, double height)
    {
        // Every style draws from the snapshot refreshed once per pass in OnRender.
        var bars = VisualizerLayout.BuildBars(width, height, _drawnLevels);
        foreach (var bar in bars)
        {
            if (bar.Height <= 0f || bar.Width <= 0f)
                continue;

            var radius = Math.Min(2f, Math.Min(bar.Width, bar.Height) / 2);
            dc.DrawRoundedRectangle(_barBrush, null, new Rect(bar.X, bar.Y, bar.Width, bar.Height), radius, radius);
        }
    }

    void DrawWaveform(DrawingContext dc, double width, double height)
    {
        var points = VisualizerLayout.BuildEnvelope(width, height, _drawnWave);
        if (points.Count < 2)
            return;

        var center = VisualizerLayout.InsetY + (height - 2 * VisualizerLayout.InsetY) / 2;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(points[0].X, center - points[0].HalfHeight), true, true);
            for (int i = 1; i < points.Count; i++)
                context.LineTo(new Point(points[i].X, center - points[i].HalfHeight), true, false);
            for (int i = points.Count - 1; i >= 0; i--)
                context.LineTo(new Point(points[i].X, center + points[i].HalfHeight), true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(_fillBrush, null, geometry);
    }

    void DrawMeter(DrawingContext dc, double width, double height)
    {
        var meter = VisualizerLayout.BuildMeter(width, height, _drawnLevel, _drawnPeak);
        DrawRounded(dc, _trackBrush, meter.Track);

        if (meter.Fill.Width > 0)
            DrawRounded(dc, _fillBrush, meter.Fill);

        if (_drawnPeak > 0.01)
        {
            var tick = new Rect(meter.PeakX - 1, meter.Track.Y - 2, 2, meter.Track.Height + 4);
            dc.DrawRectangle(_peakBrush, null, tick);
        }
    }

    void DrawGlow(DrawingContext dc, double width, double height)
    {
        var glow = VisualizerLayout.BuildGlow(width, height, _drawnLevel);
        if (glow.Width <= 0)
            return;

        dc.DrawRoundedRectangle(_glowBrush, null, new Rect(glow.X, glow.Y, glow.Width, glow.Height), 4, 4);
    }

    static void DrawRounded(DrawingContext dc, Brush brush, VisualizerBar bar)
    {
        if (bar.Width <= 0 || bar.Height <= 0)
            return;

        var radius = Math.Min(bar.Height / 2, 3f);
        dc.DrawRoundedRectangle(brush, null, new Rect(bar.X, bar.Y, bar.Width, bar.Height), radius, radius);
    }

    // Brushes follow the palette, not the frame: they are rebuilt only when the
    // palette changes and every one is frozen, so a redraw never allocates.
    void BuildBrushes()
    {
        var palette = Palette ?? VisualizerPaletteCatalog.Default;
        _barBrush = MakeGradient(palette.BarStops(), new Point(0, 1), new Point(0, 0));
        _fillBrush = MakeGradient(palette.FillStops(), new Point(0, 0), new Point(1, 0));
        _glowBrush = MakeGradient(palette.GlowStops(), new Point(0.5, 1), new Point(0.5, 0));
        _trackBrush = MakeFrozen(palette.TrackArgb);
        _peakBrush = MakeFrozen(palette.PeakArgb);
    }

    static LinearGradientBrush MakeGradient(
        IReadOnlyList<(uint Argb, double Offset)> stops, Point start, Point end)
    {
        var collection = new GradientStopCollection();
        foreach (var stop in stops)
            collection.Add(new GradientStop(ToColor(stop.Argb), stop.Offset));

        var brush = new LinearGradientBrush(collection, start, end);
        brush.Freeze();
        return brush;
    }

    static Color ToColor(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    static SolidColorBrush MakeFrozen(uint argb)
    {
        var brush = new SolidColorBrush(ToColor(argb));
        brush.Freeze();
        return brush;
    }
}
