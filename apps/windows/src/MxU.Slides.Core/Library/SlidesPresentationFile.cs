using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// The .mxuslides file: one presentation with its themes, media files and fonts, packed into
/// a zip so it can be copied, emailed or carried on a stick to another computer, Mac or
/// Windows. This is how documents move between the two apps today (the Mac's library itself
/// is an Automerge store, which Windows can't read yet). Port of the Mac's
/// SlidesPresentationFile; the layout must match it exactly:
/// <code>
/// manifest.json       { formatVersion, createdAt, name, slideCount }
/// presentation.json   { presentation, themes: [...], media: [...], audio: [...] }
/// blobs/&lt;hash&gt;.&lt;ext&gt;   each media file, named as in the library's Blobs folder
/// fonts/&lt;hash&gt;.&lt;ext&gt;   each font file the slides need that the other computer may not have
/// </code>
/// The entries sit at the top of the zip (the Mac zips the folder's contents, not the folder).
/// </summary>
public static class SlidesPresentationFile
{
    public const string FileExtension = "mxuslides";
    public const string TypeIdentifier = "com.example.mxuslides.presentation";
    public const int FormatVersion = 1;
    internal const string ManifestName = "manifest.json";
    internal const string PayloadName = "presentation.json";
    internal const string BlobsName = "blobs";
    internal const string FontsName = "fonts";

    public sealed class Manifest
    {
        [JsonPropertyName("formatVersion"), JsonRequired]
        public int FormatVersion { get; set; }

        /// <summary>Written "2026-10-24T09:30:00Z": the Mac reads dates this way and refuses fractions of a second.</summary>
        [JsonPropertyName("createdAt"), JsonRequired, JsonConverter(typeof(InternetDateConverter))]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("name"), JsonRequired]
        public string Name { get; set; } = "";

        [JsonPropertyName("slideCount"), JsonRequired]
        public int SlideCount { get; set; }
    }

    public sealed class Payload
    {
        [JsonPropertyName("presentation"), JsonRequired]
        public Presentation Presentation { get; set; } = new();

        [JsonPropertyName("themes"), JsonRequired]
        public List<Theme> Themes { get; set; } = [];

        [JsonPropertyName("media"), JsonRequired]
        public List<MediaItem> Media { get; set; } = [];

        [JsonPropertyName("audio"), JsonRequired]
        public List<AudioItem> Audio { get; set; } = [];
    }

    public enum Problem
    {
        NotAPresentation,
        UnsupportedFormat,
        Archive,
    }

    /// <summary>Why a file couldn't be read or written; the message is the Mac's wording.</summary>
    public sealed class FileException(Problem problem, string message) : Exception(message)
    {
        public Problem Problem { get; } = problem;

        public static FileException NotAPresentation(string name) => new(Problem.NotAPresentation, $"“{name}” isn't an MxU Slides presentation.");

        public static FileException UnsupportedFormat(int version) =>
            new(Problem.UnsupportedFormat, $"This presentation was made by a newer MxU Slides (format {version}). Update the app to import it.");

        public static FileException Archive(string detail) => new(Problem.Archive, detail);
    }

    public sealed record ExportSummary(IReadOnlyList<string> MissingFiles, int FontCount);

    // MARK: Writing

    /// <summary>
    /// Writes the file. Media whose file isn't on this PC is left out and named in the summary
    /// (the other computer shows it as missing until it's located there). The file is built
    /// beside the destination and moved into place at the end, so a failed export never leaves
    /// half a file under the real name.
    /// </summary>
    public static ExportSummary Write(DeckBundle bundle, BlobStore blobs, IReadOnlyList<string> fonts, string destination,
        CancellationToken cancellationToken = default)
    {
        var media = bundle.Media.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        var audio = bundle.Audio.Values.OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
        var folder = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        var missing = new List<string>();
        var fontCount = 0;
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var payload = new Payload
                {
                    Presentation = bundle.Presentation,
                    Themes = [.. bundle.Themes.Values.OrderBy(t => t.Id, StringComparer.Ordinal)],
                    Media = media,
                    Audio = audio,
                };
                var manifest = new Manifest
                {
                    FormatVersion = FormatVersion,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Name = bundle.Presentation.Name,
                    SlideCount = bundle.Presentation.Slides.Count,
                };
                AddBytes(zip, ManifestName, MxuJson.SerializeToUtf8(manifest));
                AddBytes(zip, PayloadName, MxuJson.SerializeToUtf8(payload));

                AddFolder(zip, BlobsName);
                var copied = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (hash, name) in media.Select(m => (m.FileHash, m.Name)).Concat(audio.Select(a => (a.FileHash, a.Name))))
                {
                    if (copied.Contains(hash)) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (blobs.Find(hash) is { } source)
                    {
                        // Media is already compressed; storing it as it is saves minutes on a long video.
                        AddFile(zip, $"{BlobsName}/{Path.GetFileName(source)}", source, CompressionLevel.NoCompression);
                        copied.Add(hash);
                    }
                    else
                    {
                        missing.Add(name);
                    }
                }

                AddFolder(zip, FontsName);
                var packed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in fonts)
                {
                    if (FontSharing.Document(path) is not { } font) continue;
                    var entry = $"{FontsName}/{font.Id}.{font.Ext}";
                    if (!packed.Add(entry)) continue;
                    AddFile(zip, entry, path, CompressionLevel.Optimal);
                    fontCount++;
                }
            }
            File.Move(temp, destination, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw FileException.Archive($"Couldn't write the file: {e.Message}");
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        return new ExportSummary([.. missing.Order(StringComparer.Ordinal)], fontCount);
    }

    // Unix permissions in the high half of the attributes, so the Mac unpacks plain readable files and folders.
    private const int UnixFile = 0x81A4 << 16;          // -rw-r--r--
    private const int UnixFolder = (0x41ED << 16) | 0x10; // drwxr-xr-x, plus the DOS "folder" bit

    private static void AddBytes(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.ExternalAttributes = UnixFile;
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static void AddFile(ZipArchive zip, string name, string path, CompressionLevel level)
    {
        var entry = zip.CreateEntry(name, level);
        entry.ExternalAttributes = UnixFile;
        entry.LastWriteTime = File.GetLastWriteTime(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = entry.Open();
        input.CopyTo(output);
    }

    private static void AddFolder(ZipArchive zip, string name) =>
        zip.CreateEntry(name + "/", CompressionLevel.NoCompression).ExternalAttributes = UnixFolder;

    /// <summary>
    /// A presentation ready to export: DeckBundle.Load, plus the songs its "play music" actions
    /// start, so they travel too.
    /// </summary>
    public static DeckBundle? Bundle(LibraryStore library, string id)
    {
        if (DeckBundle.Load(library, id) is not { } bundle) return null;
        foreach (var audioId in AudioIds(bundle.Presentation).Where(a => !bundle.Audio.ContainsKey(a)).Order(StringComparer.Ordinal))
            if (library.Get<AudioItem>(audioId) is { } item) bundle.Audio[audioId] = MxuJson.Clone(item);
        return bundle;
    }

    internal static HashSet<string> AudioIds(Presentation presentation) =>
        presentation.Slides.SelectMany(s => s.Actions ?? [])
            .Where(a => a.Kind == SlideActionKind.FireAudio && !string.IsNullOrEmpty(a.AudioItemId))
            .Select(a => a.AudioItemId!)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The font files to pack for a bundle: those holding the fonts its slides and themes name, from <paramref name="fonts"/>.</summary>
    public static IReadOnlyList<string> FontFiles(DeckBundle bundle, FontIndex fonts)
    {
        var names = FontSharing.Names(bundle.Presentation);
        foreach (var theme in bundle.Themes.Values) names.UnionWith(FontSharing.Names(theme));
        return [.. FontSharing.FilesToShare(names, new HashSet<string>(), fonts.Resolve).Keys.Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The file's suggested name: the presentation's, made safe for a file name ("Presentation" if it has none).</summary>
    public static string FileName(Presentation presentation) => BaseName(presentation) + "." + FileExtension;

    public static string BaseName(Presentation presentation)
    {
        // The Mac turns "/", ":" and "\" into "-"; Windows also forbids these others in file names.
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', ':', '\\']).ToHashSet();
        var name = new string([.. presentation.Name.Select(c => invalid.Contains(c) ? '-' : c)]).Trim().TrimEnd('.');
        return name.Length == 0 ? "Presentation" : name;
    }

    // MARK: Reading

    /// <summary>A file unpacked into a scratch folder: its manifest, its documents, and where its files are.</summary>
    public sealed class Contents(Manifest manifest, Payload payload, string directory)
    {
        public Manifest Manifest { get; } = manifest;

        public Payload Payload { get; } = payload;

        public string Directory { get; } = directory;

        /// <summary>The media files it carries, by the hash in their name.</summary>
        public Dictionary<string, string> BlobFiles
        {
            get
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                var folder = Path.Combine(Directory, BlobsName);
                if (System.IO.Directory.Exists(folder))
                    foreach (var file in System.IO.Directory.EnumerateFiles(folder)) map.TryAdd(Path.GetFileNameWithoutExtension(file), file);
                return map;
            }
        }

        /// <summary>The font files it carries, in name order.</summary>
        public IReadOnlyList<string> FontFiles
        {
            get
            {
                var folder = Path.Combine(Directory, FontsName);
                return System.IO.Directory.Exists(folder)
                    ? [.. System.IO.Directory.EnumerateFiles(folder).OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)]
                    : [];
            }
        }
    }

    /// <summary>
    /// Unpacks a file into <paramref name="directory"/> and reads its manifest and documents.
    /// A file from a newer app (a higher format) is refused rather than half-read. As a
    /// courtesy, a file someone re-zipped with its folder inside (one folder holding the
    /// manifest) is read too.
    /// </summary>
    public static Contents Read(string path, string directory, CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName(path);
        try
        {
            SafeZip.Extract(path, directory, cancellationToken);
        }
        catch (SafeZip.UnsafeArchiveException e)
        {
            throw FileException.Archive($"“{name}” can't be imported: {e.Message}");
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw FileException.NotAPresentation(name);
        }
        var root = Root(directory) ?? throw FileException.NotAPresentation(name);
        Manifest manifest;
        try
        {
            manifest = MxuJson.Deserialize<Manifest>(File.ReadAllBytes(Path.Combine(root, ManifestName)));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            throw FileException.NotAPresentation(name);
        }
        if (manifest.FormatVersion > FormatVersion) throw FileException.UnsupportedFormat(manifest.FormatVersion);
        try
        {
            var payload = MxuJson.Deserialize<Payload>(File.ReadAllBytes(Path.Combine(root, PayloadName)));
            return new Contents(manifest, payload, root);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            throw FileException.NotAPresentation(name);
        }
    }

    private static string? Root(string directory)
    {
        if (File.Exists(Path.Combine(directory, ManifestName))) return directory;
        var folders = Directory.EnumerateDirectories(directory).Where(d => !Path.GetFileName(d).StartsWith("__MACOSX", StringComparison.Ordinal)).ToList();
        return folders.Count == 1 && File.Exists(Path.Combine(folders[0], ManifestName)) ? folders[0] : null;
    }

    // MARK: Importing

    /// <summary>What to do when the presentation is already in the library (the third answer, Skip, imports nothing).</summary>
    public enum Conflict
    {
        Replace,
        KeepBoth,
    }

    /// <summary>What the library already has: themes by id, media and music by id → file hash.</summary>
    public sealed record Existing(IReadOnlySet<string> Themes, IReadOnlyDictionary<string, string> Media, IReadOnlyDictionary<string, string> Audio)
    {
        public static Existing Of(LibraryStore library) => new(
            library.All<Theme>().Select(t => t.Id).ToHashSet(StringComparer.Ordinal),
            library.All<MediaItem>().ToDictionary(m => m.Id, m => m.FileHash, StringComparer.Ordinal),
            library.All<AudioItem>().ToDictionary(a => a.Id, a => a.FileHash, StringComparer.Ordinal));
    }

    /// <summary>What an import will add: the documents to save and which carried files to copy in, and what the file lacked.</summary>
    public sealed record Plan(
        Presentation Presentation,
        IReadOnlyList<Theme> Themes,
        IReadOnlyList<MediaItem> Media,
        IReadOnlyList<AudioItem> Audio,
        IReadOnlySet<string> Hashes,
        IReadOnlyList<string> Missing);

    /// <summary>
    /// Works out an import, changing nothing. Themes and items the library already has (by
    /// id) are kept as they are. An item whose file the library already has under another item
    /// isn't added twice: the slides are pointed at the library's item instead. An item whose
    /// file the bundle lacks and the library hasn't got is reported missing. A new id
    /// (<paramref name="presentationId"/>) gives "Keep Both" its copy.
    /// </summary>
    public static Plan MakePlan(Payload payload, Existing existing, IReadOnlySet<string> files, string? presentationId = null)
    {
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var media = new List<MediaItem>();
        var audio = new List<AudioItem>();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();
        var mediaByHash = FirstByHash(existing.Media);
        var audioByHash = FirstByHash(existing.Audio);
        foreach (var item in payload.Media.Where(m => !existing.Media.ContainsKey(m.Id)))
        {
            if (mediaByHash.TryGetValue(item.FileHash, out var id)) idMap[item.Id] = id;
            else if (files.Contains(item.FileHash))
            {
                var copy = MxuJson.Clone(item);
                copy.Folder = null;
                copy.FolderId = null;
                media.Add(copy);
                hashes.Add(item.FileHash);
            }
            else missing.Add(item.Name);
        }
        foreach (var item in payload.Audio.Where(a => !existing.Audio.ContainsKey(a.Id)))
        {
            if (audioByHash.TryGetValue(item.FileHash, out var id)) idMap[item.Id] = id;
            else if (files.Contains(item.FileHash))
            {
                var copy = MxuJson.Clone(item);
                copy.Folder = null;
                copy.FolderId = null;
                audio.Add(copy);
                hashes.Add(item.FileHash);
            }
            else missing.Add(item.Name);
        }
        var themes = payload.Themes.Where(t => !existing.Themes.Contains(t.Id)).Select(t => Remapped(t, idMap) ?? MxuJson.Clone(t)).ToList();
        var presentation = Remapped(payload.Presentation, idMap) ?? MxuJson.Clone(payload.Presentation);
        presentation.Id = presentationId ?? presentation.Id;
        presentation.Folder = null;
        presentation.FolderId = null;
        return new Plan(presentation, themes, media, audio, hashes, [.. missing.Order(StringComparer.Ordinal)]);
    }

    private static Dictionary<string, string> FirstByHash(IReadOnlyDictionary<string, string> hashes)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, hash) in hashes.OrderBy(p => p.Key, StringComparer.Ordinal)) map.TryAdd(hash, id);
        return map;
    }

    public sealed record ImportResult(string PresentationId, string Name, int ThemesAdded, int MediaAdded, int FontsAdded, IReadOnlyList<string> Missing);

    public static bool IsInLibrary(Contents contents, LibraryStore library) =>
        library.Get<Presentation>(contents.Payload.Presentation.Id) is not null;

    /// <summary>
    /// Imports unpacked contents: copies the carried media into the library and the fonts into
    /// its fonts folder, then saves the themes, items and presentation. Reads and copies files,
    /// so call it off the UI thread. <paramref name="fontAvailable"/> says whether this PC
    /// already has a font face (by PostScript name), so a font Windows has isn't copied again.
    /// </summary>
    public static ImportResult ImportContents(Contents contents, LibraryStore library, Conflict conflict,
        Func<string, bool>? fontAvailable = null, CancellationToken cancellationToken = default)
    {
        var inLibrary = IsInLibrary(contents, library);
        var blobFiles = contents.BlobFiles;
        var plan = MakePlan(contents.Payload, Existing.Of(library), blobFiles.Keys.ToHashSet(StringComparer.Ordinal),
            inLibrary && conflict == Conflict.KeepBoth ? LibraryStore.NewId() : null);

        foreach (var hash in plan.Hashes.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (blobFiles.TryGetValue(hash, out var file)) library.Blobs.Store(file);
        }
        var fontsAdded = FontSharing.Install(contents.FontFiles, library.Root, fontAvailable ?? (_ => false));

        foreach (var theme in plan.Themes) library.Save(theme);
        foreach (var item in plan.Media) library.Save(item);
        foreach (var item in plan.Audio) library.Save(item);
        // Replace overwrites the library's copy; Keep Both and a first import add one.
        library.Save(plan.Presentation);
        return new ImportResult(plan.Presentation.Id, plan.Presentation.Name, plan.Themes.Count,
            plan.Media.Count + plan.Audio.Count, fontsAdded, plan.Missing);
    }

    /// <summary>
    /// A copy of <paramref name="value"/> with every id in <paramref name="map"/> swapped for its
    /// replacement, wherever it appears (as a value or as a key); null when nothing changed.
    /// Done on the JSON, so no field that holds a media id can be forgotten.
    /// </summary>
    internal static T? Remapped<T>(T value, IReadOnlyDictionary<string, string> map) where T : class
    {
        if (map.Count == 0) return null;
        var changed = false;
        JsonNode? Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonValue v when v.GetValueKind() == JsonValueKind.String && map.TryGetValue(v.GetValue<string>(), out var target):
                    changed = true;
                    return JsonValue.Create(target);
                case JsonArray array:
                {
                    var items = array.ToList();
                    array.Clear();
                    foreach (var item in items) array.Add(Walk(item));
                    return array;
                }
                case JsonObject map2:
                {
                    var properties = map2.ToList();
                    map2.Clear();
                    foreach (var (key, child) in properties)
                    {
                        var renamed = key;
                        if (map.TryGetValue(key, out var newKey))
                        {
                            changed = true;
                            renamed = newKey;
                        }
                        if (!map2.ContainsKey(renamed)) map2[renamed] = Walk(child);
                    }
                    return map2;
                }
                default:
                    return node;
            }
        }
        var tree = Walk(JsonSerializer.SerializeToNode(value, MxuJson.Compact));
        return changed ? tree.Deserialize<T>(MxuJson.Options) : null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Dates as the Mac's JSON writes and reads them: "2026-10-24T09:30:00Z", whole seconds, UTC.</summary>
public sealed class InternetDateConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString() ?? throw new JsonException("missing date"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}
