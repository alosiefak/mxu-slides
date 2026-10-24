using System.Text.Json.Serialization;

namespace MxU.Slides.Audio.Routing;

/// <summary>
/// A mix: something that is heard together (the music, the video sound) and the outputs it
/// plays on. The first output id is where it plays; any more are sends (the mixer's job).
/// Port of the Mac's AudioMixInventory.Entry, with the same JSON field names.
/// </summary>
public sealed record AudioMixEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString().ToUpperInvariant();

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>
    /// Output ids: empty = the system default; the first is where the mix plays, the rest are
    /// sends. <see cref="AudioMixInventory.NoOutputId"/> first means it reaches no device
    /// (streams and recordings only); <see cref="AudioMixInventory.DefaultOutputId"/> first
    /// means "the system default, plus these sends".
    /// </summary>
    [JsonPropertyName("outputIds")]
    public IReadOnlyList<string> OutputIds { get; init; } = [];

    [JsonPropertyName("folderId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FolderId { get; init; }

    [JsonIgnore]
    public bool IsMain => Id == AudioMixInventory.MainId;
}

/// <summary>
/// The mixes on this PC. There is always the main mix ("Main"), which the music plays on and,
/// unless told otherwise, the video sound too. This is the part of the Mac's
/// AudioMixInventory the routing needs (making, naming and removing mixes, and where each
/// plays); the mixer itself (folders, faders, inputs) comes later. Saved into settings.json
/// by the app whenever <see cref="Changed"/> fires.
/// <para>Threading: thread-safe; <see cref="Changed"/> is raised on the calling thread.</para>
/// </summary>
public sealed class AudioMixInventory
{
    public const string MainId = "main";

    /// <summary>A mix whose first output is this plays nowhere (the Mac's noOutputID).</summary>
    public const string NoOutputId = "none";

    /// <summary>A mix whose first output is this plays on the system default (the Mac's defaultOutputID).</summary>
    public const string DefaultOutputId = "default";

    private readonly Lock gate = new();
    private List<AudioMixEntry> entries;

    /// <param name="stored">What settings.json held; without a main mix it starts again from just "Main".</param>
    public AudioMixInventory(IEnumerable<AudioMixEntry>? stored = null)
    {
        entries = stored?.Where(e => e.Id.Length > 0).ToList() ?? [];
        if (!entries.Any(e => e.IsMain)) entries = [new AudioMixEntry { Id = MainId, Name = "Main" }];
    }

    public event Action? Changed;

    public IReadOnlyList<AudioMixEntry> Entries
    {
        get { lock (gate) return [.. entries]; }
    }

    public AudioMixEntry? Entry(string? id)
    {
        if (id is null) return null;
        lock (gate) return entries.FirstOrDefault(e => e.Id == id);
    }

    public AudioMixEntry Main => Entry(MainId) ?? new AudioMixEntry { Id = MainId, Name = "Main" };

    /// <summary>A new mix with no outputs (so on the default), named "Mix N" unless given a name.</summary>
    public AudioMixEntry Create(string? name = null)
    {
        AudioMixEntry made;
        lock (gate)
        {
            var taken = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            var number = entries.Count;
            while (taken.Contains($"Mix {number}")) number++;
            made = new AudioMixEntry { Name = name ?? $"Mix {number}" };
            entries.Add(made);
        }
        Changed?.Invoke();
        return made;
    }

    public void Rename(string id, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return;
        Change(id, e => e with { Name = trimmed });
    }

    /// <summary>Where a mix plays: an output id, <see cref="NoOutputId"/>, or <see cref="DefaultOutputId"/>.</summary>
    public string PrimaryToken(string mixId) => Entry(mixId)?.OutputIds.FirstOrDefault() ?? DefaultOutputId;

    /// <summary>Makes a mix play on an output (or the default, or nowhere), keeping its sends.</summary>
    public void SetPrimary(string mixId, string token) => Change(mixId, e =>
    {
        var sends = e.OutputIds.Skip(1).Where(id => id != token).ToList();
        IReadOnlyList<string> ids = token switch
        {
            NoOutputId => [NoOutputId],
            DefaultOutputId => sends.Count == 0 ? [] : [DefaultOutputId, .. sends],
            _ => [token, .. sends],
        };
        return e with { OutputIds = ids };
    });

    /// <summary>Adds or removes a send to an output (the mixer's "also send to").</summary>
    public void ToggleSecondary(string mixId, string outputId) => Change(mixId, e =>
    {
        var ids = e.OutputIds.ToList();
        if (ids.FirstOrDefault() == NoOutputId) ids = [];
        if (ids.Count == 0) ids = [DefaultOutputId];
        var at = ids.IndexOf(outputId, 1);
        if (at > 0) ids.RemoveAt(at);
        else if (ids[0] != outputId) ids.Add(outputId);
        if (ids is [DefaultOutputId]) ids = [];
        return e with { OutputIds = ids };
    });

    /// <summary>True for a mix that plays on no device (its first output is <see cref="NoOutputId"/>).</summary>
    public static bool IsVirtual(IReadOnlyList<string> outputIds) => outputIds.FirstOrDefault() == NoOutputId;

    /// <summary>Removes a mix; the main mix can't go.</summary>
    public void Remove(string id)
    {
        if (id == MainId) return;
        lock (gate)
            if (entries.RemoveAll(e => e.Id == id) == 0) return;
        Changed?.Invoke();
    }

    private void Change(string id, Func<AudioMixEntry, AudioMixEntry> change)
    {
        lock (gate)
        {
            var index = entries.FindIndex(e => e.Id == id);
            if (index < 0) return;
            var before = entries[index];
            var after = change(before);
            if (after.Name == before.Name && after.FolderId == before.FolderId && after.OutputIds.SequenceEqual(before.OutputIds)) return;
            entries[index] = after;
        }
        Changed?.Invoke();
    }
}
