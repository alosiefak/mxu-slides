namespace MxU.Slides.Core.Undo;

/// <summary>
/// One thing an undo step changed: a library document, or one of the Windows-only additions kept
/// beside the library (<see cref="Library.WindowsExtras"/>). Two targets are the same when they name
/// the same document or extra, so a step that saves one document ten times holds it once.
/// </summary>
public abstract record UndoTarget
{
    private UndoTarget()
    {
    }

    /// <summary>A short, stable text for this target, used in coalescing keys.</summary>
    public abstract string Key { get; }

    /// <summary>A library document, by its type (Presentation, Service…) and id.</summary>
    public sealed record Document(Type Type, string Id) : UndoTarget
    {
        public override string Key => $"{Type.Name}/{Id}";
    }

    /// <summary>A Windows extra (a transition the Mac hasn't got, a camera cut…), by its kind and owner id.</summary>
    public sealed record Extra(string Kind, string OwnerId) : UndoTarget
    {
        public override string Key => $"extra:{Kind}/{OwnerId}";
    }
}
