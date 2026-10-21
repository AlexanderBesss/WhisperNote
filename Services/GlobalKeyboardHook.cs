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

    const int VK_RCONTROL = 0xA3;
    const int VK_LMENU = 0xA4;

    readonly int _vkCode;
    readonly Func<Task> _onKeyDown;
    readonly Func<Task> _onKeyUp;
    readonly Dispatcher _dispatcher;
    bool _isKeyPressed;
    readonly bool _swallowHotkey;
    // A replayed modifier-down needs an injected modifier-up on release, or the app keeps a stuck Alt/Win.
    bool _modifierReplayed;
    // Replay must use the actual left/right VK; generic 0x10-0x12 codes show some apps the wrong key.
    ushort _physicalVk;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    readonly HookProc _hookCallback;
    IntPtr _hookHandle = IntPtr.Zero;

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

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
        _swallowHotkey = IsFocusStealingModifier(vkCode);
        _physicalVk = (ushort)vkCode;
        _hookCallback = HookCallback;
        Install();
    }

    // Bare Alt/Win presses activate menu mode or Start and steal the caret; Ctrl/Shift do not.
    internal static bool IsFocusStealingModifier(int vkCode) =>
        vkCode is 0x12 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    // Many layouts deliver AltGr as RCtrl down + LAlt down; that is not the selected Alt hotkey.
    internal static bool IsAltGrPress(int vkCode, bool rightCtrlHeld) =>
        vkCode == VK_LMENU && rightCtrlHeld;

    static bool IsAltGrPress(int vkCode) =>
        IsAltGrPress(vkCode, (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0);

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
            // Ignore only our own marker-stamped keystrokes; they must not retrigger the hotkey (other injected input still counts).
            if (ks.dwExtraInfo == AutoPaster.PasteMarker || ks.dwExtraInfo == AutoPaster.HotkeyReplayMarker)
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            if (MatchesHotkey(_vkCode, ks.vkCode))
            {
                // AltGr down always passes through; the up passes only if this hook did not swallow the press.
                if (_swallowHotkey && IsAltGrPress((int)ks.vkCode) &&
                    (IsKeyDown(wParam) || !_isKeyPressed))
                    return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

                if (IsKeyDown(wParam))
                {
                    if (_isKeyPressed)
                        return _swallowHotkey
                            ? (IntPtr)1
                            : CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                    _isKeyPressed = true;
                    _modifierReplayed = false;
                    _physicalVk = (ushort)ks.vkCode;
                    InvokeHandler(_onKeyDown, "keydown");
                    if (_swallowHotkey)
                        return (IntPtr)1;
                }
                else if (IsKeyUp(wParam))
                {
                    // Swallow the release only if this hook swallowed the press, or the app keeps a stuck Alt/Win.
                    bool wasPressed = _isKeyPressed;
                    _isKeyPressed = false;
                    InvokeHandler(_onKeyUp, "keyup");
                    if (_swallowHotkey && wasPressed)
                    {
                        ReleaseReplayedModifier();
                        return (IntPtr)1;
                    }
                }
            }
            else if (_swallowHotkey && _isKeyPressed && IsKeyDown(wParam))
            {
                // Replay the swallowed modifier plus this key, and swallow the physical key to avoid double delivery.
                if (!_modifierReplayed)
                {
                    _modifierReplayed = true;
                    AutoPaster.SendReplay(_physicalVk, keyUp: false);
                    AutoPaster.SendReplay((ushort)ks.vkCode, keyUp: false);
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    static bool IsKeyDown(IntPtr message) =>
        message == (IntPtr)WM_KEYDOWN || message == (IntPtr)WM_SYSKEYDOWN;

    static bool IsKeyUp(IntPtr message) =>
        message == (IntPtr)WM_KEYUP || message == (IntPtr)WM_SYSKEYUP;

    // WH_KEYBOARD_LL reports only left/right codes (0xA0-0xA5), never generic 0x10-0x12.
    internal static bool MatchesHotkey(int configured, uint vkCode)
    {
        if ((uint)configured == vkCode)
            return true;

        return configured switch
        {
            0x10 => vkCode is 0xA0 or 0xA1,
            0x11 => vkCode is 0xA2 or 0xA3,
            0x12 => vkCode is 0xA4 or 0xA5,
            _ => false,
        };
    }

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

    void ReleaseReplayedModifier()
    {
        if (!_modifierReplayed)
            return;
        AutoPaster.SendReplay(_physicalVk, keyUp: true);
        _modifierReplayed = false;
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            ReleaseReplayedModifier();
        }
    }
}
