using System;
using System.Windows.Threading;

namespace WhisperNote.Services;

// Feeds a SpectrumAnalyzer a speech-shaped signal so the settings window can show
// what each visualizer style looks like without opening the microphone.
public sealed class PreviewVoiceSource : IDisposable
{
    const int SampleRate = 48000;
    const int SamplesPerTick = 1024;

    public SpectrumAnalyzer Spectrum { get; } = new();

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(20) };
    readonly float[] _samples = new float[SamplesPerTick];
    readonly byte[] _bytes = new byte[SamplesPerTick * 4];
    readonly Random _rng = new();
    double _phase;
    double _time;

    public PreviewVoiceSource()
    {
        _timer.Tick += (_, _) => Pump();
    }

    // Idle unless the preview is on screen: the timer would otherwise pump ~50 FFT/s
    // for the whole life of the settings window, including tabs that show no visualizer.
    public bool Active
    {
        get => _timer.IsEnabled;
        set
        {
            if (value)
                _timer.Start();
            else
                _timer.Stop();
        }
    }

    // Internal so a test can step the signal without running a dispatcher.
    internal void Pump()
    {
        // One slow pitch drift per pass keeps the band pattern moving instead of
        // freezing on a single row of the equalizer.
        var f0 = 120 + 30 * Math.Sin(2 * Math.PI * _time / 4.0);
        for (var i = 0; i < SamplesPerTick; i++)
        {
            var step = 1.0 / SampleRate;
            var t = _time + i * step;
            _phase += f0 * step;

            var phrase = Math.Max(0.0, Math.Sin(2 * Math.PI * (t + 0.2) / 2.6));
            var syllables = 0.55 + 0.45 * Math.Sin(2 * Math.PI * t * 3.1);
            var voice = 0.55 * Math.Sin(2 * Math.PI * _phase)
                      + 0.26 * Math.Sin(6 * Math.PI * _phase)
                      + 0.14 * Math.Sin(10 * Math.PI * _phase);
            var hiss = (_rng.NextDouble() - 0.5) * 0.12;

            _samples[i] = (float)(phrase * syllables * (voice + hiss) * 0.9);
        }

        _time += SamplesPerTick / (double)SampleRate;
        Buffer.BlockCopy(_samples, 0, _bytes, 0, _bytes.Length);
        Spectrum.AddBytes(_bytes, _bytes.Length, SampleRate, 1, 32, true);
    }

    public void Dispose() => _timer.Stop();
}
