using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WhisperNote.Services;

public static class AutoPaster
{
    public sealed record PasteResult(bool Success, string ShortReason, string Detail);

    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const ushort VK_CONTROL = 0x11;
    const ushort VK_V = 0x56;
    const uint MAPVK_VK_TO_VSC = 0;

    // Tag stamped into dwExtraInfo on every keystroke we synthesize. The
    // global hotkey hook ignores exactly these events, so our own Ctrl+V
    // auto-paste can never retrigger a Ctrl-based hotkey — while injected
    // keystrokes from other tools (remappers, macros) still work as hotkeys.
    internal static readonly IntPtr PasteMarker = new(0x574E5054);

    // Second tag for the keystrokes the hotkey hook replays when the user
    // chords a shortcut (Alt+Tab, Win+E...) with a swallowed Alt/Win hotkey:
    // the hook must not mistake its own replay for a fresh hotkey press.
    internal static readonly IntPtr HotkeyReplayMarker = new(0x574E4B52);

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr hObject);

    public static PasteResult SendCtrlV()
    {
        var inputs = new[]
        {
            Key(VK_CONTROL, keyUp: false, PasteMarker),
            Key(VK_V, keyUp: false, PasteMarker),
            Key(VK_V, keyUp: true, PasteMarker),
            Key(VK_CONTROL, keyUp: true, PasteMarker)
        };

        try
        {
            var injected = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (injected == inputs.Length)
                return new PasteResult(true, "ok", "ok");

            var error = Marshal.GetLastWin32Error();
            var (shortReason, detail) = Diagnose(injected, inputs.Length, error);
            Logger.Error($"AutoPaste: {detail}");
            return new PasteResult(false, shortReason, detail);
        }
        catch (Exception ex)
        {
            Logger.Error($"AutoPaste failed: {ex.Message}");
            return new PasteResult(false, ex.Message, ex.ToString());
        }
    }

    static (string ShortReason, string Detail) Diagnose(uint injected, int expected, int error)
    {
        string foreground;
        string shortReason = $"SendInput injected {injected}/{expected} (error {error})";
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero)
                return (shortReason, $"{shortReason}; no foreground window");

            GetWindowThreadProcessId(fg, out var pid);
            string name;
            try
            {
                name = Process.GetProcessById((int)pid).ProcessName;
            }
            catch
            {
                name = $"pid {pid}";
            }

            var targetElevated = IsProcessElevated(pid);
            var selfElevated = IsProcessElevated((uint)Process.GetCurrentProcess().Id);
            foreground = $"foreground={name} targetElevated={Format(targetElevated)} selfElevated={Format(selfElevated)}";

            // UIPI silently discards synthesized input aimed at a higher-integrity
            // window; neither the return value nor GetLastError identifies it.
            if (targetElevated == true && selfElevated == false)
                shortReason = "target app runs as administrator";

            // Kernel anti-cheat (FACEIT, Vanguard, ...) blocks SendInput
            // system-wide while active. Nothing the app can do will get
            // through; name it so the user knows what to close.
            var blocker = InputBlockerDetector.FindActiveBlocker();
            if (blocker != null)
                shortReason = $"blocked by {blocker} — close it (or paste manually with Ctrl+V)";
        }
        catch (Exception ex)
        {
            foreground = $"foreground lookup failed: {ex.Message}";
        }

        return (shortReason, $"{shortReason}; {foreground}");
    }

    static string Format(bool? value) => value.HasValue ? value.Value.ToString() : "unknown";

    static bool? IsProcessElevated(uint pid)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint TOKEN_QUERY = 0x0008;
        const int TokenElevation = 20;

        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return null;

        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
                return null;

            try
            {
                var size = Marshal.SizeOf<int>();
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!GetTokenInformation(token, TokenElevation, buffer, size, out _))
                        return null;
                    return Marshal.ReadInt32(buffer) != 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    // Replays a single keystroke tagged with HotkeyReplayMarker, used by the
    // hotkey hook to re-send a swallowed Alt/Win (and the chord key) so
    // shortcuts like Alt+Tab still reach the target app.
    internal static void SendReplay(ushort vk, bool keyUp)
    {
        var inputs = new[] { Key(vk, keyUp, HotkeyReplayMarker) };
        // A failed modifier key-up leaves the target app thinking Alt/Win is
        // still held (the physical key-up is swallowed by the hook), so log it
        // the same way SendCtrlV reports a blocked/failed injection.
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            Logger.Error($"Hotkey replay failed: vk=0x{vk:X2} keyUp={keyUp} error={Marshal.GetLastWin32Error()}");
    }

    static INPUT Key(ushort vk, bool keyUp, IntPtr marker) => new()
    {
        Type = INPUT_KEYBOARD,
        Data = new InputUnion
        {
            Keyboard = new KEYBDINPUT
            {
                wVk = vk,
                // Some targets (games, RDP, non-US layouts) ignore VK-only
                // events; the scan code costs nothing and fixes those.
                wScan = (ushort)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC),
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0u,
                // Stamp our own keystrokes so the global hotkey hook can tell
                // them apart from physical (and other tools') input.
                dwExtraInfo = marker
            }
        }
    };
}
