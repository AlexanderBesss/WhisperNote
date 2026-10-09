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
    // True for Alt/Win hotkeys: their key events are hidden from the target
    // app (see HookCallback) so releasing the hotkey cannot steal the caret.
    readonly bool _swallowHotkey;
    // Set once a chord key replayed the swallowed modifier down; a matching
    // modifier key-up must be injected when the physical hotkey is released.
    bool _modifierReplayed;
    // The left/right code actually pressed when the hotkey fired. A generic
    // Alt/Ctrl selection (0x10-0x12) must not be replayed as the generic VK:
    // apps that distinguish VK_LMENU/VK_RMENU would see the wrong key.
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

    // Alt and Win, pressed and released on their own, switch the focused app
    // into menu/keyboard-access mode or open Start, which pulls the caret out
    // of the input field being dictated into. Ctrl and Shift do not, so only
    // these two families are hidden from the target app.
    internal static bool IsFocusStealingModifier(int vkCode) =>
        vkCode is 0x12 or 0xA4 or 0xA5 or 0x5B or 0x5C; // Alt, L/R Alt, L/R Win

    // AltGr is delivered as Right Ctrl down + Left Alt down on many layouts.
    // While RCtrl is logically held, a Left Alt event is the AltGr character
    // modifier, not the Alt hotkey the user selected.
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
            // Ignore only our own synthesized keystrokes (auto-paste Ctrl+V and
            // Alt/Win chord replays, both stamped with a marker): they must never
            // retrigger a hotkey. They still pass through to the target app, so a
            // replayed Alt+Tab reaches it intact. Anything else — including
            // injected input from remappers or macro tools — still counts.
            if (ks.dwExtraInfo == AutoPaster.PasteMarker || ks.dwExtraInfo == AutoPaster.HotkeyReplayMarker)
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            if (MatchesHotkey(_vkCode, ks.vkCode))
            {
                // AltGr on many layouts is delivered as Right Ctrl down +
                // Left Alt down: it is a character modifier (@, €, ...), not
                // the Alt hotkey. While RCtrl is held, let the Left Alt events
                // pass through untouched so typing such a character neither
                // fires the hotkey nor gets swallowed/replayed as an Alt
                // chord. A release is only passed through when this hook never
                // fired the press: if the press was swallowed, the release
                // must still be handled below to release the replayed modifier.
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
                    // A bare Alt/Win press activates the target app's menu bar or
                    // Start menu the instant it is released, dropping the caret
                    // from the field being dictated into. Hide the press so the
                    // auto-paste lands where the user clicked.
                    if (_swallowHotkey)
                        return (IntPtr)1;
                }
                else if (IsKeyUp(wParam))
                {
                    // Only swallow the release when this hook swallowed the
                    // matching press. A press that reached the app before the
                    // hook was installed must still be released, or the app is
                    // left with a stuck Alt/Win.
                    bool wasPressed = _isKeyPressed;
                    _isKeyPressed = false;
                    InvokeHandler(_onKeyUp, "keyup");
                    if (_swallowHotkey && wasPressed)
                    {
                        // If a shortcut chord replayed the modifier down, the app
                        // still thinks it is held; release it so the chord commits.
                        if (_modifierReplayed)
                            AutoPaster.SendReplay(_physicalVk, keyUp: true);
                        _modifierReplayed = false;
                        return (IntPtr)1;
                    }
                }
            }
            else if (_swallowHotkey && _isKeyPressed && IsKeyDown(wParam))
            {
                // First real key pressed while the Alt/Win hotkey is held: the
                // user means a shortcut (Alt+Tab, Win+E...). Replay the swallowed
                // modifier plus this key so the app sees the chord, then swallow
                // the physical key so it is not delivered twice.
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

    // The low-level hook reports the specific left/right codes (0xA0-0xA5),
    // never the generic Shift/Ctrl/Alt codes (0x10-0x12), so a generic
    // selection must match either side. A specific left/right selection keeps
    // exact matching and only fires for that side.
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

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            // A chord may have replayed the swallowed Alt/Win down (see HookCallback).
            // If the hook is torn down while that modifier is still "held" from the
            // target app's point of view (hotkey setting changed or hook disabled
            // mid-press), release it so the app is not left with a stuck Alt/Win.
            if (_modifierReplayed)
            {
                AutoPaster.SendReplay(_physicalVk, keyUp: true);
                _modifierReplayed = false;
            }
        }
    }
}
