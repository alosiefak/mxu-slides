using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Recording;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Broadcast.Library;

/// <summary>
/// Recordings as media-library items, as on the Mac:
/// <list type="bullet">
/// <item>While recording, the file is written into the library's <c>spool</c> folder (on the
/// same disk as the library, so filing it is a quick rename, not a copy).</item>
/// <item>When it's finished, <see cref="Adopt"/> files it into the library: the file moves
/// into the library's Blobs folder and a <see cref="MediaItem"/> is saved for it, in the
/// "Recordings" folder unless the preset says otherwise, stamped with when it was recorded
/// and by which preset. It then shows in the Media tab like any imported video.</item>
/// <item>A copy can also go to an ordinary folder (Videos, Dropbox, a NAS) with <c>exportFolder</c>.</item>
/// <item><see cref="LatestRecording"/> finds the newest finished recording in a folder, for
/// presets that restream "the latest recording".</item>
/// </list>
/// Port of RecordingLibrary.swift.
/// </summary>
public static class RecordingLibrary
{
    public const string DefaultFolder = "Recordings";

    /// <summary>Where recordings are written until they're finished: <c>&lt;library&gt;\spool</c> (created if missing).</summary>
    public static string SpoolDirectory(string libraryRoot)
    {
        var path = Path.Combine(libraryRoot, "spool");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Files a finished recording into the library and returns its item. Slow for a long
    /// recording (it reads the whole file to fingerprint it), so call it off the UI thread.
    /// The item is saved before the file moves: if the app stops in between, the file is still
    /// in the spool and the next launch finishes the job (finding the same item by its
    /// fingerprint), so nothing is lost or listed twice.
    /// </summary>
    /// <exception cref="IOException">The file couldn't be read or moved.</exception>
    public static MediaItem Adopt(LibraryStore library, string filePath, string folder, DateTimeOffset recordedAt, string? presetId,
        string? exportFolder = null)
    {
        var probe = MediaFileProbe.Read(filePath);
        var fileName = Path.GetFileName(filePath);
        var hash = LibraryStore.HashFile(filePath);
        var blob = Path.Combine(library.BlobDirectory, hash + Path.GetExtension(filePath).ToLowerInvariant());

        var item = library.All<MediaItem>().FirstOrDefault(m => m.FileHash == hash);
        if (item is null)
        {
            item = new MediaItem
            {
                Id = LibraryStore.NewId(),
                Name = Path.GetFileNameWithoutExtension(filePath),
                MediaKind = MediaKind.Video,
                Classification = MediaClassification.Foreground,
                FileHash = hash,
                FileName = fileName,
                FileStatus = probe.IsPlayable ? MediaFileStatus.Ready : MediaFileStatus.NeedsTranscode,
                StatusDetail = probe.Detail,
                Loops = false,
                DurationSeconds = probe.DurationSeconds,
                PixelWidth = probe.Width,
                PixelHeight = probe.Height,
                Folder = string.IsNullOrEmpty(folder) ? null : folder,
                RecordedAt = recordedAt.ToUnixTimeMilliseconds() / 1000.0,
                ImportedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds() / 1000.0,
                RecordedByPresetId = presetId,
            };
            library.Save(item);
        }

        Directory.CreateDirectory(library.BlobDirectory);
        if (File.Exists(blob)) File.Delete(filePath);
        else File.Move(filePath, blob);

        if (!string.IsNullOrWhiteSpace(exportFolder))
        {
            try
            {
                Directory.CreateDirectory(exportFolder);
                ExportCopy(blob, Path.Combine(exportFolder, fileName));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                BroadcastLog.Error("recording.export", $"{fileName}: couldn't copy to the export folder — {e.Message}");
            }
        }
        return item;
    }

    /// <summary>
    /// Files recordings a crash or a quick quit left in the spool (finished files, and
    /// interrupted ones once <see cref="Recorder.RecoverInterrupted"/> has named them). They go
    /// into the default folder, dated by when the file was made.
    /// </summary>
    public static IReadOnlyList<MediaItem> AdoptLeftovers(LibraryStore library, string spool)
    {
        var adopted = new List<MediaItem>();
        if (!Directory.Exists(spool)) return adopted;
        foreach (var file in Directory.EnumerateFiles(spool, "*.mp4").Where(f => !f.EndsWith(Recorder.PartialSuffix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            try
            {
                if (new FileInfo(file).Length == 0) continue;
                adopted.Add(Adopt(library, file, DefaultFolder, File.GetCreationTime(file), presetId: null));
                BroadcastLog.Warning("recording.adopt", $"filed a leftover recording into the library: {Path.GetFileName(file)}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                BroadcastLog.Error("recording.adopt.failed", $"{Path.GetFileName(file)}: {e.Message}");
            }
        }
        return adopted;
    }

    /// <summary>A recording is anything captured (it has a recorded date) or anything filed under "Recordings".</summary>
    public static bool IsRecording(MediaItem item) => item.RecordedAt is not null || InSubtree(item.Folder, DefaultFolder);

    /// <summary>Copies a library file out to <paramref name="destination"/>, replacing what's there; the copy is never read-only.</summary>
    public static void ExportCopy(string blob, string destination)
    {
        if (File.Exists(destination))
        {
            File.SetAttributes(destination, FileAttributes.Normal);
            File.Delete(destination);
        }
        File.Copy(blob, destination);
        var attributes = File.GetAttributes(destination);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(destination, attributes & ~FileAttributes.ReadOnly);
    }

    /// <summary>The newest finished recording in <paramref name="folder"/> (or its subfolders), not older than <paramref name="notBefore"/>.</summary>
    public static MediaItem? LatestRecording(IEnumerable<MediaItem> items, string folder, DateTimeOffset? notBefore = null)
    {
        var cutoff = notBefore?.ToUnixTimeMilliseconds() / 1000.0;
        return items
            .Where(item => item.RecordedAt is { } recordedAt
                && item.FileStatus == MediaFileStatus.Ready
                && InSubtree(item.Folder, folder)
                && (cutoff is null || recordedAt >= cutoff))
            .MaxBy(item => item.RecordedAt ?? 0);
    }

    /// <summary>"Recordings" contains "Recordings" and "Recordings/2026", not "Recordings Archive"; "" contains everything.</summary>
    internal static bool InSubtree(string? itemFolder, string folder)
    {
        var item = itemFolder ?? "";
        if (folder.Length == 0) return true;
        return item == folder || item.StartsWith(folder + "/", StringComparison.Ordinal);
    }
}
