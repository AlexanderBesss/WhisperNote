using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WhisperNote.Config;
using WhisperNote.Services;

namespace WhisperNote;

public partial class RecordingOverlayWindow : Window
{
    // The visualizer needs both the live recording state and the user's setting,
    // so the XAML MultiBinding reads these two window-level properties.
    public static readonly DependencyProperty VisualizerEnabledProperty =
        DependencyProperty.Register(
            nameof(VisualizerEnabled),
            typeof(bool),
            typeof(RecordingOverlayWindow),
            new PropertyMetadata(true));

    public static readonly DependencyProperty VisualizerStyleProperty =
        DependencyProperty.Register(
            nameof(VisualizerStyle),
            typeof(Config.VisualizerStyle),
            typeof(RecordingOverlayWindow),
            new PropertyMetadata(Config.VisualizerStyle.Spectrum));

    public static readonly DependencyProperty VisualizerPaletteProperty =
        DependencyProperty.Register(
            nameof(VisualizerPalette),
            typeof(VisualizerPalette),
            typeof(RecordingOverlayWindow),
            new PropertyMetadata(VisualizerPaletteCatalog.Default));

    public static readonly DependencyProperty VisualizerFpsProperty =
        DependencyProperty.Register(
            nameof(VisualizerFps),
            typeof(int),
            typeof(RecordingOverlayWindow),
            new PropertyMetadata(VisualizerFpsCatalog.DefaultFps));

    public static readonly DependencyProperty ProcessingAnimationProperty =
        DependencyProperty.Register(
            nameof(ProcessingAnimation),
            typeof(ProcessingStyle),
            typeof(RecordingOverlayWindow),
            new PropertyMetadata(ProcessingStyle.WaveSweep));

    public bool VisualizerEnabled
    {
        get => (bool)GetValue(VisualizerEnabledProperty);
        set => SetValue(VisualizerEnabledProperty, value);
    }

    public Config.VisualizerStyle VisualizerStyle
    {
        get => (Config.VisualizerStyle)GetValue(VisualizerStyleProperty);
        set => SetValue(VisualizerStyleProperty, value);
    }

    public VisualizerPalette VisualizerPalette
    {
        get => (VisualizerPalette)GetValue(VisualizerPaletteProperty);
        set => SetValue(VisualizerPaletteProperty, value);
    }

    public int VisualizerFps
    {
        get => (int)GetValue(VisualizerFpsProperty);
        set => SetValue(VisualizerFpsProperty, value);
    }

    public ProcessingStyle ProcessingAnimation
    {
        get => (ProcessingStyle)GetValue(ProcessingAnimationProperty);
        set => SetValue(ProcessingAnimationProperty, value);
    }

    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TRANSPARENT = 0x00000020;

    const int MONITOR_DEFAULTTONEAREST = 2;

    // BottomMargin includes the 20-unit transparent margin of the pill Border (XAML).
    const double BottomMargin = 52;

    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    static extern uint GetDpiForSystem();

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public RecordingOverlayWindow()
    {
        InitializeComponent();

        // A ShaderEffect paints past the rendering element's own Clip, so an
        // ancestor must do the clipping: the content Grid is trimmed to the
        // Border's rounded rectangle (Padding 18,10 + CornerRadius 16), which
        // reaches past the Grid's own bounds.
        PillContent.SizeChanged += (_, e) =>
            PillContent.Clip = new System.Windows.Media.RectangleGeometry(
                new Rect(-18, -10, e.NewSize.Width + 36, e.NewSize.Height + 20), 16, 16);
    }

    public void ShowRecording()
    {
        Reposition();
        Show();
    }

    public void HideRecording() => Hide();

    void Reposition()
    {
        var work = ForegroundMonitorWorkArea();
        var left = work.X + (work.Width - Width) / 2;
        var top = work.Y + work.Height - Height - BottomMargin;
        Left = Math.Min(Math.Max(left, work.X), work.X + work.Width - Width);
        Top = Math.Min(Math.Max(top, work.Y), work.Y + work.Height - Height);
    }

    static Rect ForegroundMonitorWorkArea()
    {
        var monitor = MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return SystemParameters.WorkArea;

        // rcWork is in physical pixels; must be divided by *that monitor's* DPI.
        var scale = EffectiveMonitorScale(monitor);

        return new Rect(
            info.rcWork.Left / scale,
            info.rcWork.Top / scale,
            (info.rcWork.Right - info.rcWork.Left) / scale,
            (info.rcWork.Bottom - info.rcWork.Top) / scale);
    }

    static double EffectiveMonitorScale(IntPtr monitor)
    {
        try
        {
            const int MDT_EFFECTIVE_DPI = 0;
            if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96.0;
        }
        catch (DllNotFoundException)
        {
        }

        try
        {
            var systemDpi = GetDpiForSystem();
            if (systemDpi > 0)
                return systemDpi / 96.0;
        }
        catch (Exception)
        {
        }

        return 1.0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var extended = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            extended | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
    }
}
