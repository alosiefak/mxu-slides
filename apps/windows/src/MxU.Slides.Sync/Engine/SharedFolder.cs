using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MxU.Slides.Sync.Format;

namespace MxU.Slides.Sync.Engine;

/// <summary>A file's size and last-write time: when neither moved, the file needn't be read again.</summary>
public readonly record struct FileStamp(long Length, DateTime WriteUtc)
{
    public static FileStamp Of(FileInfo file) => new(file.Length, file.LastWriteTimeUtc);
}

/// <summary>
/// The shared folder: a Mac library laid out as the Mac lays out its own (DocumentStore.swift,
/// BlobStore.swift), or a Mac backup (a .mxubackup folder, BackupBundle.swift, which has the same
/// layout plus a manifest):
/// <code>
/// presentations/&lt;id&gt;.automerge     one Automerge document per library document
/// services/  themes/  media/  …     a folder per kind (MacKind.DirectoryName)
/// blobs/&lt;sha256&gt;.&lt;ext&gt;           media files, named by their contents
/// manifest.json                     only in a backup: its format version
/// </code>
/// Sync reads and writes only the kind folders it carries and blobs/; it never touches the Mac's
/// index.sqlite, thumbnails or anything else. One file of its own sits beside them,
/// <see cref="MarkerName"/>, saying which schema version Windows synced with.
/// </summary>
public sealed partial class SharedFolder(string root)
{
    /// <summary>The Mac backup format this understands (BackupBundle.formatVersion).</summary>
    public const int BackupFormat = 1;

    /// <summary>Windows' note in the shared folder (the Mac ignores it).</summary>
    public const string MarkerName = ".mxu-windows-sync.json";

    public const string BlobsName = "blobs";

    public string Root { get; } = Path.GetFullPath(root);

    public string BlobDirectory => Path.Combine(Root, BlobsName);

    public string KindDirectory(MacKind kind) => Path.Combine(Root, kind.DirectoryName);

    public string DocumentPath(MacKind kind, string id) => Path.Combine(KindDirectory(kind), id + ".automerge");

    /// <summary>True when the folder is there and can be listed (an unplugged drive or an offline OneDrive isn't).</summary>
    public bool IsReachable()
    {
        try
        {
            if (!Directory.Exists(Root)) return false;
            using var probe = Directory.EnumerateFileSystemEntries(Root).GetEnumerator();
            probe.MoveNext();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when the folder holds at least one of the Mac's kind folders.</summary>
    public bool LooksLikeAMacLibrary() => MacKind.All.Any(k => Directory.Exists(KindDirectory(k)));

    /// <summary>True when the folder has nothing in it at all.</summary>
    public bool IsEmpty() => !Directory.EnumerateFileSystemEntries(Root).Any();

    /// <summary>Makes the folders the Mac's DocumentStore makes in a new library (a folder per kind, and blobs).</summary>
    public void MakeLibrary()
    {
        foreach (var kind in MacKind.All) Directory.CreateDirectory(KindDirectory(kind));
        Directory.CreateDirectory(BlobDirectory);
    }

    /// <summary>
    /// The documents of one kind, by id, with their stamps, or null when the kind's folder isn't
    /// there (then nothing of that kind can be taken as deleted). Files that aren't "&lt;id&gt;.automerge"
    /// (a half-written temporary, a hidden file) are left out, as the Mac leaves them out.
    /// </summary>
    public Dictionary<string, FileStamp>? List(MacKind kind)
    {
        var directory = new DirectoryInfo(KindDirectory(kind));
        if (!directory.Exists) return null;
        var found = new Dictionary<string, FileStamp>(StringComparer.Ordinal);
        foreach (var file in directory.EnumerateFiles("*.automerge"))
        {
            if (!file.Name.EndsWith(".automerge", StringComparison.Ordinal) || file.Name.StartsWith('.')) continue;
            found[Path.GetFileNameWithoutExtension(file.Name)] = FileStamp.Of(file);
        }
        return found;
    }

    /// <summary>A document's bytes, or null when it isn't there. Other trouble (in use, offline) throws IOException.</summary>
    public byte[]? Read(MacKind kind, string id)
    {
        var path = DocumentPath(kind, id);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                // Opened to share with whoever might be writing or replacing it (OneDrive, Dropbox, the Mac over SMB).
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                return copy.ToArray();
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    public FileStamp? Stamp(MacKind kind, string id)
    {
        var file = new FileInfo(DocumentPath(kind, id));
        return file.Exists ? FileStamp.Of(file) : null;
    }

    /// <summary>
    /// Writes a document whole, safely: to a temporary file beside it first (named so neither app
    /// takes it for a document), flushed to disk, then moved over the old one in one step. Someone
    /// reading at the same moment sees the old file or the new one, never half of each.
    /// </summary>
    public FileStamp Write(MacKind kind, string id, byte[] bytes)
    {
        Directory.CreateDirectory(KindDirectory(kind));
        var path = DocumentPath(kind, id);
        WriteAtomically(path, bytes);
        return FileStamp.Of(new FileInfo(path));
    }

    /// <summary>Moves a document out of the shared folder into <paramref name="trashFolder"/> (copied, then removed, as it may be another drive).</summary>
    public void MoveToTrash(MacKind kind, string id, string trashFolder)
    {
        var path = DocumentPath(kind, id);
        if (!File.Exists(path)) return;
        var target = Path.Combine(trashFolder, kind.DirectoryName, $"{DateTime.Now:yyyyMMdd-HHmmss}-{id}.automerge");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(path, target, overwrite: true);
        File.Delete(path);
    }

    internal static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.mxu-sync";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static string Fingerprint(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..32];

    // MARK: Versions

    /// <summary>A backup's format version (its manifest.json), or null when this isn't a backup.</summary>
    public int? BackupFormatVersion()
    {
        var path = Path.Combine(Root, "manifest.json");
        if (!File.Exists(path)) return null;
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        return json.RootElement.TryGetProperty("formatVersion", out var version) ? version.GetInt32() : null;
    }

    /// <summary>The schema version Windows last synced this folder with, from <see cref="MarkerName"/> (null when no Windows PC has).</summary>
    public int? WindowsSchemaVersion()
    {
        var path = Path.Combine(Root, MarkerName);
        if (!File.Exists(path)) return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            return json.RootElement.TryGetProperty("schemaVersion", out var version) ? version.GetInt32() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void WriteMarker(int schemaVersion, string app)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            note = "Written by MxU Slides for Windows, which syncs this folder with its library. The Mac app ignores this file.",
            schemaVersion,
            app,
            updatedAt = DateTimeOffset.Now,
        }, new JsonSerializerOptions { WriteIndented = true });
        WriteAtomically(Path.Combine(Root, MarkerName), json);
    }

    // MARK: Blobs

    /// <summary>A media file's name: its SHA-256 in lower-case hex, and an extension.</summary>
    [GeneratedRegex("^[0-9a-f]{64}(\\.[A-Za-z0-9]{1,8})?$")]
    public static partial Regex BlobName();

    /// <summary>The media files in a blobs folder, by name (only names that are a hash).</summary>
    public static Dictionary<string, FileInfo> ListBlobs(string directory)
    {
        var found = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        var info = new DirectoryInfo(directory);
        if (!info.Exists) return found;
        foreach (var file in info.EnumerateFiles())
            if (BlobName().IsMatch(file.Name)) found[file.Name] = file;
        return found;
    }
}
