using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WhisperNote.Services;

public sealed class InputFieldWatcher : IDisposable
{
    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(int x, int y);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool GetGUIThreadInfo(uint idThread, out GUITHREADINFO lpgui);

    readonly WindowInteropHelper _helper;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    const double OpacityInactive = 0.3;
    const double OpacityOverInput = 0.7;

    readonly DispatcherTimer _timer;
    readonly Window _window;
    readonly uint _ownProcessId;
    bool _disposed;

    public InputFieldWatcher(Window window)
    {
        _window = window;
        _helper = new WindowInteropHelper(window);
        _ownProcessId = (uint)Environment.ProcessId;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    void OnTick(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        if (GetForegroundWindow() == _helper.Handle)
            return;

        _window.Opacity = IsOverEditControl() || IsFocusedEditControl() ? OpacityOverInput : OpacityInactive;
    }

    static string GetClass(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static bool IsEditControl(IntPtr hWnd)
    {
        var cls = GetClass(hWnd);

        if (cls.Equals("Edit", StringComparison.OrdinalIgnoreCase))
            return true;
        if (cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase))
            return true;
        if (cls.Equals("RICHEDIT", StringComparison.OrdinalIgnoreCase))
            return true;
        if (cls.Equals("TextBox", StringComparison.OrdinalIgnoreCase))
            return true;
        if (cls.Equals("Scintilla", StringComparison.OrdinalIgnoreCase))
            return true;
        if (cls.Equals("Internet Explorer_Server", StringComparison.OrdinalIgnoreCase))
            return true;
        if (cls.Equals("Chrome_RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase))
            return true;
        if (cls.Equals("MozillaWindowClass", StringComparison.OrdinalIgnoreCase))
            return true;
        if (cls.Equals("CefBrowserWindow", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    bool IsOverEditControl()
    {
        if (!GetCursorPos(out var pt))
            return false;

        var hWnd = WindowFromPoint(pt.X, pt.Y);
        if (hWnd == IntPtr.Zero)
            return false;

        GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == _ownProcessId)
            return false;

        return IsEditControl(hWnd);
    }

    bool IsFocusedEditControl()
    {
        var foregroundHwnd = GetForegroundWindow();
        if (foregroundHwnd == IntPtr.Zero)
            return false;

        var threadId = GetWindowThreadProcessId(foregroundHwnd, out var pid);
        if (pid == _ownProcessId || pid == 0 || threadId == 0)
            return false;

        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(threadId, out info))
            return false;

        if (info.hwndFocus == IntPtr.Zero)
            return false;

        return IsEditControl(info.hwndFocus);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
