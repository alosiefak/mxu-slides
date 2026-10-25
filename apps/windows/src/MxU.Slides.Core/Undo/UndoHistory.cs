using MxU.Slides.Core.Library;

namespace MxU.Slides.Core.Undo;

/// <summary>
/// Undo and redo for one library, recorded where every edit ends up: <see cref="LibraryStore.Save{T}"/>,
/// <see cref="LibraryStore.Delete{T}"/> and the Windows extras beside them. Each save made while an
/// <see cref="UndoScope"/> is current is noted with the document's JSON before and after, so any
/// editor, including ones written later, gets undo just by saving as it always has.
/// <para>
/// <b>Which scope a save goes to.</b> The innermost <see cref="UndoScope.Use"/> on this thread (or the
/// code it awaits) if there is one; otherwise the scope of the window with the keyboard
/// (<see cref="Focus"/>), but only for saves on the thread that focused it, the UI thread. So a
/// background import, a remote's edit inside <see cref="Unrecorded"/>, or the app tidying up its
/// own files is never something Ctrl+Z takes back by surprise.
/// </para>
/// <para>
/// <b>Undoing never clobbers someone else's later edit.</b> The Mac undoes on Automerge, which
/// reverts only what the step touched (SubtreeRevert.swift). Here a step is undone with a
/// three-way merge (<see cref="JsonMerge"/>): it moves back only the parts of the JSON the step
/// changed, and keeps anything another window, a remote or a Mac changed since.
/// </para>
/// Windows: the Mac has an undo manager per window over Automerge (EditorReplica.swift) and a
/// journal of closures for the main window (MoveUndoJournal.swift). The rules are theirs: 50 steps,
/// a 5-second coalescing window, registrations while restoring are ignored, and a step that can no
/// longer be applied is dropped so the next one is tried.
/// </summary>
public sealed class UndoHistory
{
    /// <summary>Steps each scope keeps (MoveUndoJournal's capacity).</summary>
    public const int DefaultCapacity = 50;

    /// <summary>How close together same-key steps must be to coalesce (MoveUndoJournal's coalescingWindow).</summary>
    public static readonly TimeSpan DefaultCoalescingWindow = TimeSpan.FromSeconds(5);

    private readonly LibraryStore store;
    private readonly List<UndoScope> scopes = [];
    private readonly AsyncLocal<Frame?> ambient = new();
    private UndoScope? focused;
    private int focusThread;

    internal UndoHistory(LibraryStore store) => this.store = store;

    /// <summary>Guards every scope's stacks.</summary>
    internal Lock Gate { get; } = new();

    public int Capacity { get; set; } = DefaultCapacity;

    public TimeSpan CoalescingWindow { get; set; } = DefaultCoalescingWindow;

    /// <summary>The clock coalescing is timed by (a test's own clock in tests).</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>
    /// Runs the action it's given once the current piece of UI work has finished: the app posts it
    /// to the dispatcher. Until then, saves outside a group join one step, so a command that saves a
    /// presentation and three services undoes in one go. Null (tests) makes each save a step.
    /// </summary>
    public Action<Action>? EndOfTurn { get; set; }

    /// <summary>
    /// Something was undone or redone. Raised after the documents are written, on the thread that
    /// undid, while saves are not recorded: a handler can re-render what's live, as after any edit.
    /// </summary>
    public event Action<UndoRestore>? Restored;

    /// <summary>
    /// Raised before any scope undoes or redoes, before its own <see cref="UndoScope.Settling"/>:
    /// a window can save what it still has waiting, so an undo elsewhere doesn't land on top of it.
    /// </summary>
    public event Action? Settling;

    internal void Settle() => Settling?.Invoke();

    /// <summary>A new scope, for a window: the main window, or an editor that opens.</summary>
    public UndoScope NewScope(string name)
    {
        var scope = new UndoScope(this, name);
        lock (Gate) scopes.Add(scope);
        return scope;
    }

    /// <summary>The scopes still open.</summary>
    public IReadOnlyList<UndoScope> Scopes
    {
        get { lock (Gate) return [.. scopes]; }
    }

    /// <summary>
    /// The window with the keyboard is now <paramref name="scope"/>'s. Call it on the UI thread when
    /// the window activates; saves on that thread go to that scope.
    /// </summary>
    public void Focus(UndoScope? scope)
    {
        lock (Gate)
        {
            focused = scope;
            focusThread = Environment.CurrentManagedThreadId;
        }
    }

    /// <summary>The focused window's scope.</summary>
    public UndoScope? Focused
    {
        get { lock (Gate) return focused; }
    }

    /// <summary>The scope a save made here and now goes to, or null when it isn't recorded.</summary>
    public UndoScope? Current
    {
        get
        {
            if (ambient.Value is { } frame) return frame.Restoring ? null : frame.Scope;
            lock (Gate)
                return focused is not null && focusThread == Environment.CurrentManagedThreadId && !focused.IsDisposed ? focused : null;
        }
    }

    /// <summary>An undo or redo is writing documents now (on this thread), as the Mac's isRestoring.</summary>
    public bool IsRestoring => ambient.Value is { Restoring: true };

    /// <summary>
    /// Saves inside aren't undoable: the app's own housekeeping (a probed video length, an import
    /// tidying up) and edits from elsewhere (a remote, the Local API).
    /// </summary>
    public IDisposable Unrecorded() => Enter(new Frame(null, Restoring: false));

    internal IDisposable Enter(UndoScope scope) => Enter(new Frame(scope, Restoring: false));

    private Exit Enter(Frame frame)
    {
        var outer = ambient.Value;
        // Nothing records while restoring, even inside a Use(): the Mac's whileRestoring.
        ambient.Value = outer is { Restoring: true } ? frame with { Restoring = true } : frame;
        return new Exit(this, outer);
    }

    internal void Forget(UndoScope scope)
    {
        lock (Gate)
        {
            scopes.Remove(scope);
            if (focused == scope) focused = null;
        }
    }

    // MARK: Recording (called by LibraryStore and WindowsExtras)

    /// <summary>
    /// Called just before <paramref name="target"/> is written to <paramref name="path"/>: when a scope
    /// is recording, reads what's there now. Returns null (and costs nothing) when nothing records.
    /// </summary>
    internal UndoCapture? Capture(UndoTarget target, string path, Type? documentType)
    {
        if (Current is not { } scope) return null;
        byte[]? before;
        try
        {
            before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // Can't see what it was, so this save can't be undone; the save itself goes ahead.
        }
        // A document's file may have been written by the Mac, or by an older version: re-encoded the
        // way this app writes it, an unchanged save compares equal and undo compares like with like.
        if (before is not null && documentType is not null) before = LibraryStore.Reencoded(documentType, before);
        return new UndoCapture(scope, target, before);
    }

    /// <summary>The write went through: <paramref name="after"/> is what was written (null: deleted).</summary>
    internal static void Record(UndoCapture? capture, byte[]? after) => capture?.Scope.Note(capture.Target, capture.Before, after);

    // MARK: Undoing (called by UndoScope)

    /// <summary>
    /// Moves each of the step's targets to its state before the step (or after it, to redo), keeping
    /// any change made to it since by someone else. Returns false when nothing needed moving.
    /// </summary>
    internal bool Apply(UndoScope scope, UndoStep step, bool toBefore)
    {
        using var restoring = Enter(new Frame(null, Restoring: true));
        var moved = new List<UndoTarget>();
        IEnumerable<UndoChange> changes = toBefore ? step.Changes.Reverse() : step.Changes;
        foreach (var change in changes)
        {
            var wanted = toBefore ? change.Before : change.After;
            var basis = toBefore ? change.After : change.Before;
            var current = Read(change.Target);
            var result = JsonMerge.Apply(basis, current, wanted);
            if (JsonMerge.SameJson(result, current)) continue;
            Write(change.Target, result);
            moved.Add(change.Target);
        }
        if (moved.Count == 0) return false;
        Restored?.Invoke(new UndoRestore(scope, moved, toBefore, step.Name));
        return true;
    }

    private byte[]? Read(UndoTarget target) => target switch
    {
        UndoTarget.Document d => store.HeldJson(d.Type, d.Id),
        UndoTarget.Extra e => store.Extras.FileJson(e.Kind, e.OwnerId),
        _ => null,
    };

    private void Write(UndoTarget target, byte[]? json)
    {
        switch (target)
        {
            case UndoTarget.Document d:
                store.Restore(d.Type, d.Id, json);
                break;
            case UndoTarget.Extra e:
                store.Extras.Restore(e.Kind, e.OwnerId, json);
                break;
        }
    }

    /// <summary>Where a save goes (a scope, none, or "restoring").</summary>
    private sealed record Frame(UndoScope? Scope, bool Restoring);

    private sealed class Exit(UndoHistory history, Frame? outer) : IDisposable
    {
        private bool done;

        public void Dispose()
        {
            if (done) return;
            done = true;
            history.ambient.Value = outer;
        }
    }
}

/// <summary>A save on its way to <see cref="Scope"/>: what <see cref="Target"/> was before it.</summary>
internal sealed record UndoCapture(UndoScope Scope, UndoTarget Target, byte[]? Before);

/// <summary>What an undo or redo just changed.</summary>
/// <param name="Scope">The scope that undid.</param>
/// <param name="Targets">The documents and extras it wrote.</param>
/// <param name="Undone">True for an undo, false for a redo.</param>
/// <param name="Name">The step's name ("Move").</param>
public sealed record UndoRestore(UndoScope Scope, IReadOnlyList<UndoTarget> Targets, bool Undone, string Name)
{
    /// <summary>The ids of the documents of type <typeparamref name="T"/> it wrote.</summary>
    public IReadOnlyList<string> Ids<T>() =>
        [.. Targets.OfType<UndoTarget.Document>().Where(d => d.Type == typeof(T)).Select(d => d.Id)];
}
