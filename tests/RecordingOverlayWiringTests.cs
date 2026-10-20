using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WhisperNote.Config;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

// The pill's visualizer is fed by XAML bindings onto window-level properties and
// paints what the settings pick. Both legs fail silently, so the path from a
// setting to painted pixels is worth pinning.
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
                            ready.Set();

                            // Dispatcher.Run returns once its queue drains, which
                            // would end this thread; a frame keeps it pumping.
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

        public bool IsProcessing { get; set; }
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

    // Ink as WPF would paint it: a setting reaches the overlay only if the
    // rendered pixels say so. Rendering paints through OnRender, the path that
    // runs without the render-loop redraw hook.
    static int[] RenderFrame(VoiceVisualizer visualizer, VisualizerStyle style)
    {
        var size = new Size(300, 78);
        visualizer.Mode = style;
        visualizer.Measure(size);
        visualizer.Arrange(new Rect(0, 0, size.Width, size.Height));
        visualizer.UpdateLayout();

        // The control spreads past the pill by its negative margin, so the frame
        // has to be the control's own size: a bottom-anchored style would else
        // fall outside the picture.
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

    // The average hue of the ink, because a palette that never reaches the control
    // would leave every scheme painting the same colour.
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
            var window = new RecordingOverlayWindow { DataContext = state };
            PumpDispatcher();
            var visualizer = (VoiceVisualizer)window.FindName("Visualizer");

            Assert.Same(state.Spectrum, visualizer.Source);
            Assert.Equal(Visibility.Visible, visualizer.Visibility);

            // The style and the switch live on the window, so the drawing control
            // has to follow the window's properties.
            Assert.Equal(VisualizerStyle.Spectrum, visualizer.Mode);
            window.VisualizerStyle = VisualizerStyle.Waveform;
            Assert.Equal(VisualizerStyle.Waveform, visualizer.Mode);
            window.VisualizerStyle = VisualizerStyle.Level;
            Assert.Equal(VisualizerStyle.Level, visualizer.Mode);

            window.VisualizerEnabled = false;
            Assert.Equal(Visibility.Collapsed, visualizer.Visibility);
            window.VisualizerEnabled = true;
            Assert.Equal(Visibility.Visible, visualizer.Visibility);

            var ink = new Dictionary<VisualizerStyle, int>();
            foreach (VisualizerStyle style in Enum.GetValues<VisualizerStyle>())
                ink[style] = InkPixels(visualizer, style);

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

            state.IsRecording = false;
            Assert.Equal(Visibility.Collapsed, visualizer.Visibility);
        });
    }

}
