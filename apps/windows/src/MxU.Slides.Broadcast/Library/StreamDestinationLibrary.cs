using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Broadcast.Library;

/// <summary>
/// Stream destinations as library documents, with their keys kept in a <see cref="StreamKeyVault"/>
/// instead of the JSON. Covers the Mac's StreamDestinationLibrary (display names, moving a
/// preset's old inline destinations into the library, the in-memory destination a session
/// uses) and the key handling the Mac gets from the Keychain:
/// <list type="bullet">
/// <item><see cref="SaveDestination"/> files the key in the vault and saves the document without it.</item>
/// <item><see cref="Resolve(LibraryStore, StreamKeyVault, string)"/> gives back a copy with the key filled in, for streaming only.</item>
/// <item>A document that still carries a key (written by the Mac, or before this existed) keeps
/// working: the vault is asked first, then the document. <see cref="ScrubStreamKeys"/> moves such keys into the vault.</item>
/// </list>
/// Call from one thread at a time (the library's thread).
/// </summary>
public static class StreamDestinationLibrary
{
    public static string DisplayName(StreamDestination destination) =>
        string.IsNullOrEmpty(destination.Name) ? destination.Url : destination.Name;

    /// <summary>Identifies "the same destination" when de-duplicating: transport, trimmed URL and key.</summary>
    public static string CustomKey(StreamTransport transport, string url, string? streamKey) =>
        $"{StreamTransportJsonConverter.ToRaw(transport)}|{url.Trim()}|{streamKey ?? ""}";

    /// <summary>A preset's old inline destination as a library document (same id, key still in memory).</summary>
    public static StreamDestination Hoisted(StreamPresetDestination legacy) => new()
    {
        Id = legacy.Id,
        Name = legacy.Name,
        Transport = legacy.Transport,
        Url = legacy.Url,
        StreamKey = legacy.StreamKey,
        VideoCodec = legacy.VideoCodec,
        MaxHeight = legacy.MaxHeight,
        Hdr = legacy.Hdr,
        CanvasScreenId = legacy.CanvasScreenId,
        Width = legacy.Width,
        Height = legacy.Height,
        FrameRate = legacy.FrameRate,
    };

    /// <summary>What migrating one preset will do: new documents to create, the ids it ends up using, its kind, and what had to go.</summary>
    public sealed class MigrationPlan
    {
        public List<StreamDestination> Records { get; } = [];

        public List<string> DestinationIds { get; } = [];

        public StreamPresetKind Kind { get; set; } = StreamPresetKind.RecordOnly;

        /// <summary>Sentences for destinations that couldn't stream and were left out.</summary>
        public List<string> Dropped { get; } = [];
    }

    /// <summary>
    /// Plans moving a preset's inline destinations into the library: unready ones are dropped
    /// (with a sentence saying so), duplicates — of each other or of an existing document — are
    /// merged by <see cref="CustomKey"/>. A preset that had destinations stays a stream preset
    /// even if they were all dropped (so it warns rather than silently only recording).
    /// </summary>
    public static MigrationPlan Plan(StreamRecordPreset preset, IReadOnlyDictionary<string, string> existingCustomIds)
    {
        var plan = new MigrationPlan();
        var customIds = new Dictionary<string, string>(existingCustomIds);
        foreach (var legacy in preset.Destinations)
        {
            if (StreamDestinationReadiness.Problem(legacy) is { } problem)
            {
                plan.Dropped.Add($"{preset.Name}: removed a destination that could not stream — {problem}");
                continue;
            }
            var record = Hoisted(legacy);
            var key = CustomKey(record.Transport, record.Url, record.StreamKey);
            if (customIds.TryGetValue(key, out var existing))
            {
                if (!plan.DestinationIds.Contains(existing)) plan.DestinationIds.Add(existing);
            }
            else
            {
                customIds[key] = record.Id;
                plan.Records.Add(record);
                plan.DestinationIds.Add(record.Id);
            }
        }
        plan.Kind = preset.PresetKind
            ?? (preset.Destinations.Count == 0 && (preset.DestinationIds ?? []).Count == 0 ? StreamPresetKind.RecordOnly : StreamPresetKind.Stream);
        return plan;
    }

    /// <summary>The per-session copy a stream starts from, carrying the display name.</summary>
    public static StreamPresetDestination SessionDestination(StreamDestination destination, string displayName) => new()
    {
        Id = destination.Id,
        Name = displayName,
        Transport = destination.Transport,
        Url = destination.Url,
        StreamKey = destination.StreamKey,
        VideoCodec = destination.VideoCodec,
        MaxHeight = destination.MaxHeight,
        Hdr = destination.Hdr,
        CanvasScreenId = destination.CanvasScreenId,
        Width = destination.Width,
        Height = destination.Height,
        FrameRate = destination.FrameRate,
    };

    /// <summary>A preset's kind, inferred for presets saved before it was stored.</summary>
    public static StreamPresetKind ResolvedKind(this StreamRecordPreset preset) =>
        preset.PresetKind
        ?? ((preset.DestinationIds ?? []).Count == 0 && preset.Destinations.Count == 0 ? StreamPresetKind.RecordOnly : StreamPresetKind.Stream);

    public static bool IsRecordOnly(this StreamRecordPreset preset) => preset.ResolvedKind() == StreamPresetKind.RecordOnly;

    // MARK: Keys

    /// <summary>The key to stream with: the vault's, else one the document still carries.</summary>
    public static string? StreamKey(StreamKeyVault vault, StreamDestination destination) =>
        vault.Get(destination.Id) ?? (string.IsNullOrEmpty(destination.StreamKey) ? null : destination.StreamKey);

    /// <summary>
    /// Saves a destination. A key on <paramref name="destination"/> goes into the vault (an empty
    /// one removes it); null leaves the stored key alone. The saved document never has a key.
    /// </summary>
    public static void SaveDestination(LibraryStore store, StreamKeyVault vault, StreamDestination destination)
    {
        if (string.IsNullOrEmpty(destination.Id)) destination.Id = LibraryStore.NewId();
        var previousHadKey = !string.IsNullOrEmpty(store.Get<StreamDestination>(destination.Id)?.StreamKey);
        if (previousHadKey && destination.StreamKey is null && !vault.Contains(destination.Id))
            vault.Set(destination.Id, store.Get<StreamDestination>(destination.Id)!.StreamKey);
        if (destination.StreamKey is not null) vault.Set(destination.Id, destination.StreamKey);
        var stored = MxuJson.Clone(destination);
        stored.StreamKey = null;
        store.Save(stored);
        if (previousHadKey) DeleteBackup(store, typeof(StreamDestination), destination.Id);
    }

    /// <summary>Deletes a destination document (to the library's Trash) and its key.</summary>
    public static void DeleteDestination(LibraryStore store, StreamKeyVault vault, string id)
    {
        store.Delete<StreamDestination>(id);
        vault.Remove(id);
    }

    /// <summary>A copy of the document with its key filled in, for a session (never save it back).</summary>
    public static StreamDestination? Resolve(LibraryStore store, StreamKeyVault vault, string id)
    {
        if (store.Get<StreamDestination>(id) is not { } document) return null;
        var copy = MxuJson.Clone(document);
        copy.StreamKey = StreamKey(vault, document);
        return copy;
    }

    /// <summary>
    /// The destinations a preset streams to, keys included: its old inline ones, then the library
    /// ones it names (missing ones are skipped and logged). Empty for record-only presets.
    /// Port of ServiceControls.resolvedDestinations.
    /// </summary>
    public static List<StreamPresetDestination> Resolve(LibraryStore store, StreamKeyVault vault, StreamRecordPreset preset)
    {
        if (preset.ResolvedKind() != StreamPresetKind.Stream) return [];
        var output = new List<StreamPresetDestination>(preset.Destinations);
        foreach (var id in preset.DestinationIds ?? [])
        {
            if (Resolve(store, vault, id) is not { } destination)
            {
                BroadcastLog.Warning("stream.destination.dangling", $"{preset.Name}: destination {id} no longer exists — skipped");
                continue;
            }
            output.Add(SessionDestination(destination, DisplayName(destination)));
        }
        return output;
    }

    /// <summary>Moves keys that documents still carry into the vault and re-saves the documents without them. Returns how many moved.</summary>
    public static int ScrubStreamKeys(LibraryStore store, StreamKeyVault vault)
    {
        var moved = 0;
        foreach (var destination in store.All<StreamDestination>())
        {
            if (string.IsNullOrEmpty(destination.StreamKey)) continue;
            if (!vault.Contains(destination.Id)) vault.Set(destination.Id, destination.StreamKey);
            var stored = MxuJson.Clone(destination);
            stored.StreamKey = null;
            store.Save(stored);
            DeleteBackup(store, typeof(StreamDestination), destination.Id);
            moved++;
        }
        return moved;
    }

    /// <summary>Removes the ".bak" copy the library keeps of a document's previous version (used when that version held a key).</summary>
    private static void DeleteBackup(LibraryStore store, Type type, string id)
    {
        try { File.Delete(store.DocumentPath(type, id) + ".bak"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Moves every preset's old inline destinations into the library (keys into the vault) and
    /// records each preset's kind. Safe to run at every start: presets already moved are left
    /// alone, and destinations are never duplicated. Returns how many presets changed and the
    /// "removed a destination" sentences. Port of Library.migrateStreamDestinations.
    /// </summary>
    public static (int Presets, IReadOnlyList<string> Dropped) MigrateStreamDestinations(LibraryStore store, StreamKeyVault vault)
    {
        var existingCustomIds = new Dictionary<string, string>();
        foreach (var record in store.All<StreamDestination>())
            existingCustomIds[CustomKey(record.Transport, record.Url, StreamKey(vault, record))] = record.Id;
        var migrated = 0;
        var dropped = new List<string>();
        foreach (var preset in store.All<StreamRecordPreset>())
        {
            if (preset.Destinations.Count == 0 && preset.PresetKind is not null) continue;
            var plan = Plan(preset, existingCustomIds);
            foreach (var record in plan.Records)
            {
                if (store.Get<StreamDestination>(record.Id) is not null) continue;
                SaveDestination(store, vault, record);
                existingCustomIds[CustomKey(record.Transport, record.Url, record.StreamKey)] = record.Id;
            }
            var updated = MxuJson.Clone(preset);
            var ids = updated.DestinationIds ?? [];
            foreach (var id in plan.DestinationIds)
                if (!ids.Contains(id)) ids.Add(id);
            updated.DestinationIds = ids;
            updated.PresetKind = plan.Kind;
            updated.Destinations = [];
            store.Save(updated);
            // The library keeps the previous version as a backup; that one still has the keys.
            if (preset.Destinations.Any(d => !string.IsNullOrEmpty(d.StreamKey)))
                DeleteBackup(store, typeof(StreamRecordPreset), preset.Id);
            migrated++;
            dropped.AddRange(plan.Dropped);
        }
        return (migrated, dropped);
    }
}
