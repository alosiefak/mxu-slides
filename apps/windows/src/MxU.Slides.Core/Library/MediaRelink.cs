using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// Putting a file back behind a media item: locating a missing file, replacing an item's file
/// everywhere it's used, and bringing back a deleted item that something still uses. Every use
/// of an item points at its id, never at its file, so changing the item's file fixes every
/// slide, service and playlist at once. Port of the Mac's MediaRelink and the relinking half of
/// its AppModel (replaceMediaFile, restoreMediaTombstone, missingMediaTargets, locateMissingMedia).
/// </summary>
public static class MediaRelink
{
    /// <summary>
    /// A deleted item, brought back with a new file. It keeps its id (so everything that used
    /// it works again), its tags, trim and settings, and takes the new file's name.
    /// </summary>
    public static MediaItem Restored(MediaItem tombstone, string hash, string filePath, MediaProbe probe)
    {
        var item = MxuJson.Clone(tombstone);
        item.FileHash = hash;
        item.FileName = Path.GetFileName(filePath);
        item.Name = Path.GetFileNameWithoutExtension(filePath);
        probe.ApplyTo(item);
        return item;
    }

    /// <summary>The settings made for the old file that now apply to the new one, worth a check ("Trim", "Play Rate"…).</summary>
    public static IReadOnlyList<string> CarriedSettings(MediaItem item)
    {
        var carried = new List<string>();
        if (item.InPoint is not null || item.OutPoint is not null) carried.Add("Trim");
        if (item.PlayRate is { } rate && rate != 1) carried.Add("Play Rate");
        if (item.Effects is { Count: > 0 }) carried.Add("Effects");
        if (item.Transition is not null) carried.Add("Transition");
        if (item.Actions is { Count: > 0 }) carried.Add("Actions");
        if (item.AutoAdvance is not null) carried.Add("Auto Advance");
        return carried;
    }

    /// <summary>A stand-in for an item nobody has any record of, so a slide pointing at it can still be given a file.</summary>
    public static MediaItem PlaceholderTombstone(string id, string? name) => new()
    {
        Id = id,
        Name = name ?? "Deleted media",
        MediaKind = MediaKind.Video,
        Classification = MediaClassification.Background,
        FileHash = "",
        FileName = "",
        FileStatus = MediaFileStatus.Ready,
        StatusDetail = "",
    };

    /// <summary>
    /// Gives an item another file (Replace everywhere, or Locate with a file of your choice).
    /// Returns the settings carried over from the old file (none when it's the same file), or
    /// null when the file can't be used.
    /// </summary>
    public static IReadOnlyList<string>? ReplaceFile(LibraryStore library, string id, string filePath)
    {
        if (library.Get<MediaItem>(id) is not { } current || Probe(library, filePath) is not { } probe) return null;
        var hash = library.Blobs.Store(filePath);
        var item = MxuJson.Clone(current);
        item.FileHash = hash;
        item.FileName = Path.GetFileName(filePath);
        probe.ApplyTo(item);
        library.Save(item);
        return hash == current.FileHash ? [] : CarriedSettings(current);
    }

    /// <summary>Brings a deleted item back under its own id with a new file; null when the file can't be used.</summary>
    public static IReadOnlyList<string>? RestoreTombstone(LibraryStore library, MediaItem tombstone, string filePath)
    {
        if (Probe(library, filePath) is not { } probe) return null;
        var hash = library.Blobs.Store(filePath);
        library.Save(Restored(tombstone, hash, filePath, probe));
        return hash == tombstone.FileHash ? [] : CarriedSettings(tombstone);
    }

    /// <summary>The other item that already has this file, if any (replacing would make the two show the same picture).</summary>
    public static MediaItem? HoldingFile(LibraryStore library, string hash, string exceptId) =>
        library.All<MediaItem>().Where(m => m.FileHash == hash && m.Id != exceptId)
            .OrderBy(m => m.Name, MediaUsage.NameOrder).FirstOrDefault();

    private static MediaProbe? Probe(LibraryStore library, string filePath)
    {
        if (library.Prober is { } prober) return prober.Probe(filePath) is { SkipReason: null } probe ? probe : null;
        // Without a prober (tests) a file of a known kind is taken as it is.
        return LibraryStore.MediaKindOf(filePath) is { } kind ? new MediaProbe(kind, MediaFileStatus.Ready, "", null, null, null) : null;
    }
}

/// <summary>
/// Media whose file isn't on this PC: items whose file is gone (deleted from the Blobs
/// folder, or a library brought from another computer without its files), and deleted items
/// that slides or services still use ("tombstones"). The Mac keeps a tombstone when an item is
/// deleted; on Windows a deleted item's document waits in the library's Trash folder, so that is
/// where tombstones come from.
/// </summary>
public static class MissingMedia
{
    public sealed record Targets(IReadOnlyList<MediaItem> Missing, IReadOnlyList<MediaItem> Tombstones);

    /// <summary>A file found by a folder scan for an item whose content it doesn't match, only its file name.</summary>
    public sealed record NameMatch(MediaItem Item, bool IsTombstone, string Path);

    public static bool IsFileMissing(LibraryStore library, MediaItem item) => library.BlobPath(item) is null;

    /// <summary>
    /// The ids among these that ought to be a picture or video but have no item: deleted, or
    /// never imported here. Music is left out (a timeline's music clip keeps its song's id in the
    /// same field as a video's), and so are built-in sources ("input::item::…").
    /// </summary>
    public static IEnumerable<string> UnknownIds(LibraryStore library, IEnumerable<string> ids) =>
        ids.Where(id => !id.Contains("::", StringComparison.Ordinal) && library.Get<MediaItem>(id) is null && library.Get<AudioItem>(id) is null);

    /// <summary>How many of the media a document uses have no file here, deleted items included.</summary>
    public static int MissingCount(LibraryStore library, IReadOnlySet<string> ids)
    {
        var present = ids.Select(library.Get<MediaItem>).OfType<MediaItem>().ToList();
        return UnknownIds(library, ids).Count() + MissingIds(library, present).Count;
    }

    /// <summary>Which of these items' files are missing, from one look at the Blobs folder.</summary>
    public static HashSet<string> MissingIds(LibraryStore library, IEnumerable<MediaItem> items)
    {
        var list = items.ToList();
        var present = library.Blobs.Presence(list.Select(m => m.FileHash));
        return list.Where(m => !present.Contains(m.FileHash)).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Everything there is to locate: items with no file, and deleted items still used
    /// somewhere: from the Trash if their document is there, otherwise a placeholder.
    /// </summary>
    public static Targets Find(LibraryStore library)
    {
        var items = library.All<MediaItem>();
        var missingIds = MissingIds(library, items);
        var missing = items.Where(m => missingIds.Contains(m.Id)).ToList();
        var referenced = MediaUsage.Documents(library).SelectMany(d => d.MediaIds).ToHashSet(StringComparer.Ordinal);
        var trashed = TrashedItems(library);
        var tombstones = UnknownIds(library, referenced).Order(StringComparer.Ordinal)
            .Select(id => trashed.GetValueOrDefault(id) ?? MediaRelink.PlaceholderTombstone(id, null))
            .ToList();
        return new Targets(missing, tombstones);
    }

    /// <summary>The newest copy of each media item in the library's Trash folder, by id.</summary>
    public static Dictionary<string, MediaItem> TrashedItems(LibraryStore library)
    {
        var found = new Dictionary<string, (MediaItem Item, DateTime When)>(StringComparer.Ordinal);
        var folder = Path.Combine(library.TrashDirectory, LibraryStore.FolderFor(typeof(MediaItem)));
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    var item = MxuJson.Deserialize<MediaItem>(File.ReadAllBytes(file));
                    var when = File.GetLastWriteTimeUtc(file);
                    if (item.Id.Length > 0 && (!found.TryGetValue(item.Id, out var held) || when > held.When)) found[item.Id] = (item, when);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    // A damaged file in the Trash just isn't offered.
                }
            }
        }
        return found.ToDictionary(p => p.Key, p => p.Value.Item, StringComparer.Ordinal);
    }

    /// <summary>
    /// Looks through a folder (and the folders inside it) for exact copies of the files wanted,
    /// by content, so names don't matter. Returns hash → file. Stops once everything is found.
    /// </summary>
    public static Dictionary<string, string> Scan(string folder, IReadOnlySet<string> wanted, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in MediaFiles(folder))
        {
            if (found.Count >= wanted.Count) break;
            cancellationToken.ThrowIfCancellationRequested();
            string hash;
            try { hash = LibraryStore.HashFile(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (wanted.Contains(hash)) found.TryAdd(hash, file);
        }
        return found;
    }

    /// <summary>
    /// Files in the folder with the same name as an item's original file but different content
    /// (re-exported, re-encoded). Never relinked by themselves: the window offers each one.
    /// </summary>
    public static List<NameMatch> NameMatches(string folder, IEnumerable<(MediaItem Item, bool IsTombstone)> targets)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in MediaFiles(folder)) byName.TryAdd(Path.GetFileName(file), file);
        return [.. targets.Where(t => t.Item.FileName.Length > 0 && byName.ContainsKey(t.Item.FileName))
            .Select(t => new NameMatch(t.Item, t.IsTombstone, byName[t.Item.FileName]))];
    }

    /// <summary>
    /// Scans a folder and relinks every exact copy it finds: a missing item just gets its file
    /// back (nothing about it changes), a tombstone is restored under its own id.
    /// </summary>
    public static (int Relinked, int Restored, List<NameMatch> ByName) Locate(LibraryStore library, string folder, CancellationToken cancellationToken = default)
    {
        var targets = Find(library);
        var wantedByHash = new Dictionary<string, (MediaItem Item, bool IsTombstone)>(StringComparer.Ordinal);
        foreach (var item in targets.Missing.Where(m => m.FileHash.Length > 0)) wantedByHash[item.FileHash] = (item, false);
        foreach (var item in targets.Tombstones.Where(m => m.FileHash.Length > 0)) wantedByHash[item.FileHash] = (item, true);
        var matches = wantedByHash.Count == 0 ? [] : Scan(folder, wantedByHash.Keys.ToHashSet(StringComparer.Ordinal), cancellationToken);
        int relinked = 0, restored = 0;
        foreach (var (hash, file) in matches)
        {
            var (item, isTombstone) = wantedByHash[hash];
            if (!isTombstone)
            {
                library.Blobs.Store(file, hash);
                relinked++;
            }
            else if (MediaRelink.RestoreTombstone(library, item, file) is not null)
            {
                restored++;
            }
        }
        var unmatched = targets.Missing.Select(m => (m, false)).Concat(targets.Tombstones.Select(t => (t, true)))
            .Where(t => !matches.ContainsKey(t.Item1.FileHash));
        return (relinked, restored, NameMatches(folder, unmatched));
    }

    private static IEnumerable<string> MediaFiles(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        return Directory.EnumerateFiles(folder, "*", options).Where(f => LibraryStore.MediaKindOf(f) is not null);
    }
}
