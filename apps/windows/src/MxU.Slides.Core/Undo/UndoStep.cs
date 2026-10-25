namespace MxU.Slides.Core.Undo;

/// <summary>
/// One entry on an undo stack: everything one action changed ("Move", "Delete Slide"), as the
/// JSON of each document or extra before and after it. Null means "didn't exist": a step that
/// made a document has no "before", and one that deleted it has no "after".
/// </summary>
public sealed class UndoStep
{
    private readonly List<UndoChange> changes = [];

    internal UndoStep(string? name, string? key)
    {
        Name = name ?? "";
        NameGiven = name is not null;
        Key = key;
    }

    /// <summary>What the menu calls it: "Move" makes "Undo Move".</summary>
    public string Name { get; internal set; }

    /// <summary>The documents and extras it changed, in the order it first changed them.</summary>
    public IReadOnlyList<UndoTarget> Targets => [.. changes.Select(c => c.Target)];

    internal IReadOnlyList<UndoChange> Changes => changes;

    /// <summary>The step was named by its maker (a group), rather than worked out from what changed.</summary>
    internal bool NameGiven { get; }

    /// <summary>
    /// Steps with the same key coalesce when they come close together: each keystroke, each drag
    /// tick and each slider move joins the step before it. Null never coalesces (adding a slide twice
    /// is two steps), as on the Mac's undo journal.
    /// </summary>
    internal string? Key { get; set; }

    /// <summary>When it was last added to, for the coalescing window.</summary>
    internal DateTimeOffset Touched { get; set; }

    /// <summary>Closed to coalescing: something was undone or redone since it was made.</summary>
    internal bool Sealed { get; set; }

    /// <summary>
    /// Records one save. The first "before" of a target is kept and its "after" follows each later
    /// save, so ten saves of one document while dragging are one change from where it started.
    /// </summary>
    internal void Note(UndoTarget target, byte[]? before, byte[]? after)
    {
        if (changes.Find(c => c.Target == target) is { } known) known.After = after;
        else if (!UndoBytes.Same(before, after)) changes.Add(new UndoChange(target, before, after));
    }

    /// <summary>Takes in a later step that coalesced with this one.</summary>
    internal void Absorb(UndoStep later)
    {
        foreach (var change in later.changes) Note(change.Target, change.Before, change.After);
    }

    /// <summary>Forgets targets that ended where they began (typed a letter, then deleted it).</summary>
    internal void DropUnchanged() => changes.RemoveAll(c => UndoBytes.Same(c.Before, c.After));

    internal bool IsEmpty => changes.Count == 0;
}

/// <summary>One target's JSON before and after a step.</summary>
internal sealed class UndoChange(UndoTarget target, byte[]? before, byte[]? after)
{
    public UndoTarget Target { get; } = target;
    public byte[]? Before { get; } = before;
    public byte[]? After { get; set; } = after;
}

internal static class UndoBytes
{
    public static bool Same(byte[]? a, byte[]? b) =>
        a is null || b is null ? a is null && b is null : a.AsSpan().SequenceEqual(b);
}
