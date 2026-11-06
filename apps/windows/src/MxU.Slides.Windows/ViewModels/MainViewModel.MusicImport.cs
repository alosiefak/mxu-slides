using MxU.Slides.Audio.Library;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>A music file the library already has, as the duplicate prompt asks about it.</summary>
public sealed record AudioImportQuestion(bool SameFile, AudioItem Existing, string IncomingPath, string CopyName, int OthersLeft);

/// <summary>
/// Importing music with the Import buttons: the same duplicate question as pictures and videos
/// (MainViewModel.MediaImport.cs, the Mac's importFiles, which asks for music too). The library
/// already has the very same file, or a different file under the same name: use the song you have,
/// or add the new one as "Amazing Grace 2". Each file is hashed and copied on a background thread;
/// only the question and the redraw are on the UI thread.
/// </summary>
public sealed partial class MainViewModel
{
    private Func<AudioImportQuestion, MediaImportAnswer>? askAudioImportConflict;

    /// <summary>The duplicate prompt for music (the self-test answers it itself).</summary>
    public Func<AudioImportQuestion, MediaImportAnswer> AskAudioImportConflict
    {
        get => askAudioImportConflict ??= LibraryDepthDialogs.AskAudioImportConflict;
        set => askAudioImportConflict = value;
    }

    private Task<List<AudioItem>> lastAudioImport = Task.FromResult(new List<AudioItem>());

    /// <summary>
    /// Imports music files (folders are opened up), asking about duplicates. Imports run one after
    /// another, so two never ask over each other. Returns the songs imported or reused, in order.
    /// </summary>
    public Task<List<AudioItem>> ImportAudioFilesAsync(IEnumerable<string> paths)
    {
        var previous = lastAudioImport;
        var list = paths.ToList();
        return lastAudioImport = AfterPrevious();

        async Task<List<AudioItem>> AfterPrevious()
        {
            try { await previous; }
            catch (Exception ex) { Log.Warn($"An earlier music import failed: {ex.Message}"); }
            return await ImportAudioNow(list);
        }
    }

    private async Task<List<AudioItem>> ImportAudioNow(IReadOnlyList<string> paths)
    {
        var files = await OffUiThread(() => AudioImporter.Files(paths).ToList());
        var music = files.Where(AudioImporter.IsAudioFile).ToList();
        var problems = files.Except(music).Select(f => $"{Path.GetFileName(f)} (not a music file)").ToList();
        var dedupe = new MediaImportDedupe([], Library.All<AudioItem>());
        var importer = new AudioImporter(Library);
        bool? useExistingForRest = null;
        var results = new List<AudioItem>();
        int added = 0, reused = 0;
        for (var index = 0; index < music.Count; index++)
        {
            var path = music[index];
            var fileName = Path.GetFileName(path);
            Notice = music.Count == 1 ? $"Importing {fileName}…" : $"Importing {fileName} ({index + 1} of {music.Count})…";
            var hash = await OffUiThread(() => TryHash(path));
            if (hash is null)
            {
                problems.Add($"{fileName} (can't be read)");
                continue;
            }
            var name = Path.GetFileNameWithoutExtension(path);
            (AudioItem? Item, bool SameFile) existing = dedupe.Find(hash, name, MediaImportDedupe.Kind.Audio) switch
            {
                MediaImportDedupe.Match.SameFile same => (Library.Get<AudioItem>(same.Id), true),
                MediaImportDedupe.Match.SameName named => (Library.Get<AudioItem>(named.Id), false),
                _ => (null, false),
            };
            string? copyName = null;
            if (existing.Item is { } held)
            {
                var numbered = dedupe.AvailableName(held.Name, MediaImportDedupe.Kind.Audio);
                bool useExisting;
                if (useExistingForRest is { } rest)
                {
                    useExisting = rest;
                }
                else
                {
                    var answer = AskAudioImportConflict(new AudioImportQuestion(existing.SameFile, held, path, numbered, music.Count - index - 1));
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
            AudioItem? item;
            try
            {
                item = await OffUiThread(() => importer.Import(path, copyName, reuseSameFile: false, knownHash: hash));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Error($"Importing {path} failed", ex);
                problems.Add($"{fileName} ({ex.Message})");
                continue;
            }
            if (item is null)
            {
                problems.Add($"{fileName} (not a music file)");
                continue;
            }
            added++;
            dedupe.Note(item.Id, hash, item.Name, MediaImportDedupe.Kind.Audio);
            results.Add(item);
        }
        RefreshAudio();
        if (music.Count > 0 || problems.Count > 0)
        {
            var said = new List<string>();
            if (added > 0) said.Add(added == 1 ? "Imported 1 music file" : $"Imported {added} music files");
            if (reused > 0) said.Add(reused == 1 ? "1 was already in the library" : $"{reused} were already in the library");
            Notice = said.Count > 0 ? string.Join("; ", said) + "." : "Nothing imported.";
            if (problems.Count > 0) Problem = "Couldn't import " + string.Join(", ", problems.Take(4)) + (problems.Count > 4 ? $" and {problems.Count - 4} more" : "") + ".";
        }
        return results;
    }
}
