using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WhisperNote;

/// <summary>
/// Borderless always-on-top pill that appears at the bottom of the screen while
/// recording and hides when recording stops. It never takes focus and clicks pass
/// through, so the app being dictated into keeps the caret.
/// </summary>
public partial class RecordingOverlayWindow : Window
{
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TRANSPARENT = 0x00000020;

    const int MONITOR_DEFAULTTONEAREST = 2;

    // The window has a 20-unit transparent margin around the pill (shadow room),
    // so the visible pill sits 20 units above the window's bottom edge.
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
    }

    public void ShowRecording()
    {
        Reposition();
        Show();
    }

    public void HideRecording() => Hide();

    // Bottom-center of the monitor holding the window being dictated into.
    void Reposition()
    {
        var work = ForegroundMonitorWorkArea();
        Left = work.X + (work.Width - Width) / 2;
        Top = work.Y + work.Height - Height - BottomMargin;
    }

    static Rect ForegroundMonitorWorkArea()
    {
        var monitor = MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return SystemParameters.WorkArea;

        // rcWork is in physical pixels; the app is system-DPI-aware, so WPF
        // device-independent units use the system scale on every monitor.
        var scale = GetDpiForSystem() / 96.0;
        if (scale <= 0)
            scale = 1.0;

        return new Rect(
            info.rcWork.Left / scale,
            info.rcWork.Top / scale,
            (info.rcWork.Right - info.rcWork.Left) / scale,
            (info.rcWork.Bottom - info.rcWork.Top) / scale);
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
