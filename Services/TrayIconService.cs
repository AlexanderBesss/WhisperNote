using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;

namespace WhisperNote.Services;

/// <summary>
/// Shell notification-area icon for WhisperNote. Double-clicking the icon (or picking
/// Open in its menu) restores the main window; Exit shuts the app down.
/// The ring is red while idle and green while the mic is listening or a request is
/// being processed, matching the status dot in the main window.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    // Shell tooltips are truncated past 63 characters.
    const int MaxTooltipLength = 63;
    const string ActiveIconResourceName = "WhisperNote.TrayIcon.ico";
    const string IdleIconResourceName = "WhisperNote.TrayIconIdle.ico";

    readonly WinForms.NotifyIcon _notifyIcon;
    readonly Icon _activeIcon;
    readonly Icon _idleIcon;

    public event EventHandler? RestoreRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService(string tooltip)
    {
        _activeIcon = LoadIcon(ActiveIconResourceName);
        _idleIcon = LoadIcon(IdleIconResourceName);

        var menu = new WinForms.ContextMenuStrip();

        var openItem = new WinForms.ToolStripMenuItem("Open WhisperNote");
        openItem.Click += (_, _) => RestoreRequested?.Invoke(this, EventArgs.Empty);
        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(openItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _idleIcon,
            Text = Trim(tooltip),
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => RestoreRequested?.Invoke(this, EventArgs.Empty);
    }

    public string Tooltip
    {
        set => _notifyIcon.Text = Trim(value);
    }

    internal Icon ActiveIcon => _activeIcon;
    internal Icon IdleIcon => _idleIcon;

    /// <summary>Green ring while listening or processing, red ring while idle.</summary>
    public void SetActive(bool active) => _notifyIcon.Icon = active ? _activeIcon : _idleIcon;

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
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _activeIcon.Dispose();
        _idleIcon.Dispose();
    }
}
