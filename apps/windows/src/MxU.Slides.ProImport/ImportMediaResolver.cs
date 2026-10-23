using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.ProImport;

/// <summary>A media file a mapped document refers to, by placeholder id, with the playback settings ProPresenter gave it.</summary>
public sealed class ProMediaWant(string placeholderId, string? absolutePath, string? relativePath)
{
    public string PlaceholderId { get; } = placeholderId;
    public string? AbsolutePath { get; } = absolutePath;
    public string? RelativePath { get; } = relativePath;
    public double? InPoint { get; set; }
    public double? OutPoint { get; set; }
    public double? PlayRate { get; set; }
    public double? FadeSeconds { get; set; }
    public MediaClassification? Classification { get; set; }
    public bool? Loops { get; set; }
}

/// <summary>
/// Finds the files behind media wants (in a bundle, the show directory, or where ProPresenter left them),
/// reuses library media with the same content hash, and copies new files into the library.
/// </summary>
internal sealed class ImportMediaResolver
{
    public sealed class Resolution
    {
        public Dictionary<string, string> IdMap { get; } = [];
        /// <summary>Always empty on Windows: the library has no audio store yet, so audio wants stay unresolved.</summary>
        public HashSet<string> AudioIds { get; } = [];
        public HashSet<string> ImportedIds { get; } = [];
        public List<string> Warnings { get; } = [];
        public int Imported { get; set; }
        public int Withheld { get; set; }
    }

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".aac", ".aif", ".aiff", ".flac", ".ogg", ".wma", ".caf",
    };

    private readonly LibraryStore library;
    private readonly Dictionary<string, string> mediaByHash = [];

    public ImportMediaResolver(LibraryStore library)
    {
        this.library = library;
        foreach (var item in library.All<MediaItem>())
            mediaByHash.TryAdd(item.FileHash, item.Id);
    }

    public Resolution Resolve(
        IEnumerable<ProMediaWant> wants, string sourceFile, string? bundleRoot = null,
        string? showDirectory = null, bool importNew = true, CancellationToken cancellationToken = default)
    {
        var resolution = new Resolution();
        foreach (var want in wants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = Candidates(want, sourceFile, bundleRoot, showDirectory).FirstOrDefault(File.Exists);
            if (file is null)
            {
                var named = want.AbsolutePath ?? want.RelativePath;
                resolution.Warnings.Add(named is not null
                    ? $"media not found: {FileName(named)}"
                    : "a media reference carried no file path (web or other non-file content) and was skipped");
                continue;
            }

            string? hash = null;
            try { hash = LibraryStore.HashFile(file); }
            catch (Exception) { hash = null; }
            if (hash is not null && mediaByHash.TryGetValue(hash, out var existing))
            {
                resolution.IdMap[want.PlaceholderId] = existing;
                continue;
            }
            if (!importNew)
            {
                resolution.Withheld++;
                continue;
            }
            try
            {
                if (library.ImportMedia(file) is { } item)
                {
                    resolution.IdMap[want.PlaceholderId] = item.Id;
                    resolution.ImportedIds.Add(item.Id);
                    resolution.Imported++;
                    mediaByHash[item.FileHash] = item.Id;
                }
                else
                {
                    var reason = AudioExtensions.Contains(Path.GetExtension(file))
                        ? "audio files aren't supported in the Windows library yet"
                        : "not a supported image or video file";
                    resolution.Warnings.Add($"media skipped ({Path.GetFileName(file)}): {reason}");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                resolution.Warnings.Add($"media skipped ({Path.GetFileName(file)}): {error.Message}");
            }
        }
        return resolution;
    }

    private static string FileName(string path) => path.Split('/', '\\').LastOrDefault(p => p.Length > 0) ?? path;

    private static IEnumerable<string> Candidates(ProMediaWant want, string sourceFile, string? bundleRoot, string? showDirectory)
    {
        if (want.AbsolutePath is { } absolute)
        {
            if (bundleRoot is not null)
            {
                var inside = absolute.TrimStart('/', '\\');
                if (inside.Length > 1 && inside[1] == ':')
                {
                    yield return Path.Join(bundleRoot, inside[0] + inside[2..]);
                    yield return Path.Join(bundleRoot, inside[2..]);
                }
                else
                {
                    yield return Path.Join(bundleRoot, inside);
                }
            }
            yield return absolute;
        }
        if (want.RelativePath is { } relative)
        {
            if (showDirectory is not null)
            {
                yield return Path.Join(showDirectory, relative);
            }
            else
            {
                var guess = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(sourceFile))));
                if (guess is not null) yield return Path.Join(guess, relative);
            }
            if (Path.GetDirectoryName(Path.GetFullPath(sourceFile)) is { } folder) yield return Path.Join(folder, relative);
        }
    }
}
