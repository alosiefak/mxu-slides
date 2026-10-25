using MxU.Slides.Core.Library;

namespace MxU.Slides.Production.Actions;

/// <summary>
/// Where each owner's production steps are kept: the library's <see cref="WindowsExtras"/>, kind
/// "production", one small file per owner (a slide, media item, combo, schedule trigger or
/// timeline cue id). A Mac opening the same library never looks there, so it never sees a kind
/// of step it doesn't know (which would make it refuse the whole document).
/// <para>
/// A slide fires many times in a service, so the lists are read from disk once and kept in
/// memory; saving here (or anything else saving through the same extras store) refreshes the
/// copy. Firing a slide then costs a dictionary look-up, not a file read. Thread-safe.
/// </para>
/// </summary>
public sealed class ProductionSteps
{
    public const string ExtrasKind = "production";

    private readonly WindowsExtras extras;
    private readonly Lock gate = new();
    private Dictionary<string, IReadOnlyList<ProductionAction>>? cache;

    public ProductionSteps(WindowsExtras extras)
    {
        this.extras = extras;
        extras.Changed += (kind, owner) =>
        {
            if (kind != ExtrasKind) return;
            lock (gate) cache?.Remove(owner);
            Changed?.Invoke(owner);
        };
    }

    /// <summary>An owner's steps were saved or removed (its id). Raised on the thread that saved.</summary>
    public event Action<string>? Changed;

    /// <summary>Reads every owner's list now (the app does it in the background at start).</summary>
    public void Preload()
    {
        var loaded = new Dictionary<string, IReadOnlyList<ProductionAction>>();
        foreach (var owner in extras.Owners(ExtrasKind))
            loaded[owner] = Read(owner);
        lock (gate) cache ??= loaded;
    }

    /// <summary>The steps kept for an owner, in order (none: an empty list).</summary>
    public IReadOnlyList<ProductionAction> For(string ownerId)
    {
        lock (gate)
        {
            if (cache is not null && cache.TryGetValue(ownerId, out var known)) return known;
        }
        var read = Read(ownerId);
        lock (gate)
        {
            cache ??= [];
            cache[ownerId] = read;
        }
        return read;
    }

    /// <summary>Saves an owner's steps (an empty list removes the file).</summary>
    public void Save(string ownerId, IReadOnlyList<ProductionAction> steps)
    {
        if (steps.Count == 0) extras.Delete(ExtrasKind, ownerId);
        else extras.Save(ExtrasKind, ownerId, steps.Select(s => s.Copy()).ToList());
        lock (gate)
        {
            cache ??= [];
            cache[ownerId] = [.. steps.Select(s => s.Copy())];
        }
    }

    /// <summary>Every owner that has steps (for the Local API's list).</summary>
    public IReadOnlyList<string> Owners() => extras.Owners(ExtrasKind);

    private IReadOnlyList<ProductionAction> Read(string ownerId) =>
        extras.Get<List<ProductionAction>>(ExtrasKind, ownerId) is { } list ? list : [];
}
