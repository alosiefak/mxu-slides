using System.Security.Cryptography;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// The on-disk library: one JSON file per document, grouped by kind, plus
/// content-addressed media blobs (named by SHA-256, like the Mac app's
/// BlobStore). Writes are atomic and keep the previous version as .bak;
/// deletes move files to Trash; a file that fails to read is quarantined
/// rather than crashing the app.
/// </summary>
public sealed class LibraryStore
{
    public const string ManifestFileName = "library.json";

    private static readonly Dictionary<Type, string> Folders = new()
    {
        [typeof(Presentation)] = "Presentations",
        [typeof(Theme)] = "Themes",
        [typeof(Service)] = "Services",
        [typeof(MediaItem)] = "Media",
        [typeof(Overlay)] = "Overlays",
        [typeof(ConfidenceLayout)] = "StageLayouts",
        [typeof(AlertPreset)] = "Alerts",
        [typeof(AudioItem)] = "Audio",
        [typeof(Playlist)] = "Playlists",
        [typeof(ActionCombo)] = "ActionCombos",
        [typeof(ScheduleTrigger)] = "Schedule",
        [typeof(OutputPreset)] = "OutputPresets",
        [typeof(StreamRecordPreset)] = "StreamPresets",
        [typeof(StreamDestination)] = "StreamDestinations",
        [typeof(ControlBoard)] = "ComboBoard",
        [typeof(SchedulerBoard)] = "SchedulerBoard",
        [typeof(GroupPalette)] = "GroupPalette",
        [typeof(MIDIDevice)] = "MidiDevices",
        [typeof(TrackSet)] = "TrackSets",
        [typeof(TrackChannelList)] = "TrackChannels",
    };

    private readonly object gate = new();
    private readonly Dictionary<Type, Dictionary<string, ILibraryDocument>> documents = [];
    private readonly List<string> loadProblems = [];

    public LibraryStore(string root)
    {
        Root = root;
        foreach (var type in Folders.Keys) documents[type] = new(StringComparer.Ordinal);
        Blobs = new BlobStore(BlobDirectory);
    }

    public string Root { get; }
    public string BlobDirectory => Path.Combine(Root, "Blobs");

    /// <summary>The media files, by hash (see BlobStore.cs).</summary>
    public BlobStore Blobs { get; }

    /// <summary>Looks inside imported media (set by the app; null in tests that don't need it). See MediaImporting.cs.</summary>
    public IMediaProber? Prober { get; set; }

    public string TrashDirectory => Path.Combine(Root, "Trash");
    public string QuarantineDirectory => Path.Combine(Root, "Quarantine");
    public string BackupDirectory => Path.Combine(Root, "Backups");

    /// <summary>Files that could not be read on the last load (already moved to Quarantine).</summary>
    public IReadOnlyList<string> LoadProblems { get { lock (gate) return loadProblems.ToList(); } }

    public bool IsNew { get; private set; }

    /// <summary>Raised after a document is saved or deleted, with its type and id.</summary>
    public event Action<Type, string>? Changed;

    public static string FolderFor(Type type) =>
        Folders.TryGetValue(type, out var folder) ? folder : throw new ArgumentException($"{type.Name} is not a library document");

    public void Load()
    {
        Directory.CreateDirectory(Root);
        var manifest = Path.Combine(Root, ManifestFileName);
        IsNew = !File.Exists(manifest);
        if (IsNew)
            AtomicWrite(manifest, MxuJson.SerializeToUtf8(new LibraryManifest
            {
                LibraryId = NewId(),
                CreatedAt = DateTimeOffset.Now,
                SchemaVersion = SchemaInfo.SchemaVersion,
            }));

        lock (gate)
        {
            loadProblems.Clear();
            foreach (var (type, folder) in Folders)
            {
                var directory = Path.Combine(Root, folder);
                Directory.CreateDirectory(directory);
                var bucket = documents[type];
                bucket.Clear();
                foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
                {
                    if (TryRead(type, file) is { } doc)
                        bucket[doc.Id] = doc;
                }
            }
        }
        Directory.CreateDirectory(BlobDirectory);
    }

    private ILibraryDocument? TryRead(Type type, string file)
    {
        try
        {
            return Read(type, file);
        }
        catch (Exception primary)
        {
            // Fall back to the previous version before giving up on the document.
            var backup = file + ".bak";
            ILibraryDocument? recovered = null;
            if (File.Exists(backup))
            {
                try { recovered = Read(type, backup); }
                catch { recovered = null; }
            }
            Quarantine(file);
            loadProblems.Add(recovered is null
                ? $"{Path.GetFileName(file)}: {primary.Message}"
                : $"{Path.GetFileName(file)} was damaged; restored the previous version.");
            if (recovered is not null) AtomicWrite(file, MxuJson.SerializeToUtf8((object)recovered));
            return recovered;
        }
    }

    private static ILibraryDocument Read(Type type, string file)
    {
        var bytes = File.ReadAllBytes(file);
        var doc = System.Text.Json.JsonSerializer.Deserialize(bytes, type, MxuJson.Options) as ILibraryDocument
            ?? throw new InvalidDataException("Empty document");
        if (string.IsNullOrEmpty(doc.Id)) throw new InvalidDataException("Document has no id");
        return doc;
    }

    private void Quarantine(string file)
    {
        try
        {
            Directory.CreateDirectory(QuarantineDirectory);
            var target = Path.Combine(QuarantineDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(file)}");
            File.Move(file, target, overwrite: true);
        }
        catch
        {
            // Leaving the file where it is is safe: it will be retried next launch.
        }
    }

    // MARK: Reading

    public IReadOnlyList<T> All<T>() where T : class, ILibraryDocument
    {
        lock (gate) return documents[typeof(T)].Values.Cast<T>().OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public T? Get<T>(string? id) where T : class, ILibraryDocument
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (gate) return documents[typeof(T)].TryGetValue(id, out var doc) ? (T)doc : null;
    }

    public int Count<T>() where T : class, ILibraryDocument
    {
        lock (gate) return documents[typeof(T)].Count;
    }

    // MARK: Writing

    public void Save<T>(T doc) where T : class, ILibraryDocument
    {
        if (string.IsNullOrEmpty(doc.Id)) doc.Id = NewId();
        var path = DocumentPath(typeof(T), doc.Id);
        var bytes = MxuJson.SerializeToUtf8(doc);
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicWrite(path, bytes);
            documents[typeof(T)][doc.Id] = doc;
        }
        Changed?.Invoke(typeof(T), doc.Id);
    }

    /// <summary>Moves the document to Trash (never a permanent delete).</summary>
    public void Delete<T>(string id) where T : class, ILibraryDocument
    {
        var path = DocumentPath(typeof(T), id);
        lock (gate)
        {
            if (File.Exists(path))
            {
                var trash = Path.Combine(TrashDirectory, FolderFor(typeof(T)));
                Directory.CreateDirectory(trash);
                File.Move(path, Path.Combine(trash, $"{DateTime.Now:yyyyMMdd-HHmmss}-{id}.json"), overwrite: true);
            }
            documents[typeof(T)].Remove(id);
        }
        Changed?.Invoke(typeof(T), id);
    }

    public string DocumentPath(Type type, string id) => Path.Combine(Root, FolderFor(type), SafeFileName(id) + ".json");

    private static string SafeFileName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(id.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>Writes to a temp file, flushes it to disk, then swaps it in, keeping the old file as .bak.</summary>
    public static void AtomicWrite(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(path))
            File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(temp, path);
    }

    public static string NewId() => Guid.NewGuid().ToString().ToUpperInvariant();

    // MARK: Media

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".webp",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".ts",
    };

    public static MediaKind? MediaKindOf(string path)
    {
        var extension = Path.GetExtension(path);
        if (ImageExtensions.Contains(extension)) return MediaKind.Image;
        if (VideoExtensions.Contains(extension)) return MediaKind.Video;
        return null;
    }

    public static IEnumerable<string> SupportedMediaExtensions => ImageExtensions.Concat(VideoExtensions);

    /// <summary>
    /// Copies a media file into the library (deduplicated by content hash) and
    /// records a <see cref="MediaItem"/> for it. Returns null for unsupported files.
    /// The same file imported again returns the item already made for it (and puts its
    /// file back if it had gone missing). The app's own import asks first: see MediaImporting.
    /// </summary>
    public MediaItem? ImportMedia(string sourcePath, MediaClassification? classification = null)
    {
        if (MediaKindOf(sourcePath) is null) return null;
        var hash = HashFile(sourcePath);
        var existing = All<MediaItem>().FirstOrDefault(m => m.FileHash == hash);
        if (existing is null) return MediaImporting.AddMedia(this, sourcePath, classification: classification, knownHash: hash);
        Blobs.Store(sourcePath, hash);
        return existing;
    }

    /// <summary>The library copy of a media item's file, or null when it is missing (see MissingMedia).</summary>
    public string? BlobPath(MediaItem item) => Blobs.Find(item.FileHash);

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    // MARK: Backups

    /// <summary>
    /// Snapshots every document (not the media blobs) into Backups/yyyy-MM-dd,
    /// once per day, keeping the newest <paramref name="keep"/> snapshots.
    /// </summary>
    public string? BackupIfDue(int keep = 14)
    {
        var folder = Path.Combine(BackupDirectory, DateTime.Now.ToString("yyyy-MM-dd"));
        if (Directory.Exists(folder)) return null;
        var temp = folder + ".partial";
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        foreach (var kind in Folders.Values.Append(""))
        {
            var source = Path.Combine(Root, kind);
            if (!Directory.Exists(source)) continue;
            var target = Path.Combine(temp, kind);
            Directory.CreateDirectory(target);
            var pattern = kind == "" ? ManifestFileName : "*.json";
            foreach (var file in Directory.EnumerateFiles(source, pattern))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        Directory.Move(temp, folder);
        foreach (var old in Directory.EnumerateDirectories(BackupDirectory)
                     .Where(d => !d.EndsWith(".partial", StringComparison.Ordinal))
                     .OrderByDescending(d => d, StringComparer.Ordinal)
                     .Skip(keep))
            Directory.Delete(old, recursive: true);
        return folder;
    }
}

public sealed class LibraryManifest
{
    public string LibraryId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public int SchemaVersion { get; set; }
    public string Format { get; set; } = "mxu-slides-windows-json-1";
}
