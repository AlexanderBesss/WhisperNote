using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using WhisperNote.Config;

namespace WhisperNote;

// Flowing-colour outline drawn over the pill edge while the overlay window is
// visible, which is exactly while recording or processing. A reflecting
// gradient slid along the edge reads as colour travelling from one side of the
// pill to the other; the wide faint stroke behind the bright one is the glow.
// Radius and the 20-unit margin in XAML must track the pill Border.
public sealed class GlowBorder : FrameworkElement
{
    const double NominalFrameMs = 1000.0 / VisualizerFpsCatalog.NominalFps;
    const double Radius = 16;
    const double HaloWidth = 7;
    const double StrokeWidth = 2.4;
    const double SlideCyclesPerSecond = 0.34;

    public static readonly DependencyProperty PaletteProperty =
        DependencyProperty.Register(
            nameof(Palette),
            typeof(VisualizerPalette),
            typeof(GlowBorder),
            new PropertyMetadata(VisualizerPaletteCatalog.Default, OnPaletteChanged));

    public static readonly DependencyProperty FrameFpsProperty =
        DependencyProperty.Register(
            nameof(FrameFps),
            typeof(int),
            typeof(GlowBorder),
            new PropertyMetadata(VisualizerFpsCatalog.DefaultFps));

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

    static void OnPaletteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (GlowBorder)d;
        control.BuildBrushes();
        control.InvalidateVisual();
    }

    float _phase;
    readonly FramePacer _pacer = new(NominalFrameMs);
    bool _subscribed;

    readonly TranslateTransform _slide = new();
    Pen _haloPen = null!;
    Pen _strokePen = null!;

    public GlowBorder()
    {
        BuildBrushes();
        IsVisibleChanged += (_, _) => UpdateSubscription();
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => Detach();
    }

    void BuildBrushes()
    {
        var palette = Palette;
        var halo = MakeGradient(palette, 0x33);
        var stroke = MakeGradient(palette, 0xE6);
        halo.RelativeTransform = _slide;
        stroke.RelativeTransform = _slide;

        // Pens must stay unfrozen: freezing a pen freezes its brush, and the
        // slide mutates the brushes' shared RelativeTransform every frame.
        _haloPen = new Pen(halo, HaloWidth);
        _strokePen = new Pen(stroke, StrokeWidth);
    }

    static LinearGradientBrush MakeGradient(VisualizerPalette palette, byte alpha)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            SpreadMethod = GradientSpreadMethod.Reflect
        };

        // Reflect spread joins the gradient seamlessly at both ends, so sliding
        // it by whole gradient widths loops without a visible seam or jump.
        brush.GradientStops.Add(new GradientStop(WithAlpha(alpha, palette.HueAt(0.0)), 0.0));
        brush.GradientStops.Add(new GradientStop(WithAlpha(alpha, palette.HueAt(0.25)), 0.25));
        brush.GradientStops.Add(new GradientStop(WithAlpha(alpha, palette.Peak), 0.5));
        brush.GradientStops.Add(new GradientStop(WithAlpha(alpha, palette.HueAt(0.75)), 0.75));
        brush.GradientStops.Add(new GradientStop(WithAlpha(alpha, palette.HueAt(1.0)), 1.0));
        return brush;
    }

    static Color WithAlpha(byte alpha, uint rgb) =>
        Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

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
        var elapsed = _pacer.FrameSpeed(now) * NominalFrameMs;

        _phase = (_phase + (float)(SlideCyclesPerSecond * elapsed / 1000.0)) % 1f;
        _slide.X = -2.0 * _phase;

        var inset = StrokeWidth / 2;
        var radius = Math.Max(1.0, Radius - inset);
        var rect = new Rect(inset, inset, width - 2 * inset, height - 2 * inset);
        dc.DrawRoundedRectangle(null, _haloPen, rect, radius, radius);
        dc.DrawRoundedRectangle(null, _strokePen, rect, radius, radius);
    }
}
