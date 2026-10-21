using System;
using System.Diagnostics;

namespace WhisperNote;

// Frame pacing for the overlay animations. Environment.TickCount64 advances in
// ~15.6 ms system-tick steps, so gating a 30/60 Hz refresh-locked feed against
// it aliases the configured rate down by roughly half; the QPC clock is
// required. The gate threshold is 65% of the target interval so a real frame
// is never dropped to sub-millisecond jitter around the threshold.
public sealed class FramePacer
{
    static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

    readonly double _nominalFrameMs;
    long _lastGateStamp;
    long _lastRenderStamp;

    public FramePacer(double nominalFrameMs)
        => _nominalFrameMs = nominalFrameMs;

    public void Reset()
    {
        _lastGateStamp = 0;
        _lastRenderStamp = 0;
    }

    public bool ShouldRender(int fps, long now)
    {
        if (_lastGateStamp == 0)
        {
            _lastGateStamp = now;
            return true;
        }

        if ((now - _lastGateStamp) * MsPerTick < Math.Max(1.0, 650.0 / Math.Clamp(fps, 1, 400)))
            return false;

        _lastGateStamp = now;
        return true;
    }

    // Motion speed relative to the controls' nominal frame rate, so animations
    // keep their real-time pace at any configured FPS.
    public double FrameSpeed(long now)
    {
        var elapsed = _lastRenderStamp == 0
            ? _nominalFrameMs
            : Math.Clamp((now - _lastRenderStamp) * MsPerTick, 1.0, 4 * _nominalFrameMs);
        _lastRenderStamp = now;
        return elapsed / _nominalFrameMs;
    }

    public static double MsBetween(long from, long to)
        => (to - from) * MsPerTick;
}
