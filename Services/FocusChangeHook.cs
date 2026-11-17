using System;
using System.Runtime.InteropServices;

namespace WhisperNote.Services;

// Own Win32 focus hook rather than Automation.AddAutomationFocusChangedEventHandler:
// registering that event makes WPF's UIAutomation client install its own desktop-wide
// EVENT_OBJECT_FOCUS hook (MS.Internal.Automation.FocusTracker), and WPF deliberately
// rethrows NullReferenceException out of that native callback (its
// Misc.IsCriticalException counts NRE as critical), which terminates the process.
// Nothing can catch an exception escaping a native callback, so the crash is only
// avoidable by owning the hook and swallowing inside it.
public sealed class FocusChangeHook : IDisposable
{
    const uint EVENT_OBJECT_FOCUS = 0x8005;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    delegate void WinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    readonly WinEventProc _callback;
    readonly Action _onFocusChange;
    IntPtr _hookHandle = IntPtr.Zero;

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public FocusChangeHook(Action onFocusChange)
    {
        _onFocusChange = onFocusChange;
        // The delegate must outlive the hook: a collected callback turns the next
        // focus change into a call through a freed native thunk.
        _callback = Callback;

        var ptr = SetWinEventHook(
            EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, IntPtr.Zero, _callback,
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (ptr == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to install focus hook: {Marshal.GetLastWin32Error()}");
        _hookHandle = ptr;
    }

    void Callback(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            _onFocusChange();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
