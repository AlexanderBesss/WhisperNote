using System.Diagnostics;
using Xunit;

namespace WhisperNote.Tests;

public class FramePacerTests
{
    static int RendersPerSecond(int fps, int feedHz)
    {
        var pacer = new FramePacer(1000.0 / 30);
        int rendered = 0;
        for (int frame = 0; frame < feedHz; frame++)
        {
            var stamp = (long)(frame * 1000.0 / feedHz * Stopwatch.Frequency / 1000.0);
            if (pacer.ShouldRender(fps, stamp))
                rendered++;
        }

        return rendered;
    }

    // A coarse millisecond clock (system-tick granularity) used to gate half
    // the configured rate on 60 Hz feeds; the QPC-based pacer must keep up.
    [Fact]
    public void KeepsUpWithRefreshLockedFeeds()
    {
        Assert.InRange(RendersPerSecond(30, 60), 29, 31);
        Assert.InRange(RendersPerSecond(60, 60), 59, 61);

        // Refresh feeds are not multiples of the setting; rendering a little
        // above the target is acceptable, running below it is the bug.
        Assert.InRange(RendersPerSecond(30, 144), 30, 40);
        Assert.InRange(RendersPerSecond(60, 144), 60, 80);
        Assert.InRange(RendersPerSecond(120, 144), 120, 144);
        Assert.InRange(RendersPerSecond(240, 360), 240, 360);
        Assert.InRange(RendersPerSecond(360, 360), 300, 360);
    }

    [Fact]
    public void FrameSpeedTracksRealTimeNotFrameCount()
    {
        var pacer = new FramePacer(1000.0 / 30);
        var second = Stopwatch.Frequency;
        var start = second; // QPC stamps are never 0, the pacer's unset marker.
        pacer.FrameSpeed(start);
        // A 16.7 ms gap at 60 FPS is half a nominal 30 Hz frame.
        var half = (long)(1000.0 / 60 * second / 1000.0);
        Assert.Equal(0.5, pacer.FrameSpeed(start + half), 3);
        // A stalled feed clamps so animation cannot teleport.
        Assert.Equal(4.0, pacer.FrameSpeed(start + half + 10 * second), 3);
    }
}
