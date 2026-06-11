using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace WhisperNote.Services;

public class GlobalKeyboardHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100;
    const int WM_KEYUP = 0x0101;
    const int WM_SYSKEYDOWN = 0x0104;
    const int WM_SYSKEYUP = 0x0105;

    readonly int _vkCode;
    readonly Func<Task> _onKeyDown;
    readonly Func<Task> _onKeyUp;
    readonly Dispatcher _dispatcher;
    bool _isKeyPressed;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    readonly HookProc _hookCallback;
    IntPtr _hookHandle = IntPtr.Zero;

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public GlobalKeyboardHook(int vkCode, Func<Task> onKeyDown, Func<Task> onKeyUp)
    {
        _vkCode = vkCode;
        _onKeyDown = onKeyDown;
        _onKeyUp = onKeyUp;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _hookCallback = HookCallback;
        Install();
    }

    void Install()
    {
        var ptr = SetWindowsHookEx(WH_KEYBOARD_LL, _hookCallback, IntPtr.Zero, 0);
        if (ptr == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to install keyboard hook: {Marshal.GetLastWin32Error()}");

        _hookHandle = ptr;
    }

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var ks = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (ks.vkCode == (uint)_vkCode)
            {
                if (IsKeyDown(wParam))
                {
                    if (_isKeyPressed)
                        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                    _isKeyPressed = true;
                    InvokeHandler(_onKeyDown, "keydown");
                }
                else if (IsKeyUp(wParam))
                {
                    _isKeyPressed = false;
                    InvokeHandler(_onKeyUp, "keyup");
                }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    static bool IsKeyDown(IntPtr message) =>
        message == (IntPtr)WM_KEYDOWN || message == (IntPtr)WM_SYSKEYDOWN;

    static bool IsKeyUp(IntPtr message) =>
        message == (IntPtr)WM_KEYUP || message == (IntPtr)WM_SYSKEYUP;

    void InvokeHandler(Func<Task> handler, string label)
    {
        _dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await handler();
            }
            catch (Exception ex)
            {
                Logger.Error($"GlobalKeyboardHook {label}: {ex.Message}");
            }
        }));
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
