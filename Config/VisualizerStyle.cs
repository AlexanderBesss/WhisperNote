using System;
using System.Collections.Generic;
using System.Linq;

namespace WhisperNote.Config;

public enum VisualizerStyle
{
    Spectrum,
    Waveform,
    Level,
    Oscilloscope,
    PulseRings,
    MatrixDots,
    ForceField,
    LivingWave,
    FrequencyCity,
    SpectrumGpu,
    WaveformGpu,
    LevelGpu,
    OscilloscopeGpu,
    LivingWaveGpu,
    FrequencyCityGpu,
}

public sealed class VisualizerOption
{
    public VisualizerStyle Value { get; }
    public string Name { get; }

    public VisualizerOption(VisualizerStyle value, string name)
    {
        Value = value;
        Name = name;
    }

    public override string ToString() => Name;
}

public static class VisualizerCatalog
{
    public const string DefaultStyleName = "Spectrum";

    public static readonly IReadOnlyList<VisualizerOption> Options = new[]
    {
        new VisualizerOption(VisualizerStyle.Spectrum, "Spectrum bars"),
        new VisualizerOption(VisualizerStyle.Waveform, "Waveform"),
        new VisualizerOption(VisualizerStyle.Level, "Level meter"),
        new VisualizerOption(VisualizerStyle.Oscilloscope, "Oscilloscope scroll"),
        new VisualizerOption(VisualizerStyle.PulseRings, "Pulse rings"),
        new VisualizerOption(VisualizerStyle.MatrixDots, "Matrix dots"),
        new VisualizerOption(VisualizerStyle.ForceField, "Acoustic force field"),
        new VisualizerOption(VisualizerStyle.LivingWave, "Living waveform"),
        new VisualizerOption(VisualizerStyle.FrequencyCity, "Frequency city"),
        new VisualizerOption(VisualizerStyle.SpectrumGpu, "Spectrum GPU"),
        new VisualizerOption(VisualizerStyle.WaveformGpu, "Waveform GPU"),
        new VisualizerOption(VisualizerStyle.LevelGpu, "Level meter GPU"),
        new VisualizerOption(VisualizerStyle.OscilloscopeGpu, "Oscilloscope GPU"),
        new VisualizerOption(VisualizerStyle.LivingWaveGpu, "Living wave GPU"),
        new VisualizerOption(VisualizerStyle.FrequencyCityGpu, "Frequency city GPU"),
    };

    // Their CPU twins are the fallback when the shader cannot be
    // created on the machine.
    public static bool IsGpu(VisualizerStyle style) => style >= VisualizerStyle.SpectrumGpu;

    public static VisualizerStyle CpuTwin(VisualizerStyle style) => style switch
    {
        VisualizerStyle.SpectrumGpu => VisualizerStyle.Spectrum,
        VisualizerStyle.WaveformGpu => VisualizerStyle.Waveform,
        VisualizerStyle.LevelGpu => VisualizerStyle.Level,
        VisualizerStyle.OscilloscopeGpu => VisualizerStyle.Oscilloscope,
        VisualizerStyle.LivingWaveGpu => VisualizerStyle.LivingWave,
        VisualizerStyle.FrequencyCityGpu => VisualizerStyle.FrequencyCity,
        _ => style,
    };

    // Settings keep the style as a string so whispernote.json stays readable and
    // an older build that lacks a newer name keeps working.
    public static string NameOf(VisualizerStyle style) =>
        Options.First(option => option.Value == style).Name;

    public static VisualizerStyle Resolve(string? styleName) =>
        Options.FirstOrDefault(option =>
            string.Equals(option.Name, styleName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.Value.ToString(), styleName, StringComparison.OrdinalIgnoreCase))?.Value
        ?? VisualizerStyle.Spectrum;
}
