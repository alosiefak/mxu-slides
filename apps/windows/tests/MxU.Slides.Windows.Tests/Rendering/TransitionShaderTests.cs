using MxU.Slides.Core.Animation;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.Tests.Rendering;

// The Windows-only transitions' shaders (Phase 6) compile with the compiler in Windows
// (d3dcompiler_47.dll), as ps_2_0 so WPF can also run them in software.
public class TransitionShaderTests
{
    public static TheoryData<PictureShader> Shaders() => [.. Enum.GetValues<PictureShader>()];

    [Theory]
    [MemberData(nameof(Shaders))]
    public void EachShaderCompilesAsPixelShaderTwo(PictureShader shader)
    {
        var bytes = ShaderCompiler.Bytecode(TransitionShaders.Name(shader), TransitionShaders.Source(shader), out var message, target: TransitionShaders.Target);
        Assert.True(bytes is { Length: > 0 }, $"{shader}: {message}");
        // A compiled ps_2_0 shader starts with its version token: 0xFFFF0200.
        Assert.Equal(0xFFFF0200u, BitConverter.ToUInt32(bytes, 0));
    }

    [Fact]
    public void EveryWindowsKindHasACompilableShader()
    {
        foreach (var kind in WindowsTransitions.WindowsOnly)
        {
            var shader = PictureTransitions.ShaderFor(kind);
            Assert.NotNull(ShaderCompiler.Bytecode(TransitionShaders.Name(shader), TransitionShaders.Source(shader), out _, target: TransitionShaders.Target));
        }
    }

    [Fact]
    public void AShaderThatDoesntFitIsReportedNotThrown()
    {
        // 40 texture reads is more than ps_2_0 allows (32): the compiler says so, and nothing throws.
        var reads = string.Join(" + ", Enumerable.Range(0, 40).Select(i => $"tex2D(s, uv * {i + 1}.0)"));
        var hlsl = $"sampler2D s : register(s0); float4 main(float2 uv : TEXCOORD) : COLOR {{ return {reads}; }}";
        Assert.Null(ShaderCompiler.Bytecode("too-big", hlsl, out var message, target: "ps_2_0"));
        Assert.False(string.IsNullOrWhiteSpace(message));
    }
}
