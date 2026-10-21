// Compile each entry separately:
//   fxc /O3 /T ps_2_0 /E <entry> /Fo Shaders\proc_<entry>.ps Shaders\ProcStyles.fx
// ps_2_0 is mandatory: WPF's milcore rejects modern fxc ps_3_0 bytecode and
// ShaderEffect then silently falls back to rendering the unshaded source.
// ps_2_0 caps arithmetic at 64 instruction slots, so entries stay lean.
// Every entry must sample content: fxc strips provably unused samplers and
// ShaderEffect then refuses to render the effect at all (see finish()).
// Uniforms only need to be referenced by the entries that use them; fxc folds
// unused ones into the constant pool and WPF's writes to them are inert.

sampler2D content : register(s0);

float uTime : register(c0);
float4 uPeak : register(c1);
float4 uMid : register(c2);
float uAspect : register(c3);   // element width / height, from the control

float4 finish(float4 col, float2 uv)
{
    // fxc drops provably unused samplers and ShaderEffect requires a texture input at s0.
    float4 src = tex2D(content, uv);
    return min(col, src + float4(1, 1, 1, 1));
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float radius = 0.04;
    float2 q = abs(uv - float2(0.5, 0.5)) - float2(0.32 - radius, 0.04 - radius);
    float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
    float inside = 1.0 - smoothstep(0.0, 0.012, d);

    float shimmer = fmod(uTime * 0.9, 1.5) - 0.25;
    float gd = ((uv.x - 0.18) / 0.64 - shimmer) / 0.17;
    float g = exp(-gd * gd);

    float a = inside * (0.09 + 0.85 * g);
    float3 c = lerp(uMid.rgb, uPeak.rgb, g);
    float4 col = float4(c * a, a);
    return finish(col, uv);
}

float2 rot2(float2 v)
{
    return float2(v.x * 0.9131 - v.y * 0.4078, v.x * 0.4078 + v.y * 0.9131);
}

float4 cometOrbit(float2 uv : TEXCOORD) : COLOR
{
    float2 e = (uv - 0.5) * float2(uAspect, 1.0) / 0.30;

    float head = uTime * 1.9;
    float2 h = float2(cos(head), sin(head));
    float dh = dot(e - h, e - h);
    float glow = exp(-dh / 0.05);

    float2 t1 = rot2(h);
    float2 t2 = rot2(t1);
    float2 t3 = rot2(t2);
    float trail = exp(-dot(e - t1, e - t1) / 0.05)
                + exp(-dot(e - t2, e - t2) / 0.08)
                + exp(-dot(e - t3, e - t3) / 0.11);

    float a = glow * 0.85 + trail * 0.35;
    float3 c = lerp(uMid.rgb, uPeak.rgb, glow + trail * 0.6);
    float4 col = float4(c * a, a);
    return finish(col, uv);
}

float4 counterArcs(float2 uv : TEXCOORD) : COLOR
{
    float2 p = (uv - 0.5) * float2(uAspect, 1.0);
    float d = length(p);
    // Angle from the top without atan2: cos of the polar angle grows monotonically from the sweep start.
    float ca = p.y / max(d, 0.001);

    float a = 0.0;
    for (int i = 0; i < 3; i += 1)
    {
        float fi = (float)i;
        float dr = d - 0.20 - fi * 0.16;
        float rr = exp(-dr * dr / 0.0045);
        // Triangle wave, not a sawtooth: the fmod wrap never pops a lit ring (CPU twin's whole-turns-per-cycle invariant).
        float prog = abs(frac(uTime * 0.55 + fi * 0.3334) * 2.0 - 1.0);
        float onArc = 1.0 - step(1.0 - 2.0 * prog, ca);
        a += rr * (0.05 + 0.40 * onArc);
    }

    float3 c = lerp(uMid.rgb, uPeak.rgb, min(a * 1.4, 1.0));
    float4 col = float4(c * a, a);
    return finish(col, uv);
}

float4 voiceRipples(float2 uv : TEXCOORD) : COLOR
{
    float2 p = (uv - 0.5) * float2(uAspect, 1.0);
    float d = length(p);

    float a = 0.0;
    for (int i = 0; i < 3; i += 1)
    {
        float age = fmod(uTime * 0.75 + (float)i * 0.3334, 1.0);
        float rr = 0.15 + age * 0.70;
        a += exp(-(d - rr) * (d - rr) / 0.0016) * (1.0 - age);
    }

    float core = exp(-d * d / 0.010);
    a += core;
    float3 c = lerp(uMid.rgb, uPeak.rgb, core);
    float4 col = float4(c * a, a);
    return finish(col, uv);
}
