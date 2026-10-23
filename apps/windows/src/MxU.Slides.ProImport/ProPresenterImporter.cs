using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using RV = Pro.SerializationInterop.RVProtoData;

namespace MxU.Slides.ProImport;

/// <summary>
/// Imports ProPresenter 7 documents (.pro, .probundle, folders of them), playlist bundles (.proPlaylist)
/// and themes (.protheme) into a <see cref="LibraryStore"/>. Work runs on the thread pool; progress is
/// reported through <see cref="IProgress{T}"/>, and an import stops between documents when cancelled.
/// </summary>
public sealed partial class ProPresenterImporter(LibraryStore library)
{
    public const string DefaultFolder = "ProPresenter Import";

    private static readonly ProImportOptions DefaultOptions = new();

    internal LibraryStore Library { get; } = library;

    /// <summary>
    /// Imports whatever the user picked, by kind: .proPlaylist files as playlist bundles, .protheme files
    /// and Theme documents as themes, and everything else (.pro, .probundle, folders) as presentations.
    /// </summary>
    public Task<ProImportResult> ImportFilesAsync(
        IEnumerable<string> paths, ProImportOptions? options = null,
        IProgress<ProImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var all = paths.ToList();
        return Task.Run(async () =>
        {
            var playlists = new List<ProPlaylistBundleSummary>();
            var themes = new List<ProThemeImportSummary>();
            var documents = new List<string>();
            foreach (var path in all)
            {
                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension == ".proplaylist")
                    playlists.Add(await ImportPlaylistBundleCoreAsync(path, options, progress, cancellationToken).ConfigureAwait(false));
                else if (extension == ".protheme" || (File.Exists(path) && Path.GetFileName(path) == "Theme"))
                    themes.Add(await ImportThemeCoreAsync(path, options, progress, cancellationToken).ConfigureAwait(false));
                else
                    documents.Add(path);
            }
            var summaries = documents.Count == 0
                ? []
                : await ImportDocumentsCoreAsync(documents, options, progress, cancellationToken).ConfigureAwait(false);
            return new ProImportResult(summaries, playlists, themes);
        }, cancellationToken);
    }

    /// <summary>Imports .pro documents, .probundle packages and folders holding them.</summary>
    public Task<IReadOnlyList<ProDocumentSummary>> ImportDocumentsAsync(
        IEnumerable<string> paths, ProImportOptions? options = null,
        IProgress<ProImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var all = paths.ToList();
        return Task.Run(() => ImportDocumentsCoreAsync(all, options, progress, cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyList<ProDocumentSummary>> ImportDocumentsCoreAsync(
        List<string> paths, ProImportOptions? options, IProgress<ProImportProgress>? progress, CancellationToken cancellationToken)
    {
        options ??= DefaultOptions;
        var resolver = new ImportMediaResolver(Library);
        var writer = new ImportWriter(Library, options.Policy, options.ConfirmOverwrite);
        try
        {
            var total = CountDocuments(paths);
            var completed = 0;
            return await ImportItemsAsync(
                paths, options.Folder, resolver, writer, cancellationToken: cancellationToken,
                onDocument: path => progress?.Report(new ProImportProgress(
                    "Presentations", Path.GetFileNameWithoutExtension(path), completed++, total))).ConfigureAwait(false);
        }
        finally
        {
            writer.SaveLedger();
        }
    }

    internal async Task<List<ProDocumentSummary>> ImportItemsAsync(
        IEnumerable<string> paths, string? folder, ImportMediaResolver resolver, ImportWriter writer,
        IReadOnlyDictionary<string, string>? timerIdsByProUuid = null,
        IReadOnlyDictionary<string, string>? timerIdsByName = null,
        string? bundleRoot = null, bool importMedia = true, Action<string>? onDocument = null,
        CancellationToken cancellationToken = default)
    {
        var summaries = new List<ProDocumentSummary>();
        foreach (var path in Expand(paths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            onDocument?.Invoke(path);
            summaries.Add(await ImportOneAsync(
                path, folder, resolver, writer, timerIdsByProUuid ?? new Dictionary<string, string>(),
                timerIdsByName ?? new Dictionary<string, string>(), bundleRoot, importMedia, cancellationToken).ConfigureAwait(false));
        }
        return summaries;
    }

    internal static int CountDocuments(IEnumerable<string> paths) => Expand(paths).Count;

    private static List<string> Expand(IEnumerable<string> paths) => paths.SelectMany(path =>
    {
        if (!Directory.Exists(path)) return [path];
        return Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".pro" or ".probundle")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
    }).ToList();

    private async Task<ProDocumentSummary> ImportOneAsync(
        string path, string? folder, ImportMediaResolver resolver, ImportWriter writer,
        IReadOnlyDictionary<string, string> timerIdsByProUuid, IReadOnlyDictionary<string, string> timerIdsByName,
        string? outerBundleRoot, bool importMedia, CancellationToken cancellationToken)
    {
        var fallbackName = Path.GetFileNameWithoutExtension(path);
        ProDocumentSummary Skipped(string reason) => new() { SourcePath = path, Name = fallbackName, Warnings = [reason] };

        var proFile = path;
        var bundleRoot = outerBundleRoot;
        string? extracted = null;
        try
        {
            if (Path.GetExtension(path).Equals(".probundle", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    extracted = ImportArchive.Extract(path);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    return Skipped($"could not extract bundle: {error.Message}");
                }
                bundleRoot = extracted;
                var pro = Directory.EnumerateFiles(extracted).Order(StringComparer.Ordinal)
                    .FirstOrDefault(f => Path.GetExtension(f).Equals(".pro", StringComparison.OrdinalIgnoreCase));
                if (pro is null) return Skipped("no .pro document inside the bundle");
                proFile = pro;
            }

            ProMappedDocument mapped;
            try
            {
                var document = RV.Presentation.Parser.ParseFrom(await File.ReadAllBytesAsync(proFile, cancellationToken).ConfigureAwait(false));
                mapped = ProDocumentMapper.Map(document, fallbackName, timerIdsByProUuid, timerIdsByName);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return Skipped($"not a readable ProPresenter 7 document: {error.Message}");
            }
            var warnings = mapped.Warnings.ToList();

            if (await writer.CheckAsync<Presentation>(mapped.Presentation.Id, mapped.Presentation.Name, cancellationToken).ConfigureAwait(false) is { } reason)
            {
                return new ProDocumentSummary
                {
                    SourcePath = path, PresentationId = mapped.Presentation.Id, Name = mapped.Presentation.Name,
                    GroupHotKeys = mapped.GroupHotKeys, Skipped = reason,
                };
            }

            var resolution = resolver.Resolve(mapped.MediaWants, proFile, bundleRoot, importNew: importMedia, cancellationToken: cancellationToken);
            warnings.AddRange(resolution.Warnings);
            ApplyMediaSettings(mapped.MediaWants, resolution);

            var presentation = ProDocumentMapper.ReplacingMediaIds(mapped.Presentation, resolution.IdMap);
            presentation.Folder = folder;
            if (!writer.Write(presentation, presentation.Name, warnings))
                return Skipped($"could not save: {warnings.LastOrDefault() ?? "unknown error"}");
            return new ProDocumentSummary
            {
                SourcePath = path, PresentationId = presentation.Id, Name = presentation.Name, Warnings = warnings,
                MediaImported = resolution.Imported, GroupHotKeys = mapped.GroupHotKeys, MediaWithheld = resolution.Withheld,
            };
        }
        finally
        {
            ImportArchive.TryDelete(extracted);
        }
    }

    /// <summary>Carries ProPresenter's trim, rate, fade, loop and layer onto media items this import created.</summary>
    internal void ApplyMediaSettings(IEnumerable<ProMediaWant> wants, ImportMediaResolver.Resolution resolution)
    {
        foreach (var want in wants)
        {
            var hasSettings = want.InPoint is not null || want.OutPoint is not null || want.PlayRate is not null
                || want.FadeSeconds is not null || want.Classification is not null || want.Loops is not null;
            if (!hasSettings || !resolution.IdMap.TryGetValue(want.PlaceholderId, out var id)
                || !resolution.ImportedIds.Contains(id) || resolution.AudioIds.Contains(id)
                || Library.Get<MediaItem>(id) is not { } stored)
                continue;
            var item = MxuJson.Clone(stored);
            if (want.Classification is { } classification) item.Classification = classification;
            if (want.Loops is { } loops) item.Loops = loops;
            if (want.InPoint is { } inPoint) item.InPoint = inPoint;
            if (want.OutPoint is { } outPoint) item.OutPoint = outPoint;
            if (want.PlayRate is { } rate) item.PlayRate = rate;
            if (want.FadeSeconds is { } fade) item.Transition = new Transition { TransitionKind = TransitionKind.Dissolve, DurationSeconds = fade };
            Library.Save(item);
        }
    }

    /// <summary>Imports a .proPlaylist bundle: its presentations and media, and each playlist as a service.</summary>
    public Task<ProPlaylistBundleSummary> ImportPlaylistBundleAsync(
        string path, ProImportOptions? options = null,
        IProgress<ProImportProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ImportPlaylistBundleCoreAsync(path, options, progress, cancellationToken), cancellationToken);

    private async Task<ProPlaylistBundleSummary> ImportPlaylistBundleCoreAsync(
        string path, ProImportOptions? options, IProgress<ProImportProgress>? progress, CancellationToken cancellationToken)
    {
        options ??= DefaultOptions;
        var summary = new ProPlaylistBundleSummary { SourcePath = path };
        var resolver = new ImportMediaResolver(Library);
        string extracted;
        try
        {
            extracted = ImportArchive.Extract(path);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            summary.FailureReason = $"could not extract the bundle: {error.Message}";
            return summary;
        }
        try
        {
            var playlistDocs = new List<(RV.PlaylistDocument Doc, string File)>();
            var seenRoots = new HashSet<string>();
            foreach (var file in Directory.EnumerateFiles(extracted, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                         .Order(StringComparer.Ordinal))
            {
                if (Path.GetExtension(file).Equals(".pro", StringComparison.OrdinalIgnoreCase)) continue;
                RV.PlaylistDocument doc;
                try
                {
                    if (new FileInfo(file).Length >= 32 * 1024 * 1024) continue;
                    doc = RV.PlaylistDocument.Parser.ParseFrom(await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    continue;
                }
                var root = doc.RootNode ?? new RV.Playlist();
                if (!ProPlaylistMapper.LeafPlaylists(root).Any(p => p.Uuid.Raw().Length > 0)) continue;
                var rootId = root.Uuid.Lower();
                if (!seenRoots.Add(rootId.Length == 0 ? file : rootId)) continue;
                playlistDocs.Add((doc, file));
            }
            if (playlistDocs.Count == 0)
            {
                summary.FailureReason = "no playlist was found in the bundle";
                return summary;
            }

            var writer = new ImportWriter(Library, options.Policy, options.ConfirmOverwrite);
            try
            {
                var total = CountDocuments([extracted]);
                var completed = 0;
                summary.Documents = await ImportItemsAsync(
                    [extracted], options.Folder, resolver, writer, bundleRoot: extracted, cancellationToken: cancellationToken,
                    onDocument: file => progress?.Report(new ProImportProgress(
                        "Presentations", Path.GetFileNameWithoutExtension(file), completed++, total))).ConfigureAwait(false);
                summary.MediaImported += summary.Documents.Sum(d => d.MediaImported);

                var rootPath = SwiftCompat.FullNormalizedPath(extracted);
                var byOriginalPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var byFileName = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var document in summary.Documents)
                {
                    if (document.PresentationId is not { } id) continue;
                    var standardized = SwiftCompat.FullNormalizedPath(document.SourcePath);
                    if (standardized.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                        byOriginalPath[standardized[rootPath.Length..]] = id;
                    var fileName = Path.GetFileName(document.SourcePath).ToLowerInvariant();
                    byFileName[fileName] = byFileName.ContainsKey(fileName) ? null : id;
                }
                string? ResolvePresentation(ProPlaylistPresentationRef reference)
                {
                    if (reference.AbsolutePath is { } absolute)
                    {
                        var standardized = SwiftCompat.NormalizedPath(absolute);
                        if (byOriginalPath.TryGetValue(standardized, out var id)) return id;
                        if (byFileName.GetValueOrDefault(Path.GetFileName(standardized).ToLowerInvariant()) is { } byName) return byName;
                    }
                    if (reference.RelativePath is { } relative
                        && byFileName.GetValueOrDefault(Path.GetFileName(SwiftCompat.NormalizedPath(relative)).ToLowerInvariant()) is { } match)
                        return match;
                    return null;
                }

                progress?.Report(new ProImportProgress("Playlists"));
                foreach (var (doc, docFile) in playlistDocs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mapped = ProPlaylistMapper.MapPresentationPlaylists(doc, ResolvePresentation);
                    var resolution = resolver.Resolve(mapped.MediaWants, docFile, extracted, cancellationToken: cancellationToken);
                    summary.MediaImported += resolution.Imported;
                    summary.Warnings.AddRange(resolution.Warnings);
                    foreach (var mappedService in mapped.Services)
                    {
                        var (service, dropped) = ProPlaylistMapper.ReplacingMediaIds(mappedService.Service, resolution.IdMap, resolution.AudioIds);
                        var label = $"playlist \"{service.Name}\"";
                        if (await writer.CheckAsync<Service>(service.Id, label, cancellationToken).ConfigureAwait(false) is null
                            && writer.Write(service, label, summary.Warnings))
                            summary.Services.Add(service.Name);
                        summary.Warnings.AddRange(mappedService.Warnings.Concat(dropped).Select(w => $"{label}: {w}"));
                    }
                }
            }
            finally
            {
                writer.SaveLedger();
            }
            writer.AppendSkipSummary(summary.Warnings);
            if (summary.Services.Count == 0 && summary.Documents.Count == 0) summary.FailureReason = "the bundle contained nothing importable";
            return summary;
        }
        finally
        {
            ImportArchive.TryDelete(extracted);
        }
    }

    /// <summary>ProPresenter's show directory on this PC, when it can be found (Documents\ProPresenter).</summary>
    public static string? ProPresenterShowDirectory()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (documents.Length == 0) return null;
        var candidate = Path.Combine(documents, "ProPresenter");
        return Directory.Exists(Path.Combine(candidate, "Libraries")) || Directory.Exists(Path.Combine(candidate, "Configuration"))
            ? candidate : null;
    }
}
