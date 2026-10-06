using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;

namespace WhisperNote.Services;

/// <summary>
/// Shell notification-area icon for WhisperNote. Clicking the icon (or picking
/// Open in its menu) restores the main window; Exit shuts the app down.
/// The ring is red while idle, blinks while the mic is listening and turns
/// green while a request is being processed, matching the recording overlay.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    // Shell tooltips are truncated past 63 characters.
    const int MaxTooltipLength = 63;
    const string ActiveIconResourceName = "WhisperNote.TrayIcon.ico";
    const string IdleIconResourceName = "WhisperNote.TrayIconIdle.ico";

    // Recording blink, matching the overlay's pulsing dot: opacity fades
    // linearly from 1.0 to 0.2 over 0.7 s and back, forever.
    const int BlinkStepMs = 50;
    const int BlinkHalfCycleSteps = 14; // 14 x 50 ms = 0.7 s
    const double BlinkMinOpacity = 0.2;

    readonly WinForms.NotifyIcon _notifyIcon;
    readonly Icon _activeIcon;
    readonly Icon _idleIcon;
    readonly List<Icon> _blinkFrames = new();
    readonly WinForms.Timer _blinkTimer;
    bool _blinking;
    int _blinkFrame;
    ContextMenu? _menu;

    public event EventHandler? RestoreRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService(string tooltip)
    {
        _activeIcon = LoadIcon(ActiveIconResourceName);
        _idleIcon = LoadIcon(IdleIconResourceName);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _idleIcon,
            Text = Trim(tooltip),
            Visible = true
        };
        _notifyIcon.MouseUp += (_, e) =>
        {
            // Left click restores the window; a double-click simply restores
            // twice, which the idempotent restore handles. Right click opens
            // the menu.
            if (e.Button == WinForms.MouseButtons.Left)
                RestoreRequested?.Invoke(this, EventArgs.Empty);
            else if (e.Button == WinForms.MouseButtons.Right)
                ShowMenu();
        };

        _blinkTimer = new WinForms.Timer { Interval = BlinkStepMs };
        _blinkTimer.Tick += (_, _) =>
        {
            _blinkFrame = (_blinkFrame + 1) % _blinkFrames.Count;
            _notifyIcon.Icon = _blinkFrames[_blinkFrame];
        };
    }

    void ShowMenu()
    {
        _menu ??= BuildMenu();
        _menu.IsOpen = true;
    }

    /// <summary>
    /// WPF context menu so the tray menu matches the app's dark rounded design
    /// instead of the default WinForms look.
    /// </summary>
    ContextMenu BuildMenu()
    {
        var resources = Application.Current.Resources;

        var menu = new ContextMenu
        {
            Style = resources["TrayContextMenu"] as Style,
            Placement = PlacementMode.MousePoint
        };

        var itemStyle = resources["TrayMenuItem"] as Style;
        var openItem = new MenuItem { Header = "Open WhisperNote", Style = itemStyle };
        openItem.Click += (_, _) => RestoreRequested?.Invoke(this, EventArgs.Empty);
        var exitItem = new MenuItem { Header = "Exit", Style = itemStyle };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(openItem);
        menu.Items.Add(new Separator { Style = resources["TrayMenuSeparator"] as Style });
        menu.Items.Add(exitItem);
        return menu;
    }

    public string Tooltip
    {
        set => _notifyIcon.Text = Trim(value);
    }

    internal Icon ActiveIcon => _activeIcon;
    internal Icon IdleIcon => _idleIcon;

    /// <summary>
    /// Red ring while idle, blinking red ring while recording (same timings as
    /// the overlay's pulsing dot), green ring while processing.
    /// </summary>
    public void SetState(bool recording, bool processing)
    {
        if (recording)
        {
            if (!_blinking)
                StartBlinking();
            return;
        }

        StopBlinking();
        _notifyIcon.Icon = processing ? _activeIcon : _idleIcon;
    }

    void StartBlinking()
    {
        if (_blinkFrames.Count == 0)
            BuildBlinkFrames();
        if (_blinkFrames.Count == 0)
        {
            // Frame generation failed; fall back to the steady red ring.
            _notifyIcon.Icon = _idleIcon;
            return;
        }

        _blinking = true;
        _blinkFrame = 0;
        _notifyIcon.Icon = _blinkFrames[0];
        _blinkTimer.Start();
    }

    void StopBlinking()
    {
        _blinkTimer.Stop();
        _blinking = false;
    }

    /// <summary>
    /// Renders the idle icon's red ring at a series of opacities tracing the
    /// overlay's 1.0 -> 0.2 -> 1.0 fade, one frame per timer step. Only the red
    /// ring pixels fade, so the white mic stays readable like the overlay's dot.
    /// </summary>
    void BuildBlinkFrames()
    {
        try
        {
            using var source = _idleIcon.ToBitmap();
            for (var step = 0; step <= BlinkHalfCycleSteps; step++)
                AddFrame(source, 1.0 - (1.0 - BlinkMinOpacity) * step / BlinkHalfCycleSteps);
            for (var step = BlinkHalfCycleSteps - 1; step >= 1; step--)
                AddFrame(source, 1.0 - (1.0 - BlinkMinOpacity) * step / BlinkHalfCycleSteps);
        }
        catch (Exception ex)
        {
            Logger.Error($"Tray blink frames failed: {ex.Message}");
            foreach (var frame in _blinkFrames)
                frame.Dispose();
            _blinkFrames.Clear();
        }

        void AddFrame(Bitmap source, double opacity)
        {
            var frame = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    var p = source.GetPixel(x, y);
                    if (IsRingRed(p) && p.A > 0)
                        p = System.Drawing.Color.FromArgb((int)Math.Round(p.A * opacity), p.R, p.G, p.B);
                    frame.SetPixel(x, y, p);
                }
            }

            var handle = frame.GetHicon();
            try
            {
                using var icon = Icon.FromHandle(handle);
                // Clone so the frame owns a private copy; the temporary
                // GDI handle can then be released.
                _blinkFrames.Add((Icon)icon.Clone());
            }
            finally
            {
                DestroyIcon(handle);
                frame.Dispose();
            }
        }

        static bool IsRingRed(System.Drawing.Color p) => p.R > 150 && p.R - p.G > 60 && p.R - p.B > 60;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// The same artwork as a WPF image, for the window and taskbar icon.
    /// </summary>
    public static ImageSource? LoadWindowImage()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ActiveIconResourceName);
            if (stream == null)
                return null;

            var decoder = new IconBitmapDecoder(
                stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
        }
        catch (Exception ex)
        {
            Logger.Error($"Window icon load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Gives a window the app icon for its title bar and taskbar button.</summary>
    public static void ApplyWindowIcon(System.Windows.Window window)
    {
        var image = LoadWindowImage();
        if (image != null)
            window.Icon = image;
    }

    static string Trim(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? "WhisperNote"
            : text.Length <= MaxTooltipLength ? text : text[..MaxTooltipLength];

    static Icon LoadIcon(string resourceName)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream != null)
                return new Icon(stream);
        }
        catch (Exception ex)
        {
            Logger.Error($"Tray icon {resourceName} load failed: {ex.Message}");
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        StopBlinking();
        _blinkTimer.Dispose();
        if (_menu != null)
            _menu.IsOpen = false;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _activeIcon.Dispose();
        _idleIcon.Dispose();
        foreach (var frame in _blinkFrames)
            frame.Dispose();
        _blinkFrames.Clear();
    }
}
