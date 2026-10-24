using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>What the duplicate prompt is asked: a file the library already has (the same file, or another file under the same name).</summary>
public sealed record MediaImportQuestion(bool SameFile, MediaItem Existing, string IncomingPath, string CopyName, int OthersLeft);

/// <summary>The answer: use the library's item, or add the file as a new copy; and whether to do the same for the rest of the import.</summary>
public sealed record MediaImportAnswer(bool UseExisting, bool ForRest);

/// <summary>
/// Importing pictures and videos (the Import media button, and files dropped on the media bin
/// or the service). Port of the Mac's AppModel.importFiles:
/// <list type="number">
/// <item>Each file is hashed off the UI thread. If the library already has the same file, or a
/// different file under the same name, the operator is asked: <b>Use Existing</b> or <b>Add as
/// New Copy</b> (numbered "Background 2"), with "do the same for the rest" for big drops.</item>
/// <item>A new file is probed with Windows' own decoders (<see cref="LibraryStore.Prober"/>): a
/// picture Windows can't read is skipped, a video it can't play well is kept but flagged
/// "needs transcode", so it's caught now, not during the service.</item>
/// </list>
/// A .mxuslides file in the drop is imported as a presentation.
/// </summary>
public sealed partial class MainViewModel
{
    private Func<MediaImportQuestion, MediaImportAnswer>? askMediaImportConflict;

    /// <summary>The duplicate prompt (the self-test answers it itself).</summary>
    public Func<MediaImportQuestion, MediaImportAnswer> AskMediaImportConflict
    {
        get => askMediaImportConflict ??= question => LibraryDialogs.AskMediaImportConflict(question, Media);
        set => askMediaImportConflict = value;
    }

    private Task<List<MediaItem>> lastMediaImport = Task.FromResult(new List<MediaItem>());

    /// <summary>
    /// Imports files (folders are opened up), asking about duplicates. Imports run one after
    /// another, so two drops never ask their questions over each other. Returns the items
    /// imported or reused, in order; <paramref name="addToService"/> also adds each to the open service.
    /// </summary>
    public Task<List<MediaItem>> ImportMediaFilesAsync(IEnumerable<string> paths, bool addToService = false)
    {
        var previous = lastMediaImport;
        var list = paths.ToList();
        return lastMediaImport = AfterPrevious();

        async Task<List<MediaItem>> AfterPrevious()
        {
            try { await previous; }
            catch (Exception ex) { Log.Warn($"An earlier media import failed: {ex.Message}"); }
            return await ImportMediaNow(list, addToService);
        }
    }

    private async Task<List<MediaItem>> ImportMediaNow(IReadOnlyList<string> paths, bool addToService)
    {
        var files = await OffUiThread(() => MediaImporting.Files(paths));
        var decks = files.Where(IsPresentationFile).ToList();
        var media = files.Where(f => !IsPresentationFile(f) && LibraryStore.MediaKindOf(f) is not null).ToList();
        var problems = files.Except(decks).Except(media).Select(f => $"{Path.GetFileName(f)} (not a picture or video)").ToList();
        var dedupe = new MediaImportDedupe(Library.All<MediaItem>(), []);
        bool? useExistingForRest = null;
        var results = new List<MediaItem>();
        int added = 0, reused = 0, flagged = 0;
        for (var index = 0; index < media.Count; index++)
        {
            var path = media[index];
            var fileName = Path.GetFileName(path);
            Notice = media.Count == 1 ? $"Importing {fileName}…" : $"Importing {fileName} ({index + 1} of {media.Count})…";
            var hash = await OffUiThread(() => TryHash(path));
            if (hash is null)
            {
                problems.Add($"{fileName} (can't be read)");
                continue;
            }
            var name = Path.GetFileNameWithoutExtension(path);
            (MediaItem? Item, bool SameFile) existing = dedupe.Find(hash, name, MediaImportDedupe.Kind.Media) switch
            {
                MediaImportDedupe.Match.SameFile same => (Library.Get<MediaItem>(same.Id), true),
                MediaImportDedupe.Match.SameName named => (Library.Get<MediaItem>(named.Id), false),
                _ => (null, false),
            };
            string? copyName = null;
            if (existing.Item is { } held)
            {
                var numbered = dedupe.AvailableName(held.Name, MediaImportDedupe.Kind.Media);
                bool useExisting;
                if (useExistingForRest is { } rest)
                {
                    useExisting = rest;
                }
                else
                {
                    var answer = AskMediaImportConflict(new MediaImportQuestion(existing.SameFile, held, path, numbered, media.Count - index - 1));
                    useExisting = answer.UseExisting;
                    if (answer.ForRest) useExistingForRest = useExisting;
                }
                if (useExisting)
                {
                    reused++;
                    results.Add(held);
                    continue;
                }
                copyName = numbered;
            }
            var probe = await OffUiThread(() => Library.Prober?.Probe(path));
            if (probe?.SkipReason is { } reason)
            {
                problems.Add($"{fileName} ({reason})");
                continue;
            }
            MediaItem? item;
            try
            {
                item = await OffUiThread(() => MediaImporting.AddMedia(Library, path, copyName, knownHash: hash, probe: probe));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Importing {path} failed", ex);
                problems.Add($"{fileName} ({ex.Message})");
                continue;
            }
            if (item is null)
            {
                problems.Add($"{fileName} (Windows can't read it)");
                continue;
            }
            added++;
            if (item.FileStatus == MediaFileStatus.NeedsTranscode) flagged++;
            dedupe.Note(item.Id, hash, copyName ?? name, MediaImportDedupe.Kind.Media);
            results.Add(item);
        }
        RefreshMedia();
        if (addToService) foreach (var item in results) AddMediaToService(item);
        if (media.Count > 0)
        {
            // The Mac's summary: "Imported 3 of 4 files — 1 already in the library — 1 flagged for transcode".
            var parts = new List<string>();
            if (added > 0 || reused == 0) parts.Add($"Imported {added} of {media.Count} file{(media.Count == 1 ? "" : "s")}");
            if (reused > 0) parts.Add($"{reused} already in the library");
            if (flagged > 0) parts.Add($"{flagged} flagged for transcode (right-click it › Transcode now)");
            if (problems.Count > 0) parts.Add($"{problems.Count} skipped");
            Notice = string.Join(" — ", parts) + ".";
        }
        if (problems.Count > 0) Problem = $"Couldn't import: {string.Join("; ", problems)}";
        if (decks.Count > 0) await ImportPresentationFilesAsync(decks);
        return results;
    }

    private static string? TryHash(string path)
    {
        try { return LibraryStore.HashFile(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Runs disk work (hashing, copying, unzipping, decoding) on a thread of its own, so the
    /// UI never waits for it and the shared thread pool, which show timing keeps clear of, is
    /// left alone.
    /// </summary>
    private static Task<T> OffUiThread<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
