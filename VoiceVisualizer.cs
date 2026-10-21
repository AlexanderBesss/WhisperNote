using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using WhisperNote.Config;
using WhisperNote.Services;

namespace WhisperNote;

// Draws the live mic signal behind the recording pill. One control, nine styles;
// it redraws at the configured rate (30/60/120 FPS, 30 by default) and only while
// a snapshot actually changed, so an idle
// overlay costs nothing and a hidden overlay redraws never. The scrolling styles
// keep their own frame history in this control, so switching style resets it.
public sealed class VoiceVisualizer : FrameworkElement
{
    // All animation step sizes below are authored against the 30 Hz nominal
    // rate; the snapshot pass rescales them by the measured frame time, so a
    // 60/120 FPS setting renders smoother motion, never faster motion.
    const double NominalFrameMs = 1000.0 / VisualizerFpsCatalog.NominalFps;

    public static readonly DependencyProperty FrameFpsProperty =
        DependencyProperty.Register(
            nameof(FrameFps),
            typeof(int),
            typeof(VoiceVisualizer),
            new PropertyMetadata(VisualizerFpsCatalog.DefaultFps, OnPresentationChanged));

    public int FrameFps
    {
        get => (int)GetValue(FrameFpsProperty);
        set => SetValue(FrameFpsProperty, value);
    }

    const double PeakDecay = 0.96;

    // Single voice blob (Pulse rings): base circle at idle, breathing with the
    // overall level, and its outline pushed into lobes by the live spectrum.
    // Fractions keep the loudest lobe inside the pill (see DrawPulseRings).
    const float BlobIdleFraction = 0.34f;
    const float BlobVoiceFraction = 0.28f;
    const float BlobLobeFraction = 0.52f;

    const int FieldParticleCount = 1500;
    const int FieldColorSteps = 8;
    const int FieldAlphaSteps = 6;
    const float FieldTilt = -0.42f;

    const int WaveLines = 5;
    const int WaveHarmonics = 4;
    const int WaveSamples = 64;

    const int CityRows = 20;
    const int CityAlphaSteps = 6;

    // Built once per process: the shell lattice and the city grain jitter never
    // depend on the palette or the signal, only their shading does.
    static readonly SphereParticle[] FieldSphere =
        ParticleFieldGeometry.BuildSphere(FieldParticleCount, SpectrumAnalyzer.BandCount);
    static readonly GrainOffset[] CityGrains =
        ParticleFieldGeometry.BuildGrainPattern(SpectrumAnalyzer.BandCount * CityRows * 2, 0x50414C);

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
    SolidColorBrush _trackBrush = null!;
    SolidColorBrush _peakBrush = null!;
    Pen _tracePen = null!;
    RadialGradientBrush _coreBrush = null!;
    SolidColorBrush[] _fieldBrushes = new SolidColorBrush[FieldColorSteps * FieldAlphaSteps];
    Pen[] _wavePens = new Pen[WaveLines * 2];
    SolidColorBrush[] _cityBrushes = new SolidColorBrush[SpectrumAnalyzer.BandCount * CityAlphaSteps];
    SolidColorBrush[] _cityReflectionBrushes = new SolidColorBrush[SpectrumAnalyzer.BandCount * CityAlphaSteps];
    SolidColorBrush _cityGroundBrush = null!;
    Color _rippleRgb;

    readonly float[] _drawnLevels = new float[SpectrumAnalyzer.BandCount];
    readonly float[] _drawnWave = new float[SpectrumAnalyzer.WaveformPoints];

    // Scrolling-style history, oldest first: one scalar per frame for the trace.
    readonly float[] _traceHistory = new float[VisualizerLayout.TraceColumns];

    // The voice blob is drawn from the live snapshot with frozen brushes; only
    // its geometry changes per frame, nothing animates between frames.
    Pen _blobPen = null!;
    SolidColorBrush _blobFill = null!;

    // Animation clocks for the particle styles; they only tick while their own
    // style is drawn, so hidden styles never advance or animate.
    float _fieldPhase;
    float _wavePhase;
    float _cityPhase;

    double _drawnLevel;
    double _drawnPeak;
    readonly FramePacer _pacer = new(NominalFrameMs);

    // Frame-time clock: _frameSpeed is the measured frame time in nominal (30 Hz)
    // frames; _nominalMs carries the leftover toward the next 30 Hz tick.
    double _nominalMs;
    double _frameSpeed = 1;
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

        var now = Stopwatch.GetTimestamp();
        if (!_pacer.ShouldRender(FrameFps, now))
            return;

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

        // Wall-clock frame time drives everything that must not speed up when
        // the configured FPS rises: step sizes authored at 30 Hz are multiplied
        // by _frameSpeed, and history/onset samples stay on 30 Hz nominal ticks.
        var now = Stopwatch.GetTimestamp();
        _frameSpeed = _pacer.FrameSpeed(now);
        var elapsed = _frameSpeed * NominalFrameMs;

        _nominalMs += elapsed;
        var nominalTick = false;
        while (_nominalMs >= NominalFrameMs)
        {
            _nominalMs -= NominalFrameMs;
            nominalTick = true;
        }

        // Peak-hold is a render-side VU decay: the analyzer reports the smoothed
        // level only, which is what the meter fill follows.
        _drawnPeak = Math.Max(_drawnLevel, _drawnPeak * Math.Pow(PeakDecay, _frameSpeed));
        if (_drawnPeak < 0.01)
            _drawnPeak = 0;

        AdvanceStyleHistory(_frameSpeed, nominalTick);
    }

    // Only the style being drawn keeps a history; the others stay cleared so a
    // later switch animates from a blank frame, never from stale motion.
    void AdvanceStyleHistory(double speed, bool nominalTick)
    {
        switch (Mode)
        {
            case VisualizerStyle.Oscilloscope:
                if (!nominalTick)
                    break;
                Array.Copy(_traceHistory, 1, _traceHistory, 0, VisualizerLayout.TraceColumns - 1);
                // The newest waveform sample is the rightmost envelope point.
                _traceHistory[^1] = _drawnWave[^1];
                break;

            case VisualizerStyle.ForceField:
                _fieldPhase += 0.026f * (float)speed;
                break;

            case VisualizerStyle.LivingWave:
                _wavePhase += 0.055f * (float)speed;
                break;

            case VisualizerStyle.FrequencyCity:
                _cityPhase += 0.09f * (float)speed;
                break;
        }
    }

    void ResetSnapshot()
    {
        Array.Clear(_drawnLevels);
        Array.Clear(_drawnWave);
        Array.Clear(_traceHistory);
        _pacer.Reset();
        _nominalMs = 0;
        _frameSpeed = 1;
        _fieldPhase = 0;
        _wavePhase = 0;
        _cityPhase = 0;
        _drawnLevel = _drawnPeak = 0;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = RenderSize.Width;
        var height = RenderSize.Height;
        if (width <= 0 || height <= 0)
            return;

        RefreshSnapshot();

        switch (Mode)
        {
            case VisualizerStyle.Waveform:
                DrawWaveform(dc, width, height);
                break;
            case VisualizerStyle.Level:
                DrawMeter(dc, width, height);
                break;
            case VisualizerStyle.Oscilloscope:
                DrawTrace(dc, width, height);
                break;
            case VisualizerStyle.PulseRings:
                DrawPulseRings(dc, width, height);
                break;
            case VisualizerStyle.MatrixDots:
                DrawDots(dc, width, height);
                break;
            case VisualizerStyle.ForceField:
                DrawForceField(dc, width, height);
                break;
            case VisualizerStyle.LivingWave:
                DrawLivingWave(dc, width, height);
                break;
            case VisualizerStyle.FrequencyCity:
                DrawCity(dc, width, height);
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

    void DrawTrace(DrawingContext dc, double width, double height)
    {
        // The trace reuses the envelope mapping: history is one scalar per frame,
        // oldest sample at the left edge, newest at the right.
        var points = VisualizerLayout.BuildEnvelope(width, height, _traceHistory);
        if (points.Count < 2)
            return;

        var center = VisualizerLayout.InsetY + (height - 2 * VisualizerLayout.InsetY) / 2;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(points[0].X, center - points[0].HalfHeight), false, false);
            for (int i = 1; i < points.Count; i++)
                context.LineTo(new Point(points[i].X, center - points[i].HalfHeight), true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, _tracePen, geometry);

        var head = points[^1];
        dc.DrawEllipse(_peakBrush, null, new Point(head.X, center - head.HalfHeight), 2.5, 2.5);
    }

    // One blob pinned to the pill centre: the 20 live bands are wrapped around a
    // circle and each pushes the outline outward at its own direction, so the
    // silhouette morphs with the voice instead of leaving ripples behind.
    // Nothing eases or trails between frames; the analyzer's fast attack is what
    // makes it snap while speaking.
    void DrawPulseRings(DrawingContext dc, double width, double height)
    {
        var centerX = width / 2;
        var centerY = height / 2;

        // Idle sits at a third of the half-height; full voice adds 0.28 more,
        // and the loudest possible lobe (0.52 over the base) still lands inside
        // the pill, so the blob never clips against the edges.
        var halfSpan = (float)Math.Min(width, height) / 2f - 2f;
        var baseRadius = halfSpan * (BlobIdleFraction + BlobVoiceFraction * (float)_drawnLevel);
        var vertices = VisualizerLayout.BuildFrequencyRing(
            centerX, centerY, baseRadius, baseRadius * BlobLobeFraction, _drawnLevels);
        if (vertices.Count < 3)
            return;

        // Midpoint + quadratic passes make the outline an organic outline instead
        // of twenty straight chords.
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(Midpoint(vertices[^1], vertices[0]), false, true);
            for (int i = 0; i < vertices.Count; i++)
            {
                var current = vertices[i];
                context.QuadraticBezierTo(
                    new Point(current.X, current.Y),
                    Midpoint(current, vertices[(i + 1) % vertices.Count]),
                    true, true);
            }
        }
        geometry.Freeze();

        dc.DrawGeometry(_blobFill, _blobPen, geometry);

        var core = 4 + 16 * (float)_drawnLevel;
        dc.DrawEllipse(_coreBrush, null, new Point(centerX, centerY), core, core);
    }

    static Point Midpoint(VisualizerVertex a, VisualizerVertex b) =>
        new Point((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);

    static Point Midpoint(Point a, Point b) =>
        new Point((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);

    void DrawDots(DrawingContext dc, double width, double height)
    {
        foreach (var dot in VisualizerLayout.BuildDotGrid(width, height))
        {
            var level = dot.Column < _drawnLevels.Length ? Math.Clamp(_drawnLevels[dot.Column], 0f, 1f) : 0f;
            var lit = (int)Math.Round(level * VisualizerLayout.DotRowsPerSide);
            dc.DrawEllipse(dot.Row < lit ? _fillBrush : _trackBrush, null,
                new Point(dot.X, dot.Y), dot.Radius, dot.Radius);
        }
    }

    // Reference: a particle shell where loud frequency bands bulge outward at
    // their own longitude, the surface ripples, and depth shades the far side.
    void DrawForceField(DrawingContext dc, double width, double height)
    {
        var centerX = width / 2;
        var centerY = height / 2;
        // Base radius leaves room for the 1.48x loudest bulge so splats stay
        // round instead of clipping against the pill's top and bottom.
        var radius = (float)height * 0.30f * (0.9f + 0.22f * (float)_drawnLevel);
        var reach = radius * 1.5f;
        // Splat width tracks the sphere so the lattice reads as a continuous mesh
        // rather than a dither of isolated dots. It must exceed the projected
        // nearest-neighbour spacing (sphereRadius*sqrt(2*pi/count), sparsest at the
        // disc centre) plus the bulge wobble; below that black holes appear even
        // when silent.
        var splat = radius * 0.12f;

        var cosSpin = MathF.Cos(_fieldPhase);
        var sinSpin = MathF.Sin(_fieldPhase);
        var cosTilt = MathF.Cos(FieldTilt);
        var sinTilt = MathF.Sin(FieldTilt);

        foreach (var particle in FieldSphere)
        {
            ParticleFieldGeometry.RotateProject(particle, cosSpin, sinSpin, cosTilt, sinTilt,
                out var ux, out var uy, out var depth);

            var level = Math.Clamp(_drawnLevels[particle.Band], 0f, 1f);
            var bulge = 1f + 0.38f * level +
                0.07f * MathF.Sin(4.5f * particle.Latitude + 2.4f * _fieldPhase + 1.5f * particle.Longitude);

            var x = centerX + radius * bulge * ux;
            var y = centerY - radius * bulge * uy;

            var colorStep = Math.Clamp((int)((centerY + reach - y) / (2 * reach) * (FieldColorSteps - 1)), 0, FieldColorSteps - 1);
            var alphaStep = Math.Clamp((int)((depth + 1f) * 0.5f * (FieldAlphaSteps - 1)), 0, FieldAlphaSteps - 1);

            // Particles seen edge-on pile up along the silhouette; brightening
            // that band is what makes the reference art glow at the shell edge.
            if (MathF.Abs(depth) < 0.3f && ux * ux + uy * uy > 0.55f)
                alphaStep = Math.Min(FieldAlphaSteps - 1, alphaStep + 2);

            dc.DrawRectangle(_fieldBrushes[colorStep * FieldAlphaSteps + alphaStep], null,
                new Rect(x - splat * 0.5f, y - splat * 0.5f, splat, splat));
        }
    }

    // Reference: several luminous lines flowing past each other; the voice's
    // harmonic content drives the standing waves, the phases make them travel.
    void DrawLivingWave(DrawingContext dc, double width, double height)
    {
        var drawableW = (float)width - 2 * VisualizerLayout.InsetX;
        var drawableH = (float)height - 2 * VisualizerLayout.InsetY;
        if (drawableW <= 0 || drawableH <= 0)
            return;

        var maxHalf = drawableH / 2f;
        var centerY = VisualizerLayout.InsetY + maxHalf;

        var harmonic = new float[WaveHarmonics];
        for (int h = 0; h < WaveHarmonics; h++)
        {
            var sum = 0f;
            for (int band = h * SpectrumAnalyzer.BandCount / WaveHarmonics;
                 band < (h + 1) * SpectrumAnalyzer.BandCount / WaveHarmonics; band++)
                sum += _drawnLevels[band];
            harmonic[h] = sum / (SpectrumAnalyzer.BandCount / WaveHarmonics);
        }

        for (int line = 0; line < WaveLines; line++)
        {
            var shape = 0.55f + 0.45f * MathF.Abs(MathF.Sin(line * 1.1f));
            var sign = line % 2 == 0 ? 1f : -1f;

            var points = new Point[WaveSamples];
            for (int i = 0; i < WaveSamples; i++)
            {
                var u = (float)i / (WaveSamples - 1) * 2f * MathF.PI;

                var harmonicSum = 0f;
                for (int h = 0; h < WaveHarmonics; h++)
                {
                    var travel = (h % 2 == 0 ? 1f : -1f) * _wavePhase * (1f + 0.35f * h);
                    harmonicSum += harmonic[h] * MathF.Sin((h + 1) * u + travel + line * 0.85f + h * 1.9f);
                }

                // The live envelope carries the voice's peaks; the travelling
                // harmonics underneath keep the lines flowing between words.
                // A 1-2-1 kernel tames single-sample spikes so even noisy
                // input produces fluid lines, matching the reference art.
                var raw = i < _drawnWave.Length ? _drawnWave[i] : 0f;
                var before = i > 0 && i - 1 < _drawnWave.Length ? _drawnWave[i - 1] : raw;
                var after = i + 1 < _drawnWave.Length ? _drawnWave[i + 1] : raw;
                var envelope = (before + 2 * raw + after) / 4f;
                var y = centerY + sign * shape * maxHalf
                    * (0.5f * harmonicSum + 0.75f * (envelope - 0.42f));

                // At silence the lines fan apart instead of stacking on one
                // flat line, so the style never looks switched off.
                y += (line - (WaveLines - 1) / 2f) * (1.6f + 2.2f * (1f - (float)_drawnLevel));

                points[i] = new Point(
                    VisualizerLayout.InsetX + (float)i / (WaveSamples - 1) * drawableW, y);
            }

            // Midpoint quadratic Bézier smoothing: the envelope is only 64
            // samples, and straight segments between them read as jagged.
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(points[0], false, false);
                for (int i = 1; i < WaveSamples; i++)
                    context.QuadraticBezierTo(points[i - 1], Midpoint(points[i - 1], points[i]), true, false);
                context.LineTo(points[WaveSamples - 1], true, false);
            }
            geometry.Freeze();

            // The wide faint pass under the thin bright one fakes the neon bloom.
            dc.DrawGeometry(null, _wavePens[line * 2], geometry);
            dc.DrawGeometry(null, _wavePens[line * 2 + 1], geometry);
        }
    }

    // Reference: each band is a tower of twinkling grains with a beacon light on
    // its roof, all mirrored in a dim reflection under the street line.
    void DrawCity(DrawingContext dc, double width, double height)
    {
        var drawableW = (float)width - 2 * VisualizerLayout.InsetX;
        var drawableH = (float)height - 2 * VisualizerLayout.InsetY;
        if (drawableW <= 0 || drawableH <= 0)
            return;

        // The mirror gets real depth: the skyline owns two thirds of the pill.
        var baselineY = VisualizerLayout.InsetY + drawableH * 0.66f;
        var maxBar = (baselineY - VisualizerLayout.InsetY) * 0.94f;
        var rowHeight = maxBar / CityRows;
        var cellWidth = drawableW / SpectrumAnalyzer.BandCount;
        var grainSize = Math.Min(2.4f, cellWidth * 0.38f);
        var bottom = VisualizerLayout.InsetY + drawableH;

        dc.DrawRectangle(_cityGroundBrush, null, new Rect(VisualizerLayout.InsetX, baselineY, drawableW, 1));

        for (int band = 0; band < SpectrumAnalyzer.BandCount; band++)
        {
            var level = Math.Clamp(_drawnLevels[band], 0f, 1f);
            // Towers keep a short base even in silence: an empty skyline, not a
            // switched-off meter.
            var barHeight = Math.Max(level, 0.05f) * maxBar;

            for (int row = 0; row < CityRows; row++)
            {
                for (int sub = 0; sub < 2; sub++)
                {
                    var grain = CityGrains[(band * CityRows + row) * 2 + sub];
                    var grainY = baselineY - (row + grain.DY) * rowHeight;
                    if (grainY < baselineY - barHeight)
                        continue;

                    var sparkle = 0.55f + 0.45f * MathF.Sin(grain.Phase + _cityPhase * (1.3f + grain.DY));
                    var step = Math.Clamp((int)(sparkle * (CityAlphaSteps - 1)), 1, CityAlphaSteps - 1);
                    var grainX = VisualizerLayout.InsetX + (band + 0.25f + 0.5f * sub + grain.DX * 0.9f) * cellWidth;

                    dc.DrawRectangle(_cityBrushes[band * CityAlphaSteps + step], null,
                        new Rect(grainX - grainSize / 2, grainY - grainSize / 2, grainSize, grainSize));

                    var reflectionY = baselineY + (baselineY - grainY) * 0.9f;
                    if (reflectionY <= bottom)
                    {
                        var reflectionStep = Math.Max(1, step - 3);
                        dc.DrawRectangle(_cityReflectionBrushes[band * CityAlphaSteps + reflectionStep], null,
                            new Rect(grainX - grainSize / 2, reflectionY - grainSize / 2, grainSize, grainSize));
                    }
                }
            }

            if (barHeight > rowHeight * 1.4f)
                dc.DrawEllipse(_peakBrush, null,
                    new Point(VisualizerLayout.InsetX + (band + 0.5f) * cellWidth, baselineY - barHeight), 1.6, 1.6);
        }
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
        _trackBrush = MakeFrozen(palette.TrackArgb);
        _peakBrush = MakeFrozen(palette.PeakArgb);

        _tracePen = new Pen(_fillBrush, 2.0)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        _tracePen.Freeze();

        _rippleRgb = _peakBrush.Color;

        // The blob keeps a constant ink; only its geometry moves, so the pen and
        // fill are built once per palette instead of per frame.
        _blobFill = new SolidColorBrush(Color.FromArgb(0x1F, _rippleRgb.R, _rippleRgb.G, _rippleRgb.B));
        _blobFill.Freeze();
        _blobPen = new Pen(new SolidColorBrush(Color.FromArgb(0xD8, _rippleRgb.R, _rippleRgb.G, _rippleRgb.B)), 1.8);
        _blobPen.Freeze();

        _coreBrush = new RadialGradientBrush(
            ToColor(0xC8000000u | palette.Glow & 0x00FFFFFFu),
            Color.FromArgb(0, 0, 0, 0));
        _coreBrush.Freeze();

        // Force field: a vertical colour ramp times a depth alpha ramp, quantised
        // so every particle shares frozen brushes with no per-frame allocs.
        for (int colour = 0; colour < FieldColorSteps; colour++)
        {
            var hue = palette.HueAt((double)colour / (FieldColorSteps - 1)) & 0x00FFFFFFu;
            for (int depth = 0; depth < FieldAlphaSteps; depth++)
                _fieldBrushes[colour * FieldAlphaSteps + depth] = MakeFrozen((uint)(0x55 + depth * 0x1C) << 24 | hue);
        }

        // Living waveform: a wide faint pass under a thin bright one fakes neon bloom.
        for (int line = 0; line < WaveLines; line++)
        {
            var hue = palette.HueAt((double)line / (WaveLines - 1)) & 0x00FFFFFFu;
            var glow = new Pen(MakeFrozen(0x3A000000u | hue), 4.0);
            glow.Freeze();
            var core = new Pen(MakeFrozen(0xC8000000u | hue), 1.5);
            core.Freeze();
            _wavePens[line * 2] = glow;
            _wavePens[line * 2 + 1] = core;
        }

        // Frequency city: tower hues walk the palette ramp; twinkle picks one of
        // a handful of shared alpha steps, reflection a dimmed copy of the same.
        for (int band = 0; band < SpectrumAnalyzer.BandCount; band++)
        {
            var hue = palette.HueAt((double)band / (SpectrumAnalyzer.BandCount - 1)) & 0x00FFFFFFu;
            for (int step = 1; step < CityAlphaSteps; step++)
            {
                uint alpha = (uint)(0x18 + step * 0x2C);
                _cityBrushes[band * CityAlphaSteps + step] = MakeFrozen(alpha << 24 | hue);
                _cityReflectionBrushes[band * CityAlphaSteps + step] = MakeFrozen((uint)(alpha * 0.4) << 24 | hue);
            }

            _cityBrushes[band * CityAlphaSteps] = MakeFrozen(0);
            _cityReflectionBrushes[band * CityAlphaSteps] = MakeFrozen(0);
        }

        // A barely-there ground line reads as the mirror edge without the
        // hard rule _trackBrush would draw across the pill.
        _cityGroundBrush = MakeFrozen(0x12000000u | palette.HueAt(0.0));
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
