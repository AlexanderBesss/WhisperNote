using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WhisperNote.Config;

namespace WhisperNote;

// Shaders/ProcStyles.fx compiled with fxc /T ps_2_0: newer bytecode targets are
// silently rejected by WPF. The c0 Time/c1 Peak/c2 Mid/c3 Aspect register layout
// must stay identical to the HLSL entries or the bindings break.
public sealed class ProcessingGpuEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(ProcessingGpuEffect), 0);

    public static readonly DependencyProperty TimeProperty =
        DependencyProperty.Register(
            "Time",
            typeof(double),
            typeof(ProcessingGpuEffect),
            new PropertyMetadata(0.0, PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty PeakColorProperty =
        DependencyProperty.Register(
            "PeakColor",
            typeof(Color),
            typeof(ProcessingGpuEffect),
            new PropertyMetadata(Colors.White, PixelShaderConstantCallback(1)));

    public static readonly DependencyProperty MidColorProperty =
        DependencyProperty.Register(
            "MidColor",
            typeof(Color),
            typeof(ProcessingGpuEffect),
            new PropertyMetadata(Colors.White, PixelShaderConstantCallback(2)));

    public static readonly DependencyProperty AspectProperty =
        DependencyProperty.Register(
            "Aspect",
            typeof(double),
            typeof(ProcessingGpuEffect),
            new PropertyMetadata(1.0, PixelShaderConstantCallback(3)));

    public double Time
    {
        get => (double)GetValue(TimeProperty);
        set => SetValue(TimeProperty, value);
    }

    public double Aspect
    {
        get => (double)GetValue(AspectProperty);
        set => SetValue(AspectProperty, value);
    }

    public Color PeakColor
    {
        get => (Color)GetValue(PeakColorProperty);
        set => SetValue(PeakColorProperty, value);
    }

    public Color MidColor
    {
        get => (Color)GetValue(MidColorProperty);
        set => SetValue(MidColorProperty, value);
    }

    static readonly Dictionary<ProcessingStyle, string> ShaderFile = new()
    {
        [ProcessingStyle.ShaderShimmer] = "proc_main",
        [ProcessingStyle.CometOrbitGpu] = "proc_cometOrbit",
        [ProcessingStyle.CounterArcsGpu] = "proc_counterArcs",
        [ProcessingStyle.VoiceRipplesGpu] = "proc_voiceRipples",
    };

    public ProcessingGpuEffect(ProcessingStyle style)
    {
        PixelShader = new PixelShader
        {
            UriSource = new Uri(
                $"pack://application:,,,/WhisperNote;component/Shaders/{ShaderFile[style]}.ps",
                UriKind.Absolute),
        };

        UpdateShaderValue(InputProperty);
        UpdateShaderValue(TimeProperty);
        UpdateShaderValue(PeakColorProperty);
        UpdateShaderValue(MidColorProperty);
        UpdateShaderValue(AspectProperty);
    }
}
