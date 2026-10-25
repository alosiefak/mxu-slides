using MxU.Slides.Core.Library;

namespace MxU.Slides.Core.Editing;

/// <summary>
/// Locked objects: a background plate or a logo you don't want to nudge by accident. A locked
/// object can't be picked or dragged on the canvas (clicks go through it to what's behind); it
/// can still be picked in the layers list, to change it or unlock it.
/// <para>
/// The shared format has no "locked" field and the Mac has no lock, so the lock is a Windows extra
/// (WindowsExtras.cs, kind "object locks", one per object id). A Mac never sees it and loses
/// nothing: the object draws the same. Locks are read once and kept in memory, as the canvas asks
/// on every mouse move. Windows only.
/// </para>
/// </summary>
public sealed class ObjectLocks
{
    public const string Kind = "object locks";

    private readonly WindowsExtras extras;
    private readonly Lock gate = new();
    private HashSet<string>? locked;

    public ObjectLocks(WindowsExtras extras)
    {
        this.extras = extras;
        // A lock saved, deleted or undone anywhere (another window, an undo) is read again.
        extras.Changed += (kind, owner) =>
        {
            if (kind != Kind) return;
            lock (gate)
            {
                if (locked is null) return;
                if (extras.Get<Mark>(Kind, owner) is { Locked: true }) locked.Add(owner);
                else locked.Remove(owner);
            }
        };
    }

    public bool IsLocked(string objectId)
    {
        lock (gate)
        {
            locked ??= [.. extras.Owners(Kind)];
            return locked.Contains(objectId);
        }
    }

    /// <summary>Locks or unlocks an object (saved at once, so it undoes like any edit).</summary>
    public void Set(string objectId, bool on)
    {
        if (IsLocked(objectId) == on) return;
        if (on) extras.Save(Kind, objectId, new Mark(true));
        else extras.Delete(Kind, objectId);
    }

    internal sealed record Mark(bool Locked);
}
