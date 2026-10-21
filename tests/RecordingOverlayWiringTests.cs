using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WhisperNote.Config;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

public class RecordingOverlayWiringTests
{
    // WPF is dispatcher-affine and the AppDomain allows one Application, so all
    // checks run in one session on one STA thread with the app's own resources.
    static class WpfThread
    {
        static readonly object _gate = new();
        static Dispatcher? _dispatcher;
        static Exception? _threadFailure;

        public static void Run(Action action)
        {
            lock (_gate)
            {
                if (_dispatcher == null)
                {
                    var ready = new ManualResetEventSlim();
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            _dispatcher = Dispatcher.CurrentDispatcher;
                            var app = new App();
                            app.InitializeComponent();
                            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                            EnsurePackPrefix();

                            ready.Set();

                            Dispatcher.PushFrame(new DispatcherFrame(true));
                        }
                        catch (Exception ex)
                        {
                            _threadFailure = ex;
                        }
                    })
                    { IsBackground = true };

                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                    ready.Wait();
                }

                if (_threadFailure != null)
                    throw new Exception("WPF thread died: " + _threadFailure);

                var dispatcher = _dispatcher!;
                Exception? failure = null;
                dispatcher.Invoke(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                });

                if (failure != null)
                    throw failure;
            }
        }
    }

    // Stands in for the recording-state object the pill binds to. WPF reads
    // binding paths through reflection, which needs a public type, and the pill
    // follows the recording flag through INotifyPropertyChanged.
    public class OverlayState : INotifyPropertyChanged
    {
        SpectrumAnalyzer _spectrum = new();
        bool _isRecording;
        bool _isProcessing;
        int _queuedCount;

        public event PropertyChangedEventHandler? PropertyChanged;

        public SpectrumAnalyzer Spectrum
        {
            get => _spectrum;
            set { _spectrum = value; Raise(nameof(Spectrum)); }
        }

        public bool IsRecording
        {
            get => _isRecording;
            set { _isRecording = value; Raise(nameof(IsRecording)); }
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set { _isProcessing = value; Raise(nameof(IsProcessing)); Raise(nameof(QueueSquares)); }
        }

        public int QueuedCount
        {
            get => _queuedCount;
            set { _queuedCount = value; Raise(nameof(QueuedCount)); Raise(nameof(QueueSquares)); }
        }

        public int QueueSquares => _isProcessing ? Math.Min(TranscriptionQueue.Capacity, _queuedCount + 1) : 0;

        public string StatusText { get; set; } = "Recording";
        public string InfoText { get; set; } = "00:03";

        void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // Bindings on an inherited DataContext attach on a dispatcher pass, and a
    // window that is never shown never gets one on its own.
    static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Input, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    static SpectrumAnalyzer FedAnalyzer()
    {
        var analyzer = new SpectrumAnalyzer();
        var samples = 48000;
        var buffer = new byte[samples * 4];
        for (int i = 0; i < samples; i++)
        {
            var value = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / 48000));
            BitConverter.GetBytes(value).CopyTo(buffer, i * 4);
        }

        for (int offset = 0; offset + 2048 <= buffer.Length; offset += 2048)
            analyzer.AddBytes(buffer.AsSpan(offset, 2048).ToArray(), 2048, 48000, 1, 32, true);

        return analyzer;
    }

    static int[] RenderFrame(VoiceVisualizer visualizer, VisualizerStyle style)
    {
        var size = new Size(260, 73);
        visualizer.Mode = style;
        // Detaches the effect so the helper measures the CPU twin's drawing;
        // the shader itself is verified by AssertShaderPaints.
        visualizer.Effect = null;
        visualizer.Measure(size);
        visualizer.Arrange(new Rect(0, 0, size.Width, size.Height));
        visualizer.UpdateLayout();

        var width = (int)Math.Ceiling(visualizer.RenderSize.Width);
        var height = (int)Math.Ceiling(visualizer.RenderSize.Height);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visualizer);

        var pixels = new int[width * height];
        bitmap.CopyPixels(pixels, 4 * width, 0);
        return pixels;
    }

    static int InkPixels(VoiceVisualizer visualizer, VisualizerStyle style) =>
        RenderFrame(visualizer, style).Count(pixel => (pixel >> 24) != 0);

    static int InkPixels(ProcessingVisualizer visualizer, ProcessingStyle style)
    {
        var size = new Size(260, 73);
        visualizer.Mode = style;
        // Detaches the effect so the helper measures the CPU twin's drawing;
        // the shader itself is verified by AssertShaderPaints.
        visualizer.Effect = null;
        visualizer.Measure(size);
        visualizer.Arrange(new Rect(0, 0, size.Width, size.Height));
        visualizer.UpdateLayout();
        var bitmap = new RenderTargetBitmap(260, 73, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visualizer);

        var pixels = new int[260 * 73];
        bitmap.CopyPixels(pixels, 4 * 260, 0);
        return pixels.Count(pixel => (pixel >> 24) != 0);
    }

    static void AssertShaderPaints(FrameworkElement element, Enum style)
    {
        element.Measure(new Size(260, 73));
        element.Arrange(new Rect(0, 0, 260, 73));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(260, 73, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new int[260 * 73];
        bitmap.CopyPixels(pixels, 4 * 260, 0);
        int inked = pixels.Count(pixel => ((pixel >> 24) & 0xff) > 20);
        Assert.True(inked > 100, $"the {style} shader bytecode does not render (inked pixels: {inked})");
    }

    // A ShaderEffect paints past its own Clip; only an ancestor's clip contains
    // it (the pill's Border does this in production). Re-parenting the window's
    // live controls is illegal, so containment is proved on throwaway instances.
    static void AssertRoundedHostClips(FrameworkElement fresh, Enum style)
    {
        var host = new System.Windows.Controls.Grid();
        host.Clip = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 260, 73), 16, 16);
        host.Children.Add(fresh);
        host.Measure(new Size(260, 73));
        host.Arrange(new Rect(0, 0, 260, 73));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(260, 73, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var pixels = new int[260 * 73];
        bitmap.CopyPixels(pixels, 4 * 260, 0);
        int inked = pixels.Count(pixel => ((pixel >> 24) & 0xff) > 20);
        Assert.True(inked > 100, $"{style} in a clipped host paints nothing (inked: {inked})");
        Assert.Equal(0, (pixels[0] >> 24) & 0xff);
    }

    // The fetch chain can replace the pack prefix handlers mid-process (the
    // real handler then cannot resolve these URIs here), so re-assert ours
    // right before each ShaderEffect construction that fetches.
    static void EnsurePackPrefix()
    {
        System.Net.WebRequest.RegisterPrefix("pack", new ResourcePackRequestCreator());
        System.Net.WebRequest.RegisterPrefix("pack://", new ResourcePackRequestCreator());
    }

    private sealed class ResourcePackRequestCreator : System.Net.IWebRequestCreate
    {
        public System.Net.WebRequest Create(Uri requestUri) => new ResourcePackRequest(requestUri);
    }

    private sealed class ResourcePackRequest(Uri uri) : System.Net.WebRequest
    {
        // Reads the shader from the repo's Shaders folder: Application.GetResourceStream
        // wakes System.IO.Packaging, whose static init overwrites the "pack" prefix
        // with a handler that cannot resolve pack URIs in the test host.
        public override System.Net.WebResponse GetResponse()
        {
            var tail = uri.AbsolutePath;
            int cut = tail.IndexOf("/Shaders/", StringComparison.OrdinalIgnoreCase);
            if (cut < 0)
                throw new System.Net.WebException($"unexpected pack uri {uri}");
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "Shaders")))
                dir = dir.Parent;
            if (dir == null)
                throw new System.Net.WebException("no Shaders folder above the test binaries");
            var path = System.IO.Path.Combine(dir.FullName, tail[(cut + 1)..].Replace('/', System.IO.Path.DirectorySeparatorChar));
            return new ResourcePackResponse(System.IO.File.OpenRead(path));
        }
    }

    private sealed class ResourcePackResponse(System.IO.Stream stream) : System.Net.WebResponse
    {
        public override long ContentLength => stream.Length;
        public override System.IO.Stream GetResponseStream() => stream;
    }

    static (int Red, int Green, int Blue) InkTint(VoiceVisualizer visualizer)
    {
        long red = 0, green = 0, blue = 0, painted = 0;
        foreach (var pixel in RenderFrame(visualizer, VisualizerStyle.Spectrum))
        {
            if ((pixel >> 24) == 0)
                continue;

            red += (pixel >> 16) & 0xFF;
            green += (pixel >> 8) & 0xFF;
            blue += pixel & 0xFF;
            painted++;
        }

        painted = Math.Max(1, painted);
        return ((int)(red / painted), (int)(green / painted), (int)(blue / painted));
    }

    [Fact]
    public void SettingsReachThePillAndChangeWhatItPaints()
    {
        WpfThread.Run(() =>
        {
            var state = new OverlayState { IsRecording = true, Spectrum = FedAnalyzer() };
            EnsurePackPrefix();
            var window = new RecordingOverlayWindow { DataContext = state };
            PumpDispatcher();
            var visualizer = (VoiceVisualizer)window.FindName("Visualizer");

            Assert.Same(state.Spectrum, visualizer.Source);
            Assert.Equal(Visibility.Visible, visualizer.Visibility);

            Assert.Equal(VisualizerStyle.Spectrum, visualizer.Mode);
            window.VisualizerStyle = VisualizerStyle.Waveform;
            Assert.Equal(VisualizerStyle.Waveform, visualizer.Mode);
            window.VisualizerStyle = VisualizerStyle.Level;
            Assert.Equal(VisualizerStyle.Level, visualizer.Mode);

            window.VisualizerEnabled = false;
            Assert.Equal(Visibility.Collapsed, visualizer.Visibility);
            window.VisualizerEnabled = true;
            Assert.Equal(Visibility.Visible, visualizer.Visibility);

            // The status text trades places with the drawing: it would cover the
            // visualization, so it is hidden exactly while the visualizer shows.
            var statusText = (System.Windows.Controls.StackPanel)window.FindName("StatusTextPanel");
            Assert.Equal(Visibility.Collapsed, statusText.Visibility);
            window.VisualizerEnabled = false;
            Assert.Equal(Visibility.Visible, statusText.Visibility);
            window.VisualizerEnabled = true;
            Assert.Equal(Visibility.Collapsed, statusText.Visibility);
            state.IsRecording = false;
            Assert.Equal(Visibility.Visible, statusText.Visibility);
            state.IsRecording = true;

            var ink = new Dictionary<VisualizerStyle, int>();
            foreach (VisualizerStyle style in Enum.GetValues<VisualizerStyle>())
            {
                // GPU twins draw their CPU twin once the effect is detached, so
                // their ink would collide with the original; they are verified
                // through the shader-render guard instead.
                if (VisualizerCatalog.IsGpu(style))
                    continue;
                ink[style] = InkPixels(visualizer, style);
            }

            // A GPU style whose effect is detached (a tier-0 machine, or a
            // shader that failed to load) must never paint a blank pill.
            foreach (VisualizerStyle style in Enum.GetValues<VisualizerStyle>())
            {
                if (!VisualizerCatalog.IsGpu(style))
                    continue;
                int fallback = InkPixels(visualizer, style);
                Assert.True(fallback > 50, $"{style} with a detached effect paints nothing");
            }

            var painted = string.Join(", ", ink.Select(pair => $"{pair.Key}={pair.Value}"));
            foreach (var pair in ink)
                Assert.True(pair.Value > 50,
                    $"{pair.Key} paints nothing with levels present ({state.Spectrum.Level:0.000}): {painted}");

            Assert.Equal(ink.Count, ink.Values.Distinct().Count());

            // Colours travel the same way as the style, and a scheme that never
            // reaches the control would leave every preset painting the same hue.
            var warm = VisualizerPaletteCatalog.Resolve("Signal Red");
            var cool = VisualizerPaletteCatalog.Resolve("Mint Lime");
            Assert.NotSame(warm, cool);
            Assert.Same(VisualizerPaletteCatalog.Default, visualizer.Palette);

            window.VisualizerPalette = cool;
            Assert.Same(cool, visualizer.Palette);
            var mint = InkTint(visualizer);

            window.VisualizerPalette = warm;
            Assert.Same(warm, visualizer.Palette);
            var red = InkTint(visualizer);

            var hues = $"signal red={red}, mint lime={mint}";
            Assert.True(red.Red > red.Green, $"the red scheme did not paint red: {hues}");
            Assert.True(mint.Green > mint.Red, $"the mint scheme did not paint green: {hues}");

            // Frame rate travels the same way as the style, and the default must
            // be the nominal rate the animation step sizes are authored against.
            Assert.Equal(VisualizerFpsCatalog.DefaultFps, visualizer.FrameFps);
            window.VisualizerFps = 120;
            Assert.Equal(120, visualizer.FrameFps);
            window.VisualizerFps = 60;
            Assert.Equal(60, visualizer.FrameFps);

            var processing = (ProcessingVisualizer)window.FindName("Processing");
            Assert.Equal(Visibility.Collapsed, processing.Visibility);
            state.IsProcessing = true;
            Assert.Equal(Visibility.Visible, processing.Visibility);

            // Processing owns the pill: the status text is hidden even with
            // the voice visualizer off, leaving the animation as the feedback.
            window.VisualizerEnabled = false;
            Assert.Equal(Visibility.Collapsed, statusText.Visibility);
            window.VisualizerEnabled = true;

            Assert.Equal(ProcessingStyle.WaveSweep, processing.Mode);
            window.ProcessingAnimation = ProcessingStyle.CometOrbit;
            Assert.Equal(ProcessingStyle.CometOrbit, processing.Mode);

            window.ProcessingAnimation = ProcessingStyle.ShaderShimmer;
            Assert.True(processing.Effect != null, "processing.Effect null");

            if (RenderCapability.Tier >> 16 >= 2)
            {
                // Bytecode newer than ps_2_0 is silently rejected by WPF and
                // the plain source renders instead, so prove every GPU style
                // paints pixels its near-transparent base fill cannot produce.
                foreach (ProcessingStyle style in Enum.GetValues<ProcessingStyle>())
                {
                    if (!ProcessingCatalog.IsGpu(style))
                        continue;
                    window.ProcessingAnimation = style;
                    var effect = (ProcessingGpuEffect)processing.Effect!;
                    effect.Time = 0.5;
                    effect.Aspect = 260.0 / 73.0;
                    AssertShaderPaints(processing, style);
                }

                foreach (VisualizerStyle style in Enum.GetValues<VisualizerStyle>())
                {
                    if (!VisualizerCatalog.IsGpu(style))
                        continue;
                    visualizer.Mode = style;
                    visualizer.PumpGpuAudioForTests();
                    var effect = (VisualizerGpuEffect)visualizer.Effect!;
                    effect.Time = 0.7;
                    effect.Level = Math.Clamp(state.Spectrum.Level, 0.4, 1.0);
                    effect.Aux = new System.Windows.Media.Media3D.Point4D(0, 0, 0, 0.6);
                    effect.Aspect = 260.0 / 73.0;
                    AssertShaderPaints(visualizer, style);
                }

                EnsurePackPrefix();
                AssertRoundedHostClips(
                    new ProcessingVisualizer { Mode = ProcessingStyle.ShaderShimmer },
                    ProcessingStyle.ShaderShimmer);

                // Re-fetching the same pack URI a second time throws in this
                // host even with our prefix registered, so the fresh control
                // borrows the window's already-loaded effect instance.
                var freshViz = new VoiceVisualizer
                {
                    Mode = VisualizerStyle.Spectrum,
                    Effect = visualizer.Effect,
                };
                freshViz.PumpGpuAudioForTests();
                Assert.True(freshViz.Effect != null, "freshViz.Effect null");
                ((VisualizerGpuEffect)freshViz.Effect!).Time = 0.7;
                AssertRoundedHostClips(freshViz, VisualizerStyle.SpectrumGpu);

                visualizer.Mode = VisualizerStyle.Spectrum;
            }

            window.ProcessingAnimation = ProcessingStyle.WaveSweep;
            Assert.Null(processing.Effect);

            var procInk = new Dictionary<ProcessingStyle, int>();
            foreach (ProcessingStyle style in Enum.GetValues<ProcessingStyle>())
            {
                if (ProcessingCatalog.IsGpu(style))
                    continue;
                procInk[style] = InkPixels(processing, style);
            }

            var procPainted = string.Join(", ", procInk.Select(pair => $"{pair.Key}={pair.Value}"));
            foreach (var pair in procInk)
                Assert.True(pair.Value > 50, $"{pair.Key} paints nothing: {procPainted}");
            Assert.Equal(procInk.Count, procInk.Values.Distinct().Count());

            foreach (ProcessingStyle style in Enum.GetValues<ProcessingStyle>())
            {
                if (!ProcessingCatalog.IsGpu(style))
                    continue;
                int fallback = InkPixels(processing, style);
                Assert.True(fallback > 50, $"{style} with a detached effect paints nothing");
            }
            window.ProcessingAnimation = ProcessingStyle.WaveSweep;

            state.IsProcessing = false;
            Assert.Equal(Visibility.Collapsed, processing.Visibility);

            // The queue bar shows one square for the clip in flight plus one per
            // waiting clip, and the counters drive it through the binding.
            var queue = (QueueBar)window.FindName("Queue");
            Assert.Equal(0, queue.Squares);
            state.IsProcessing = true;
            state.QueuedCount = 3;
            Assert.Equal(4, queue.Squares);
            state.QueuedCount = 0;
            Assert.Equal(1, queue.Squares);
            state.IsProcessing = false;
            Assert.Equal(0, queue.Squares);

            var barInk = InkOfSquares(new QueueBar { Squares = 3 });
            Assert.True(barInk > 90, $"the queue bar paints too little: {barInk}");
            Assert.Equal(0, InkOfSquares(new QueueBar()));

            // The slide-off ghost: dropping the count briefly paints the
            // vacated leftmost slot (columns below the surviving squares),
            // and the ghost is gone once its animation expires.
            var ghostBar = new QueueBar { Squares = 3 };
            ghostBar.Measure(new Size(45, 12));
            ghostBar.Arrange(new Rect(0, 0, 45, 12));
            ghostBar.UpdateLayout();
            ghostBar.Squares = 2;
            var ghostInk = InkInColumns(ghostBar, 15, 28);
            Assert.True(ghostInk > 20, $"the slide-off ghost does not paint: {ghostInk}");
            Thread.Sleep(400);
            Assert.Equal(0, InkInColumns(ghostBar, 15, 28));

            // The glow outline is wired to the window's palette and fps, paints
            // a ring around the pill, and its colours flow along the edge.
            var glow = (GlowBorder)window.FindName("Glow");
            Assert.NotNull(glow);
            Assert.Same(window.VisualizerPalette, glow.Palette);
            Assert.Equal(window.VisualizerFps, glow.FrameFps);
            window.VisualizerFps = 120;
            Assert.Equal(120, glow.FrameFps);
            window.VisualizerFps = VisualizerFpsCatalog.DefaultFps;

            var outline = new GlowBorder();
            outline.Measure(new Size(260, 73));
            outline.Arrange(new Rect(0, 0, 260, 73));
            outline.UpdateLayout();
            outline.InvalidateVisual();
            PumpDispatcher();
            var glowA = GlowPixels(outline);
            var ringInk = glowA.Count(pixel => ((pixel >> 24) & 0xFF) != 0);
            Assert.True(ringInk > 800, $"the glow outline paints too little: {ringInk}");

            // The control advances its slide by real elapsed time per render,
            // so the flow check pumps frames at an app-like pace.
            for (int frame = 0; frame < 30; frame++)
            {
                Thread.Sleep(15);
                outline.InvalidateVisual();
                PumpDispatcher();
            }

            var glowB = GlowPixels(outline);
            // Inside the bright top-edge stroke band (centred on row ~1), on
            // the straight run away from the corners.
            const int edge = 1 * 260 + 65;
            var flowed = Math.Abs(((glowA[edge] >> 16) & 0xFF) - ((glowB[edge] >> 16) & 0xFF))
                + Math.Abs(((glowA[edge] >> 8) & 0xFF) - ((glowB[edge] >> 8) & 0xFF));
            Assert.True(flowed > 8, $"the glow outline does not flow: {flowed}");

            state.IsRecording = false;
            Assert.Equal(Visibility.Collapsed, visualizer.Visibility);

            // A ShaderEffect paints past its own Clip, so the pill's rounded
            // containment is wired on the ancestor content Grid. A Window that
            // was never Shown lays out nothing, so arrange its content root.
            var pillRoot = (System.Windows.Controls.Grid)window.Content;
            pillRoot.Measure(new Size(600, 140));
            pillRoot.Arrange(new Rect(0, 0, 600, 140));
            pillRoot.UpdateLayout();
            var pillContent = (System.Windows.Controls.Grid)window.FindName("PillContent");
            var pillClip = Assert.IsType<System.Windows.Media.RectangleGeometry>(pillContent.Clip);
            Assert.Equal(16, pillClip.RadiusX, 2);
            Assert.Equal(16, pillClip.RadiusY, 2);
            Assert.Equal(-18, pillClip.Bounds.X, 2);
            Assert.Equal(-10, pillClip.Bounds.Y, 2);
            Assert.Equal(pillContent.RenderSize.Width + 36, pillClip.Bounds.Width, 2);
            Assert.Equal(pillContent.RenderSize.Height + 20, pillClip.Bounds.Height, 2);
        });
    }

    static int[] GlowPixels(GlowBorder outline)
    {
        var bitmap = new RenderTargetBitmap(260, 73, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(outline);
        var pixels = new int[260 * 73];
        bitmap.CopyPixels(pixels, 4 * 260, 0);
        return pixels;
    }

    static int InkInColumns(QueueBar bar, int from, int to)
    {
        bar.InvalidateVisual();
        PumpDispatcher();
        var bitmap = new RenderTargetBitmap(45, 12, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(bar);
        var pixels = new int[45 * 12];
        bitmap.CopyPixels(pixels, 4 * 45, 0);
        int ink = 0;
        for (int y = 0; y < 12; y++)
            for (int x = from; x < to; x++)
                if (((pixels[y * 45 + x] >> 24) & 0xFF) != 0)
                    ink++;
        return ink;
    }

    // A standalone bar lets the pop-in animation expire (sleep) before forcing a
    // fresh render pass, so the counted ink is the steady-state row of squares.
    static int InkOfSquares(QueueBar bar)
    {
        bar.Measure(new Size(45, 12));
        bar.Arrange(new Rect(0, 0, 45, 12));
        bar.UpdateLayout();
        Thread.Sleep(400);
        bar.InvalidateVisual();
        PumpDispatcher();
        var bitmap = new RenderTargetBitmap(45, 12, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(bar);
        var pixels = new int[45 * 12];
        bitmap.CopyPixels(pixels, 4 * 45, 0);
        int ink = 0;
        foreach (var pixel in pixels)
            if (((pixel >> 24) & 0xFF) != 0)
                ink++;
        return ink;
    }

}
