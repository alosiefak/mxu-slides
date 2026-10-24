using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using MxU.Slides.Core.Animation;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// One picture pass on an item, on the graphics card: colour adjust, hue rotate,
/// invert, posterize, pixelate, vignette, warp, scatter, stained glass, grain, and the
/// animation passes glitch, burn and tint. One shader holds them all (a mode picks
/// which); each is a line-for-line port of the matching Mac Metal shader in
/// RenderEngine/ShaderSource.swift. <see cref="Opacity"/> blends with the untouched picture.
/// </summary>
public sealed class ItemEffect : ShaderEffect
{
    // c0: mode, opacity · c1: the pass's parameters (pixels where they're distances) ·
    // c2: WPF fills in how far uv moves per pixel (ddx, ddy), giving the picture's size in pixels.
    private const string Hlsl = """
        sampler2D input : register(s0);
        float4 modeOpacity : register(c0);
        float4 p : register(c1);
        float4 ddxDdy : register(c2);

        float hash3(float3 q)
        {
            q = frac(q * float3(0.1031, 0.1030, 0.0973));
            q += dot(q, q.yxz + 33.33);
            return frac((q.x + q.y) * q.z);
        }

        float noise3(float3 q)
        {
            float3 i = floor(q);
            float3 f = frac(q);
            f = f * f * (3.0 - 2.0 * f);
            float x00 = lerp(hash3(i), hash3(i + float3(1, 0, 0)), f.x);
            float x10 = lerp(hash3(i + float3(0, 1, 0)), hash3(i + float3(1, 1, 0)), f.x);
            float x01 = lerp(hash3(i + float3(0, 0, 1)), hash3(i + float3(1, 0, 1)), f.x);
            float x11 = lerp(hash3(i + float3(0, 1, 1)), hash3(i + float3(1, 1, 1)), f.x);
            return lerp(lerp(x00, x10, f.y), lerp(x01, x11, f.y), f.z);
        }

        float2 scatterOffset(float2 q, float roll)
        {
            float2 c = floor(q);
            return float2(hash3(float3(c, roll)), hash3(float3(c + 71.3, roll + 3.7))) - 0.5;
        }

        float3 hueRotate(float3 rgb, float radians)
        {
            float k = cos(radians), t = sin(radians);
            float3x3 m = float3x3(
                0.213 + k * 0.787 - t * 0.213, 0.715 - k * 0.715 - t * 0.715, 0.072 - k * 0.072 + t * 0.928,
                0.213 - k * 0.213 + t * 0.143, 0.715 + k * 0.285 + t * 0.140, 0.072 - k * 0.072 - t * 0.283,
                0.213 - k * 0.213 - t * 0.787, 0.715 - k * 0.715 + t * 0.715, 0.072 + k * 0.928 + t * 0.072);
            return saturate(mul(m, rgb));
        }

        float4 straightOut(float3 rgb, float a) { return float4(rgb * a, a); }

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float mode = modeOpacity.x;
            float2 size = 1.0 / max(float2(ddxDdy.x, ddxDdy.w), 1e-6);
            float4 c = tex2D(input, uv);
            float3 s = c.a > 0.0 ? c.rgb / c.a : c.rgb;
            float4 result = c;

            if (mode < 1.5)            // colour adjust: brightness, contrast, saturation, hue (radians)
            {
                if (abs(p.w) > 1e-4) s = hueRotate(s, p.w);
                float luma = dot(s, float3(0.2126, 0.7152, 0.0722));
                s = lerp(float3(luma, luma, luma), s, p.z);
                s = (s - 0.5) * (1.0 + p.y) + 0.5;
                s = max(s + p.x, 0.0);
                result = straightOut(s, c.a);
            }
            else if (mode < 2.5) result = straightOut(hueRotate(s, p.x), c.a);
            else if (mode < 3.5) result = straightOut(1.0 - s, c.a);
            else if (mode < 4.5)       // posterize
            {
                float steps = max(p.x - 1.0, 1.0);
                result = straightOut(round(s * steps) / steps, c.a);
            }
            else if (mode < 5.5)       // pixelate
            {
                float2 cell = max(p.x, 1.0) / size;
                result = tex2D(input, (floor(uv / cell) + 0.5) * cell);
            }
            else if (mode < 6.5)       // vignette
            {
                float aspect = size.x / max(size.y, 1.0);
                float2 q = uv - 0.5;
                q.x *= aspect;
                float span = length(float2(0.5 * aspect, 0.5));
                float d = length(q) / max(span, 1e-4);
                float shade = 1.0 - clamp(p.x, 0.0, 1.5) * smoothstep(0.35, 1.0, d);
                result = straightOut(s * max(shade, 0.0), c.a);
            }
            else if (mode < 7.5)       // warp: amount px, cell px, time
            {
                float2 px = uv * size;
                float3 q = float3(px / max(p.y, 1.0), p.z);
                float nx = noise3(q) * 0.7 + noise3(q * 2.0 + 17.0) * 0.3 - 0.5;
                float ny = noise3(q + float3(41.7, 13.3, 7.9)) * 0.7 + noise3(q * 2.0 + float3(29.1, 5.2, 3.3)) * 0.3 - 0.5;
                result = tex2D(input, (px + float2(nx, ny) * 2.0 * p.x) / size);
            }
            else if (mode < 8.5)       // scatter: amount px, roll, size px, smooth
            {
                float2 px = uv * size;
                float2 q = px / max(p.z, 1.0);
                float2 n = scatterOffset(q, p.y);
                if (p.w > 0.001)
                {
                    float2 f = frac(q - 0.5);
                    f = f * f * (3.0 - 2.0 * f);
                    float2 b = q - 0.5;
                    float2 smoothed = lerp(lerp(scatterOffset(b, p.y), scatterOffset(b + float2(1, 0), p.y), f.x),
                                           lerp(scatterOffset(b + float2(0, 1), p.y), scatterOffset(b + float2(1, 1), p.y), f.x), f.y);
                    n = lerp(n, smoothed, p.w);
                }
                result = tex2D(input, (px + n * 2.0 * p.x) / size);
            }
            else if (mode < 9.5)       // stained glass: cell px, leading px, jitter, time
            {
                float cellSize = max(p.x, 2.0);
                float2 q = uv * size / cellSize;
                float2 g = floor(q), f = frac(q);
                float d1 = 8.0, d2 = 8.0;
                float2 best = g + 0.5;
                [unroll] for (int j = -1; j <= 1; j++)
                {
                    [unroll] for (int i = -1; i <= 1; i++)
                    {
                        float2 o = float2(i, j);
                        float2 cc = g + o;
                        float2 h = float2(hash3(float3(cc, 1.0)), hash3(float3(cc, 2.0)));
                        float2 r = 0.5 + (h - 0.5) * p.z;
                        r += 0.15 * p.z * float2(sin(p.w + h.x * 6.2831), cos(p.w + h.y * 6.2831));
                        float dist = length(o + r - f);
                        if (dist < d1) { d2 = d1; d1 = dist; best = cc + r; }
                        else if (dist < d2) { d2 = dist; }
                    }
                }
                float4 pane = tex2D(input, best * cellSize / size);
                float lead = p.y / cellSize;
                float edge = smoothstep(lead, lead + 1.5 / cellSize, d2 - d1);
                result = float4(pane.rgb * edge, pane.a);
            }
            else if (mode < 10.5)      // grain: amount, size px, roll
            {
                float2 cell = floor(uv * size / max(p.y, 1.0));
                float n = hash3(float3(cell, p.z)) - 0.5;
                result = straightOut(saturate(s + n * p.x), c.a);
            }
            else if (mode < 11.5)      // glitch: amount, phase, shift px
            {
                float roll = floor(p.y * 24.0);
                float band = floor(uv.y * 18.0);
                float active = hash3(float3(band, roll, 1.0)) > (1.0 - 0.55 * p.x) ? 1.0 : 0.0;
                float dir = hash3(float3(band, roll, 2.0)) - 0.5;
                float2 at = uv + float2(active * dir * p.z * p.x / max(size.x, 1.0), 0.0);
                float split = p.x * 6.0 / max(size.x, 1.0);
                float4 g = tex2D(input, at);
                float4 r = tex2D(input, at + float2(split, 0.0));
                float4 b = tex2D(input, at - float2(split, 0.0));
                result = float4(r.r, g.g, b.b, max(g.a, max(r.a, b.a)));
            }
            else if (mode < 12.5)      // burn: amount, phase (a warm light leak sweeping across)
            {
                float center = lerp(-0.4, 1.4, p.y);
                float d = (uv.x - center) / 0.35;
                float leak = p.x * exp(-d * d);
                float3 rgb = c.rgb * (1.0 + leak * 1.5) + float3(1.0, 0.72, 0.42) * leak * c.a;
                result = float4(min(rgb, c.a * 4.0), c.a);
            }
            else                       // tint: colour, amount
            {
                result = straightOut(lerp(s, p.xyz, saturate(p.w)), c.a);
            }
            return lerp(c, result, modeOpacity.y);
        }
        """;

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(ItemEffect), 0);

    public static readonly DependencyProperty ModeOpacityProperty = DependencyProperty.Register(nameof(ModeOpacity), typeof(Point4D),
        typeof(ItemEffect), new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty ParametersProperty = DependencyProperty.Register(nameof(Parameters), typeof(Point4D),
        typeof(ItemEffect), new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(1)));

    private static PixelShader? Shader => RenderCapability.IsPixelShaderVersionSupported(3, 0) ? ShaderCompiler.Compile("ItemEffect", Hlsl) : null;

    /// <summary>True when this PC can draw item effects (a graphics card with pixel shader 3.0).</summary>
    public static bool IsSupported => Shader is not null;

    public ItemEffect()
    {
        PixelShader = Shader;
        DdxUvDdyUvRegisterIndex = 2;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(ModeOpacityProperty);
        UpdateShaderValue(ParametersProperty);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }

    public Point4D ModeOpacity { get => (Point4D)GetValue(ModeOpacityProperty); set => SetValue(ModeOpacityProperty, value); }

    public Point4D Parameters { get => (Point4D)GetValue(ParametersProperty); set => SetValue(ParametersProperty, value); }

    /// <summary>
    /// The shader mode and parameters for a pass at <paramref name="hostTime"/>, with distances
    /// turned from canvas units into pixels by <paramref name="pixelsPerUnit"/>; null for passes
    /// drawn elsewhere (blur) or not drawn on Windows (echo, ghost trails).
    /// </summary>
    public static (int Mode, Point4D Parameters)? Pass(AnimationEffect effect, double hostTime, double pixelsPerUnit) => effect switch
    {
        AnimationEffect.ColorAdjust a => (1, new Point4D(a.Brightness, a.Contrast, a.Saturation, a.Hue * Math.PI / 180)),
        AnimationEffect.HueRotate h => (2, new Point4D(h.Degrees * Math.PI / 180, 0, 0, 0)),
        AnimationEffect.Invert => (3, new Point4D()),
        AnimationEffect.Posterize l => (4, new Point4D(l.Levels, 0, 0, 0)),
        AnimationEffect.Pixelate x => (5, new Point4D(x.Size * pixelsPerUnit, 0, 0, 0)),
        AnimationEffect.Vignette v => (6, new Point4D(v.Strength, 0, 0, 0)),
        AnimationEffect.Warp w => (7, new Point4D(w.Amount * pixelsPerUnit, Math.Max(w.Scale * pixelsPerUnit, 1), hostTime * w.Speed, 0)),
        AnimationEffect.Scatter sc => (8, new Point4D(sc.Amount * pixelsPerUnit, sc.Speed > 0.001 ? Math.Floor(hostTime * 60 * sc.Speed) : 0,
            Math.Max(sc.Size * pixelsPerUnit, 1), sc.Smooth)),
        AnimationEffect.StainedGlass g => (9, new Point4D(Math.Max(g.CellSize * pixelsPerUnit, 2), g.Leading * pixelsPerUnit, g.Jitter, hostTime * g.Speed)),
        AnimationEffect.Grain n => (10, new Point4D(n.Amount, Math.Max(n.Size * pixelsPerUnit, 1), n.Speed > 0.001 ? Math.Floor(hostTime * 60 * n.Speed) : 0, 0)),
        AnimationEffect.Glitch gl => (11, new Point4D(gl.Amount, gl.Phase, 40 * pixelsPerUnit, 0)),
        AnimationEffect.Burn b => (12, new Point4D(b.Amount, b.Phase, 0, 0)),
        AnimationEffect.Tint t => (13, new Point4D(t.Color.Red, t.Color.Green, t.Color.Blue, Math.Clamp(t.Amount, 0, 1))),
        _ => null,
    };

    protected override Freezable CreateInstanceCore() => new ItemEffect();
}
