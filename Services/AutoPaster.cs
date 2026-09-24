using System;
using System.Runtime.InteropServices;

namespace WhisperNote.Services;

public static class AutoPaster
{
    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const ushort VK_CONTROL = 0x11;
    const ushort VK_V = 0x56;

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

    public static bool SendCtrlV()
    {
        var inputs = new[]
        {
            Key(VK_CONTROL, keyUp: false),
            Key(VK_V, keyUp: false),
            Key(VK_V, keyUp: true),
            Key(VK_CONTROL, keyUp: true)
        };

        try
        {
            var injected = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (injected != inputs.Length)
            {
                Logger.Error($"AutoPaste: SendInput injected {injected}/{inputs.Length} keystrokes (error {Marshal.GetLastWin32Error()})");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"AutoPaste failed: {ex.Message}");
            return false;
        }
    }

    static INPUT Key(ushort vk, bool keyUp) => new()
    {
        Type = INPUT_KEYBOARD,
        Data = new InputUnion
        {
            Keyboard = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0u
            }
        }
    };
}
