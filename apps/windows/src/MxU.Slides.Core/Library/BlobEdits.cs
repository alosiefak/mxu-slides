using System.Text.Json;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// Media files edited where they live. Someone opens a picture straight from the library's Blobs
/// folder, touches it up and saves: the file is named by the hash of its old bytes, so the library
/// must take it in again under its new hash, and every item that used it follows (keeping its name
/// and settings: <see cref="MediaRelink.Refingerprinted"/>). Port of the Mac's "library files are
/// editable again, and an edited file takes its new bytes" (BlobStore.editedFiles, LibraryFileEdits).
/// <para>
/// To notice an edit cheaply, each file's last-write time is remembered when the library knows its
/// bytes are right (the Mac writes it into the file's extended attributes; Windows keeps a small
/// map, <c>blob-times.json</c>, beside the library). A file whose time has moved on since is a
/// candidate; it's only looked at once it has been still for <see cref="Settle"/> (an editor may
/// save in several goes), and only adopted if its bytes really changed.
/// </para>
/// A difference from the Mac: a file with no time remembered yet (the first launch of this
/// version) is noted as it is, not hashed, so a big media library isn't read end to end at once.
/// </summary>
public sealed class BlobEdits
{
    public const string FileName = "blob-times.json";

    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    private readonly string folder;
    private readonly string mapPath;
    private readonly Lock gate = new();
    private Dictionary<string, long>? known;

    public BlobEdits(string blobFolder, string libraryRoot)
    {
        folder = blobFolder;
        mapPath = Path.Combine(libraryRoot, FileName);
    }

    /// <summary>Notes that <paramref name="path"/>'s bytes are the ones the library knows (after an import, a check, an adoption).</summary>
    public void StampKnown(string path)
    {
        try
        {
            var time = File.GetLastWriteTimeUtc(path).Ticks;
            lock (gate) Map()[Path.GetFileName(path)] = time;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The files whose time has moved on since it was noted: those still for <see cref="Settle"/>
    /// (ready to look at), and whether others are still being written (look again later). Files
    /// never noted are noted now, as they are.
    /// </summary>
    public (IReadOnlyList<(string Hash, string Path)> Settled, bool Settling) EditedFiles(DateTime nowUtc)
    {
        var settled = new List<(string, string)>();
        var settling = false;
        if (!Directory.Exists(folder)) return (settled, false);
        lock (gate)
        {
            var map = Map();
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
            {
                var hash = Path.GetFileNameWithoutExtension(file.Name);
                if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) continue;
                var time = file.LastWriteTimeUtc.Ticks;
                if (!map.TryGetValue(file.Name, out var noted))
                {
                    map[file.Name] = time;
                    continue;
                }
                if (noted == time) continue;
                if (nowUtc - file.LastWriteTimeUtc >= Settle) settled.Add((hash.ToLowerInvariant(), file.FullName));
                else settling = true;
            }
        }
        return (settled, settling);
    }

    /// <summary>Forgets a file that has gone (adopted under its new name, or removed).</summary>
    public void Forget(string path)
    {
        lock (gate) Map().Remove(Path.GetFileName(path));
    }

    /// <summary>Writes the map (on the library's chores thread: it's small).</summary>
    public void Save()
    {
        Dictionary<string, long> copy;
        lock (gate) copy = new Dictionary<string, long>(Map(), StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(Path.GetDirectoryName(mapPath))) LibraryStore.AtomicWrite(mapPath, MxuJson.SerializeToUtf8(copy, indented: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private Dictionary<string, long> Map()
    {
        if (known is not null) return known;
        try
        {
            known = File.Exists(mapPath) ? JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllBytes(mapPath)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            known = null;
        }
        known = known is null ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, long>(known, StringComparer.OrdinalIgnoreCase);
        return known;
    }

    /// <summary>
    /// The media files only <paramref name="deleted"/> items used: they leave the library with
    /// them (the Mac's "one Delete takes the file off this Mac", removeBlobs). A file another
    /// media item or piece of music still names stays.
    /// </summary>
    public static IReadOnlyList<string> UnusedAfterDelete(LibraryStore library, IEnumerable<string> deletedHashes)
    {
        var named = library.All<MediaItem>().Select(m => m.FileHash).Concat(library.All<AudioItem>().Select(a => a.FileHash)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. deletedHashes.Where(h => h.Length > 0 && !named.Contains(h)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
