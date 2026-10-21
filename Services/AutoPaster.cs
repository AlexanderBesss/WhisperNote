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
    const ushort VK_C = 0x43;
    const uint MAPVK_VK_TO_VSC = 0;

    // dwExtraInfo tag the global hotkey hook must ignore, so our injected keys never retrigger a hotkey.
    internal static readonly IntPtr PasteMarker = new(0x574E5054);

    // dwExtraInfo tag for hook-replayed keys; the hook must not treat its own replay as a fresh hotkey.
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
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hWnd);

    // Injected keys follow the foreground window: if the selection toolbar popup holds it,
    // keys land in WhisperNote, so focus must be handed back to the user's original window.
    public static bool ReclaimForeignForeground(IntPtr target)
    {
        if (target == IntPtr.Zero || !IsWindow(target))
            return false;
        var fg = GetForegroundWindow();
        if (fg == target)
            return false;
        GetWindowThreadProcessId(fg, out var fgPid);
        if ((int)fgPid != Environment.ProcessId)
            return false;
        return SetForegroundWindow(target);
    }

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
            var injected = Inject(inputs);
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

    public static bool SendCtrlC()
    {
        var inputs = new[]
        {
            Key(VK_CONTROL, keyUp: false, PasteMarker),
            Key(VK_C, keyUp: false, PasteMarker),
            Key(VK_C, keyUp: true, PasteMarker),
            Key(VK_CONTROL, keyUp: true, PasteMarker)
        };

        var injected = Inject(inputs);
        if (injected != inputs.Length)
            Logger.Error($"AutoPaste: SendCtrlC injected {injected}/{inputs.Length} (error {Marshal.GetLastWin32Error()})");
        return injected == inputs.Length;
    }

    // UIPI silently drops injected keys aimed at a higher-integrity window and no
    // API reports the drop. Detect that case up front so callers can bail out
    // honestly instead of showing a false success.
    public static bool IsTargetElevatedBlocked(IntPtr targetHint)
    {
        try
        {
            var window = targetHint != IntPtr.Zero && IsWindow(targetHint) ? targetHint : GetForegroundWindow();
            if (window == IntPtr.Zero)
                return false;
            GetWindowThreadProcessId(window, out var pid);
            if ((int)pid == Environment.ProcessId)
                return false;
            return IsProcessElevated(pid) == true && IsProcessElevated((uint)Environment.ProcessId) == false;
        }
        catch
        {
            return false;
        }
    }

    static (string ShortReason, string Detail) Diagnose(uint injected, int expected, int error)
    {
        string shortReason = $"SendInput injected {injected}/{expected} (error {error})";
        string foreground;
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

            if (targetElevated == true && selfElevated == false)
                shortReason = "target app runs as administrator";

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

    internal static void SendReplay(ushort vk, bool keyUp)
    {
        var inputs = new[] { Key(vk, keyUp, HotkeyReplayMarker) };
        if (Inject(inputs) != inputs.Length)
            Logger.Error($"Hotkey replay failed: vk=0x{vk:X2} keyUp={keyUp} error={Marshal.GetLastWin32Error()}");
    }

    static uint Inject(INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    static INPUT Key(ushort vk, bool keyUp, IntPtr marker) => new()
    {
        Type = INPUT_KEYBOARD,
        Data = new InputUnion
        {
            Keyboard = new KEYBDINPUT
            {
                wVk = vk,
                // Some targets (games, RDP, non-US layouts) ignore VK-only input; wScan is required.
                wScan = (ushort)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC),
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0u,
                dwExtraInfo = marker
            }
        }
    };
}
