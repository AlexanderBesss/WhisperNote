using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WhisperNote.Services;

namespace WhisperNote;

public partial class SelectionToolbarWindow : Window
{
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_SHOWWINDOW = 0x0040;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    const int PlacementGapPx = 8;

    // How long the green check floats where the button stood after a successful fix.
    public const int ConfirmationVisibleMs = 500;

    // Failures deserve a longer look than the success flash: the user must be able
    // to notice that nothing changed and that the X is the reason why.
    public const int FailureVisibleMs = 1500;

    DispatcherTimer? _confirmationTimer;
    RotateTransform? _spinTransform;

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

    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    readonly Action _onClick;

    public SelectionToolbarWindow(Action onClick)
    {
        _onClick = onClick;
        InitializeComponent();
        // Park far off-screen so the very first Show() cannot flash at the default spot
        // before SetWindowPos places the window.
        Left = -32000;
        Top = -32000;
    }

    void FixButton_Click(object sender, RoutedEventArgs e) => _onClick();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // NOACTIVATE keeps the target app focused and its selection intact when the
        // popup is clicked; TOOLWINDOW keeps the popup out of Alt+Tab.
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }

    public void ShowAt(IReadOnlyList<UiRectPx> rects)
    {
        if (!IsVisible)
            Show();

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
            dpi = 96;
        var scale = dpi / 96.0;

        var popupWidth = (int)Math.Ceiling(ActualWidth * scale);
        var popupHeight = (int)Math.Ceiling(ActualHeight * scale);

        var anchor = PickAnchor(rects);
        var monitorRect = new RECT
        {
            Left = anchor.Left,
            Top = anchor.Top,
            Right = anchor.Right,
            Bottom = anchor.Bottom,
        };

        var work = new UiRectPx(
            (int)SystemParameters.VirtualScreenLeft,
            (int)SystemParameters.VirtualScreenTop,
            (int)(SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth),
            (int)(SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight));
        var monitor = MonitorFromRect(ref monitorRect, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
                work = new UiRectPx(
                    info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
        }

        if (SelectionWatcher.TryComputePlacement(
                rects, popupWidth, popupHeight, work, (int)(PlacementGapPx * scale), out var x, out var y))
        {
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0,
                SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    static UiRectPx PickAnchor(IReadOnlyList<UiRectPx> rects)
    {
        var anchor = rects[0];
        foreach (var rect in rects)
            if (rect.Bottom > anchor.Bottom || (rect.Bottom == anchor.Bottom && rect.Right > anchor.Right))
                anchor = rect;
        return anchor;
    }

    // Flashes a green check at the button's old spot, then hides and resets to button mode.
    public void ShowConfirmation(IReadOnlyList<UiRectPx> rects) =>
        FlashGlyphAndAutoHide(rects, DoneGlyph, ConfirmationVisibleMs);

    // Flashes a red X at the button's old spot when the fix failed for any reason
    // (API timeout/unavailable, copy or paste refused, nothing selected...).
    public void ShowFailure(IReadOnlyList<UiRectPx> rects) =>
        FlashGlyphAndAutoHide(rects, FailedGlyph, FailureVisibleMs);

    void FlashGlyphAndAutoHide(IReadOnlyList<UiRectPx> rects, UIElement glyph, int visibleMs)
    {
        FixButton.Visibility = Visibility.Collapsed;
        ProcessingGlyph.Visibility = Visibility.Collapsed;
        DoneGlyph.Visibility = Visibility.Collapsed;
        FailedGlyph.Visibility = Visibility.Collapsed;
        glyph.Visibility = Visibility.Visible;
        // Re-measure now: ShowAt places by ActualWidth, which still reports the
        // previous content until the next layout pass.
        UpdateLayout();
        ShowAt(rects);

        _confirmationTimer?.Stop();
        _confirmationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(visibleMs),
        };
        _confirmationTimer.Tick += (_, _) =>
        {
            if (_confirmationTimer != null)
            {
                _confirmationTimer.Stop();
                _confirmationTimer = null;
            }
            HidePopup();
        };
        _confirmationTimer.Start();
    }

    // Swaps the button for a spinning sync glyph while the API round trip runs.
    public void ShowProcessing()
    {
        FixButton.Visibility = Visibility.Collapsed;
        DoneGlyph.Visibility = Visibility.Collapsed;
        ProcessingGlyph.Visibility = Visibility.Visible;

        _spinTransform ??= new RotateTransform();
        ProcessingGlyph.RenderTransformOrigin = new Point(0.5, 0.5);
        ProcessingGlyph.RenderTransform = _spinTransform;
        _spinTransform.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9)))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
    }

    void RestoreButtonMode()
    {
        _spinTransform?.BeginAnimation(RotateTransform.AngleProperty, null);
        ProcessingGlyph.Visibility = Visibility.Collapsed;
        DoneGlyph.Visibility = Visibility.Collapsed;
        FailedGlyph.Visibility = Visibility.Collapsed;
        FixButton.Visibility = Visibility.Visible;
    }

    // Hiding always resets to button mode so the next ShowAt shows the button again.
    public void HidePopup()
    {
        _confirmationTimer?.Stop();
        _confirmationTimer = null;
        if (IsVisible)
            Hide();
        RestoreButtonMode();
    }
}
