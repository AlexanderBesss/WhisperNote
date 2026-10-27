using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WhisperNote.Config;

namespace WhisperNote;

public sealed class ProcessingVisualizer : FrameworkElement
{
    const double NominalFrameMs = 1000.0 / VisualizerFpsCatalog.NominalFps;

    // Near-invisible base a ShaderEffect needs so the element has non-empty
    // content; the GPU styles paint everything themselves.
    static readonly SolidColorBrush BaseFill = MakeFrozen(0x01FFFFFF);

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(
            nameof(Mode),
            typeof(ProcessingStyle),
            typeof(ProcessingVisualizer),
            new PropertyMetadata(ProcessingStyle.WaveSweep, OnPresentationChanged));

    public static readonly DependencyProperty PaletteProperty =
        DependencyProperty.Register(
            nameof(Palette),
            typeof(VisualizerPalette),
            typeof(ProcessingVisualizer),
            new PropertyMetadata(VisualizerPaletteCatalog.Default, OnPaletteChanged));

    public static readonly DependencyProperty FrameFpsProperty =
        DependencyProperty.Register(
            nameof(FrameFps),
            typeof(int),
            typeof(ProcessingVisualizer),
            new PropertyMetadata(VisualizerFpsCatalog.DefaultFps));

    public ProcessingStyle Mode
    {
        get => (ProcessingStyle)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public VisualizerPalette Palette
    {
        get => (VisualizerPalette)GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    public int FrameFps
    {
        get => (int)GetValue(FrameFpsProperty);
        set => SetValue(FrameFpsProperty, value);
    }

    static void OnPresentationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ProcessingVisualizer)d;
        control._phase = 0;
        control._shaderClockStart = 0;
        control.Effect = control._gpu.TryGetValue((ProcessingStyle)e.NewValue, out var effect)
            ? effect
            : null;
        control.InvalidateVisual();
    }

    static void OnPaletteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ProcessingVisualizer)d;
        control.BuildBrushes();
        control.InvalidateVisual();
    }

    float _phase;
    readonly FramePacer _pacer = new(NominalFrameMs);
    double _frameSpeed = 1;
    bool _subscribed;

    uint _peakRgb;
    uint _glowRgb;
    uint _hue0;
    uint _hueMid;
    SolidColorBrush _trackBrush = null!;
    Pen _basePen = null!;

    readonly Dictionary<ProcessingStyle, ProcessingGpuEffect> _gpu = new();
    long _shaderClockStart;

    public ProcessingVisualizer()
    {
        BuildBrushes();
        // ShaderEffect only runs on hardware with pixel-shader support
        // (tier 2); elsewhere the styles must draw their CPU twin, so no
        // effects are built at all.
        if (RenderCapability.Tier >> 16 >= 2)
        {
            foreach (ProcessingStyle style in Enum.GetValues<ProcessingStyle>())
            {
                if (!ProcessingCatalog.IsGpu(style))
                    continue;

                try
                {
                    var effect = new ProcessingGpuEffect(style);
                    effect.PeakColor = PeakColor();
                    effect.MidColor = MidColor();
                    _gpu[style] = effect;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"ProcessingGpuEffect({style}) failed to load: {ex.Message}");
                }
            }
        }

        IsVisibleChanged += (_, _) => UpdateSubscription();
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => Detach();
    }

    void UpdateSubscription()
    {
        var shouldRun = IsVisible && PresentationSource.FromVisual(this) != null;
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
        if (!IsVisible)
        {
            Detach();
            return;
        }

        var now = Stopwatch.GetTimestamp();

        if (_gpu.TryGetValue(Mode, out var effect) && ReferenceEquals(Effect, effect))
        {
            // The GPU styles only nudge their time and aspect uniforms: motion
            // runs at the composition rate regardless of the configured CPU
            // frame rate.
            if (_shaderClockStart == 0)
                _shaderClockStart = now;
            effect.Time = FramePacer.MsBetween(_shaderClockStart, now) / 1000.0;
            var size = RenderSize;
            effect.Aspect = size.Height > 0 ? size.Width / size.Height : 1.0;
            return;
        }

        if (!_pacer.ShouldRender(FrameFps, now))
            return;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = RenderSize.Width;
        var height = RenderSize.Height;
        if (width <= 0 || height <= 0)
            return;

        var now = Stopwatch.GetTimestamp();
        _frameSpeed = _pacer.FrameSpeed(now);

        _phase = (_phase + 0.030f * (float)_frameSpeed) % 1f;

        if (ProcessingCatalog.IsGpu(Mode))
        {
            if (Effect != null)
            {
                // A ShaderEffect whose registered sampler brush is null is
                // skipped without an error, so the element must draw real
                // content; the shader itself paints everything on top.
                // Aspect is set here too: the first composited frame can
                // precede the render-loop hook, and Aspect=1 flattens arcs.
                ((ProcessingGpuEffect)Effect).Aspect = width / height;
                dc.DrawRectangle(BaseFill, null, new Rect(0, 0, width, height));
                return;
            }

            switch (ProcessingCatalog.CpuTwin(Mode))
            {
                case ProcessingStyle.WaveSweep:
                    DrawWaveSweep(dc, width, height);
                    return;
                case ProcessingStyle.CometOrbit:
                    DrawCometOrbit(dc, width, height);
                    return;
                case ProcessingStyle.ShimmerBar:
                    DrawShimmerBar(dc, width, height);
                    return;
                case ProcessingStyle.CounterArcs:
                    DrawCounterArcs(dc, width, height);
                    return;
                case ProcessingStyle.VoiceRipples:
                    DrawVoiceRipples(dc, width, height);
                    return;
            }
            return;
        }

        switch (Mode)
        {
            case ProcessingStyle.WaveSweep:
                DrawWaveSweep(dc, width, height);
                break;
            case ProcessingStyle.CometOrbit:
                DrawCometOrbit(dc, width, height);
                break;
            case ProcessingStyle.ShimmerBar:
                DrawShimmerBar(dc, width, height);
                break;
            case ProcessingStyle.CounterArcs:
                DrawCounterArcs(dc, width, height);
                break;
            case ProcessingStyle.VoiceRipples:
                DrawVoiceRipples(dc, width, height);
                break;
        }
    }

    void DrawWaveSweep(DrawingContext dc, double width, double height)
    {
        const int segments = 56;
        var midY = height / 2;
        var amp = height * 0.17f;
        var scanX = (_phase * 1.4f - 0.2f) * (float)width;
        var window = width * 0.24;

        Point PointAt(int i)
        {
            var x = (float)(i / (double)(segments - 1) * width);
            var env = MathF.Sin(MathF.PI * x / (float)width);
            var wave = MathF.Sin(2f * MathF.PI * (x / (float)width * 3f + _phase * 4f))
                     + 0.6f * MathF.Sin(2f * MathF.PI * (x / (float)width * 5f - _phase * 3f));
            return new Point(x, midY + amp * env * wave * 0.8f);
        }

        for (int i = 0; i < segments - 1; i++)
        {
            var a = PointAt(i);
            var b = PointAt(i + 1);
            var mid = (a.X + b.X) / 2;
            var distance = Math.Abs(mid - scanX);
            if (distance < window)
            {
                var alpha = (byte)(0xE0 * (1f - distance / window));
                var pen = new Pen(MakeFrozen((uint)alpha << 24 | _peakRgb), 2.2);
                pen.Freeze();
                dc.DrawLine(pen, a, b);
            }
            else
            {
                dc.DrawLine(_basePen, a, b);
            }
        }

        var linePen = new Pen(MakeFrozen(0x40u << 24 | _glowRgb), 1.2);
        linePen.Freeze();
        dc.DrawLine(linePen, new Point(scanX, height * 0.16), new Point(scanX, height * 0.84));
    }

    void DrawCometOrbit(DrawingContext dc, double width, double height)
    {
        var center = new Point(width / 2, height / 2);
        var radius = Math.Min(width, height) * 0.30f;
        const int dots = 11;

        for (int i = dots - 1; i >= 0; i--)
        {
            var tail = i / (float)dots;
            // Two turns per phase cycle: the %1 phase wrap becomes seamless (a
            // whole number of orbits), and the head lands where the 0.30-turn
            // tail trail points already sit, so the loop never visibly resets.
            var angle = 2f * MathF.PI * (_phase * 2f - tail * 0.30f);
            var strength = (1f - tail) * (1f - tail);
            var size = 1.3f + 3.4f * strength;
            var brush = MakeFrozen((uint)(0xE0 * strength) << 24 | _peakRgb);
            dc.DrawEllipse(brush, null,
                new Point(center.X + radius * MathF.Cos(angle), center.Y + radius * 0.82f * MathF.Sin(angle)),
                size, size);
        }
    }

    void DrawShimmerBar(DrawingContext dc, double width, double height)
    {
        var left = width * 0.18;
        var barWidth = width * 0.64;
        var centerY = height / 2;
        const float half = 2.8f;

        dc.DrawRoundedRectangle(_trackBrush, null,
            new Rect(left, centerY - half, barWidth, half * 2), half, half);

        const int segments = 26;
        var segment = barWidth / segments;
        var shimmer = (_phase * 1.5f + 0.25f) % 1.5f - 0.25f;

        for (int i = 0; i < segments; i++)
        {
            var position = (i + 0.5f) / segments;
            var distance = (position - shimmer) / 0.17f;
            var strength = MathF.Exp(-distance * distance);
            if (strength < 0.04f)
                continue;

            var brush = MakeFrozen((uint)(0xD8 * strength) << 24 | _peakRgb);
            var x = left + position * barWidth - segment / 2;
            dc.DrawRoundedRectangle(brush, null,
                new Rect(x, centerY - half, segment + 0.8, half * 2), half, half);
        }
    }

    void DrawCounterArcs(DrawingContext dc, double width, double height)
    {
        var center = new Point(width / 2, height / 2);
        var radius = Math.Min(width, height) * 0.34f;

        // Whole turns and whole beats per phase cycle: the %1 phase wrap must
        // land every oscillator back on itself, or the arcs visibly snap.
        DrawArc(dc, center, radius,
            _phase * 2f * MathF.PI, 2.4f,
            0xB8u << 24 | _hue0, 2.4);
        DrawArc(dc, center, radius * 0.62f,
            3.1f - _phase * 2f * MathF.PI, 1.7f,
            0xA0u << 24 | _hueMid, 2.0);

        var pulse = 0.4f + 0.35f * MathF.Sin(_phase * 4f * MathF.PI);
        var core = MakeFrozen((uint)(0xD0 * pulse) << 24 | _peakRgb);
        dc.DrawEllipse(core, null, center, 3.2f, 3.2f);
    }

    static void DrawArc(DrawingContext dc, Point center, double radius, double startAngle, double sweep, uint argb, double thickness)
    {
        var start = new Point(center.X + radius * Math.Cos(startAngle), center.Y + radius * Math.Sin(startAngle));
        var end = new Point(center.X + radius * Math.Cos(startAngle + sweep), center.Y + radius * Math.Sin(startAngle + sweep));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Counterclockwise, true, false);
        }
        geometry.Freeze();

        var pen = new Pen(MakeFrozen(argb), thickness);
        pen.StartLineCap = PenLineCap.Round;
        pen.EndLineCap = PenLineCap.Round;
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    void DrawVoiceRipples(DrawingContext dc, double width, double height)
    {
        var center = new Point(width / 2, height / 2);
        for (int i = 0; i < 3; i++)
        {
            var t = (_phase * 0.8f + i / 3f) % 1f;
            var alpha = (byte)(0xA0 * (1f - t));
            var pen = new Pen(MakeFrozen((uint)alpha << 24 | Palette.HueAt(t)), 1.7);
            pen.Freeze();
            dc.DrawEllipse(null, pen, center, width * 0.05f + t * width * 0.40f, height * 0.06f + t * height * 0.36f);
        }

        var core = MakeFrozen(0xC0u << 24 | _peakRgb);
        dc.DrawEllipse(core, null, center, 3.4f, 3.4f);
    }

    void BuildBrushes()
    {
        var palette = Palette ?? VisualizerPaletteCatalog.Default;
        _peakRgb = palette.Peak;
        _glowRgb = palette.Glow;
        _hue0 = palette.HueAt(0.0);
        _hueMid = palette.HueAt(0.5);
        _trackBrush = MakeFrozen(palette.TrackArgb);

        _basePen = new Pen(MakeFrozen(0x66u << 24 | _hueMid), 1.6);
        _basePen.Freeze();

        foreach (var effect in _gpu.Values)
        {
            effect.PeakColor = PeakColor();
            effect.MidColor = MidColor();
        }
    }

    Color PeakColor() => Color.FromRgb(
        (byte)(_peakRgb >> 16), (byte)(_peakRgb >> 8), (byte)_peakRgb);

    Color MidColor() => Color.FromRgb(
        (byte)(_hueMid >> 16), (byte)(_hueMid >> 8), (byte)_hueMid);

    static SolidColorBrush MakeFrozen(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }
}
