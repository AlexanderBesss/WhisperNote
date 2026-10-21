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
    FrequencyCity
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
