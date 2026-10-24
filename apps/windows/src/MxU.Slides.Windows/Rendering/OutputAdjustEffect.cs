using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// The per-screen correction pass, on the graphics card: a corner-pin (keystone)
/// warp, brightness, contrast, gamma, black level and RGB levels, edge-blend ramps
/// for projectors that overlap, and an optional mask. Line-for-line port of the Mac's
/// <c>output_adjust_fragment</c> Metal shader. Applied to the element that shows the
/// whole canvas; texture coordinates are 0–1 across it.
/// </summary>
public sealed class OutputAdjustEffect : ShaderEffect
{
    // Registers: c0 colour (brightness, contrast, gamma, black level), c1 RGB levels + mask on,
    // c2–c5 edge blends (width, curve, intensity, black lift) left/right/top/bottom,
    // c6 the slice's source rect, c7–c9 the homography rows (output uv → canvas uv).
    private const string Hlsl = """
        sampler2D input : register(s0);
        sampler2D mask : register(s1);
        float4 color : register(c0);
        float4 rgbLevels : register(c1);
        float4 blendLeft : register(c2);
        float4 blendRight : register(c3);
        float4 blendTop : register(c4);
        float4 blendBottom : register(c5);
        float4 sourceRect : register(c6);
        float4 warp0 : register(c7);
        float4 warp1 : register(c8);
        float4 warp2 : register(c9);

        float2 edgeBlend(float distance, float4 edge)
        {
            if (edge.x <= 0.0) return float2(1.0, 0.0);
            float t = saturate(distance / max(edge.x, 1e-4));
            float ramp = pow(t, max(edge.y, 0.01));
            return float2(1.0 - edge.z * (1.0 - ramp), edge.w * ramp);
        }

        float4 main(float2 position : TEXCOORD) : COLOR
        {
            float3 p = float3(position, 1.0);
            float q = dot(warp2.xyz, p);
            float2 uv = float2(dot(warp0.xyz, p), dot(warp1.xyz, p)) / max(q, 1e-6);
            float2 local = (uv - sourceRect.xy) / sourceRect.zw;
            if (any(local < 0.0) || any(local > 1.0)) return float4(0, 0, 0, 0);
            float4 c = tex2D(input, uv);
            float alpha = c.a;
            float3 rgb = alpha > 0.0 ? c.rgb / alpha : c.rgb;
            rgb = max(rgb + color.w, 0.0);
            rgb = rgb + color.x;
            rgb = (rgb - 0.5) * (1.0 + color.y) + 0.5;
            rgb = rgb * (1.0 + rgbLevels.xyz);
            rgb = pow(max(rgb, 0.0), 1.0 / clamp(1.0 + color.z, 0.05, 20.0));
            float2 l = edgeBlend(local.x, blendLeft);
            float2 r = edgeBlend(1.0 - local.x, blendRight);
            float2 t = edgeBlend(local.y, blendTop);
            float2 b = edgeBlend(1.0 - local.y, blendBottom);
            float falloff = l.x * r.x * t.x * b.x;
            float lift = max(max(l.y, r.y), max(t.y, b.y));
            float coverage = rgbLevels.w > 0.5 ? tex2D(mask, uv).r : 1.0;
            float outAlpha = alpha * falloff * coverage;
            return float4(rgb * outAlpha + lift * alpha * coverage, outAlpha);
        }
        """;

    private static readonly Point4D NoBlend = new(0, 1, 1, 0);

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(OutputAdjustEffect), 0);
    public static readonly DependencyProperty MaskProperty = RegisterPixelShaderSamplerProperty("Mask", typeof(OutputAdjustEffect), 1);
    public static readonly DependencyProperty ColorProperty = Register(nameof(Color), 0, new Point4D());
    public static readonly DependencyProperty RgbLevelsProperty = Register(nameof(RgbLevels), 1, new Point4D());
    public static readonly DependencyProperty BlendLeftProperty = Register(nameof(BlendLeft), 2, NoBlend);
    public static readonly DependencyProperty BlendRightProperty = Register(nameof(BlendRight), 3, NoBlend);
    public static readonly DependencyProperty BlendTopProperty = Register(nameof(BlendTop), 4, NoBlend);
    public static readonly DependencyProperty BlendBottomProperty = Register(nameof(BlendBottom), 5, NoBlend);
    public static readonly DependencyProperty SourceRectProperty = Register(nameof(SourceRect), 6, new Point4D(0, 0, 1, 1));
    public static readonly DependencyProperty Warp0Property = Register(nameof(Warp0), 7, new Point4D(1, 0, 0, 0));
    public static readonly DependencyProperty Warp1Property = Register(nameof(Warp1), 8, new Point4D(0, 1, 0, 0));
    public static readonly DependencyProperty Warp2Property = Register(nameof(Warp2), 9, new Point4D(0, 0, 1, 0));

    private static DependencyProperty Register(string name, int register, Point4D fallback) =>
        DependencyProperty.Register(name, typeof(Point4D), typeof(OutputAdjustEffect),
            new UIPropertyMetadata(fallback, PixelShaderConstantCallback(register)));

    /// <summary>The compiled shader, or null when this PC can't run ps_3_0 shaders (then corrections are skipped).</summary>
    public static PixelShader? Shader =>
        RenderCapability.IsPixelShaderVersionSupported(3, 0) ? ShaderCompiler.Compile("OutputAdjust", Hlsl) : null;

    public OutputAdjustEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(MaskProperty);
        foreach (var property in new[] { ColorProperty, RgbLevelsProperty, BlendLeftProperty, BlendRightProperty, BlendTopProperty,
                     BlendBottomProperty, SourceRectProperty, Warp0Property, Warp1Property, Warp2Property })
            UpdateShaderValue(property);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }

    /// <summary>White where the picture shows, black where it's masked out (red channel), across the canvas.</summary>
    public Brush? Mask { get => (Brush?)GetValue(MaskProperty); set => SetValue(MaskProperty, value); }

    /// <summary>Brightness, contrast, gamma, black level (each 0 = unchanged).</summary>
    public Point4D Color { get => (Point4D)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }

    /// <summary>Red, green, blue level offsets (0 = unchanged); W is 1 when <see cref="Mask"/> is set.</summary>
    public Point4D RgbLevels { get => (Point4D)GetValue(RgbLevelsProperty); set => SetValue(RgbLevelsProperty, value); }

    public Point4D BlendLeft { get => (Point4D)GetValue(BlendLeftProperty); set => SetValue(BlendLeftProperty, value); }
    public Point4D BlendRight { get => (Point4D)GetValue(BlendRightProperty); set => SetValue(BlendRightProperty, value); }
    public Point4D BlendTop { get => (Point4D)GetValue(BlendTopProperty); set => SetValue(BlendTopProperty, value); }
    public Point4D BlendBottom { get => (Point4D)GetValue(BlendBottomProperty); set => SetValue(BlendBottomProperty, value); }

    /// <summary>The part of the canvas this screen shows (x, y, width, height in 0–1).</summary>
    public Point4D SourceRect { get => (Point4D)GetValue(SourceRectProperty); set => SetValue(SourceRectProperty, value); }

    public Point4D Warp0 { get => (Point4D)GetValue(Warp0Property); set => SetValue(Warp0Property, value); }
    public Point4D Warp1 { get => (Point4D)GetValue(Warp1Property); set => SetValue(Warp1Property, value); }
    public Point4D Warp2 { get => (Point4D)GetValue(Warp2Property); set => SetValue(Warp2Property, value); }

    /// <summary>Sets the warp from a row-major 3×3 homography (output uv → canvas uv), as Core's OutputWarp computes it.</summary>
    public void SetWarp(IReadOnlyList<double> h)
    {
        Warp0 = new Point4D(h[0], h[1], h[2], 0);
        Warp1 = new Point4D(h[3], h[4], h[5], 0);
        Warp2 = new Point4D(h[6], h[7], h[8], 0);
    }

    protected override Freezable CreateInstanceCore() => new OutputAdjustEffect();
}
