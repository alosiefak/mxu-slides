using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using MxU.Slides.Core.Animation;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// One Windows-only transition on the graphics card: the shader for its family
/// (<see cref="TransitionShaders"/>), the old picture as <see cref="Previous"/>, the new picture as
/// the effect's own input, and eight registers of numbers (<see cref="Set"/>) that
/// <see cref="PictureTransitions.Mix"/> works out each frame. <see cref="SceneView"/> puts one on a
/// layer while a picture transition plays there.
/// </summary>
public sealed class TransitionEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(TransitionEffect), 0, SamplingMode.Bilinear);

    public static readonly DependencyProperty PreviousProperty =
        RegisterPixelShaderSamplerProperty(nameof(Previous), typeof(TransitionEffect), 1, SamplingMode.Bilinear);

    private static readonly DependencyProperty[] Registers = [.. Enumerable.Range(0, 8).Select(i =>
        DependencyProperty.Register($"C{i}", typeof(Point4D), typeof(TransitionEffect), new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(i))))];

    public TransitionEffect(PictureShader shader)
    {
        Family = shader;
        PixelShader = TransitionShaders.Shader(shader);
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(PreviousProperty);
        foreach (var register in Registers) UpdateShaderValue(register);
    }

    /// <summary>Which shader this is.</summary>
    public PictureShader Family { get; }

    /// <summary>The new picture: left unset, it is what the effect is applied to.</summary>
    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }

    /// <summary>The old picture, stretched over the same area as the new one.</summary>
    public Brush? Previous { get => (Brush?)GetValue(PreviousProperty); set => SetValue(PreviousProperty, value); }

    /// <summary>This frame's numbers. Registers that haven't changed aren't sent again.</summary>
    public void Set(PictureMix mix)
    {
        for (var i = 0; i < Registers.Length; i++)
        {
            var v = i < mix.Constants.Count ? mix.Constants[i] : Vec4.Zero;
            var value = new Point4D(v.X, v.Y, v.Z, v.W);
            if ((Point4D)GetValue(Registers[i]) != value) SetValue(Registers[i], value);
        }
    }

    protected override Freezable CreateInstanceCore() => new TransitionEffect(Family);
}
