using MxU.Slides.Broadcast.Media;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Broadcast.Library;

/// <summary>
/// Turns a flagged video ("needs transcode") into one that plays: makes an H.264 copy
/// (<see cref="MediaTranscoder"/>), files it into the library's media files and points the
/// item at it. The item keeps its id, name and settings, so every slide and service using it
/// gets the new file. While it works the item says "transcoding"; if it fails it says why
/// ("transcode failed"). Port of the Mac's TranscodeQueue. Slow, so call it on a background
/// thread of its own, never the UI thread or the thread pool's.
/// </summary>
public sealed class TranscodeQueue(LibraryStore library)
{
    /// <summary>Transcodes every item that needs it; returns each one's new status by id.</summary>
    public Dictionary<string, MediaFileStatus> Drain(CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, MediaFileStatus>(StringComparer.Ordinal);
        foreach (var item in library.All<MediaItem>().Where(m => m.FileStatus == MediaFileStatus.NeedsTranscode).ToList())
            results[item.Id] = Transcode(item.Id, null, cancellationToken);
        return results;
    }

    /// <summary>Transcodes one item; returns its new status (TranscodeFailed when it's gone, or it couldn't be done).</summary>
    public MediaFileStatus Transcode(string itemId, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (library.Get<MediaItem>(itemId) is not { } item) return MediaFileStatus.TranscodeFailed;
        if (library.BlobPath(item) is not { } source)
            return Finish(itemId, MediaFileStatus.TranscodeFailed, $"blob missing: {item.FileHash}");
        Finish(itemId, MediaFileStatus.Transcoding, "");
        // Written beside the media files (same disk, so filing it is a rename); ".partial." keeps
        // it out of the store's listing until it's finished.
        var output = Path.Combine(library.BlobDirectory, $"{Guid.NewGuid():N}.partial.mp4");
        try
        {
            MediaTranscoder.ToMp4(source, output, progress, cancellationToken);
            var hash = library.Blobs.Adopt(output);
            var probe = MediaImportProbe.ProbeVideo(library.Blobs.Find(hash) ?? output);
            return Finish(itemId, probe.FileStatus == MediaFileStatus.Ready ? MediaFileStatus.Ready : MediaFileStatus.TranscodeFailed,
                probe.FileStatus == MediaFileStatus.Ready ? "" : $"the copy didn't play either: {probe.StatusDetail}", hash, probe);
        }
        catch (OperationCanceledException)
        {
            TryDelete(output);
            return Finish(itemId, MediaFileStatus.NeedsTranscode, item.StatusDetail);
        }
        catch (Exception e) when (e is MediaTranscoder.TranscodeException or IOException or UnauthorizedAccessException)
        {
            TryDelete(output);
            return Finish(itemId, MediaFileStatus.TranscodeFailed, e.Message);
        }
    }

    /// <summary>Saves the item's new status (on a copy, so nothing holding the old one sees half a change).</summary>
    private MediaFileStatus Finish(string id, MediaFileStatus status, string detail, string? newHash = null, MediaProbe? probe = null)
    {
        if (library.Get<MediaItem>(id) is not { } current) return status;
        var item = MxuJson.Clone(current);
        item.FileStatus = status;
        item.StatusDetail = detail;
        if (newHash is not null)
        {
            item.FileHash = newHash;
            item.DurationSeconds = probe?.DurationSeconds ?? item.DurationSeconds;
            item.PixelWidth = probe?.PixelWidth ?? item.PixelWidth;
            item.PixelHeight = probe?.PixelHeight ?? item.PixelHeight;
        }
        library.Save(item);
        return status;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
