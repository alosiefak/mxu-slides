using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

/// <summary>A throwaway folder (and library in it) for one test, deleted afterwards.</summary>
public abstract class ScratchFolderTest : IDisposable
{
    protected ScratchFolderTest() => Directory.CreateDirectory(Scratch);

    protected string Scratch { get; } = Path.Combine(Path.GetTempPath(), "MxU-core-test-" + Guid.NewGuid().ToString("N"));

    protected string PathFor(string name) => Path.Combine(Scratch, name);

    /// <summary>Writes a small file with this text and returns its path.</summary>
    protected string WriteFile(string name, string text)
    {
        var path = PathFor(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    protected LibraryStore NewLibrary(string name = "library")
    {
        var library = new LibraryStore(PathFor(name));
        library.Load();
        return library;
    }

    public void Dispose()
    {
        try { Directory.Delete(Scratch, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    public static MediaItem Media(string id, string hash, string name = "Clip", MediaKind kind = MediaKind.Video) => new()
    {
        Id = id,
        Name = name,
        MediaKind = kind,
        Classification = MediaClassification.Background,
        FileHash = hash,
        FileName = name + (kind == MediaKind.Video ? ".mp4" : ".png"),
        FileStatus = MediaFileStatus.Ready,
        StatusDetail = "",
    };

    public static AudioItem Audio(string id, string hash, string name) => new()
    {
        Id = id, Name = name, FileHash = hash, FileName = name + ".mp3", Tags = [], Favorite = false,
    };
}

/// <summary>A prober for tests: every file of a known kind is the size and status it's told.</summary>
public sealed class FakeProber(MediaFileStatus status = MediaFileStatus.Ready, string detail = "", string? skip = null) : IMediaProber
{
    public List<string> Probed { get; } = [];

    public MediaProbe? Probe(string path)
    {
        Probed.Add(path);
        if (LibraryStore.MediaKindOf(path) is not { } kind) return null;
        return new MediaProbe(kind, status, detail, kind == MediaKind.Video ? 12.5 : null, 1920, 1080) { SkipReason = skip };
    }
}
