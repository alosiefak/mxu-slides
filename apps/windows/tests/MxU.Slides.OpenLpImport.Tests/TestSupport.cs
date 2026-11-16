using MxU.Slides.Core.Library;

namespace MxU.Slides.OpenLpImport.Tests;

/// <summary>A temp folder deleted when the test ends.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mxu-openlp-test-" + Guid.NewGuid().ToString("N"));
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
