using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// Combines an item's live picture with its trail (what it looked like a moment ago, from
/// <see cref="TrailStore"/>) on the graphics card: Echo keeps the brighter of the two, Ghost
/// Trails mixes them by how much of the trail is kept. The same sums as the Mac's
/// <c>compositor_echo</c> and <c>compositor_ghost_trails</c> shaders, with the trail's drift
/// already worked out on the processor (WPF shaders can't read the previous frame).
/// </summary>
public sealed class TrailEffect : ShaderEffect
{
    // c0: x = mode (0 echo, 1 ghost), y = how much of the trail is kept, z = 1 when there is a trail yet,
    //     w = the effect's opacity (blends the result with the untouched picture).
    private const string Hlsl = """
        sampler2D input : register(s0);
        sampler2D trail : register(s1);
        float4 modeKeep : register(c0);

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float4 fresh = tex2D(input, uv);
            if (modeKeep.z < 0.5) return fresh;
            float4 t = tex2D(trail, uv);
            float4 result = modeKeep.x < 0.5 ? max(fresh, t) : lerp(fresh, t, modeKeep.y);
            return lerp(fresh, result, modeKeep.w);
        }
        """;

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(TrailEffect), 0);

    public static readonly DependencyProperty TrailProperty =
        RegisterPixelShaderSamplerProperty(nameof(Trail), typeof(TrailEffect), 1, SamplingMode.Bilinear);

    public static readonly DependencyProperty ModeKeepProperty = DependencyProperty.Register(nameof(ModeKeep), typeof(Point4D),
        typeof(TrailEffect), new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(0)));

    private static PixelShader? Shader => RenderCapability.IsPixelShaderVersionSupported(3, 0) ? ShaderCompiler.Compile("TrailEffect", Hlsl) : null;

    /// <summary>True when this PC can blend trails on the graphics card; otherwise the trail is drawn under the item instead.</summary>
    public static bool IsSupported => Shader is not null;

    public TrailEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(TrailProperty);
        UpdateShaderValue(ModeKeepProperty);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }

    /// <summary>The trail picture, stretched over the same area as the item's picture.</summary>
    public Brush? Trail { get => (Brush?)GetValue(TrailProperty); set => SetValue(TrailProperty, value); }

    public Point4D ModeKeep { get => (Point4D)GetValue(ModeKeepProperty); set => SetValue(ModeKeepProperty, value); }

    protected override Freezable CreateInstanceCore() => new TrailEffect();
}
