using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using WhisperNote.Config;

namespace WhisperNote;

public sealed class QueueBar : FrameworkElement
{
    const double NominalFrameMs = 1000.0 / VisualizerFpsCatalog.NominalFps;
    const double SquareSize = 6;
    const double Pitch = 9;
    const int MaxSquares = 5;
    const int PulseSteps = 5;
    const int GhostSteps = 5;
    const double DepartLengthMs = 300;
    const double PopLengthMs = 160;

    public static readonly DependencyProperty SquaresProperty =
        DependencyProperty.Register(
            nameof(Squares),
            typeof(int),
            typeof(QueueBar),
            new PropertyMetadata(0, OnSquaresChanged));

    public static readonly DependencyProperty PaletteProperty =
        DependencyProperty.Register(
            nameof(Palette),
            typeof(VisualizerPalette),
            typeof(QueueBar),
            new PropertyMetadata(VisualizerPaletteCatalog.Default, OnPaletteChanged));

    public static readonly DependencyProperty FrameFpsProperty =
        DependencyProperty.Register(
            nameof(FrameFps),
            typeof(int),
            typeof(QueueBar),
            new PropertyMetadata(VisualizerFpsCatalog.DefaultFps));

    public int Squares
    {
        get => (int)GetValue(SquaresProperty);
        set => SetValue(SquaresProperty, value);
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

    static void OnSquaresChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (QueueBar)d;
        var old = Math.Clamp((int)e.OldValue, 0, MaxSquares);
        var now = Math.Clamp((int)e.NewValue, 0, MaxSquares);
        if (old == now)
            return;

        // The change direction picks which transition plays; it starts from the
        // slot the departing/arriving square would occupy in its old layout.
        control._animKind = now < old ? 1 : 2;
        control._animOldCount = old;
        control._animStart = Stopwatch.GetTimestamp();
        control.InvalidateVisual();
    }

    static void OnPaletteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (QueueBar)d;
        control.BuildBrushes();
        control.InvalidateVisual();
    }

    float _phase;
    readonly FramePacer _pacer = new(NominalFrameMs);
    double _frameSpeed = 1;
    bool _subscribed;

    int _animKind;
    int _animOldCount;
    long _animStart;

    readonly SolidColorBrush[] _pulse = new SolidColorBrush[PulseSteps];
    readonly SolidColorBrush[] _ghost = new SolidColorBrush[GhostSteps];
    SolidColorBrush _waiting = null!;

    public QueueBar()
    {
        BuildBrushes();
        IsVisibleChanged += (_, _) => UpdateSubscription();
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => Detach();
    }

    void BuildBrushes()
    {
        var palette = Palette;
        var peak = palette.Peak & 0x00FFFFFFu;
        for (int i = 0; i < PulseSteps; i++)
            _pulse[i] = MakeFrozen((uint)(0x60 + i * 0x27) << 24 | peak);
        for (int i = 0; i < GhostSteps; i++)
            _ghost[i] = MakeFrozen((uint)(0xF0 - i * 0x30) << 24 | peak);
        _waiting = MakeFrozen(0x99u << 24 | palette.HueAt(0.5));
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
        if (!_pacer.ShouldRender(FrameFps, now))
            return;

        if (Squares == 0 && _animKind == 0)
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

        var count = Math.Clamp(Squares, 0, MaxSquares);
        var centerY = height / 2;
        var y = centerY - SquareSize / 2;

        // Squares are pinned to the right edge so surviving jobs never move;
        // k = 0 is the newest waiting clip, k = count-1 the job in flight.
        for (int k = 0; k < count; k++)
        {
            var x = width - SquareSize - k * Pitch;
            var isLatest = _animKind == 2 && k == 0;
            if (isLatest)
            {
                var progress = AnimationProgress(now);
                if (progress >= 1)
                {
                    EndAnimation();
                    isLatest = false;
                }
                else
                {
                    var step = (int)(progress * (PulseSteps - 1));
                    var slide = (1 - progress) * 12;
                    dc.DrawRoundedRectangle(_pulse[step], null,
                        new Rect(x + slide, y, SquareSize, SquareSize), 1.5, 1.5);
                    continue;
                }
            }

            var brush = k == count - 1
                ? _pulse[PulseIndex()]
                : _waiting;
            dc.DrawRoundedRectangle(brush, null,
                new Rect(x, y, SquareSize, SquareSize), 1.5, 1.5);
        }

        // A finished job slides off the bar from the slot the old in-flight
        // square occupied, so survivors never move.
        if (_animKind == 1)
        {
            var progress = AnimationProgress(now);
            if (progress >= 1)
                EndAnimation();
            else
            {
                var fromX = width - SquareSize - (_animOldCount - 1) * Pitch;
                var step = (int)(progress * (GhostSteps - 1));
                dc.DrawRoundedRectangle(_ghost[step], null,
                    new Rect(fromX - progress * 16, y, SquareSize, SquareSize), 1.5, 1.5);
            }
        }
    }

    double AnimationProgress(long now) =>
        Math.Clamp(FramePacer.MsBetween(_animStart, now) / (_animKind == 1 ? DepartLengthMs : PopLengthMs), 0, 1);

    void EndAnimation()
    {
        _animKind = 0;
    }

    int PulseIndex()
    {
        var pulse = 0.5 + 0.5 * Math.Sin(_phase * 2 * Math.PI);
        return Math.Clamp((int)(pulse * (PulseSteps - 1) + 0.5f), 0, PulseSteps - 1);
    }

    static SolidColorBrush MakeFrozen(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }
}
