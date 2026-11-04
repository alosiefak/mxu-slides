using System.Text.Json.Nodes;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Sync.Format;

namespace MxU.Slides.Sync.Engine;

/// <summary>
/// One Windows document as sync sees it: the bytes of its JSON file (what <see cref="LibraryStore.SaveIfUnchanged{T}"/>
/// compares against), its JSON, and the fingerprint of its canonical form.
/// </summary>
public sealed record LocalDocument(byte[] File, JsonObject Json, string Hash, DateTime WriteUtc)
{
    public string Name => Json["name"] is JsonValue name && name.TryGetValue<string>(out var text) ? text : "";
}

/// <summary>
/// The Windows library (<see cref="LibraryStore"/>, the app's working store) as sync uses it. It
/// reads documents from their files, never from the objects the app is holding: the app may be
/// changing one of those on its own thread, while a file is always a whole saved version (they're
/// written atomically). Sync saves through <see cref="LibraryStore.SaveIfUnchanged{T}"/>, so it can
/// never overwrite an edit it hasn't seen.
/// </summary>
public sealed class WindowsLibrary
{
    private readonly LibraryStore store;
    private readonly Dictionary<Type, Kind> kinds = [];

    private sealed record Kind(
        Func<IEnumerable<string>> Ids,
        Func<object, byte[]?, bool> SaveIfUnchanged,
        Func<string, byte[], bool> DeleteIfUnchanged);

    public WindowsLibrary(LibraryStore store)
    {
        this.store = store;
        Add<Presentation>();
        Add<Service>();
        Add<Theme>();
        Add<MediaItem>();
        Add<AudioItem>();
        Add<Playlist>();
        Add<Overlay>();
        Add<AlertPreset>();
        Add<ActionCombo>();
        Add<ScheduleTrigger>();
        Add<ConfidenceLayout>();
        Add<GroupPalette>();
        Add<EffectPresetBoard>();
        Add<AnimationPresetBoard>();
        Add<TrackChannelList>();
        Add<TrackSet>();
    }

    private void Add<T>() where T : class, ILibraryDocument => kinds[typeof(T)] = new Kind(
        () => store.All<T>().Select(d => d.Id),
        (doc, expected) => store.SaveIfUnchanged((T)doc, expected),
        (id, expected) => store.DeleteIfUnchanged<T>(id, expected));

    public LibraryStore Store => store;

    public string BlobDirectory => store.BlobDirectory;

    /// <summary>The ids of one kind's documents.</summary>
    public IEnumerable<string> Ids(MacKind kind) => For(kind).Ids();

    /// <summary>A document as its file holds it, or null when there's none. A file that won't parse throws.</summary>
    public LocalDocument? Read(MacKind kind, string id)
    {
        var path = store.DocumentPath(kind.WindowsType!, id);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) return null;
                var bytes = File.ReadAllBytes(path);
                var json = JsonNode.Parse(bytes)?.AsObject() ?? throw new InvalidDataException($"{path} is empty");
                var model = MacDocuments.ToModel(json, kind);
                var canonical = MxuJson.SerializeToUtf8(model, indented: false);
                return new LocalDocument(bytes, MacDocuments.ToJson(model), MacDocuments.Hash(canonical), file.LastWriteTimeUtc);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < 5)
            {
                // The app is swapping in a new version of the file (File.Replace) this moment.
                Thread.Sleep(40 * (attempt + 1));
            }
        }
    }

    /// <summary>The document file's size and time, or null when there's none.</summary>
    public FileStamp? Stamp(MacKind kind, string id)
    {
        var file = new FileInfo(store.DocumentPath(kind.WindowsType!, id));
        return file.Exists ? FileStamp.Of(file) : null;
    }

    /// <summary>Saves <paramref name="json"/> as the document, if its file still holds <paramref name="expected"/> (null: no file). False when the file moved on.</summary>
    public bool SaveIfUnchanged(MacKind kind, JsonObject json, byte[]? expected) =>
        For(kind).SaveIfUnchanged(MacDocuments.ToModel(json, kind), expected);

    /// <summary>Moves the document to the library's Trash, if its file still holds <paramref name="expected"/>.</summary>
    public bool DeleteIfUnchanged(MacKind kind, string id, byte[] expected) => For(kind).DeleteIfUnchanged(id, expected);

    private Kind For(MacKind kind) =>
        kind.WindowsType is { } type && kinds.TryGetValue(type, out var found) ? found : throw new ArgumentException($"Sync doesn't carry {kind.RawValue}");
}
