using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// The parts of a backup that can be left out of a restore, in the Mac's two columns: the content
/// (songs, media, themes…) and the set-up (presets, devices, settings). Port of the Mac's
/// <c>BackupSection</c> (BackupOptions.swift); Windows adds <see cref="WindowsExtras"/>, the
/// Windows-only additions kept beside the documents (HOW-IT-WORKS section 41).
/// </summary>
public enum BackupSection
{
    Presentations, Media, MediaFiles, Themes, Overlays, Alerts, ConfidenceLayouts, Combos, Playlists, Services, Schedules,
    OutputPresets, Streaming, MidiDevices, ControlBoards, Groups, PresetBoards, Fonts, WindowsExtras, Settings,
}

public static class BackupSections
{
    public static IReadOnlyList<BackupSection> All { get; } = Enum.GetValues<BackupSection>();

    public static bool IsContent(this BackupSection section) => section <= BackupSection.Schedules;

    public static string Title(this BackupSection section) => section switch
    {
        BackupSection.Presentations => "Presentations",
        BackupSection.Media => "Media & audio library",
        BackupSection.MediaFiles => "Include the media files",
        BackupSection.Themes => "Themes",
        BackupSection.Overlays => "Overlays",
        BackupSection.Alerts => "Alerts",
        BackupSection.ConfidenceLayouts => "Confidence layouts",
        BackupSection.Combos => "Action combos",
        BackupSection.Playlists => "Playlists",
        BackupSection.Services => "Services",
        BackupSection.Schedules => "Scheduler",
        BackupSection.OutputPresets => "Output presets",
        BackupSection.Streaming => "Stream & record presets",
        BackupSection.MidiDevices => "MIDI devices",
        BackupSection.ControlBoards => "Control boards",
        BackupSection.Groups => "Groups & hot keys",
        BackupSection.PresetBoards => "Effect & animation presets",
        BackupSection.Fonts => "Imported fonts",
        BackupSection.WindowsExtras => "Windows-only additions",
        _ => "App settings",
    };

    /// <summary>Which section each kind of document belongs to (the Mac's <c>section(for:)</c>, by Windows' folders).</summary>
    public static BackupSection For(Type document) => document.Name switch
    {
        nameof(Presentation) => BackupSection.Presentations,
        nameof(MediaItem) or nameof(AudioItem) or nameof(TrackSet) => BackupSection.Media,
        nameof(Theme) => BackupSection.Themes,
        nameof(Overlay) => BackupSection.Overlays,
        nameof(AlertPreset) => BackupSection.Alerts,
        nameof(ConfidenceLayout) => BackupSection.ConfidenceLayouts,
        nameof(ActionCombo) => BackupSection.Combos,
        nameof(Playlist) => BackupSection.Playlists,
        nameof(Service) => BackupSection.Services,
        nameof(ScheduleTrigger) or nameof(SchedulerBoard) => BackupSection.Schedules,
        nameof(OutputPreset) => BackupSection.OutputPresets,
        nameof(StreamRecordPreset) or nameof(StreamDestination) => BackupSection.Streaming,
        nameof(MIDIDevice) => BackupSection.MidiDevices,
        nameof(ControlBoard) => BackupSection.ControlBoards,
        nameof(GroupPalette) => BackupSection.Groups,
        nameof(AnimationPresetBoard) or nameof(EffectPresetBoard) => BackupSection.PresetBoards,
        _ => BackupSection.Settings, // the track list (TrackChannelList), as on the Mac
    };
}

/// <summary>
/// What to do with a document the library already has. The Mac's choices are Merge (Automerge
/// joins both copies' edits), Keep mine and Replace. Windows' documents are plain JSON with no edit
/// history to merge, so its default is the nearest thing: keep whichever copy was saved last.
/// <see cref="Mirror"/> (Windows only) makes the library exactly the backup.
/// </summary>
public enum BackupPolicy
{
    NewerWins,
    KeepMine,
    Replace,
    Mirror,
}

public static class BackupPolicies
{
    public static string Title(this BackupPolicy policy) => policy switch
    {
        BackupPolicy.KeepMine => "Keep mine — only add what's missing",
        BackupPolicy.Replace => "Replace with the backup's copy",
        BackupPolicy.Mirror => "Make the library exactly the backup (anything not in it goes to the Trash)",
        _ => "Keep whichever copy was changed last",
    };
}

/// <summary>The backup's own description, at the top of the file. The Mac's fields, plus a few Windows ones.</summary>
public sealed record BackupManifest
{
    public int FormatVersion { get; init; } = LibraryBackup.FormatVersion;

    /// <summary>When it was made, whole seconds in UTC (the Mac's JSON reader refuses fractions).</summary>
    public string CreatedAt { get; init; } = "";

    /// <summary>How many documents of each kind, by folder ("Presentations": 120).</summary>
    public Dictionary<string, int> DocumentCounts { get; init; } = [];

    public int BlobCount { get; init; }
    public int? FontCount { get; init; }
    public int? SettingsCount { get; init; }
    public int? ExtrasCount { get; init; }
    public bool IncludesMedia { get; init; }

    /// <summary>"MxU Slides for Windows 1.0.0", or what made it.</summary>
    public string? App { get; init; }

    /// <summary>The PC it was made on.</summary>
    public string? Computer { get; init; }

    public string? LibraryId { get; init; }

    [JsonIgnore]
    public DateTimeOffset? Created => DateTimeOffset.TryParse(CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    [JsonIgnore]
    public int DocumentTotal => DocumentCounts.Values.Sum();
}

public sealed record BackupProgress(string Phase, string Detail = "", int Completed = 0, int? Total = null);

/// <summary>A backup that can't be read: not a backup, from a newer app, or a Mac's.</summary>
public sealed class BackupException(string message) : Exception(message);

/// <param name="IncludeMedia">The pictures, videos and music files themselves (large).</param>
/// <param name="Settings">This PC's portable settings files, by name ("settings.json", "timers.json"), already trimmed to what may travel.</param>
public sealed record BackupWriteOptions(bool IncludeMedia, IReadOnlyDictionary<string, byte[]>? Settings = null, string? App = null, string? Computer = null);

/// <summary>A section's documents, set against the library's: how many are new, identical, different, and only in the library.</summary>
public sealed record BackupComparison(int InBackup, int New, int Same, int Different, int OnlyHere);

/// <summary>What a backup holds, read without changing anything ("Restore shows what's in a backup before replacing anything").</summary>
public sealed record BackupScan(
    BackupManifest Manifest, IReadOnlyDictionary<BackupSection, int> Counts, IReadOnlyDictionary<BackupSection, BackupComparison> Compared,
    bool IsDailyCopy, IReadOnlyList<string> Warnings)
{
    public int Count(BackupSection section) => Counts.GetValueOrDefault(section);
}

public sealed record BackupRestoreOptions(BackupPolicy Policy = BackupPolicy.NewerWins, IReadOnlySet<BackupSection>? Excluded = null)
{
    /// <summary>The media files come only with the media items they belong to (the Mac's rule).</summary>
    public bool Includes(BackupSection section) =>
        Excluded?.Contains(section) != true && (section != BackupSection.MediaFiles || Excluded?.Contains(BackupSection.Media) != true);
}

public sealed class BackupRestoreResult
{
    public required BackupManifest Manifest { get; init; }
    public Dictionary<BackupSection, int> Added { get; } = [];
    public int Replaced { get; set; }
    public int Kept { get; set; }
    public int Same { get; set; }
    public int Removed { get; set; }
    public int MediaFilesCopied { get; set; }
    public int FontsCopied { get; set; }
    public int ExtrasCopied { get; set; }

    /// <summary>The backup's settings files, for the app to apply at the next launch (as the Mac stages them).</summary>
    public Dictionary<string, byte[]> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the copy of the library as it was before the restore went.</summary>
    public string? SafetyCopy { get; set; }

    public List<string> Warnings { get; } = [];

    /// <summary>The kinds of document the restore changed, so the app redraws just those.</summary>
    public HashSet<Type> ChangedKinds { get; } = [];

    public int DocumentsLanded => Added.Values.Sum() + Replaced + Removed;
}

/// <summary>
/// A whole library in one file, and putting it back. Port of the Mac's BackupBundle.swift, with
/// what's different on Windows:
/// <list type="bullet">
/// <item>The Mac's backup is a folder (a macOS "package", which Finder shows as one file). Here
/// it's one real file, a zip, with the library's own folders inside: <c>Presentations/*.json</c>,
/// <c>Windows extras/…</c>, <c>fonts/…</c>, <c>Blobs/…</c> (the media, when chosen) and
/// <c>settings/…</c>, with <c>manifest.json</c> describing it. A zip can be emailed or put on a
/// USB stick as it is.</item>
/// <item>The two can't read each other's: the Mac's documents are Automerge files, which Windows
/// can't read yet (Phase 10), so a Mac backup is recognised and refused with that reason.</item>
/// <item>Restoring keeps a safety copy of the library as it was, first, and refuses to go on
/// without one; and documents are "merged" by keeping the newer copy (<see cref="BackupPolicy"/>).</item>
/// </list>
/// The old daily copies (<c>Backups/2026-10-24</c> folders of documents) can be restored too.
/// Everything here does disk work: call it off the UI thread.
/// </summary>
public static class LibraryBackup
{
    public const string FileExtension = "mxubackup";
    public const int FormatVersion = 1;
    public const string ManifestName = "manifest.json";
    public const string BlobsFolder = "Blobs";
    public const string SettingsFolder = "settings";

    /// <summary>A document bigger than this inside a backup is treated as damage, not read into memory.</summary>
    private const long MaxDocumentBytes = 64L * 1024 * 1024;

    private static readonly Dictionary<string, Type> TypeOfFolder =
        LibraryStore.DocumentTypes.ToDictionary(LibraryStore.FolderFor, t => t, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NeverCopied = new(StringComparer.OrdinalIgnoreCase) { "Backups", "Trash", "Quarantine" };

    // MARK: Writing

    /// <summary>
    /// Writes the whole library to <paramref name="destination"/>. It's built beside it under a
    /// ".partial" name and moved into place at the end, so a failed or cancelled backup never
    /// leaves half a file under the real name, or replaces a good older one.
    /// </summary>
    public static BackupManifest Write(LibraryStore store, string destination, BackupWriteOptions options,
        IProgress<BackupProgress>? progress = null, CancellationToken cancel = default)
    {
        var partial = destination + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        try
        {
            BackupManifest manifest;
            using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                progress?.Report(new BackupProgress("Copying documents…"));
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var type in LibraryStore.DocumentTypes)
                {
                    var folder = LibraryStore.FolderFor(type);
                    var files = ListFiles(Path.Combine(store.Root, folder), "*.json");
                    foreach (var path in files)
                    {
                        cancel.ThrowIfCancellationRequested();
                        Add(zip, path, $"{folder}/{Path.GetFileName(path)}", CompressionLevel.Optimal);
                    }
                    counts[folder] = files.Count;
                }
                // The library's own small files: its id (library.json), what was last used, the import ledger, the starter markers.
                foreach (var path in ListFiles(store.Root, "*").Where(p => !IsScratch(p)))
                    Add(zip, path, Path.GetFileName(path), CompressionLevel.Optimal);

                var extras = Path.Combine(store.Root, WindowsExtras.FolderName);
                var extrasCount = 0;
                foreach (var path in ListFiles(extras, "*.json", recursive: true))
                {
                    cancel.ThrowIfCancellationRequested();
                    Add(zip, path, $"{WindowsExtras.FolderName}/{Path.GetRelativePath(extras, path).Replace('\\', '/')}", CompressionLevel.Optimal);
                    extrasCount++;
                }

                progress?.Report(new BackupProgress("Copying fonts…"));
                var fonts = ListFiles(Path.Combine(store.Root, FontSharing.LibraryFolderName), "*").Where(p => !IsScratch(p)).ToList();
                foreach (var path in fonts) Add(zip, path, $"{FontSharing.LibraryFolderName}/{Path.GetFileName(path)}", CompressionLevel.Optimal);

                var blobs = 0;
                if (options.IncludeMedia)
                {
                    var media = ListFiles(store.BlobDirectory, "*").Where(p => !IsScratch(p)).ToList();
                    for (var i = 0; i < media.Count; i++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (i % 5 == 0) progress?.Report(new BackupProgress("Copying media files…", Path.GetFileName(media[i]), i, media.Count));
                        // Pictures and videos are compressed already: storing them as they are saves minutes.
                        Add(zip, media[i], $"{BlobsFolder}/{Path.GetFileName(media[i])}", CompressionLevel.NoCompression);
                        blobs++;
                    }
                }

                foreach (var (name, bytes) in options.Settings ?? new Dictionary<string, byte[]>())
                    AddBytes(zip, $"{SettingsFolder}/{Path.GetFileName(name)}", bytes, DateTimeOffset.Now);

                progress?.Report(new BackupProgress("Finishing…"));
                manifest = new BackupManifest
                {
                    CreatedAt = Stamp(DateTimeOffset.UtcNow),
                    DocumentCounts = counts,
                    BlobCount = blobs,
                    FontCount = fonts.Count,
                    SettingsCount = options.Settings?.Count ?? 0,
                    ExtrasCount = extrasCount,
                    IncludesMedia = options.IncludeMedia,
                    App = options.App,
                    Computer = options.Computer,
                    LibraryId = ReadLibraryId(store.Root),
                };
                AddBytes(zip, ManifestName, JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJson), DateTimeOffset.Now);
            }
            File.Move(partial, destination, overwrite: true);
            return manifest;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    // MARK: Reading

    /// <summary>
    /// What a backup holds, and (with <paramref name="compareWith"/>) how it differs from the library
    /// now. Nothing is changed. Throws <see cref="BackupException"/> for something that isn't a backup.
    /// </summary>
    public static BackupScan Scan(string path, LibraryStore? compareWith = null)
    {
        using var source = BackupSource.Open(path);
        var counts = new Dictionary<BackupSection, int>();
        foreach (var (folder, type) in TypeOfFolder)
            counts[BackupSections.For(type)] = counts.GetValueOrDefault(BackupSections.For(type)) + source.Files(folder).Count(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        counts[BackupSection.MediaFiles] = source.Files(BlobsFolder).Count;
        counts[BackupSection.Fonts] = source.Files(FontSharing.LibraryFolderName).Count;
        counts[BackupSection.WindowsExtras] = source.Entries.Count(e => e.StartsWith(WindowsExtras.FolderName + "/", StringComparison.OrdinalIgnoreCase));
        counts[BackupSection.Settings] = source.Files(SettingsFolder).Count;

        var compared = new Dictionary<BackupSection, BackupComparison>();
        if (compareWith is not null)
            foreach (var group in TypeOfFolder.GroupBy(pair => BackupSections.For(pair.Value)))
            {
                int inBackup = 0, added = 0, same = 0, different = 0, onlyHere = 0;
                foreach (var (folder, _) in group)
                {
                    var names = source.Files(folder).Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in names)
                    {
                        inBackup++;
                        var local = Path.Combine(compareWith.Root, folder, name);
                        if (!File.Exists(local)) added++;
                        else if (SameBytes(local, source.Read($"{folder}/{name}", MaxDocumentBytes))) same++;
                        else different++;
                    }
                    onlyHere += ListFiles(Path.Combine(compareWith.Root, folder), "*.json").Count(p => !names.Contains(Path.GetFileName(p)));
                }
                compared[group.Key] = new BackupComparison(inBackup, added, same, different, onlyHere);
            }
        return new BackupScan(source.Manifest, counts, compared, source.IsDailyCopy, IntegrityWarnings(source));
    }

    // MARK: Restoring

    /// <summary>
    /// Puts a backup's contents into the library. First a safety copy of the library as it is
    /// goes into <paramref name="safetyFolder"/> (documents, Windows extras, fonts and
    /// <paramref name="currentSettings"/>; media files are never removed by a restore, so they
    /// needn't be copied); if that copy can't be made, nothing is restored. Then each document is
    /// added, kept or replaced as <paramref name="options"/> says, through the library's own save
    /// (so each still gets its .bak, and the app hears about it), and media files and fonts the
    /// library hasn't got are copied in. A damaged document is skipped with a warning; the rest go on.
    /// </summary>
    public static BackupRestoreResult Restore(LibraryStore store, string path, BackupRestoreOptions options, string? safetyFolder,
        IReadOnlyDictionary<string, byte[]>? currentSettings = null, LibraryUsage? usage = null,
        IProgress<BackupProgress>? progress = null, CancellationToken cancel = default)
    {
        using var source = BackupSource.Open(path);
        var result = new BackupRestoreResult { Manifest = source.Manifest };
        result.Warnings.AddRange(IntegrityWarnings(source));

        if (safetyFolder is not null)
        {
            progress?.Report(new BackupProgress("Keeping a copy of your library as it is…"));
            var safety = Path.Combine(safetyFolder, BackupFiles.SafetyCopyName(DateTimeOffset.Now));
            Write(store, safety, new BackupWriteOptions(IncludeMedia: false, currentSettings, "MxU Slides for Windows (before a restore)"), cancel: cancel);
            result.SafetyCopy = safety;
        }

        var work = TypeOfFolder
            .Where(pair => options.Includes(BackupSections.For(pair.Value)))
            .SelectMany(pair => source.Files(pair.Key).Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).Select(name => (Folder: pair.Key, Type: pair.Value, Name: name)))
            .ToList();
        for (var i = 0; i < work.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var (folder, type, name) = work[i];
            if (i % 20 == 0) progress?.Report(new BackupProgress("Restoring documents…", folder, i, work.Count));
            var entry = $"{folder}/{name}";
            try
            {
                var bytes = source.Read(entry, MaxDocumentBytes);
                if (JsonSerializer.Deserialize(bytes, type, MxuJson.Options) is not ILibraryDocument document || string.IsNullOrEmpty(document.Id))
                    throw new InvalidDataException("it has no id");
                var local = store.DocumentPath(type, document.Id);
                if (!File.Exists(local))
                {
                    SaveDocument(store, type, document);
                    result.Added[BackupSections.For(type)] = result.Added.GetValueOrDefault(BackupSections.For(type)) + 1;
                    result.ChangedKinds.Add(type);
                }
                else if (SameBytes(local, bytes))
                {
                    result.Same++;
                }
                else if (Takes(options.Policy, source.Modified(entry), File.GetLastWriteTimeUtc(local)))
                {
                    SaveDocument(store, type, document);
                    result.Replaced++;
                    result.ChangedKinds.Add(type);
                }
                else
                {
                    result.Kept++;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException or IOException)
            {
                result.Warnings.Add($"{entry} could not be imported: {ex.Message}");
            }
        }

        if (options.Policy == BackupPolicy.Mirror)
            foreach (var (folder, type) in TypeOfFolder.Where(pair => options.Includes(BackupSections.For(pair.Value))))
            {
                var inBackup = source.Files(folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var local in ListFiles(Path.Combine(store.Root, folder), "*.json").Where(p => !inBackup.Contains(Path.GetFileName(p))))
                {
                    if (ReadId(local, type) is not { } id) continue;
                    DeleteDocument(store, type, id);
                    result.Removed++;
                    result.ChangedKinds.Add(type);
                }
            }

        if (options.Includes(BackupSection.MediaFiles))
        {
            var names = source.Files(BlobsFolder);
            for (var i = 0; i < names.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                if (i % 5 == 0) progress?.Report(new BackupProgress("Copying media files…", names[i], i, names.Count));
                if (CopyMissing(source, $"{BlobsFolder}/{names[i]}", Path.Combine(store.BlobDirectory, names[i]), verifyHash: true, result.Warnings)) result.MediaFilesCopied++;
            }
        }
        if (options.Includes(BackupSection.Fonts))
            foreach (var name in source.Files(FontSharing.LibraryFolderName))
                if (CopyMissing(source, $"{FontSharing.LibraryFolderName}/{name}", Path.Combine(store.Root, FontSharing.LibraryFolderName, name), verifyHash: false, result.Warnings))
                    result.FontsCopied++;
        if (options.Includes(BackupSection.WindowsExtras))
            foreach (var entry in source.Entries.Where(e => e.StartsWith(WindowsExtras.FolderName + "/", StringComparison.OrdinalIgnoreCase)))
            {
                if (SafeRelative(entry) is not { } relative) continue;
                var local = Path.Combine(store.Root, relative);
                if (File.Exists(local) && !Takes(options.Policy, source.Modified(entry), File.GetLastWriteTimeUtc(local))) continue;
                var bytes = source.Read(entry, MaxDocumentBytes);
                if (File.Exists(local) && SameBytes(local, bytes)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                LibraryStore.AtomicWrite(local, bytes);
                result.ExtrasCopied++;
            }

        // The library's own small files. Its id (library.json) is never taken from a backup: this library stays itself.
        foreach (var name in source.Files(""))
        {
            if (name.Equals(LibraryStore.ManifestFileName, StringComparison.OrdinalIgnoreCase) || name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals(LibraryUsage.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            var section = name.Equals("ImportLedger.json", StringComparison.OrdinalIgnoreCase) ? BackupSection.Presentations : BackupSection.Themes;
            if (!options.Includes(section)) continue;
            var local = Path.Combine(store.Root, name);
            var replace = options.Policy is BackupPolicy.Replace or BackupPolicy.Mirror;
            if (File.Exists(local) && !replace) continue;
            LibraryStore.AtomicWrite(local, source.Read(name, MaxDocumentBytes));
        }
        // When things were last used always comes along, merged: the later time of the two wins.
        if (source.Entries.Contains(LibraryUsage.FileName))
        {
            var stamps = ReadUsage(source.Read(LibraryUsage.FileName, MaxDocumentBytes));
            if (usage is not null) usage.Merge(stamps);
            else MergeUsageFile(Path.Combine(store.Root, LibraryUsage.FileName), stamps);
        }
        if (options.Includes(BackupSection.Settings))
            foreach (var name in source.Files(SettingsFolder))
                result.Settings[name] = source.Read($"{SettingsFolder}/{name}", MaxDocumentBytes);

        progress?.Report(new BackupProgress("Finished"));
        return result;
    }

    /// <summary>Does the backup's copy win over the library's under this policy?</summary>
    private static bool Takes(BackupPolicy policy, DateTimeOffset? backupSaved, DateTime localSavedUtc) => policy switch
    {
        BackupPolicy.KeepMine => false,
        BackupPolicy.Replace or BackupPolicy.Mirror => true,
        // A zip keeps times to two seconds; within that, the library's copy stays.
        _ => backupSaved is { } saved && saved.UtcDateTime > localSavedUtc.AddSeconds(2),
    };

    // MARK: Pieces

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string Stamp(DateTimeOffset when) => when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static List<string> IntegrityWarnings(BackupSource source)
    {
        var warnings = new List<string>();
        foreach (var (folder, expected) in source.Manifest.DocumentCounts)
        {
            var found = source.Files(folder).Count(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            if (found < expected) warnings.Add($"This backup is incomplete: {folder} lists {expected} documents but holds {found}.");
        }
        var blobs = source.Files(BlobsFolder).Count;
        if (blobs < source.Manifest.BlobCount) warnings.Add($"This backup is incomplete: it lists {source.Manifest.BlobCount} media files but holds {blobs}.");
        return warnings;
    }

    // Saving and deleting through the library itself: LibraryStore.Save<T> and Delete<T> are generic, and
    // a backup's documents are known only by their folder, so the right one is picked by reflection.
    private static readonly MethodInfo SaveMethod = typeof(LibraryStore).GetMethod(nameof(LibraryStore.Save))!;
    private static readonly MethodInfo DeleteMethod = typeof(LibraryStore).GetMethod(nameof(LibraryStore.Delete))!;

    private static void SaveDocument(LibraryStore store, Type type, ILibraryDocument document) =>
        SaveMethod.MakeGenericMethod(type).Invoke(store, [document]);

    private static void DeleteDocument(LibraryStore store, Type type, string id) =>
        DeleteMethod.MakeGenericMethod(type).Invoke(store, [id]);

    private static string? ReadId(string path, Type type)
    {
        try { return (JsonSerializer.Deserialize(File.ReadAllBytes(path), type, MxuJson.Options) as ILibraryDocument)?.Id; }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { return null; }
    }

    /// <summary>Copies one file in when the library hasn't got it. A media file is checked against the hash it's named by.</summary>
    private static bool CopyMissing(BackupSource source, string entry, string target, bool verifyHash, List<string> warnings)
    {
        if (File.Exists(target) || SafeRelative(entry) is null) return false;
        var temp = target + ".partial";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                source.CopyTo(entry, output);
            var name = Path.GetFileNameWithoutExtension(target);
            if (verifyHash && name.Length == 64 && name.All(char.IsAsciiHexDigit) && !LibraryStore.HashFile(temp).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{Path.GetFileName(target)} in the backup is damaged (its contents don't match its name), so it was left out.");
                TryDelete(temp);
                return false;
            }
            File.Move(temp, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            warnings.Add($"{Path.GetFileName(target)} could not be copied: {ex.Message}");
            TryDelete(temp);
            return false;
        }
    }

    private static Dictionary<string, double> ReadUsage(byte[] bytes)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, double>>(bytes) ?? []; }
        catch (JsonException) { return []; }
    }

    private static void MergeUsageFile(string path, Dictionary<string, double> stamps)
    {
        var merged = LibraryUsage.ReadFile(path);
        foreach (var (id, at) in stamps) merged[id] = Math.Max(at, merged.GetValueOrDefault(id));
        LibraryStore.AtomicWrite(path, MxuJson.SerializeToUtf8(merged, indented: false));
    }

    private static string? ReadLibraryId(string root)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, LibraryStore.ManifestFileName)));
            return json.RootElement.TryGetProperty("LibraryId", out var id) ? id.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// An entry's path as a path inside the library, or null when it tries to climb out ("..") or
    /// names something Windows can't (a drive, a reserved name): a damaged or hostile zip can't
    /// write outside the library. The same rules as SafeZip.cs.
    /// </summary>
    internal static string? SafeRelative(string entry) =>
        entry.Length > 0 && SafeZip.IsSafeRelativePath(entry) ? Path.Combine(entry.Split('/')) : null;

    private static bool SameBytes(string path, byte[] bytes)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Length == bytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
        }
        catch (IOException) { return false; }
    }

    private static bool IsScratch(string path) =>
        path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase);

    private static List<string> ListFiles(string folder, string pattern, bool recursive = false)
    {
        if (!Directory.Exists(folder)) return [];
        return [.. Directory.EnumerateFiles(folder, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Where(p => !IsScratch(p)).Order(StringComparer.Ordinal)];
    }

    /// <summary>Adds a file, read with a retry or two: the app may be saving it at that moment (its save swaps the file in).</summary>
    private static void Add(ZipArchive zip, string path, string entryName, CompressionLevel level)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var entry = zip.CreateEntry(entryName, level);
                entry.LastWriteTime = File.GetLastWriteTime(path);
                using var output = entry.Open();
                input.CopyTo(output);
                return;
            }
            catch (FileNotFoundException)
            {
                return; // deleted while the backup ran: it isn't part of the library any more
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void AddBytes(ZipArchive zip, string entryName, byte[] bytes, DateTimeOffset when)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = when;
        using var output = entry.Open();
        output.Write(bytes);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A backup to read: a .mxubackup zip, or a folder (an old daily copy of the documents). Each
    /// entry is a library-relative path with "/" between folders.
    /// </summary>
    private abstract class BackupSource : IDisposable
    {
        public required BackupManifest Manifest { get; init; }
        public bool IsDailyCopy { get; init; }
        public abstract IReadOnlySet<string> Entries { get; }

        /// <summary>The file names directly inside a folder of the backup ("" for its top level).</summary>
        public IReadOnlyList<string> Files(string folder)
        {
            var prefix = folder.Length == 0 ? "" : folder + "/";
            return [.. Entries.Where(e => e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.IndexOf('/', prefix.Length) < 0 && e.Length > prefix.Length)
                .Select(e => e[prefix.Length..]).Order(StringComparer.Ordinal)];
        }

        public abstract byte[] Read(string entry, long limit);
        public abstract void CopyTo(string entry, Stream output);
        public abstract DateTimeOffset? Modified(string entry);
        public abstract void Dispose();

        public static BackupSource Open(string path)
        {
            if (Directory.Exists(path)) return FolderSource.OpenFolder(path);
            if (!File.Exists(path)) throw new BackupException($"“{Path.GetFileName(path)}” can't be found.");
            try
            {
                return ZipSource.OpenZip(path);
            }
            catch (InvalidDataException)
            {
                throw new BackupException($"“{Path.GetFileName(path)}” isn't an MxU Slides library backup.");
            }
        }

        protected static BackupManifest ReadManifest(byte[] bytes, string name)
        {
            BackupManifest? manifest;
            try { manifest = JsonSerializer.Deserialize<BackupManifest>(bytes, ManifestJson); }
            catch (JsonException) { manifest = null; }
            if (manifest is null) throw new BackupException($"“{name}” isn't an MxU Slides library backup.");
            if (manifest.FormatVersion > FormatVersion)
                throw new BackupException($"This backup was made by a newer MxU Slides (format {manifest.FormatVersion}). Update the app to import it.");
            return manifest;
        }
    }

    private sealed class ZipSource : BackupSource
    {
        private ZipArchive zip = null!;
        private Dictionary<string, ZipArchiveEntry> entries = null!;

        public override IReadOnlySet<string> Entries => entries.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        public static ZipSource OpenZip(string path)
        {
            var zip = ZipFile.OpenRead(path);
            try
            {
                var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/')).GroupBy(e => e.FullName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                if (!entries.TryGetValue(ManifestName, out var manifestEntry))
                    throw new BackupException($"“{Path.GetFileName(path)}” isn't an MxU Slides library backup.");
                using var stream = manifestEntry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return new ZipSource { zip = zip, entries = entries, Manifest = ReadManifest(memory.ToArray(), Path.GetFileName(path)) };
            }
            catch
            {
                zip.Dispose();
                throw;
            }
        }

        public override byte[] Read(string entry, long limit)
        {
            var found = entries[entry];
            if (found.Length > limit) throw new InvalidDataException("it's far bigger than a document");
            using var output = new MemoryStream();
            CopyTo(entry, output);
            return output.ToArray();
        }

        /// <summary>
        /// Copies an entry out, never more than it says it holds: a damaged or hostile zip (a "zip
        /// bomb") that inflates beyond that is stopped there rather than filling the disk.
        /// </summary>
        public override void CopyTo(string entry, Stream output)
        {
            var found = entries[entry];
            using var input = found.Open();
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                total += read;
                if (total > found.Length) throw new InvalidDataException("it holds more than it says");
                output.Write(buffer, 0, read);
            }
        }

        public override DateTimeOffset? Modified(string entry) => entries.TryGetValue(entry, out var found) ? found.LastWriteTime : null;

        public override void Dispose() => zip.Dispose();
    }

    private sealed class FolderSource : BackupSource
    {
        private string root = "";
        private HashSet<string> entries = null!;

        public override IReadOnlySet<string> Entries => entries;

        public static FolderSource OpenFolder(string path)
        {
            var root = Path.GetFullPath(path);
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .Where(f => !IsScratch(f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // A Mac's backup is a folder too, of Automerge documents, which Windows can't read yet (Phase 10).
            if (files.Any(f => f.EndsWith(".automerge", StringComparison.OrdinalIgnoreCase)))
                throw new BackupException("This backup was made on a Mac. Its documents are in the Mac's own library format, which MxU Slides for Windows can't read yet. "
                    + "To move songs across, export them on the Mac as MxU Slides presentations (.mxuslides) and import those here.");
            if (files.Contains(ManifestName))
                return new FolderSource { root = root, entries = files, Manifest = ReadManifest(File.ReadAllBytes(Path.Combine(root, ManifestName)), Path.GetFileName(root)) };
            // An old daily copy (Backups/2026-10-24): the documents and library.json, no manifest. It's described from what's there.
            if (!files.Contains(LibraryStore.ManifestFileName))
                throw new BackupException($"“{Path.GetFileName(root)}” isn't an MxU Slides library backup.");
            var counts = TypeOfFolder.Keys.ToDictionary(folder => folder, folder => files.Count(f => f.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)), StringComparer.Ordinal);
            var created = DateTime.TryParseExact(Path.GetFileName(root), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var day)
                ? new DateTimeOffset(day) : new DateTimeOffset(Directory.GetCreationTime(root));
            return new FolderSource
            {
                root = root, entries = files, IsDailyCopy = true,
                Manifest = new BackupManifest { CreatedAt = Stamp(created), DocumentCounts = counts, App = "MxU Slides for Windows (daily copy of the documents)" },
            };
        }

        private string PathOf(string entry) => SafeRelative(entry) is { } relative ? Path.Combine(root, relative) : throw new InvalidDataException("a path outside the backup");

        public override byte[] Read(string entry, long limit)
        {
            var path = PathOf(entry);
            if (new FileInfo(path).Length > limit) throw new InvalidDataException("it's far bigger than a document");
            return File.ReadAllBytes(path);
        }

        public override void CopyTo(string entry, Stream output)
        {
            using var input = File.OpenRead(PathOf(entry));
            input.CopyTo(output);
        }

        public override DateTimeOffset? Modified(string entry) => File.GetLastWriteTimeUtc(PathOf(entry));

        public override void Dispose() { }
    }
}
