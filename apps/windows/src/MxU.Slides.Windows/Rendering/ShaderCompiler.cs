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
    /// thumbnails, pictures, and PCs that can't capture on the graphics card). Safe to call from any
    /// thread: the shader comes back frozen. The compiling happens outside the lock, so a shader being
    /// warmed up on another thread (TransitionShaders.WarmUp) never holds up one the screen needs now.
    /// </summary>
    public static PixelShader? Compile(string name, string hlsl, string entryPoint = "main", string target = "ps_3_0")
    {
        lock (Cache)
            if (Cache.TryGetValue(name, out var cached)) return cached;
        PixelShader? shader = null;
        try
        {
            if (Bytecode(name, hlsl, out var message, entryPoint, target) is { } bytes)
            {
                shader = new PixelShader();
                shader.SetStreamSource(new MemoryStream(bytes));
                shader.Freeze();
            }
            else
            {
                Log.Error($"Shader {name} didn't compile: {message}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Shader {name} couldn't be compiled on this PC", ex);
        }
        lock (Cache)
        {
            // Two threads may have compiled it at once: keep the first, so everyone shares one.
            if (Cache.TryGetValue(name, out var first)) return first;
            Cache[name] = shader;
            return shader;
        }
    }

    /// <summary>The compiled bytes of a shader, or null with the compiler's complaint in <paramref name="message"/> (the unit tests use this).</summary>
    public static byte[]? Bytecode(string name, string hlsl, out string message, string entryPoint = "main", string target = "ps_3_0")
    {
        var source = Encoding.UTF8.GetBytes(hlsl);
        var hr = D3DCompile(source, (nuint)source.Length, name, IntPtr.Zero, IntPtr.Zero, entryPoint, target,
            OptimizationLevel3, 0, out var code, out var errors);
        message = errors is null ? $"0x{hr:X8}" : Marshal.PtrToStringAnsi(errors.GetBufferPointer(), (int)errors.GetBufferSize()) ?? "";
        if (hr < 0 || code is null) return null;
        var bytes = new byte[(int)code.GetBufferSize()];
        Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);
        return bytes;
    }
}
