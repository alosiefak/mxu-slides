using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Follow;

/// <summary>
/// A backup holds a copy of its booth's library items, so it can carry on with them if it takes
/// over (MxU PR #12, FollowCopy.swift). The Mac copies its Automerge saves and merges them; a
/// Windows library is JSON files, so a Windows booth offers each item's JSON and a Windows backup
/// lands it as it is (the booth's copy wins). A Mac's Automerge save can't be read here yet, and a
/// Windows booth offers its items only to a backup that asks for JSON, so a Mac backup never
/// gets a file it can't read. Copying only ever reads from the booth: its library never changes.
/// </summary>
public static class FollowCopy
{
    /// <summary>What a JSON save's version starts with ("json-" and its SHA-256), so a Mac's heads never match one.</summary>
    public const string JsonHeads = "json-";

    /// <summary>
    /// The kinds a backup copies, by the Mac's names: what the church shares, plus the setup a
    /// booth needs to run the service (output, stream and record presets). Not this computer's own
    /// gear (MIDI devices) or settings.
    /// </summary>
    public static IReadOnlyDictionary<string, Type> Kinds { get; } = new Dictionary<string, Type>
    {
        ["presentation"] = typeof(Presentation),
        ["service"] = typeof(Service),
        ["theme"] = typeof(Theme),
        ["media"] = typeof(MediaItem),
        ["audio"] = typeof(AudioItem),
        ["playlist"] = typeof(Playlist),
        ["overlay"] = typeof(Overlay),
        ["outputPreset"] = typeof(OutputPreset),
        ["alertPreset"] = typeof(AlertPreset),
        ["streamRecordPreset"] = typeof(StreamRecordPreset),
        ["streamDestination"] = typeof(StreamDestination),
        ["actionCombo"] = typeof(ActionCombo),
        ["scheduleTrigger"] = typeof(ScheduleTrigger),
        ["note"] = typeof(NoteDocument),
        ["confidenceLayout"] = typeof(ConfidenceLayout),
    };

    /// <summary>One item as offered: its kind, id, version and (for media and music) the file it plays.</summary>
    public sealed record Offer(string Kind, string Id, IReadOnlyList<string> Heads, string? File, string? Ext);

    /// <summary>A JSON save's version.</summary>
    public static IReadOnlyList<string> Heads(ReadOnlySpan<byte> json) => [JsonHeads + Convert.ToHexStringLower(SHA256.HashData(json))];

    public static bool IsJson(IReadOnlyList<string> heads) => heads.Count == 1 && heads[0].StartsWith(JsonHeads, StringComparison.Ordinal);

    /// <summary>The booth's side: every item it holds of the copied kinds, its save's version and its file.</summary>
    public static List<Offer> Offered(LibraryStore library)
    {
        var offers = new List<Offer>();
        foreach (var (kind, type) in Kinds)
        {
            foreach (var doc in All(library, type))
            {
                var path = library.DocumentPath(type, doc.Id);
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                var hash = doc switch
                {
                    MediaItem m => m.FileHash,
                    AudioItem a => a.FileHash,
                    _ => null,
                };
                var file = hash is not null && IsHash(hash) && library.Blobs.Find(hash) is { } blob ? blob : null;
                offers.Add(new Offer(kind, doc.Id, Heads(bytes), file is null ? null : hash,
                    file is null ? null : Path.GetExtension(file).TrimStart('.')));
            }
        }
        return offers;
    }

    /// <summary>One item's save, for a backup: the JSON file as it is on disk, or null if it isn't here.</summary>
    public static byte[]? Save(LibraryStore library, string kind, string id)
    {
        if (!Kinds.TryGetValue(kind, out var type) || Find(library, type, id) is null) return null;
        try
        {
            return File.ReadAllBytes(library.DocumentPath(type, id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The version of the copy this backup holds (null: it has none).</summary>
    public static IReadOnlyList<string>? HeldHeads(LibraryStore library, string kind, string id) =>
        Save(library, kind, id) is { } bytes ? Heads(bytes) : null;

    /// <summary>
    /// The backup's side: lands the booth's save here, replacing this computer's copy. A save that
    /// isn't that kind or names another id is refused (false), so a booth can't write anything else.
    /// </summary>
    public static bool Land(LibraryStore library, string kind, string id, byte[] json)
    {
        if (!Kinds.TryGetValue(kind, out var type)) return false;
        ILibraryDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize(json, type, MxuJson.Options) as ILibraryDocument;
        }
        catch (JsonException)
        {
            return false;
        }
        if (doc is null || doc.Id != id) return false;
        SaveMethod.MakeGenericMethod(type).Invoke(library, [doc]);
        return true;
    }

    /// <summary>Takes away a copy this backup made that the booth no longer holds (to Trash, as any delete).</summary>
    public static void Remove(LibraryStore library, string key)
    {
        var parts = key.Split('/', 2);
        if (parts.Length != 2 || !Kinds.TryGetValue(parts[0], out var type) || Find(library, type, parts[1]) is null) return;
        DeleteMethod.MakeGenericMethod(type).Invoke(library, [parts[1]]);
    }

    /// <summary>A media file's name: a lower-case SHA-256 (as FollowFiles.isHash).</summary>
    public static bool IsHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static readonly MethodInfo AllMethod = typeof(LibraryStore).GetMethod(nameof(LibraryStore.All))!;
    private static readonly MethodInfo GetMethod = typeof(LibraryStore).GetMethod(nameof(LibraryStore.Get))!;
    private static readonly MethodInfo SaveMethod = typeof(LibraryStore).GetMethod(nameof(LibraryStore.Save))!;
    private static readonly MethodInfo DeleteMethod = typeof(LibraryStore).GetMethod(nameof(LibraryStore.Delete))!;

    private static IEnumerable<ILibraryDocument> All(LibraryStore library, Type type) =>
        ((System.Collections.IEnumerable)AllMethod.MakeGenericMethod(type).Invoke(library, null)!).Cast<ILibraryDocument>();

    private static ILibraryDocument? Find(LibraryStore library, Type type, string id) =>
        GetMethod.MakeGenericMethod(type).Invoke(library, [id]) as ILibraryDocument;
}
