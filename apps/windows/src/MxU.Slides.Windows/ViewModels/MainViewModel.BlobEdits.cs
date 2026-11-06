using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The library's media files, as files. Two of the Mac's newer rules:
/// <list type="bullet">
/// <item><b>An edited file takes its new bytes.</b> A picture opened from the Blobs folder, touched up
/// and saved is noticed (a watch on the folder; the rules are Core's <see cref="BlobEdits"/>), taken in
/// under its new hash, and every item using it follows, keeping its name and settings. A video on
/// screen plays the new file straight away (MediaHub.ReloadInPlace.cs).</item>
/// <item><b>Deleting an item takes its file out of the library</b> when nothing else uses it. The Mac
/// removes the file; here it moves to the library's Trash folder, like the document, so a mistake
/// can still be undone by hand.</item>
/// </list>
/// All the file work (listing, hashing, probing, moving) happens on the library's own background
/// thread; only the saving of the items and the redraw come back to the UI thread.
/// </summary>
public sealed partial class MainViewModel
{
    private BlobEdits? blobEdits;
    private FileSystemWatcher? blobWatcher;
    private int blobScanGeneration;

    /// <summary>Starts watching the media folder for files edited in place (once the window is up).</summary>
    public void StartWatchingMediaFiles()
    {
        if (blobEdits is not null) return;
        Directory.CreateDirectory(Library.BlobDirectory);
        blobEdits = new BlobEdits(Library.BlobDirectory, Library.Root);
        try
        {
            blobWatcher = new FileSystemWatcher(Library.BlobDirectory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            // Events come on Windows' own threads, often several for one save: each just asks for a look soon.
            blobWatcher.Changed += (_, _) => ScheduleBlobScan(TimeSpan.FromSeconds(2));
            blobWatcher.Created += (_, _) => ScheduleBlobScan(TimeSpan.FromSeconds(2));
            blobWatcher.Renamed += (_, _) => ScheduleBlobScan(TimeSpan.FromSeconds(2));
            blobWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            Log.Warn($"Can't watch the media folder for edits: {ex.Message}");
        }
        ScheduleBlobScan(TimeSpan.Zero);
    }

    /// <summary>Looks for edited files now, rather than when the watch next fires (the self-test).</summary>
    internal void CheckMediaFilesSoon() => ScheduleBlobScan(TimeSpan.Zero);

    /// <summary>Whether the library has noted this file's time yet (the self-test edits it only then).</summary>
    internal bool MediaFileNoted(string path) => blobEdits?.Knows(path) == true;

    private void ScheduleBlobScan(TimeSpan after)
    {
        var generation = Interlocked.Increment(ref blobScanGeneration);
        LibraryChores.Queue.AsyncAfter(after, () =>
        {
            // A later event asked again: that look will do.
            if (generation == Volatile.Read(ref blobScanGeneration)) ScanBlobEdits();
        });
    }

    /// <summary>On the chores thread: adopts each edited file that has settled, and looks again later for ones still being written.</summary>
    private void ScanBlobEdits()
    {
        if (blobEdits is not { } edits) return;
        var (settled, settling) = edits.EditedFiles(DateTime.UtcNow);
        foreach (var (oldHash, path) in settled)
        {
            string? current;
            try { current = LibraryStore.HashFile(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { current = null; }
            if (current is null || current.Equals(oldHash, StringComparison.OrdinalIgnoreCase))
            {
                // The same bytes (a touch, a copy over itself), or not readable now: just note it.
                edits.StampKnown(path);
                continue;
            }
            var destination = Path.Combine(Library.BlobDirectory, current + Path.GetExtension(path).ToLowerInvariant());
            try
            {
                if (!File.Exists(destination)) File.Move(path, destination);
                else File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"An edited media file couldn't be taken in: {ex.Message}");
                continue;
            }
            edits.Forget(path);
            edits.StampKnown(destination);
            Library.Blobs.NoteDirectoryChanged();
            var probe = Library.Prober?.Probe(destination);
            var duration = global::MxU.Slides.Audio.Library.AudioImporter.IsAudioFile(destination) ? global::MxU.Slides.Audio.Library.AudioImporter.DurationOf(destination) : null;
            Log.Info($"Media file edited in place: {oldHash} is now {current}");
            OnUi(() => AdoptEditedFile(oldHash, current, probe, duration));
        }
        edits.Save();
        if (settling) ScheduleBlobScan(BlobEdits.Settle);
    }

    /// <summary>Every item that used the old bytes now uses the new ones, keeping its name and settings (the Mac's adoptChangedFile).</summary>
    private void AdoptEditedFile(string oldHash, string newHash, MediaProbe? probe, double? duration)
    {
        var changed = new List<string>();
        using (BatchOfChanges())
        {
            foreach (var item in Library.All<MediaItem>().Where(m => m.FileHash.Equals(oldHash, StringComparison.OrdinalIgnoreCase)))
            {
                Library.Save(MediaRelink.Refingerprinted(item, newHash, probe));
                changed.Add(item.Id);
            }
            foreach (var song in Library.All<AudioItem>().Where(a => a.FileHash.Equals(oldHash, StringComparison.OrdinalIgnoreCase)))
            {
                song.FileHash = newHash;
                song.DurationSeconds = duration ?? song.DurationSeconds;
                Library.Save(song);
            }
        }
        if (changed.Count > 0)
        {
            MediaFilesChanged(changed);
            Notice = changed.Count == 1 && Library.Get<MediaItem>(changed[0]) is { } one
                ? $"“{one.Name}” was edited: its new file is in use everywhere."
                : $"{changed.Count} media items' file was edited: the new file is in use everywhere.";
        }
    }

    /// <summary>
    /// The files of deleted media and music that nothing else uses leave the library's media folder,
    /// for its Trash folder (on the chores thread). Returns how many files go.
    /// </summary>
    private int RemoveUnusedFiles(IEnumerable<string> hashes)
    {
        var unused = BlobEdits.UnusedAfterDelete(Library, hashes);
        if (unused.Count == 0) return 0;
        var library = Library;
        LibraryChores.Queue.Async(() =>
        {
            var trash = Path.Combine(library.TrashDirectory, LibraryBackup.BlobsFolder);
            var moved = new List<(string Hash, string From, string To)>();
            foreach (var hash in unused)
            {
                if (library.Blobs.Find(hash) is not { } file) continue;
                var to = Path.Combine(trash, Path.GetFileName(file));
                try
                {
                    Directory.CreateDirectory(trash);
                    File.Move(file, to, overwrite: true);
                    moved.Add((hash, file, to));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"A deleted item's file couldn't be moved to the Trash: {ex.Message}");
                }
            }
            library.Blobs.NoteDirectoryChanged();
            if (moved.Count == 0) return;
            Log.Info($"{moved.Count} file{(moved.Count == 1 ? "" : "s")} of deleted items moved to the library's Trash");
            // The same file imported again in the moment between (or an undo): it comes straight back.
            OnUi(() =>
            {
                var back = moved.Where(m => BlobEdits.UnusedAfterDelete(library, [m.Hash]).Count == 0).ToList();
                if (back.Count > 0) LibraryChores.Queue.Async(() => MoveBack(library, back));
            });
        });
        return unused.Count;
    }

    private static void MoveBack(LibraryStore library, IEnumerable<(string Hash, string From, string To)> files)
    {
        foreach (var (_, from, to) in files)
        {
            try
            {
                if (!File.Exists(from) && File.Exists(to)) File.Move(to, from);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"A file back in use couldn't come back from the Trash: {ex.Message}");
            }
        }
        library.Blobs.NoteDirectoryChanged();
    }
}
