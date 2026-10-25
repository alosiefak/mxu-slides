namespace MxU.Slides.Core.Undo;

/// <summary>
/// One window's undo and redo stacks. The Mac gives each window its own undo manager: the main
/// window's journal, and each editor's own history. Here the main window has a scope and each
/// editor window makes its own (<see cref="UndoHistory.NewScope"/>), so Ctrl+Z in an editor never
/// undoes something done in the main window, and the other way round.
/// <para>
/// A scope records whatever is saved through the library while it is current: while its window
/// has the keyboard (<see cref="UndoHistory.Focus"/>), or inside <see cref="Use"/>. Saves made in
/// one piece of UI work (one click, one key) are one step; <see cref="BeginGroup"/> makes a step
/// span longer, such as a whole drag.
/// </para>
/// Its members can be called from any thread. <see cref="Changed"/> is raised on the thread that
/// changed the stacks: usually the UI thread, as that's where editing happens.
/// </summary>
public sealed class UndoScope : IDisposable
{
    private readonly List<UndoStep> undo = [];
    private readonly List<UndoStep> redo = [];
    private UndoStep? open;
    private int groupDepth;
    private string? groupName;
    private string? groupKey;
    private bool disposed;

    internal UndoScope(UndoHistory history, string name)
    {
        History = history;
        Name = name;
    }

    public UndoHistory History { get; }

    /// <summary>Who it belongs to ("Main window", "Theme editor"), for logs and tests.</summary>
    public string Name { get; }

    private Lock Gate => History.Gate;

    /// <summary>The undo stack or the redo stack changed (or a name did): menus should look again.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised just before an undo or a redo, so the scope's owner can save anything still waiting
    /// (an autosave due in half a second, a text box not yet committed). That edit then becomes a
    /// step of its own first, and is the one undone.
    /// </summary>
    public event Action? Settling;

    public bool CanUndo
    {
        get { lock (Gate) return undo.Count > 0 || open is { IsEmpty: false }; }
    }

    public bool CanRedo
    {
        get { lock (Gate) return redo.Count > 0; }
    }

    /// <summary>What Undo would undo ("Move"), or null when there's nothing.</summary>
    public string? UndoName
    {
        get { lock (Gate) return undo.Count > 0 ? undo[^1].Name : null; }
    }

    /// <summary>What Redo would redo, or null.</summary>
    public string? RedoName
    {
        get { lock (Gate) return redo.Count > 0 ? redo[^1].Name : null; }
    }

    /// <summary>"Undo Move", or plain "Undo" when there's nothing to undo.</summary>
    public string UndoMenuText => UndoName is { Length: > 0 } name ? $"Undo {name}" : "Undo";

    /// <summary>"Redo Move", or plain "Redo".</summary>
    public string RedoMenuText => RedoName is { Length: > 0 } name ? $"Redo {name}" : "Redo";

    /// <summary>The undo stack's names, newest first.</summary>
    public IReadOnlyList<string> UndoNames
    {
        get { lock (Gate) return [.. undo.AsEnumerable().Reverse().Select(s => s.Name)]; }
    }

    /// <summary>The redo stack's names, newest first.</summary>
    public IReadOnlyList<string> RedoNames
    {
        get { lock (Gate) return [.. redo.AsEnumerable().Reverse().Select(s => s.Name)]; }
    }

    /// <summary>
    /// Makes this the scope saves go to, on this thread (and anything it awaits), until disposed.
    /// It beats the focused window's scope. An editor that saves from a timer, after its window
    /// has lost the keyboard, can save inside this so the step still lands in its own scope.
    /// </summary>
    public IDisposable Use() => History.Enter(this);

    /// <summary>
    /// Starts a step that lasts until <see cref="EndGroup"/> (or disposing what this returns): every
    /// save into this scope in between is one step, called <paramref name="name"/>. Use it for an
    /// action that spans several pieces of UI work, like a drag from mouse-down to mouse-up.
    /// Groups nest; the outermost one names the step.
    /// <para>
    /// With a <paramref name="coalesceKey"/>, the step joins the one before it when that has the same
    /// key and was touched within <see cref="UndoHistory.CoalescingWindow"/>: give every nudge of an
    /// object the key "nudge:objectId", and a burst of arrow keys undoes as one "Move".
    /// </para>
    /// </summary>
    public IDisposable BeginGroup(string name, string? coalesceKey = null)
    {
        lock (Gate)
        {
            if (groupDepth == 0)
            {
                // Saves made earlier in this piece of work are a step of their own.
                if (open is not null) CloseLocked(open);
                groupName = name;
                groupKey = coalesceKey;
            }
            groupDepth++;
        }
        return new GroupEnd(this);
    }

    /// <summary>Ends the innermost <see cref="BeginGroup"/>. The outermost one's end closes the step.</summary>
    public void EndGroup()
    {
        var closed = false;
        lock (Gate)
        {
            if (groupDepth == 0) return;
            groupDepth--;
            if (groupDepth == 0)
            {
                if (open is not null) closed = CloseLocked(open);
                groupName = groupKey = null;
            }
        }
        if (closed) RaiseChanged();
    }

    /// <summary>A group is open (an action is still going on, such as a drag).</summary>
    public bool InGroup
    {
        get { lock (Gate) return groupDepth > 0; }
    }

    /// <summary>
    /// Puts back what the newest step changed. A step whose documents have all been changed
    /// since by someone else (another window, a remote) or are gone is dropped, and the one under
    /// it is tried, as the Mac's journal does. Returns false when nothing was undone, or while a
    /// group is open.
    /// </summary>
    public bool Undo() => Step(undoing: true);

    /// <summary>Does again what was last undone.</summary>
    public bool Redo() => Step(undoing: false);

    /// <summary>Forgets every step (both stacks).</summary>
    public void Clear()
    {
        lock (Gate)
        {
            undo.Clear();
            redo.Clear();
            open = null;
        }
        RaiseChanged();
    }

    /// <summary>The window closed: its steps are forgotten, as the Mac drops a closed window's undo manager.</summary>
    public void Dispose()
    {
        lock (Gate)
        {
            if (disposed) return;
            disposed = true;
            undo.Clear();
            redo.Clear();
            open = null;
        }
        History.Forget(this);
    }

    internal bool IsDisposed
    {
        get { lock (Gate) return disposed; }
    }

    // MARK: Recording

    /// <summary>A save landed in this scope.</summary>
    internal void Note(UndoTarget target, byte[]? before, byte[]? after)
    {
        UndoStep? turn = null;
        var closed = false;
        lock (Gate)
        {
            if (disposed) return;
            // Saving a document that hasn't changed isn't an edit.
            if (open is null && UndoBytes.Same(before, after)) return;
            if (open is null)
            {
                open = new UndoStep(groupDepth > 0 ? groupName : null, groupDepth > 0 ? groupKey : null);
                if (groupDepth == 0) turn = open;
            }
            open.Note(target, before, after);
            // With nothing to tell us when the UI's work is done (tests, a background thread),
            // each save outside a group is a step at once.
            if (groupDepth == 0 && History.EndOfTurn is null) closed = CloseLocked(open);
        }
        if (turn is not null && History.EndOfTurn is { } later) later(() => CloseTurn(turn));
        if (closed) RaiseChanged();
    }

    private void CloseTurn(UndoStep step)
    {
        bool closed;
        lock (Gate)
        {
            if (open != step || groupDepth > 0) return;
            closed = CloseLocked(step);
        }
        if (closed) RaiseChanged();
    }

    /// <summary>
    /// Finishes the open step: names it, then pushes it, or folds it into the step before when they
    /// coalesce. Returns whether the stacks changed.
    /// </summary>
    private bool CloseLocked(UndoStep step)
    {
        if (open == step) open = null;
        step.DropUnchanged();
        if (step.IsEmpty) return false;
        var described = UndoDescription.Of(step);
        if (!step.NameGiven) step.Name = described.Name;
        step.Key ??= described.Key;
        var now = History.Clock.GetUtcNow();
        if (undo.Count > 0 && undo[^1] is { Sealed: false, Key: { } key } top && key == step.Key
            && now - top.Touched < History.CoalescingWindow)
        {
            top.Absorb(step);
            top.Touched = now;
            top.DropUnchanged();
            if (top.IsEmpty) undo.RemoveAt(undo.Count - 1);
        }
        else
        {
            step.Touched = now;
            undo.Add(step);
            if (undo.Count > History.Capacity) undo.RemoveRange(0, undo.Count - History.Capacity);
        }
        redo.Clear();
        return true;
    }

    // MARK: Undoing

    private bool Step(bool undoing)
    {
        History.Settle();
        Settling?.Invoke();
        var changed = false;
        try
        {
            while (true)
            {
                UndoStep step;
                lock (Gate)
                {
                    if (disposed || groupDepth > 0) return false;
                    // A piece of work still collecting saves becomes a step first.
                    if (open is not null) changed |= CloseLocked(open);
                    var from = undoing ? undo : redo;
                    if (from.Count == 0) return false;
                    step = from[^1];
                    from.RemoveAt(from.Count - 1);
                    changed = true;
                }
                bool applied;
                try
                {
                    applied = History.Apply(this, step, toBefore: undoing);
                }
                catch
                {
                    // Applying is safe to retry (it only moves what still needs moving): keep the step.
                    lock (Gate) (undoing ? undo : redo).Add(step);
                    throw;
                }
                if (!applied) continue;
                lock (Gate)
                {
                    (undoing ? redo : undo).Add(step);
                    // A new edit starts a fresh step rather than joining one from before the undo.
                    foreach (var s in undo) s.Sealed = true;
                }
                return true;
            }
        }
        finally
        {
            if (changed) RaiseChanged();
        }
    }

    private void RaiseChanged() => Changed?.Invoke();

    private sealed class GroupEnd(UndoScope scope) : IDisposable
    {
        private int done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 0) scope.EndGroup();
        }
    }
}
