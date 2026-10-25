using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// The Windows-only transitions of slides, presentations and media items (Phase 6), kept beside the
/// library in <see cref="WindowsExtras"/> because a shared document can only hold the Mac's seven kinds.
/// <list type="number">
/// <item>Choosing a Windows-only kind (say Push Left, 0.8 s) writes its nearest Mac kind into the
/// document (Dissolve, 0.8 s: <see cref="WindowsTransitions.ToShared"/>), so a Mac reading it plays
/// something close, and keeps the Push beside the library with a fingerprint of that Dissolve.</item>
/// <item>When the outputs ask, the Push is used only while the document still holds that same
/// Dissolve. If a Mac has since changed the slide's transition (or removed it), the fingerprints
/// differ and the Mac's choice wins.</item>
/// <item>Choosing one of the Mac's kinds (or the default) removes the extra: the document says it all.</item>
/// </list>
/// Reading happens on every push to the outputs, so answers are remembered until an extra changes.
/// Windows only: the Mac has no twin.
/// </summary>
public sealed class TransitionExtras
{
    public const string SlideKind = "slide transitions";
    public const string PresentationKind = "presentation transitions";
    public const string MediaKind = "media transitions";

    private readonly Lock gate = new();
    private readonly Dictionary<(string Kind, string Owner, string Basis), WindowsTransition?> remembered = [];

    public TransitionExtras(WindowsExtras store)
    {
        Store = store;
        store.Changed += (kind, owner) =>
        {
            lock (gate)
                foreach (var key in remembered.Keys.Where(k => k.Kind == kind && k.Owner == owner).ToList())
                    remembered.Remove(key);
        };
    }

    /// <summary>Where the extras are kept.</summary>
    public WindowsExtras Store { get; }

    /// <summary>The Windows-only transition a slide comes in with, or null: none, or a Mac changed the slide's own transition since.</summary>
    public WindowsTransition? ForSlide(Slide slide) => Get(SlideKind, slide.Id, slide.Transition);

    /// <summary>The Windows-only transition of a presentation (its slides' default), or null.</summary>
    public WindowsTransition? ForPresentation(Presentation presentation) => Get(PresentationKind, presentation.Id, presentation.Transition);

    /// <summary>The Windows-only transition of a media item, or null.</summary>
    public WindowsTransition? ForMedia(MediaItem item) => Get(MediaKind, item.Id, item.Transition);

    /// <summary>
    /// What a picker shows as a slide's choice: its Windows-only transition when it has a valid one,
    /// else its shared one; null when it has none of its own (it follows its presentation or the room).
    /// </summary>
    public WindowsTransition? ChosenFor(Slide slide) => Chosen(slide.Transition, ForSlide(slide));

    public WindowsTransition? ChosenFor(Presentation presentation) => Chosen(presentation.Transition, ForPresentation(presentation));

    public WindowsTransition? ChosenFor(MediaItem item) => Chosen(item.Transition, ForMedia(item));

    /// <summary>Gives a slide <paramref name="choice"/> (null = no transition of its own). The caller saves the presentation.</summary>
    public void Choose(Slide slide, WindowsTransition? choice) => slide.Transition = Choose(SlideKind, slide.Id, choice);

    /// <summary>Gives a presentation <paramref name="choice"/> (null = the room default). The caller saves it.</summary>
    public void Choose(Presentation presentation, WindowsTransition? choice) => presentation.Transition = Choose(PresentationKind, presentation.Id, choice);

    /// <summary>Gives a media item <paramref name="choice"/> (null = the default). The caller saves it.</summary>
    public void Choose(MediaItem item, WindowsTransition? choice) => item.Transition = Choose(MediaKind, item.Id, choice);

    // MARK: Copies

    /// <summary>A duplicated slide keeps its Windows-only transition (the copy has a new id, so the extra is copied too).</summary>
    public void Copied(Slide original, Slide copy)
    {
        if (ForSlide(original) is { } windows && copy.Transition is { } shared) Store.Save(SlideKind, copy.Id, windows, WindowsExtras.Fingerprint(shared));
    }

    /// <summary>A duplicated presentation keeps its Windows-only transition, and its slides theirs (by position, where their ids changed).</summary>
    public void Copied(Presentation original, Presentation copy)
    {
        if (ForPresentation(original) is { } windows && copy.Transition is { } shared)
            Store.Save(PresentationKind, copy.Id, windows, WindowsExtras.Fingerprint(shared));
        foreach (var (from, to) in original.Slides.Zip(copy.Slides).Where(pair => pair.First.Id != pair.Second.Id))
            Copied(from, to);
    }

    /// <summary>
    /// When a song's slides are rebuilt (its lyrics edited), the new slides get new ids; those that
    /// took over an old slide's transition (SongEditing carries it by section) take over its Windows
    /// kind too, matched in order among the slides with the same shared transition.
    /// </summary>
    public void CarryOver(Presentation before, Presentation after)
    {
        var waiting = before.Slides.Select(slide => (Slide: slide, Windows: ForSlide(slide))).Where(p => p.Windows is not null).ToList();
        foreach (var slide in after.Slides)
        {
            if (slide.Transition is not { } shared || before.Slides.Any(s => s.Id == slide.Id)) continue;
            var basis = WindowsExtras.Fingerprint(shared);
            var match = waiting.FindIndex(p => WindowsExtras.Fingerprint(p.Slide.Transition) == basis);
            if (match < 0) continue;
            Store.Save(SlideKind, slide.Id, waiting[match].Windows!, basis);
            waiting.RemoveAt(match);
        }
    }

    private static WindowsTransition? Chosen(Transition? shared, WindowsTransition? windows) =>
        windows ?? (shared is null ? null : WindowsTransitions.FromShared(shared));

    /// <summary>
    /// Records a choice and returns what the shared document should now hold: null for none, the
    /// transition itself for a Mac kind, or the nearest Mac kind for a Windows-only one (whose extra
    /// is saved with that value's fingerprint).
    /// </summary>
    private Transition? Choose(string kind, string owner, WindowsTransition? choice)
    {
        if (choice is null || choice.IsShared)
        {
            Store.Delete(kind, owner);
            return choice?.ToShared();
        }
        var shared = choice.ToShared();
        Store.Save(kind, owner, choice, WindowsExtras.Fingerprint(shared));
        return shared;
    }

    private WindowsTransition? Get(string kind, string owner, Transition? shared)
    {
        // No shared transition means none of its own (a Mac set it back to the default): an extra can't stand alone.
        if (shared is null || string.IsNullOrEmpty(owner)) return null;
        var basis = WindowsExtras.Fingerprint(shared);
        lock (gate)
            if (remembered.TryGetValue((kind, owner, basis), out var known)) return known;
        // A kind this build doesn't know (a newer Windows build wrote it) reads as unreadable: none.
        var found = Store.Get<WindowsTransition>(kind, owner, basis) is { IsShared: false } extra ? extra : null;
        lock (gate) remembered[(kind, owner, basis)] = found;
        return found;
    }
}
