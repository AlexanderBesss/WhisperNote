using System;
using System.Collections.Generic;
using System.Linq;

namespace WhisperNote.Config;

public enum ProcessingStyle
{
    WaveSweep,
    CometOrbit,
    ShimmerBar,
    // Not offered as options anymore; kept as the CPU fallback look of the
    // GPU twins that still are.
    CounterArcs,
    VoiceRipples,
    ShaderShimmer,
    CometOrbitGpu,
    CounterArcsGpu,
    VoiceRipplesGpu,
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

// The style is stored as a display name with fallback, exactly
// like the visualizer style.
public static class ProcessingCatalog
{
    public const string DefaultStyleName = "Wave sweep";

    public static readonly IReadOnlyList<ProcessingOption> Options = new[]
    {
        new ProcessingOption(ProcessingStyle.WaveSweep, "Wave sweep"),
        new ProcessingOption(ProcessingStyle.CometOrbit, "Comet orbit"),
        new ProcessingOption(ProcessingStyle.ShimmerBar, "Shimmer bar"),
        new ProcessingOption(ProcessingStyle.ShaderShimmer, "Shimmer GPU"),
        new ProcessingOption(ProcessingStyle.CometOrbitGpu, "Comet orbit GPU"),
        new ProcessingOption(ProcessingStyle.CounterArcsGpu, "Counter arcs GPU"),
        new ProcessingOption(ProcessingStyle.VoiceRipplesGpu, "Voice ripples GPU"),
    };

    // The GPU twins render through ShaderEffect; the CPU style they twin with
    // is the fallback when the pixel shader cannot be created on the machine.
    public static bool IsGpu(ProcessingStyle style) =>
        style >= ProcessingStyle.ShaderShimmer;

    public static ProcessingStyle CpuTwin(ProcessingStyle style) => style switch
    {
        ProcessingStyle.ShaderShimmer => ProcessingStyle.ShimmerBar,
        ProcessingStyle.CometOrbitGpu => ProcessingStyle.CometOrbit,
        ProcessingStyle.CounterArcsGpu => ProcessingStyle.CounterArcs,
        ProcessingStyle.VoiceRipplesGpu => ProcessingStyle.VoiceRipples,
        _ => style,
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
