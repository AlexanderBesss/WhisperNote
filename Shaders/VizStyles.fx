// Compile each entry separately:
//   fxc /O3 /T ps_2_0 /E <entry> /Fo Shaders\viz_<entry>.ps Shaders\VizStyles.fx
// ps_2_0 is mandatory: WPF's milcore rejects modern fxc ps_3_0 bytecode and
// ShaderEffect then silently falls back to rendering the unshaded source.
// Every entry must sample content: fxc strips provably unused samplers and
// ShaderEffect then refuses to render the effect at all (see finish()).
//
// s1 carries the live audio state as a 96x4 gray texture written by the
// control: row 0 = 20 spectrum bins (0..1), row 1 = 64 waveform samples
// (v*0.5+0.5), row 2 = 96 oscilloscope history cells (v*0.5+0.5), row 3 =
// [level, peak, spare, spare] (currently unread; the uniforms carry scalars).

sampler2D content : register(s0);
sampler2D audio : register(s1) = sampler_state
{
    AddressU = Clamp;
    AddressV = Clamp;
    MinFilter = POINT;
    MagFilter = POINT;
    MipFilter = POINT;
};

float uTime : register(c0);
float uLevel : register(c1);
float4 uPeak : register(c2);
float4 uMid : register(c3);
float4 uAux : register(c4); // w = peak hold; x/y/z unused
float uAspect : register(c5); // element width / height, from the control

float binAt(float x)
{
    float b = floor(clamp(x, 0.0, 0.9999) * 20.0);
    return tex2D(audio, float2((b + 0.5) / 96.0, 0.125)).r;
}

float waveAt(float x)
{
    float pos = clamp(x, 0.0, 1.0) * 63.0;
    float i = floor(pos);
    float f = pos - i;
    float a = tex2D(audio, float2((i + 0.5) / 96.0, 0.375)).r;
    float b = tex2D(audio, float2((min(i + 1.0, 63.0) + 0.5) / 96.0, 0.375)).r;
    return (a * (1.0 - f) + b * f) * 2.0 - 1.0;
}

float traceAt(float x)
{
    float pos = clamp(x, 0.0, 1.0) * 95.0;
    float i = floor(pos);
    float f = pos - i;
    float a = tex2D(audio, float2((i + 0.5) / 96.0, 0.625)).r;
    float b = tex2D(audio, float2((i + 1.5) / 96.0, 0.625)).r;
    return (a * (1.0 - f) + b * f) * 2.0 - 1.0;
}

float4 finish(float4 col, float2 uv)
{
    // fxc drops provably unused samplers and ShaderEffect requires a texture input at s0.
    float4 src = tex2D(content, uv);
    return min(col, src + float4(1, 1, 1, 1));
}

float4 spectrum(float2 uv : TEXCOORD) : COLOR
{
    float h = binAt(uv.x) * (1.0 + 0.15 * uAux.w);
    float top = 1.0 - clamp(0.06 + h * 0.88, 0.06, 0.97);
    float inside = smoothstep(top - 0.015, top + 0.015, uv.y);
    float lx = frac(uv.x * 20.0);
    float edge = smoothstep(0.0, 0.12, lx) * smoothstep(1.0, 0.88, lx);
    float shimmer = 0.94 - 0.12 * abs(frac(uTime * 0.6 + uv.x * 6.4) - 0.5);

    float a = edge * inside * (0.35 + 0.60 * h) * shimmer + edge * (1.0 - inside) * 0.10;
    float3 c = lerp(uMid.rgb, uPeak.rgb, h);
    float4 col = float4(c * a, a);
    return finish(col, uv);
}

float4 waveform(float2 uv : TEXCOORD) : COLOR
{
    float y = 0.5 - waveAt(uv.x) * (0.16 + uLevel * 0.30);
    float curve = exp(-(uv.y - y) * (uv.y - y) / 0.0016);
    float track = exp(-(uv.y - 0.5) * (uv.y - 0.5) / 0.02) * 0.12;
    float shimmer = 0.85 + 0.15 * sin(uv.x * 40.0 - uTime * 6.0);

    float a = curve * (0.55 + 0.45 * uLevel) * shimmer + track;
    float hold = exp(-(uv.y - (0.5 - uAux.w * 0.44)) * (uv.y - (0.5 - uAux.w * 0.44)) / 0.0006);
    float3 c = lerp(uMid.rgb, uPeak.rgb, curve);

    float4 col = float4(c * a + uMid.rgb * hold * 0.25, a + hold * 0.25);
    return finish(col, uv);
}

float4 levelMeter(float2 uv : TEXCOORD) : COLOR
{
    float2 p = uv - float2(0.5, 0.5);
    float2 q = abs(p) - float2(0.30, 0.045);
    float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - 0.045;
    float inside = 1.0 - smoothstep(0.0, 0.010, d);

    float fillEdge = 0.20 + uLevel * 0.60;
    float fill = step(uv.x, fillEdge) * inside;
    float ticks = step(0.85, frac(uv.x * 16.0));
    float pulse = 0.94 + 0.06 * sin(uTime * 5.0);

    float a = inside * 0.14 * (1.0 - fill) + fill * (0.55 + 0.40 * uLevel) * pulse;
    a += (1.0 - fill) * inside * ticks * 0.10;
    float3 c = lerp(uMid.rgb, uPeak.rgb, fill);

    float holdX = 0.20 + uAux.w * 0.60;
    float hold = exp(-(uv.x - holdX) * (uv.x - holdX) / 0.0002) * inside * 0.5;
    float4 col = float4((c * a + uMid.rgb * hold), a + hold);
    return finish(col, uv);
}

float4 oscilloscope(float2 uv : TEXCOORD) : COLOR
{
    float y = 0.5 - traceAt(uv.x) * 0.40;
    float curve = exp(-(uv.y - y) * (uv.y - y) / 0.0012);

    float gx = abs(frac(uv.x * 24.0) - 0.5);
    float gy = abs(frac(uv.y * 8.0) - 0.5);
    float grid = max(0.30 - gx, 0.30 - gy);
    grid = smoothstep(0.0, 0.08, grid) * 0.06;

    float sx = frac(uTime * 0.25);
    float dx = uv.x - sx;
    dx = min(abs(dx), abs(abs(dx) - 1.0));
    float sweep = exp(-dx * dx / 0.0008) * (0.10 + uLevel * 0.25);

    float a = curve * (0.55 + 0.45 * uLevel) + grid + sweep;
    float3 c = lerp(uMid.rgb, uPeak.rgb, curve + sweep);
    float4 col = float4(c * a, a);
    return finish(col, uv);
}

float cheapHash(float x, float y)
{
    float t = frac(x * 0.61803 + y * 1.3137) * 17.13;
    return frac(t * 10.0);
}

float4 livingWave(float2 uv : TEXCOORD) : COLOR
{
    float base = sin(uv.x * 6.2832 + uTime * 1.7);
    float a = 0.0;
    float strength = 0.0;
    for (int i = 0; i < 4; i += 1)
    {
        float fi = (float)i;
        float e = binAt(fi * 0.25 + 0.12);
        float dr = uv.y - (0.5 + (fi - 1.5) * 0.07 + base * (0.02 + fi * 0.015) + e * 0.10);
        float band = exp(-dr * dr / 0.0022);
        a += band * (i == 1 || i == 2 ? 0.55 : 0.28);
        strength = max(strength, band);
    }

    float breathe = 0.92 + 0.10 * uLevel + 0.15 * uAux.w;
    float3 c = lerp(uMid.rgb, uPeak.rgb, strength);
    float4 col = float4(c * a * breathe, a * breathe);
    return finish(col, uv);
}

float4 frequencyCity(float2 uv : TEXCOORD) : COLOR
{
    float b = floor(uv.x * 20.0);
    float h = binAt((b + 0.5) / 20.0) + 0.12;
    float inBody = step(0.86 - h * 0.70, uv.y) * step(uv.y, 0.86);

    float fx = frac(uv.x * 80.0);
    float wy = floor((0.86 - uv.y) * 24.0);
    float t = cheapHash(floor(uv.x * 80.0), wy + floor(uTime * 1.5));
    float lit = step(0.62 - 0.30 * h - 0.15 * uAux.w, t)
              * step(0.25, fx) * step(fx, 0.75) * inBody;

    float winA = lit * (0.55 + 0.45 * uLevel);
    float a = inBody * (0.20 + 0.25 * h) + winA + step(0.86, uv.y) * (0.03 + 0.08 * uAux.w);
    float3 c = lerp(uMid.rgb, uPeak.rgb, min(lit + winA, 1.0));
    float4 col = float4(c * a, a);
    return finish(col, uv);
}
