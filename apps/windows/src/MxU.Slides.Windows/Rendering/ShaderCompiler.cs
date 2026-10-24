using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Effects;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// Compiles HLSL pixel shaders for WPF effects at run time with the shader compiler
/// that ships in every copy of Windows 10 and 11 (<c>d3dcompiler_47.dll</c>), so the
/// repo holds readable shader source rather than compiled binaries. Results are cached.
/// </summary>
public static class ShaderCompiler
{
    [ComImport, Guid("8BA5FB08-5195-40E2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3DBlob
    {
        [PreserveSig]
        IntPtr GetBufferPointer();

        [PreserveSig]
        nuint GetBufferSize();
    }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DCompile(byte[] source, nuint sourceSize, string? sourceName, IntPtr defines, IntPtr include,
        string entryPoint, string target, uint flags1, uint flags2, out ID3DBlob? code, out ID3DBlob? errors);

    private const uint OptimizationLevel3 = 1 << 15;

    private static readonly Dictionary<string, PixelShader?> Cache = [];

    /// <summary>
    /// A WPF pixel shader from HLSL, or null (logged) if this PC can't compile or run it. Shaders are
    /// ps_3_0 unless they ask for ps_2_0, which WPF can also run without a graphics card (in software:
    /// thumbnails, pictures, and PCs that can't capture on the graphics card).
    /// </summary>
    public static PixelShader? Compile(string name, string hlsl, string entryPoint = "main", string target = "ps_3_0")
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            PixelShader? shader = null;
            try
            {
                var source = Encoding.UTF8.GetBytes(hlsl);
                var hr = D3DCompile(source, (nuint)source.Length, name, IntPtr.Zero, IntPtr.Zero, entryPoint, target,
                    OptimizationLevel3, 0, out var code, out var errors);
                if (hr < 0 || code is null)
                {
                    var message = errors is null ? $"0x{hr:X8}" : Marshal.PtrToStringAnsi(errors.GetBufferPointer(), (int)errors.GetBufferSize());
                    Log.Error($"Shader {name} didn't compile: {message}");
                }
                else
                {
                    var bytes = new byte[(int)code.GetBufferSize()];
                    Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);
                    shader = new PixelShader();
                    shader.SetStreamSource(new MemoryStream(bytes));
                    shader.Freeze();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Shader {name} couldn't be compiled on this PC", ex);
            }
            Cache[name] = shader;
            return shader;
        }
    }
}
