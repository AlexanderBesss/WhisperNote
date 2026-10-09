using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

public class HotkeyMatchTests
{
    [Theory]
    // Exact codes always match themselves.
    [InlineData(0xA3, 0xA3, true)] // Right Ctrl
    [InlineData(0xA2, 0xA2, true)] // Left Ctrl
    [InlineData(0xA5, 0xA5, true)] // Right Alt
    [InlineData(0x14, 0x14, true)] // Caps Lock
    // The low-level hook reports specific left/right codes, so a generic
    // Shift/Ctrl/Alt selection must fire for either side.
    [InlineData(0x11, 0xA2, true)] // Ctrl <- Left Ctrl
    [InlineData(0x11, 0xA3, true)] // Ctrl <- Right Ctrl
    [InlineData(0x10, 0xA0, true)] // Shift <- Left Shift
    [InlineData(0x10, 0xA1, true)] // Shift <- Right Shift
    [InlineData(0x12, 0xA4, true)] // Alt <- Left Alt
    [InlineData(0x12, 0xA5, true)] // Alt <- Right Alt
    // A specific side never fires for the other side or another family.
    [InlineData(0xA3, 0xA2, false)] // Right Ctrl <- Left Ctrl
    [InlineData(0xA2, 0xA3, false)] // Left Ctrl <- Right Ctrl
    [InlineData(0xA3, 0xA5, false)] // Right Ctrl <- Right Alt
    [InlineData(0x11, 0xA1, false)] // Ctrl <- Right Shift
    [InlineData(0x14, 0xA3, false)] // Caps Lock <- Right Ctrl
    public void MatchesHotkey(int configured, uint vkCode, bool expected)
    {
        Assert.Equal(expected, GlobalKeyboardHook.MatchesHotkey(configured, vkCode));
    }

    [Theory]
    // Alt and Win taps activate menu mode / Start and steal the caret, so
    // their hotkey events must be hidden from the target app.
    [InlineData(0x12, true)] // Alt
    [InlineData(0xA4, true)] // Left Alt
    [InlineData(0xA5, true)] // Right Alt
    [InlineData(0x5B, true)] // Left Win
    [InlineData(0x5C, true)] // Right Win
    // Ctrl/Shift/Caps Lock taps are harmless to focus and stay pass-through.
    [InlineData(0x11, false)] // Ctrl
    [InlineData(0xA2, false)] // Left Ctrl
    [InlineData(0xA3, false)] // Right Ctrl
    [InlineData(0x10, false)] // Shift
    [InlineData(0xA0, false)] // Left Shift
    [InlineData(0xA1, false)] // Right Shift
    [InlineData(0x14, false)] // Caps Lock
    public void FocusStealingModifiers(int vkCode, bool expected)
    {
        Assert.Equal(expected, GlobalKeyboardHook.IsFocusStealingModifier(vkCode));
    }

    [Theory]
    // AltGr on many layouts is delivered as Right Ctrl + Left Alt: while
    // RCtrl is held, a Left Alt event is the character modifier, not the
    // Alt hotkey, and must not be swallowed or replayed as an Alt chord.
    [InlineData(0xA4, true, true)]  // Left Alt while RCtrl held = AltGr
    [InlineData(0xA4, false, false)] // plain Left Alt press
    [InlineData(0xA5, true, false)] // Right Alt is never the AltGr half
    [InlineData(0x5B, true, false)] // Left Win + RCtrl is a chord, not AltGr
    [InlineData(0x41, true, false)] // A key + RCtrl is a chord, not AltGr
    public void AltGrPress(int vkCode, bool rightCtrlHeld, bool expected)
    {
        Assert.Equal(expected, GlobalKeyboardHook.IsAltGrPress(vkCode, rightCtrlHeld));
    }
}
