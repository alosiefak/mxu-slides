using Google.Protobuf;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using RV = Pro.SerializationInterop.RVProtoData;

namespace MxU.Slides.ProImport.Tests;

/// <summary>A temp folder deleted when the test ends.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mxu-proimport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A LibraryStore in a temp folder.</summary>
internal sealed class TestLibrary : IDisposable
{
    private readonly TempFolder folder = new();

    public TestLibrary()
    {
        Store = new LibraryStore(folder.Path);
        Store.Load();
    }

    public LibraryStore Store { get; }

    public void Dispose() => folder.Dispose();
}

internal sealed class RecordingProgress : IProgress<ProImportProgress>
{
    public List<string> Phases { get; } = [];

    public void Report(ProImportProgress value)
    {
        lock (Phases) Phases.Add(value.Phase);
    }
}

internal static class Proto
{
    public static RV.UUID Uuid(string value) => new() { String = value };

    public static RV.Color Color(float red, float green, float blue, float alpha = 1) =>
        new() { Red = red, Green = green, Blue = blue, Alpha = alpha };

    public static ByteString RtfBytes(string rtf) => ByteString.CopyFromUtf8(rtf);

    public static RV.Slide.Types.Element Wrap(RV.Graphics.Types.Element element) => new() { Element_ = element };

    /// <summary>A file URL as ProPresenter writes it: Mac-style paths keep their slash, Windows paths gain file:///C:/.</summary>
    public static string FileUrl(string path) => path.StartsWith('/')
        ? "file://" + string.Join("/", path.Split('/').Select(Uri.EscapeDataString))
        : new Uri(path).AbsoluteUri;

    /// <summary>A tiny stand-in media file; distinct seeds give distinct content hashes.</summary>
    public static void WriteImage(string path, byte seed = 1)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, seed, seed, seed]);
    }

    public static void Write(IMessage message, string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, message.ToByteArray());
    }

    public static string Json<T>(T value) => MxuJson.Serialize(value, indented: false);
}
