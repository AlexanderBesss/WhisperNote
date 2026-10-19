using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WhisperNote.Services;

// Minimal WH_MOUSE_LL observer: only signals button releases; never swallows input.
public sealed class GlobalMouseHook : IDisposable
{
    const int WH_MOUSE_LL = 14;
    const int WM_LBUTTONUP = 0x0202;
    const int WM_RBUTTONUP = 0x0205;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    readonly HookProc _callback;
    readonly Action _onButtonUp;
    readonly Dispatcher _dispatcher;
    IntPtr _hookHandle = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT
    {
        public int x;
        public int y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    public GlobalMouseHook(Action onButtonUp)
    {
        _onButtonUp = onButtonUp;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _callback = HookCallback;

        var ptr = SetWindowsHookEx(WH_MOUSE_LL, _callback, IntPtr.Zero, 0);
        if (ptr == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to install mouse hook: {Marshal.GetLastWin32Error()}");
        _hookHandle = ptr;
    }

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = (int)wParam;
            if (message == WM_LBUTTONUP || message == WM_RBUTTONUP)
                _dispatcher.BeginInvoke(_onButtonUp);
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
