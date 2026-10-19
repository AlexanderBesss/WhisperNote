using System.Collections.Generic;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

public class SelectionFixTests
{
    [Theory]
    [InlineData(null, "abc", true)] // non-text clipboard became text
    [InlineData("abc", "abc", false)] // Ctrl+C left the old text in place
    [InlineData("abc", "xyz", true)]
    [InlineData("abc", null, false)]
    [InlineData("abc", "", false)]
    [InlineData(null, null, false)]
    public void ClipboardChangeDetection(string? before, string? after, bool expected)
    {
        Assert.Equal(expected, SelectionFixer.ClipboardChanged(before, after));
    }

    [Theory]
    [InlineData("corrected", "corrected", true)] // clipboard still holds our last write
    [InlineData("something new", "corrected", false)] // user copied meanwhile
    public void RestoreDecision(string current, string lastWritten, bool expected)
    {
        Assert.Equal(expected, SelectionFixer.ShouldRestore(current, lastWritten));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    public void FixBlockedByAnyBusyState(bool running, bool recording, bool processing, bool blocked)
    {
        Assert.Equal(blocked, SelectionFixer.IsBlocked(running, recording, processing));
    }

    private static UiRectPx Rect(int left, int top, int right, int bottom) =>
        new(left, top, right, bottom);

    private static readonly UiRectPx Work = Rect(0, 0, 1000, 800);

    [Fact]
    public void PlacementSitsRightOfSelection()
    {
        Assert.True(SelectionWatcher.TryComputePlacement(
            new[] { Rect(100, 40, 200, 60) }, 120, 40, Work, 8, out var x, out var y));
        Assert.Equal(208, x);
        Assert.Equal(30, y);
    }

    [Fact]
    public void PlacementFlipsLeftNearRightEdge()
    {
        Assert.True(SelectionWatcher.TryComputePlacement(
            new[] { Rect(900, 40, 990, 60) }, 120, 40, Work, 8, out var x, out var y));
        Assert.Equal(900 - 8 - 120, x);
    }

    [Fact]
    public void PlacementClampsInsideWorkArea()
    {
        // Flipping still lands left of the work area and the selection sits at the top edge.
        Assert.True(SelectionWatcher.TryComputePlacement(
            new[] { Rect(10, -100, 995, -80) }, 120, 40, Work, 8, out var x, out var y));
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void PlacementAnchorsOnLowestLine()
    {
        var rects = new[] { Rect(100, 40, 200, 60), Rect(300, 100, 400, 120) };
        Assert.True(SelectionWatcher.TryComputePlacement(rects, 120, 40, Work, 8, out var x, out var y));
        Assert.Equal(408, x);
        Assert.Equal(90, y);
    }

    [Fact]
    public void PlacementNeedsRects()
    {
        Assert.False(SelectionWatcher.TryComputePlacement(
            new List<UiRectPx>(), 120, 40, Work, 8, out _, out _));
    }
}
