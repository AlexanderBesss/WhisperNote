using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using WhisperNote.Config;

namespace WhisperNote;

// Shaders/VizStyles.fx compiled with fxc /T ps_2_0: newer bytecode targets are
// silently rejected by WPF. s1 is the control's live audio data texture, c0..c5
// the animation uniforms; the register layout must match the HLSL entries or the
// bindings break.
public sealed class VisualizerGpuEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(VisualizerGpuEffect), 0);

    public static readonly DependencyProperty AudioProperty =
        RegisterPixelShaderSamplerProperty("Audio", typeof(VisualizerGpuEffect), 1);

    public static readonly DependencyProperty TimeProperty =
        DependencyProperty.Register(
            "Time",
            typeof(double),
            typeof(VisualizerGpuEffect),
            new PropertyMetadata(0.0, PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(
            "Level",
            typeof(double),
            typeof(VisualizerGpuEffect),
            new PropertyMetadata(0.0, PixelShaderConstantCallback(1)));

    public static readonly DependencyProperty PeakColorProperty =
        DependencyProperty.Register(
            "PeakColor",
            typeof(Color),
            typeof(VisualizerGpuEffect),
            new PropertyMetadata(Colors.White, PixelShaderConstantCallback(2)));

    public static readonly DependencyProperty MidColorProperty =
        DependencyProperty.Register(
            "MidColor",
            typeof(Color),
            typeof(VisualizerGpuEffect),
            new PropertyMetadata(Colors.White, PixelShaderConstantCallback(3)));

    public static readonly DependencyProperty AuxProperty =
        DependencyProperty.Register(
            "Aux",
            typeof(Point4D),
            typeof(VisualizerGpuEffect),
            new PropertyMetadata(new Point4D(0, 0, 0, 0), PixelShaderConstantCallback(4)));

    public static readonly DependencyProperty AspectProperty =
        DependencyProperty.Register(
            "Aspect",
            typeof(double),
            typeof(VisualizerGpuEffect),
            new PropertyMetadata(1.0, PixelShaderConstantCallback(5)));

    public double Aspect
    {
        get => (double)GetValue(AspectProperty);
        set => SetValue(AspectProperty, value);
    }

    public Brush Audio
    {
        get => (Brush)GetValue(AudioProperty);
        set => SetValue(AudioProperty, value);
    }

    public double Time
    {
        get => (double)GetValue(TimeProperty);
        set => SetValue(TimeProperty, value);
    }

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
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

    public Point4D Aux
    {
        get => (Point4D)GetValue(AuxProperty);
        set => SetValue(AuxProperty, value);
    }

    static readonly Dictionary<VisualizerStyle, string> ShaderFile = new()
    {
        [VisualizerStyle.SpectrumGpu] = "viz_spectrum",
        [VisualizerStyle.WaveformGpu] = "viz_waveform",
        [VisualizerStyle.LevelGpu] = "viz_levelMeter",
        [VisualizerStyle.OscilloscopeGpu] = "viz_oscilloscope",
        [VisualizerStyle.LivingWaveGpu] = "viz_livingWave",
        [VisualizerStyle.FrequencyCityGpu] = "viz_frequencyCity",
    };

    public VisualizerGpuEffect(VisualizerStyle style, Brush audioData)
    {
        Audio = audioData;
        PixelShader = new PixelShader
        {
            UriSource = new Uri(
                $"pack://application:,,,/WhisperNote;component/Shaders/{ShaderFile[style]}.ps",
                UriKind.Absolute),
        };

        UpdateShaderValue(InputProperty);
        UpdateShaderValue(AudioProperty);
        UpdateShaderValue(TimeProperty);
        UpdateShaderValue(LevelProperty);
        UpdateShaderValue(PeakColorProperty);
        UpdateShaderValue(MidColorProperty);
        UpdateShaderValue(AuxProperty);
        UpdateShaderValue(AspectProperty);
    }
}
