using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// What a look inside a media file found, the way the Mac's MediaRelink.Probe and
/// MediaImporter.probeVideo report it: what kind it is, whether this PC can play it
/// (<see cref="FileStatus"/>, with the reason in <see cref="StatusDetail"/>), its length and
/// picture size. <see cref="HasVideo"/> and <see cref="HasAudio"/> say which tracks it has;
/// the documents have no field for them (nor does the Mac store them), so they only shape the
/// status and the import's report.
/// </summary>
public sealed record MediaProbe(
    MediaKind MediaKind,
    MediaFileStatus FileStatus,
    string StatusDetail,
    double? DurationSeconds,
    int? PixelWidth,
    int? PixelHeight)
{
    public bool HasVideo { get; init; }

    public bool HasAudio { get; init; }

    /// <summary>Set when the file can't be used at all (a picture Windows can't decode): the import skips it, saying why.</summary>
    public string? SkipReason { get; init; }

    /// <summary>Copies what the probe found onto an item (its id, name and settings are kept).</summary>
    public void ApplyTo(MediaItem item)
    {
        item.MediaKind = MediaKind;
        item.FileStatus = FileStatus;
        item.StatusDetail = StatusDetail;
        item.DurationSeconds = DurationSeconds;
        item.PixelWidth = PixelWidth;
        item.PixelHeight = PixelHeight;
    }
}

/// <summary>
/// Looks inside media files. The Windows app's one uses Windows' own decoders (Media
/// Foundation and the imaging codecs); Core stays free of them, so tests can use a fake.
/// </summary>
public interface IMediaProber
{
    /// <summary>What the file is; null when it isn't a picture or video at all. Never throws.</summary>
    MediaProbe? Probe(string path);
}

/// <summary>
/// Bringing image and video files into the library as new items. The import itself (which
/// asks about duplicates first) is the app's; this is the part that copies the file in and
/// writes the item. Port of the file-handling half of the Mac's MediaImporter.
/// </summary>
public static class MediaImporting
{
    /// <summary>
    /// The files to import: files as they are, folders opened up (sorted by name, hidden files
    /// left out), so dropping a folder imports what's in it. MediaImporter.files(in:).
    /// </summary>
    public static IReadOnlyList<string> Files(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (!Directory.Exists(path))
            {
                files.Add(path);
                continue;
            }
            var children = new DirectoryInfo(path).EnumerateFileSystemInfos()
                .Where(info => !info.Name.StartsWith('.') && !info.Attributes.HasFlag(FileAttributes.Hidden))
                .OrderBy(info => info.Name, StringComparer.Ordinal)
                .Select(info => info.FullName);
            files.AddRange(Files(children));
        }
        return files;
    }

    /// <summary>
    /// Copies a file in and saves a new item for it, named <paramref name="name"/> (default:
    /// the file's name). What the file is comes from <paramref name="probe"/> or, if none is
    /// given, the library's <see cref="LibraryStore.Prober"/>. A picture that can't be read is
    /// skipped (null), as on the Mac, while a video this PC can't play is still kept but
    /// flagged, so the problem shows now rather than in the middle of the service.
    /// </summary>
    public static MediaItem? AddMedia(LibraryStore library, string sourcePath, string? name = null,
        MediaClassification? classification = null, string? knownHash = null, MediaProbe? probe = null)
    {
        if (LibraryStore.MediaKindOf(sourcePath) is not { } kind) return null;
        probe ??= library.Prober?.Probe(sourcePath);
        if ((library.Prober is not null && probe is null) || probe?.SkipReason is not null) return null;
        if (probe is not null) kind = probe.MediaKind;
        var hash = library.Blobs.Store(sourcePath, knownHash ?? LibraryStore.HashFile(sourcePath));
        var item = new MediaItem
        {
            Id = LibraryStore.NewId(),
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(sourcePath) : name,
            MediaKind = kind,
            Classification = classification ?? (kind == MediaKind.Video ? MediaClassification.Background : MediaClassification.Foreground),
            FileHash = hash,
            FileName = Path.GetFileName(sourcePath),
            FileStatus = MediaFileStatus.Ready,
            StatusDetail = "",
            Loops = kind == MediaKind.Video,
            ImportedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds() / 1000.0,
        };
        probe?.ApplyTo(item);
        library.Save(item);
        return item;
    }
}
