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
}
