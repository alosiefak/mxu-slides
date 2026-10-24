namespace MxU.Slides.Audio;

/// <summary>
/// The order a playlist plays in: straight through, looped, one track on repeat, or
/// shuffled (every track once per pass, never the same one twice across a wrap).
/// It only deals in entry ids; the player looks the tracks up. Port of PlaylistQueue.swift
/// (a Swift value type; here a class, so copy it with <see cref="Clone"/>).
/// Methods that shuffle take an optional <see cref="Random"/> so tests can seed them.
/// </summary>
public sealed class PlaylistQueue
{
    public enum RepeatBehavior
    {
        None,
        Playlist,
        Single,
    }

    private List<int> order;

    // Where in `order` we are; null before the first start and after a pass ends.
    private int? cursor;

    public PlaylistQueue(IReadOnlyList<string> entryIds, RepeatBehavior repeatBehavior = RepeatBehavior.None, bool shuffled = false)
    {
        EntryIds = entryIds.ToList();
        Repeat = repeatBehavior;
        IsShuffled = shuffled;
        order = Enumerable.Range(0, EntryIds.Count).ToList();
    }

    public IReadOnlyList<string> EntryIds { get; private set; }

    public RepeatBehavior Repeat { get; set; }

    public bool IsShuffled { get; private set; }

    public PlaylistQueue Clone() => new(EntryIds, Repeat, IsShuffled) { order = order.ToList(), cursor = cursor };

    public string? CurrentEntryId =>
        cursor is { } c && c >= 0 && c < order.Count ? EntryIds[order[c]] : null;

    /// <summary>"Track 2 of 5" in this pass (1-based).</summary>
    public (int Index, int Count)? PositionInPass =>
        cursor is { } c && c >= 0 && c < order.Count ? (c + 1, order.Count) : null;

    /// <summary>Starts a pass, at <paramref name="entryId"/> when given (shuffled: it leads the pass).</summary>
    public void Start(string? entryId = null, Random? random = null)
    {
        if (EntryIds.Count == 0) return;
        random ??= Random.Shared;
        var found = entryId is null ? -1 : IndexOf(EntryIds, entryId);
        var chosen = found >= 0 ? found : IsShuffled ? random.Next(0, EntryIds.Count) : 0;
        if (IsShuffled)
        {
            var rest = Enumerable.Range(0, EntryIds.Count).Where(i => i != chosen).ToList();
            Shuffle(rest, random);
            order = [chosen, .. rest];
        }
        else
        {
            order = Enumerable.Range(0, EntryIds.Count).ToList();
        }
        cursor = order.IndexOf(chosen);
    }

    /// <summary>The operator pressed Next: always moves on, even under repeat-one.</summary>
    public string? Next(Random? random = null) => Step(random ?? Random.Shared);

    public string? Previous()
    {
        if (order.Count == 0) return null;
        if (cursor is not { } position)
        {
            cursor = 0;
            return CurrentEntryId;
        }
        if (position > 0) cursor = position - 1;
        else if (Repeat != RepeatBehavior.None) cursor = order.Count - 1;
        return CurrentEntryId;
    }

    /// <summary>A track finished by itself: repeat-one plays it again, otherwise like Next.</summary>
    public string? AdvanceAfterNaturalEnd(Random? random = null)
    {
        if (Repeat == RepeatBehavior.Single) return CurrentEntryId;
        return Step(random ?? Random.Shared);
    }

    private string? Step(Random random)
    {
        if (order.Count == 0) return null;
        if (cursor is not { } position)
        {
            Start(random: random);
            return CurrentEntryId;
        }
        if (position + 1 < order.Count)
        {
            cursor = position + 1;
            return CurrentEntryId;
        }
        if (Repeat == RepeatBehavior.None)
        {
            cursor = null;
            return null;
        }
        if (IsShuffled)
        {
            // A fresh shuffle for the next pass, but never the track that just played first.
            var justPlayed = order[position];
            Shuffle(order, random);
            if (order.Count > 1 && order[0] == justPlayed)
            {
                var swap = random.Next(1, order.Count);
                (order[0], order[swap]) = (order[swap], order[0]);
            }
        }
        cursor = 0;
        return CurrentEntryId;
    }

    /// <summary>Turns shuffle on (the current track leads a fresh shuffle) or off (back to list order).</summary>
    public void SetShuffled(bool shuffled, Random? random = null)
    {
        if (shuffled == IsShuffled) return;
        random ??= Random.Shared;
        IsShuffled = shuffled;
        int? currentIndex = cursor is { } c && c >= 0 && c < order.Count ? order[c] : null;
        if (shuffled)
        {
            if (currentIndex is not { } current) return;
            var rest = Enumerable.Range(0, EntryIds.Count).Where(i => i != current).ToList();
            Shuffle(rest, random);
            order = [current, .. rest];
            cursor = 0;
        }
        else
        {
            order = Enumerable.Range(0, EntryIds.Count).ToList();
            cursor = currentIndex;
        }
    }

    /// <summary>
    /// The playlist was edited: keep the current track and the order already played,
    /// add new tracks to the end. If the current track was removed the queue parks
    /// (no current entry) until the next start or step.
    /// </summary>
    public void UpdateEntries(IReadOnlyList<string> newEntryIds, Random? random = null)
    {
        random ??= Random.Shared;
        var currentId = CurrentEntryId;
        var oldIds = EntryIds;
        EntryIds = newEntryIds.ToList();
        if (newEntryIds.Count == 0)
        {
            order = [];
            cursor = null;
            return;
        }
        var newIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < newEntryIds.Count; index++) newIndexById.TryAdd(newEntryIds[index], index);
        var seen = new HashSet<int>();
        var survivors = new List<int>();
        foreach (var oldIndex in order)
        {
            if (oldIndex < 0 || oldIndex >= oldIds.Count) continue;
            if (!newIndexById.TryGetValue(oldIds[oldIndex], out var newIndex) || !seen.Add(newIndex)) continue;
            survivors.Add(newIndex);
        }
        var added = Enumerable.Range(0, newEntryIds.Count).Where(i => !seen.Contains(i)).ToList();
        if (IsShuffled)
        {
            Shuffle(added, random);
            order = [.. survivors, .. added];
        }
        else
        {
            order = Enumerable.Range(0, newEntryIds.Count).ToList();
        }
        if (currentId is not null && newIndexById.TryGetValue(currentId, out var currentIndex))
            cursor = order.IndexOf(currentIndex);
        else if (cursor is not null)
            cursor = null;
    }

    private static int IndexOf(IReadOnlyList<string> ids, string id)
    {
        for (var index = 0; index < ids.Count; index++)
            if (ids[index] == id) return index;
        return -1;
    }

    /// <summary>Fisher–Yates, like Swift's shuffle(using:).</summary>
    private static void Shuffle(List<int> items, Random random)
    {
        for (var index = items.Count - 1; index > 0; index--)
        {
            var other = random.Next(0, index + 1);
            (items[index], items[other]) = (items[other], items[index]);
        }
    }
}
