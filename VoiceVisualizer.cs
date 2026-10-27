using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using WhisperNote.Config;
using WhisperNote.Services;

namespace WhisperNote;

public sealed class VoiceVisualizer : FrameworkElement
{
    // Animation steps are authored at the 30 Hz nominal rate; multiply by _frameSpeed so 60/120 FPS renders smoother motion, never faster.
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

    // Blob radius = halfSpan*(0.34 + 0.28*level); a lobe of 0.52 over that base is the loudest that stays inside the pill.
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

    static readonly SolidColorBrush BaseFill = MakeFrozen(0x01FFFFFF);
    readonly Dictionary<VisualizerStyle, VisualizerGpuEffect> _gpu = new();
    readonly WriteableBitmap _audioData = new(96, 4, 96, 96, PixelFormats.Gray8, null);
    readonly ImageBrush _audioBrush = new();
    readonly byte[] _audioBytes = new byte[96 * 4];
    long _gpuClockStart;

    readonly float[] _traceHistory = new float[VisualizerLayout.TraceColumns];

    Pen _blobPen = null!;
    SolidColorBrush _blobFill = null!;

    float _fieldPhase;
    float _wavePhase;
    float _cityPhase;

    double _drawnLevel;
    double _drawnPeak;
    readonly FramePacer _pacer = new(NominalFrameMs);

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

    public VoiceVisualizer()
    {
        _audioBrush.ImageSource = _audioData;
        _audioBrush.Stretch = Stretch.Fill;
        // ShaderEffect needs pixel-shader tier 2; below it no effects are built and the styles must draw their CPU twin.
        if (RenderCapability.Tier >> 16 >= 2)
        {
            foreach (VisualizerStyle style in Enum.GetValues<VisualizerStyle>())
            {
                if (!VisualizerCatalog.IsGpu(style))
                    continue;

                try
                {
                    _gpu[style] = new VisualizerGpuEffect(style, _audioBrush);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"VisualizerGpuEffect({style}) failed to load: {ex.Message}");
                }
            }
        }

        // Must run after the _gpu effects are built: BuildBrushes seeds their palette colors.
        BuildBrushes();

        IsVisibleChanged += (_, _) => UpdateSubscription();
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => Detach();
    }

    static void OnPresentationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var visualizer = (VoiceVisualizer)d;
        visualizer.ResetSnapshot();
        visualizer._gpuClockStart = 0;
        visualizer.Effect = visualizer._gpu.TryGetValue(visualizer.Mode, out var effect)
            ? effect
            : null;
        visualizer.UpdateSubscription();
        visualizer.InvalidateVisual();
    }

    void UpdateSubscription()
    {
        // IsLoaded deliberately not required: the pill can be visible before its loaded pass settles, and a missed frame here would freeze the bars.
        // A GPU style runs the hook even with no Source so the settings preview animates (flat data) without the analyzer.
        var shouldRun = IsVisible
            && PresentationSource.FromVisual(this) != null
            && (Source != null || _gpu.ContainsKey(Mode));
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

        // GPU motion runs at the composition rate and is deliberately not gated by FrameFps.
        if (_gpu.TryGetValue(Mode, out var gpuEffect) && ReferenceEquals(Effect, gpuEffect))
        {
            RefreshSnapshot();
            WriteAudioTexture();
            if (_gpuClockStart == 0)
                _gpuClockStart = now;
            gpuEffect.Time = FramePacer.MsBetween(_gpuClockStart, now) / 1000.0;
            gpuEffect.Level = _drawnLevel;
            gpuEffect.Aux = new Point4D(0, 0, 0, _drawnPeak);
            var size = RenderSize;
            gpuEffect.Aspect = size.Height > 0 ? size.Width / size.Height : 1.0;
            return;
        }

        var source = Source;
        if (source == null)
        {
            Detach();
            return;
        }

        if (!_pacer.ShouldRender(FrameFps, now))
            return;

        InvalidateVisual();
    }

    // Snapshot is pulled here (OnRender), not in the render hook, so a style change, show, or layout pass draws live values.
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

        // History/onset samples tick only on nominal 30 Hz ticks, so a higher FPS never speeds them up.
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

        // Peak-hold is a render-side VU decay: the analyzer reports the smoothed level only.
        _drawnPeak = Math.Max(_drawnLevel, _drawnPeak * Math.Pow(PeakDecay, _frameSpeed));
        if (_drawnPeak < 0.01)
            _drawnPeak = 0;

        AdvanceStyleHistory(_frameSpeed, nominalTick);
    }

    void AdvanceStyleHistory(double speed, bool nominalTick)
    {
        switch (VisualizerCatalog.CpuTwin(Mode))
        {
            case VisualizerStyle.Oscilloscope:
                if (!nominalTick)
                    break;
                Array.Copy(_traceHistory, 1, _traceHistory, 0, VisualizerLayout.TraceColumns - 1);
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

        if (Effect != null)
        {
            // Must draw non-empty content: a ShaderEffect is silently skipped when the element renders nothing.
            // Aspect set here too: the first composited frame can precede the render hook, and Aspect=1 flattens radial geometry.
            ((VisualizerGpuEffect)Effect).Aspect = width / height;
            dc.DrawRectangle(BaseFill, null, new Rect(0, 0, width, height));
            return;
        }

        RefreshSnapshot();

        switch (VisualizerCatalog.CpuTwin(Mode))
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

    void DrawPulseRings(DrawingContext dc, double width, double height)
    {
        var centerX = width / 2;
        var centerY = height / 2;

        var halfSpan = (float)Math.Min(width, height) / 2f - 2f;
        var baseRadius = halfSpan * (BlobIdleFraction + BlobVoiceFraction * (float)_drawnLevel);
        var vertices = VisualizerLayout.BuildFrequencyRing(
            centerX, centerY, baseRadius, baseRadius * BlobLobeFraction, _drawnLevels);
        if (vertices.Count < 3)
            return;

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

    void DrawForceField(DrawingContext dc, double width, double height)
    {
        var centerX = width / 2;
        var centerY = height / 2;
        // height*0.30 leaves headroom for the 1.48x loudest bulge; a bigger base clips splats against the pill top/bottom.
        var radius = (float)height * 0.30f * (0.9f + 0.22f * (float)_drawnLevel);
        var reach = radius * 1.5f;
        // splat must exceed projected neighbour spacing (radius*sqrt(2*pi/count), sparsest at the disc centre) plus the bulge wobble, else black holes appear even when silent.
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

            if (MathF.Abs(depth) < 0.3f && ux * ux + uy * uy > 0.55f)
                alphaStep = Math.Min(FieldAlphaSteps - 1, alphaStep + 2);

            dc.DrawRectangle(_fieldBrushes[colorStep * FieldAlphaSteps + alphaStep], null,
                new Rect(x - splat * 0.5f, y - splat * 0.5f, splat, splat));
        }
    }

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

                var raw = i < _drawnWave.Length ? _drawnWave[i] : 0f;
                var before = i > 0 && i - 1 < _drawnWave.Length ? _drawnWave[i - 1] : raw;
                var after = i + 1 < _drawnWave.Length ? _drawnWave[i + 1] : raw;
                var envelope = (before + 2 * raw + after) / 4f;
                var y = centerY + sign * shape * maxHalf
                    * (0.5f * harmonicSum + 0.75f * (envelope - 0.42f));

                y += (line - (WaveLines - 1) / 2f) * (1.6f + 2.2f * (1f - (float)_drawnLevel));

                points[i] = new Point(
                    VisualizerLayout.InsetX + (float)i / (WaveSamples - 1) * drawableW, y);
            }

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(points[0], false, false);
                for (int i = 1; i < WaveSamples; i++)
                    context.QuadraticBezierTo(points[i - 1], Midpoint(points[i - 1], points[i]), true, false);
                context.LineTo(points[WaveSamples - 1], true, false);
            }
            geometry.Freeze();

            dc.DrawGeometry(null, _wavePens[line * 2], geometry);
            dc.DrawGeometry(null, _wavePens[line * 2 + 1], geometry);
        }
    }

    void DrawCity(DrawingContext dc, double width, double height)
    {
        var drawableW = (float)width - 2 * VisualizerLayout.InsetX;
        var drawableH = (float)height - 2 * VisualizerLayout.InsetY;
        if (drawableW <= 0 || drawableH <= 0)
            return;

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

        _blobFill = new SolidColorBrush(Color.FromArgb(0x1F, _rippleRgb.R, _rippleRgb.G, _rippleRgb.B));
        _blobFill.Freeze();
        _blobPen = new Pen(new SolidColorBrush(Color.FromArgb(0xD8, _rippleRgb.R, _rippleRgb.G, _rippleRgb.B)), 1.8);
        _blobPen.Freeze();

        _coreBrush = new RadialGradientBrush(
            ToColor(0xC8000000u | palette.Glow & 0x00FFFFFFu),
            Color.FromArgb(0, 0, 0, 0));
        _coreBrush.Freeze();

        for (int colour = 0; colour < FieldColorSteps; colour++)
        {
            var hue = palette.HueAt((double)colour / (FieldColorSteps - 1)) & 0x00FFFFFFu;
            for (int depth = 0; depth < FieldAlphaSteps; depth++)
                _fieldBrushes[colour * FieldAlphaSteps + depth] = MakeFrozen((uint)(0x55 + depth * 0x1C) << 24 | hue);
        }

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

        _cityGroundBrush = MakeFrozen(0x12000000u | palette.HueAt(0.0));

        var peak = ToColor(0xFF000000u | palette.Peak);
        var mid = ToColor(0xFF000000u | palette.Glow);
        foreach (var effect in _gpu.Values)
        {
            effect.PeakColor = peak;
            effect.MidColor = mid;
        }
    }

    // Gray8 rows the VizStyles shaders sample: bins, waveform, trace history,
    // then [level, peak]; signed signals ride the 0.5-centered encoding.
    void WriteAudioTexture()
    {
        for (int i = 0; i < SpectrumAnalyzer.BandCount; i++)
            _audioBytes[i] = (byte)(Math.Clamp(_drawnLevels[i], 0f, 1f) * 255f);
        for (int i = 0; i < SpectrumAnalyzer.WaveformPoints; i++)
            _audioBytes[96 + i] = (byte)((Math.Clamp(_drawnWave[i], -1f, 1f) * 0.5f + 0.5f) * 255f);
        for (int i = 0; i < VisualizerLayout.TraceColumns; i++)
            _audioBytes[192 + i] = (byte)((Math.Clamp(_traceHistory[i], -1f, 1f) * 0.5f + 0.5f) * 255f);
        _audioBytes[288] = (byte)(Math.Clamp(_drawnLevel, 0, 1) * 255);
        _audioBytes[289] = (byte)(Math.Clamp(_drawnPeak, 0, 1) * 255);
        _audioData.WritePixels(new Int32Rect(0, 0, 96, 4), _audioBytes, 96, 0);
    }

    internal void PumpGpuAudioForTests()
    {
        RefreshSnapshot();
        WriteAudioTexture();
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
