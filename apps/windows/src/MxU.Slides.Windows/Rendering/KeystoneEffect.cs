using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// Keystone on an object: its top or bottom edge drawn narrower or wider, as the Mac's
/// build projector does (<see cref="BuildProjector"/>). WPF's 2-D transforms can only make
/// parallelograms, so the warp is a small pixel shader: for each pixel of the output it works
/// out which point of the untouched item belongs there and reads it.
/// <list type="bullet">
/// <item><b>Perspective</b> (the Mac's default): the trapezoid is a picture tipped back, so the
/// mapping is a homography (Core's <see cref="OutputWarp"/>, the same maths as a screen's corner pin)
/// and the narrow end is foreshortened.</item>
/// <item><b>Stretch</b>: rows stay evenly spaced and each is just squeezed or widened, as the Mac's
/// 32 strips draw it.</item>
/// </list>
/// The effect covers <see cref="Region"/>, the item's frame plus room for the wider edge and
/// its shadow; coordinates in the shader are 0–1 across that region. It is a ps_2_0 shader so
/// WPF can also draw it in software (thumbnails, the editor, the self-test's pictures).
/// </summary>
public sealed class KeystoneEffect : ShaderEffect
{
    // c0 the frame inside the region (x, y, width, height in 0–1), c1 x = 1 for stretch,
    // y/z the top and bottom edge scales, c2–c4 the homography rows (output uv → source uv).
    private const string Hlsl = """
        sampler2D input : register(s0);
        float4 frame : register(c0);
        float4 mode : register(c1);
        float4 warp0 : register(c2);
        float4 warp1 : register(c3);
        float4 warp2 : register(c4);

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float3 p = float3(uv, 1.0);
            float q = dot(warp2.xyz, p);
            float2 perspective = float2(dot(warp0.xyz, p), dot(warp1.xyz, p)) / max(q, 1e-6);

            float2 local = (uv - frame.xy) / frame.zw;
            float edge = max(mode.y + (mode.z - mode.y) * local.y, 1e-4);
            float2 stretch = frame.xy + float2(((local.x * 2.0 - 1.0) / edge + 1.0) * 0.5, local.y) * frame.zw;

            float2 source = mode.x > 0.5 ? stretch : perspective;
            if (mode.x < 0.5 && q <= 0.0) return float4(0, 0, 0, 0);
            if (any(source < 0.0) || any(source > 1.0)) return float4(0, 0, 0, 0);
            return tex2D(input, source);
        }
        """;

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(KeystoneEffect), 0, SamplingMode.Bilinear);
    public static readonly DependencyProperty FrameProperty = Register(nameof(Frame), 0, new Point4D(0, 0, 1, 1));
    public static readonly DependencyProperty ModeProperty = Register(nameof(Mode), 1, new Point4D(0, 1, 1, 0));
    public static readonly DependencyProperty Warp0Property = Register(nameof(Warp0), 2, new Point4D(1, 0, 0, 0));
    public static readonly DependencyProperty Warp1Property = Register(nameof(Warp1), 3, new Point4D(0, 1, 0, 0));
    public static readonly DependencyProperty Warp2Property = Register(nameof(Warp2), 4, new Point4D(0, 0, 1, 0));

    private static DependencyProperty Register(string name, int register, Point4D fallback) =>
        DependencyProperty.Register(name, typeof(Point4D), typeof(KeystoneEffect), new UIPropertyMetadata(fallback, PixelShaderConstantCallback(register)));

    private static PixelShader? Shader =>
        RenderCapability.IsPixelShaderVersionSupported(2, 0) || RenderCapability.IsPixelShaderVersionSupportedInSoftware(2, 0)
            ? ShaderCompiler.Compile("Keystone", Hlsl, target: "ps_2_0")
            : null;

    /// <summary>True when this PC can draw keystone (on the graphics card or in software); otherwise objects draw unwarped.</summary>
    public static bool IsSupported => Shader is not null;

    public KeystoneEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        foreach (var property in new[] { FrameProperty, ModeProperty, Warp0Property, Warp1Property, Warp2Property }) UpdateShaderValue(property);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Point4D Frame { get => (Point4D)GetValue(FrameProperty); set => SetValue(FrameProperty, value); }
    public Point4D Mode { get => (Point4D)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public Point4D Warp0 { get => (Point4D)GetValue(Warp0Property); set => SetValue(Warp0Property, value); }
    public Point4D Warp1 { get => (Point4D)GetValue(Warp1Property); set => SetValue(Warp1Property, value); }
    public Point4D Warp2 { get => (Point4D)GetValue(Warp2Property); set => SetValue(Warp2Property, value); }

    protected override Freezable CreateInstanceCore() => new KeystoneEffect();

    /// <summary>
    /// The area the warp covers, in canvas units: the frame, widened for an edge drawn over 100%,
    /// plus a margin for shadows and outlines that reach past the frame.
    /// </summary>
    public static Rect Region(SceneRect frame, double keystoneTop, double keystoneBottom)
    {
        var widest = Math.Max(1, Math.Max(keystoneTop, keystoneBottom));
        var margin = Math.Max(frame.Width, frame.Height) * 0.1 + 24;
        var dx = frame.Width * (widest - 1) / 2 + margin;
        return new Rect(frame.X - dx, frame.Y - margin, Math.Max(1, frame.Width + 2 * dx), Math.Max(1, frame.Height + 2 * margin));
    }

    /// <summary>Sets the warp for an item's frame inside <paramref name="region"/>; false when the trapezoid is degenerate (draw nothing).</summary>
    public bool Set(SceneRect frame, Rect region, double keystoneTop, double keystoneBottom, bool stretch)
    {
        (double, double) Unit(double x, double y) => ((x - region.X) / region.Width, (y - region.Y) / region.Height);
        var corners = BuildProjector.KeystoneCorners(frame, keystoneTop, keystoneBottom).Select(p => Unit(p.X, p.Y)).ToArray();
        var sources = new[] { Unit(frame.X, frame.Y), Unit(frame.Right, frame.Y), Unit(frame.X, frame.Bottom), Unit(frame.Right, frame.Bottom) };
        var (fx, fy) = sources[0];
        Frame = new Point4D(fx, fy, frame.Width / region.Width, frame.Height / region.Height);
        Mode = new Point4D(stretch ? 1 : 0, keystoneTop, keystoneBottom, 0);
        if (stretch) return keystoneTop > 0 || keystoneBottom > 0;
        if (OutputWarp.CenteredHomography(corners, sources) is not { } h) return false;
        Warp0 = new Point4D(h[0], h[1], h[2], 0);
        Warp1 = new Point4D(h[3], h[4], h[5], 0);
        Warp2 = new Point4D(h[6], h[7], h[8], 0);
        return true;
    }
}
