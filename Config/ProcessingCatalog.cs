using System;
using System.Collections.Generic;
using System.Linq;

namespace WhisperNote.Config;

public enum ProcessingStyle
{
    WaveSweep,
    CometOrbit,
    ShimmerBar,
    CounterArcs,
    VoiceRipples,
}

public sealed class ProcessingOption
{
    public ProcessingStyle Value { get; }
    public string Name { get; }

    public ProcessingOption(ProcessingStyle value, string name)
    {
        Value = value;
        Name = name;
    }

    public override string ToString() => Name;
}

// The post-recording pass (transcript + grammar) gets its own animated
// indicator; the style is stored as a display name with fallback, exactly
// like the visualizer style.
public static class ProcessingCatalog
{
    public const string DefaultStyleName = "Wave sweep";

    public static readonly IReadOnlyList<ProcessingOption> Options = new[]
    {
        new ProcessingOption(ProcessingStyle.WaveSweep, "Wave sweep"),
        new ProcessingOption(ProcessingStyle.CometOrbit, "Comet orbit"),
        new ProcessingOption(ProcessingStyle.ShimmerBar, "Shimmer bar"),
        new ProcessingOption(ProcessingStyle.CounterArcs, "Counter arcs"),
        new ProcessingOption(ProcessingStyle.VoiceRipples, "Voice ripples"),
    };

    public static ProcessingStyle Resolve(string? styleName)
    {
        if (string.IsNullOrWhiteSpace(styleName))
            return ProcessingStyle.WaveSweep;

        foreach (var option in Options)
            if (string.Equals(option.Name, styleName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(option.Value.ToString(), styleName, StringComparison.OrdinalIgnoreCase))
                return option.Value;

        return ProcessingStyle.WaveSweep;
    }

    public static string NameOf(ProcessingStyle style) =>
        Options.First(option => option.Value == style).Name;
}
