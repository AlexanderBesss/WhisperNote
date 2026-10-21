using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Threading;

namespace WhisperNote.Services;

// Screen rectangle in physical pixels (UIA reports physical coordinates).
public readonly struct UiRectPx
{
    public int Left { get; }
    public int Top { get; }
    public int Right { get; }
    public int Bottom { get; }

    public UiRectPx(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

// TargetWindow is the foreground window when the selection was observed: the fix
// injects keys into it if focus wanders to WhisperNote's own popup in between.
public sealed record SelectionSnapshot(IReadOnlyList<UiRectPx> Rects, IntPtr TargetWindow);

public sealed class SelectionWatcher : IDisposable
{
    // TextPattern covers keyboard-driven selection changes; the mouse hook covers
    // drag-selects, which some providers only signal on release.
    const int DebounceMs = 200;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    public event Action<SelectionSnapshot?>? SelectionChanged;

    readonly AutomationFocusChangedEventHandler _focusHandler;
    readonly AutomationEventHandler _selectionHandler;
    readonly DispatcherTimer _debounce;
    readonly Dispatcher _dispatcher;
    readonly int _ownProcessId = Environment.ProcessId;
    GlobalMouseHook? _mouseHook;
    AutomationElement? _subscribedElement;
    readonly object _subscriptionGate = new();
    int _queryVersion;

    public SelectionWatcher()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            ScheduleQuery();
        };

        _focusHandler = (_, _) => ScheduleDebounce();
        _selectionHandler = (_, _) => ScheduleDebounce();

        // Install the mouse hook first: if it throws, no global UIA focus handler
        // has been registered that MainWindow cannot clean up (MainWindow nulls
        // the watcher on failure, and a static handler would keep it alive).
        _mouseHook = new GlobalMouseHook(ScheduleDebounce);
        try
        {
            // Core-era rename of Framework's AddFocusChangedEventHandler; no CacheRequest overload.
            Automation.AddAutomationFocusChangedEventHandler(_focusHandler);
        }
        catch
        {
            _mouseHook.Dispose();
            _mouseHook = null;
            throw;
        }
    }

    void ScheduleDebounce()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    // Core-era AutomationEventArgs no longer carries Source, so the TextPattern
    // handler is (re)attached to whatever element is focused at query time.
    void SubscribeToElement(AutomationElement? element)
    {
        lock (_subscriptionGate)
        {
            if (ReferenceEquals(element, _subscribedElement))
                return;

            if (_subscribedElement != null)
            {
                try
                {
                    Automation.RemoveAutomationEventHandler(
                        TextPattern.TextSelectionChangedEvent, _subscribedElement, _selectionHandler);
                }
                catch (Exception ex)
                {
                    Logger.Info($"SelectionWatcher: stale selection handler detach failed: {ex.Message}");
                }
            }

            _subscribedElement = element;
            if (element == null)
                return;

            try
            {
                Automation.AddAutomationEventHandler(
                    TextPattern.TextSelectionChangedEvent, element, TreeScope.Element, _selectionHandler);
            }
            catch (Exception ex)
            {
                Logger.Info($"SelectionWatcher: selection handler attach failed: {ex.Message}");
            }
        }
    }

    void ScheduleQuery()
    {
        var version = Interlocked.Increment(ref _queryVersion);
        Task.Run(() =>
        {
            SelectionSnapshot? snapshot = null;
            try
            {
                var element = AutomationElement.FocusedElement;
                // Core client exposes ProcessId only as a property request, not a property.
                if (element != null &&
                    element.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty) is int pid &&
                    pid != _ownProcessId)
                {
                    SubscribeToElement(element);
                    if (element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) &&
                        patternObject is TextPattern textPattern)
                    {
                        var ranges = textPattern.GetSelection();
                        if (ranges is { Length: > 0 })
                        {
                            var range = ranges[ranges.Length - 1];
                            // UIA reports a bare caret as a collapsed "selection"; a real
                            // selection compares its endpoints apart.
                            if (range.CompareEndpoints(TextPatternRangeEndpoint.Start,
                                    range, TextPatternRangeEndpoint.End) != 0)
                            {
                                var rects = new List<UiRectPx>();
                                foreach (var rect in range.GetBoundingRectangles())
                                {
                                    if (rect.Width > 0 && rect.Height > 0)
                                        rects.Add(new UiRectPx(
                                            (int)rect.Left, (int)rect.Top,
                                            (int)(rect.Left + rect.Width), (int)(rect.Top + rect.Height)));
                                }
                                if (rects.Count > 0)
                                    snapshot = new SelectionSnapshot(rects, GetForegroundWindow());
                            }
                        }
                    }
                }
                else
                {
                    SubscribeToElement(null);
                }
            }
            catch (Exception ex)
            {
                // Focused element churn mid-query is normal UIA noise; the next debounce recovers.
                Logger.Info($"SelectionWatcher: query failed: {ex.Message}");
            }

            if (Volatile.Read(ref _queryVersion) == version)
                _dispatcher.BeginInvoke(() => SelectionChanged?.Invoke(snapshot));
        });
    }

    internal static bool TryComputePlacement(
        IReadOnlyList<UiRectPx> rects, int popupWidth, int popupHeight,
        UiRectPx work, int gap, out int x, out int y)
    {
        x = y = 0;
        if (rects.Count == 0)
            return false;

        var anchor = rects[0];
        foreach (var rect in rects)
            if (rect.Bottom > anchor.Bottom || (rect.Bottom == anchor.Bottom && rect.Right > anchor.Right))
                anchor = rect;

        x = anchor.Right + gap;
        if (x + popupWidth > work.Right)
            x = anchor.Left - gap - popupWidth;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - popupWidth));

        var centerY = (anchor.Top + anchor.Bottom) / 2;
        y = centerY - popupHeight / 2;
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - popupHeight));
        return true;
    }

    public void Dispose()
    {
        _debounce.Stop();
        // UIA handler removal round-trips through every provider on the machine and can
        // block indefinitely behind one wedged app. Keep it off the shutdown UI thread
        // with a bounded wait: a still-pending detach runs on a background thread and
        // dies with the process instead of hanging the exit.
        try
        {
            var detach = Task.Run(() =>
            {
                Automation.RemoveAutomationFocusChangedEventHandler(_focusHandler);
                SubscribeToElement(null);
            });
            if (!detach.Wait(TimeSpan.FromSeconds(2)))
                Logger.Error("Selection watcher UIA detach still pending; leaving it to process exit");
        }
        catch (Exception ex)
        {
            Logger.Error($"Selection watcher detach failed: {ex.Message}");
        }
        _mouseHook?.Dispose();
        _mouseHook = null;
    }
}
