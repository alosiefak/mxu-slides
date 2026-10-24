using System.Security.Cryptography;
using System.Text;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.PptxImport;

public sealed record PptxImportOptions
{
    /// <summary>The library folder the imported presentation lands in.</summary>
    public string? Folder { get; init; } = PptxImporter.DefaultFolder;
}

public enum PptxImportPhase
{
    Reading,
    Slides,
    Media,
    Saving,
}

public sealed record PptxImportProgress(string SourcePath, PptxImportPhase Phase, int Completed, int Total);

/// <summary>
/// The outcome for one deck. A deck that could not be imported has no presentation id
/// and its reason as the only warning. <see cref="StoredFonts"/> are the deck's embedded
/// fonts, copied to <c>&lt;library&gt;/fonts</c> for the app to load (the Mac registers them).
/// </summary>
public sealed record PptxImportResult(
    string SourcePath,
    string? PresentationId,
    string Name,
    IReadOnlyList<string> Warnings,
    int MediaImported,
    IReadOnlyList<string> MissingFonts,
    IReadOnlyList<string> StoredFonts)
{
    public bool Succeeded => PresentationId is not null;

    public string? FailureReason => Succeeded ? null : Warnings.FirstOrDefault();
}

/// <summary>
/// Imports PowerPoint decks into the library as presentations. Each deck gets an
/// id derived from its path, so importing the same file again replaces it in place.
/// Ordinary problems (a damaged file, a legacy .ppt, an unsafe archive) come back as
/// a failed <see cref="PptxImportResult"/>; only cancellation throws.
/// </summary>
public static class PptxImporter
{
    public const string DefaultFolder = "PowerPoint Import";

    public static Task<PptxImportResult> ImportAsync(
        string path, LibraryStore library, PptxImportOptions? options = null,
        IProgress<PptxImportProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ImportOne(path, library, options ?? new PptxImportOptions(), progress, cancellationToken), cancellationToken);

    /// <summary>Imports files and folders of decks (.pptx and .ppt, searched recursively, in name order).</summary>
    public static async Task<IReadOnlyList<PptxImportResult>> ImportItemsAsync(
        IEnumerable<string> paths, LibraryStore library, PptxImportOptions? options = null,
        IProgress<PptxImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var results = new List<PptxImportResult>();
        foreach (var path in Expand(paths))
            results.Add(await ImportAsync(path, library, options, progress, cancellationToken).ConfigureAwait(false));
        return results;
    }

    /// <summary>The one-line summary the Mac shows after an import.</summary>
    public static string Summary(IReadOnlyCollection<PptxImportResult> results)
    {
        var summary = $"Imported {results.Count(r => r.Succeeded)} of {results.Count} PowerPoint decks";
        var mediaCount = results.Sum(r => r.MediaImported);
        if (mediaCount > 0) summary += $" — {mediaCount} media files";
        var warned = results.Count(r => r.Warnings.Count > 0);
        if (warned > 0) summary += $" — {warned} with warnings";
        return summary;
    }

    private static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!Directory.Exists(path))
            {
                yield return path;
                continue;
            }
            var decks = Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true })
                .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".pptx" or ".ppt")
                .OrderBy(Path.GetFileName, StringComparer.Ordinal);
            foreach (var deck in decks) yield return deck;
        }
    }

    private static PptxImportResult ImportOne(
        string path, LibraryStore library, PptxImportOptions options,
        IProgress<PptxImportProgress>? progress, CancellationToken cancellationToken)
    {
        var fallbackName = Path.GetFileNameWithoutExtension(path);
        PptxImportResult Skipped(string reason) => new(path, null, fallbackName, [reason], 0, [], []);
        void Report(PptxImportPhase phase, int completed, int total) =>
            progress?.Report(new PptxImportProgress(path, phase, completed, total));

        if (Path.GetExtension(path).Equals(".ppt", StringComparison.OrdinalIgnoreCase))
            return Skipped("legacy .ppt is not supported — open it in PowerPoint and save as .pptx");

        var sourceId = PresentationId(Path.GetFullPath(path));
        string? extractedDirectory = null;
        try
        {
            PptxMappedDocument mapped;
            var fontWarnings = new List<string>();
            var storedFonts = new List<string>();
            try
            {
                Report(PptxImportPhase.Reading, 0, 1);
                extractedDirectory = PptxArchive.Extract(path, cancellationToken);
                var package = new PptxPackage(extractedDirectory);
                var activatedFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var font in PptxPresentationParser.EmbeddedFonts(package))
                {
                    if (FontActivator.Store(font.FilePath, library.Root) is { } stored)
                    {
                        if (!storedFonts.Contains(stored)) storedFonts.Add(stored);
                        activatedFamilies.Add(font.Typeface);
                    }
                    else
                    {
                        var variant = font.Variant == "regular" ? "" : $" ({font.Variant})";
                        fontWarnings.Add($"embedded font \"{font.Typeface}\"{variant} could not be activated — restricted or unreadable");
                    }
                }

                var warnings = new List<string>();
                var parsed = PptxPresentationParser.Parse(package, warnings);
                var slides = new List<PptxSlide>();
                for (var index = 0; index < parsed.SlidePartPaths.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Report(PptxImportPhase.Slides, index, parsed.SlidePartPaths.Count);
                    slides.Add(PptxSlideParser.Parse(parsed.SlidePartPaths[index], package, warnings, parsed.PartPath));
                }
                var deck = new PptxDeck(parsed.SlideWidthEmu, parsed.SlideHeightEmu)
                {
                    Slides = slides,
                    Sections = parsed.Sections,
                    Warnings = warnings,
                };
                // A family counts as present when this deck brought it, Windows has it, or an earlier
                // import already saved it into the library (the app loads those for drawing).
                var libraryFamilies = new HashSet<string>(Core.Library.FontFiles.Families(library.Root), StringComparer.OrdinalIgnoreCase);
                mapped = PptxDocumentMapper.Map(deck, fallbackName, sourceId,
                    family => activatedFamilies.Contains(family) || FontActivator.IsInstalled(family) || libraryFamilies.Contains(family));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Skipped($"not a readable PowerPoint document: {e.Message}");
            }
            var allWarnings = mapped.Warnings.Concat(fontWarnings).ToList();

            var wants = mapped.MediaWants.ToList();
            if (mapped.Bakes.Count > 0)
            {
                wants.AddRange(TextureBaker.BakeAll(
                    mapped.Bakes, mapped.Presentation.CanvasWidth ?? 1920, mapped.Presentation.CanvasHeight ?? 1080, extractedDirectory));
            }

            var resolution = ResolveMedia(wants, library, report: (done, total) => Report(PptxImportPhase.Media, done, total), cancellationToken);
            allWarnings.AddRange(resolution.Warnings);

            var presentation = PptxDocumentMapper.ReplacingMediaIds(mapped.Presentation, resolution.IdMap);
            presentation.Folder = options.Folder;
            Report(PptxImportPhase.Saving, 0, 1);
            try
            {
                library.Save(presentation);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Skipped($"could not save: {e.Message}");
            }
            return new PptxImportResult(path, presentation.Id, presentation.Name, allWarnings, resolution.Imported, mapped.MissingFonts, storedFonts);
        }
        finally
        {
            if (extractedDirectory is not null) PptxArchive.TryDelete(extractedDirectory);
        }
    }

    private sealed record Resolution(Dictionary<string, string> IdMap, List<string> Warnings, int Imported);

    /// <summary>
    /// Port of the Mac's ImportMediaResolver for absolute paths: reuse library media
    /// with the same content hash, otherwise import it as foreground media.
    /// </summary>
    private static Resolution ResolveMedia(
        IReadOnlyList<MediaWant> wants, LibraryStore library, Action<int, int> report, CancellationToken cancellationToken)
    {
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var imported = 0;
        var mediaByHash = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in library.All<MediaItem>()) mediaByHash.TryAdd(item.FileHash, item.Id);

        for (var index = 0; index < wants.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            report(index, wants.Count);
            var want = wants[index];
            var fileName = Path.GetFileName(want.AbsolutePath);
            if (!File.Exists(want.AbsolutePath))
            {
                warnings.Add($"media not found: {fileName}");
                continue;
            }
            string? hash;
            try
            {
                hash = LibraryStore.HashFile(want.AbsolutePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                hash = null;
            }
            if (hash is not null && mediaByHash.TryGetValue(hash, out var existing))
            {
                idMap[want.PlaceholderId] = existing;
                continue;
            }

            MediaItem? media;
            try
            {
                media = library.ImportMedia(want.AbsolutePath, MediaClassification.Foreground);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"media skipped ({fileName}): {e.Message}");
                continue;
            }
            if (media is null)
            {
                warnings.Add($"media skipped ({fileName}): not a supported image or video file");
                continue;
            }
            if (media.MediaKind == MediaKind.Video && media.Loops)
            {
                // Slide videos are foreground clips that play once, as on the Mac.
                media.Loops = false;
                library.Save(media);
            }
            idMap[want.PlaceholderId] = media.Id;
            imported++;
            mediaByHash[media.FileHash] = media.Id;
        }
        return new Resolution(idMap, warnings, imported);
    }

    /// <summary>A stable UUID-shaped id from the source path's SHA-256, so re-importing a file replaces it.</summary>
    internal static string PresentationId(string sourcePath)
    {
        var hex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath)).AsSpan(0, 16));
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }
}
