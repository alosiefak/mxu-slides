using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Infrastructure;
using SceneTransition = MxU.Slides.Windows.Rendering.SceneTransition;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The Windows-only transitions (Phase 6), as the screens and pickers use them:
/// <list type="bullet">
/// <item>A slide's, presentation's or media item's transition: the shared document keeps the
/// Mac-readable part and <see cref="TransitionExtras"/> the Windows kind (Core/Library/TransitionExtras.cs).</item>
/// <item>The two room defaults: Settings › Transitions and the live header's chips. They are this
/// PC's settings, so they hold any kind directly (Infrastructure/AppSettings.Transitions.cs).</item>
/// <item>What the outputs play: the same rules as the Mac's (<see cref="LiveTransitions"/>), with the
/// Windows kinds filled in.</item>
/// </list>
/// </summary>
public sealed partial class MainViewModel
{
    private TransitionExtras? transitionExtras;

    /// <summary>The Windows-only transitions kept beside this library.</summary>
    public TransitionExtras TransitionExtras => transitionExtras ??= new TransitionExtras(Library.Extras);

    // MARK: What the outputs play

    /// <summary>What's live now, with the Windows kinds that go with it.</summary>
    private LiveCues LiveCuesNow() => LiveCues.Of(Show, TransitionExtras);

    /// <summary>The transition each layer plays going from <paramref name="before"/> to <paramref name="after"/>.</summary>
    private SceneTransition CueTransitions(LiveCues before, LiveCues after, bool clearAll) =>
        SceneTransition.PerLayer(LiveTransitions.For(before, after, id => Library.Get<MediaItem>(id)?.Transition, RoomTransitionDefaults(), clearAll,
            mediaWindows: id => Library.Get<MediaItem>(id) is { } item ? TransitionExtras.ForMedia(item) : null));

    /// <summary>The room defaults as the outputs play them (see <see cref="OutputTransitions.PlayedSeconds"/>); a cut is no transition.</summary>
    private RoomTransitions RoomTransitionDefaults()
    {
        WindowsTransition? Played(bool media) => RoomTransitionChoice(media) is { Kind: not WindowsTransitionKind.Cut } chosen
            ? chosen with { DurationSeconds = OutputTransitions.PlayedSeconds(chosen.Kind, chosen.DurationSeconds) }
            : null;
        return RoomTransitions.Of(Played(false), Played(true));
    }

    // MARK: The room defaults

    /// <summary>A room default as Settings and the chips show it: the kind, its length as set, and its direction, softness and colour.</summary>
    public WindowsTransition RoomTransitionChoice(bool media)
    {
        var (kind, seconds) = RoomTransitionSetting(media);
        var options = media ? Settings.MediaTransitionOptions : Settings.SlideTransitionOptions;
        return new WindowsTransition
        {
            Kind = OutputTransitions.Kind(kind), DurationSeconds = seconds,
            Direction = options.Direction, Softness = options.Softness, ColorHex = options.ColorHex,
        };
    }

    /// <summary>Changes a room default (Settings and the chips both do): saved at once; the next cue that doesn't name its own uses it.</summary>
    public void SetRoomTransition(bool media, WindowsTransition choice)
    {
        var options = new TransitionOptions(choice.Direction, Math.Clamp(choice.Softness, 0, 1), choice.ColorHex);
        var optionsChanged = (media ? Settings.MediaTransitionOptions : Settings.SlideTransitionOptions) != options;
        if (media) Settings.MediaTransitionOptions = options;
        else Settings.SlideTransitionOptions = options;
        // The kind and length go through the older setter (MainViewModel.Present.cs), which rounds
        // the length as the Mac stores it, saves, and tells the chips.
        var before = RoomTransitionSetting(media);
        SetRoomTransition(media, OutputTransitions.Setting(choice.Kind), choice.DurationSeconds);
        if (optionsChanged && RoomTransitionSetting(media) == before)
        {
            // Only the direction, softness or colour changed, which that setter doesn't look at.
            Settings.Save();
            TransitionsChanged?.Invoke();
        }
    }

    // MARK: Slides, presentations and media items

    /// <summary>A slide's own transition as a picker shows it; null when it has none (it follows its presentation or the room).</summary>
    public WindowsTransition? SlideTransitionChoice(Slide slide) => TransitionExtras.ChosenFor(slide);

    public WindowsTransition? PresentationTransitionChoice(Presentation presentation) => TransitionExtras.ChosenFor(presentation);

    public WindowsTransition? MediaTransitionChoice(MediaItem item) => TransitionExtras.ChosenFor(item);

    /// <summary>
    /// Gives a slide its own transition (null = none: it follows its presentation), and saves the
    /// presentation. The open presentation is edited in place, as the slide editor does. False when
    /// the slide can't be found or saved.
    /// </summary>
    public bool SaveSlideTransition(string presentationId, string slideId, WindowsTransition? choice)
    {
        var presentation = CurrentPresentation?.Id == presentationId ? CurrentPresentation : Library.Get<Presentation>(presentationId);
        if (presentation?.Slides.FirstOrDefault(s => s.Id == slideId) is not { } slide) return false;
        return TrySave("the slide's transition", () =>
        {
            TransitionExtras.Choose(slide, choice);
            SavePresentation(presentation);
        });
    }

    /// <summary>Gives a presentation a transition for its slides that don't have their own (null = the room default), and saves it.</summary>
    public bool SavePresentationTransition(string presentationId, WindowsTransition? choice)
    {
        var presentation = CurrentPresentation?.Id == presentationId ? CurrentPresentation : Library.Get<Presentation>(presentationId);
        if (presentation is null) return false;
        return TrySave("the presentation's transition", () =>
        {
            TransitionExtras.Choose(presentation, choice);
            SavePresentation(presentation);
        });
    }

    /// <summary>Gives a media item its own transition (null = the default), and saves it.</summary>
    public bool SaveMediaTransition(string mediaId, WindowsTransition? choice)
    {
        if (Library.Get<MediaItem>(mediaId) is not { } item) return false;
        return TrySave("the media item's transition", () =>
        {
            TransitionExtras.Choose(item, choice);
            Library.Save(item);
        });
    }

    private bool TrySave(string what, Action save)
    {
        try
        {
            save();
            TransitionsChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't save {what}", ex);
            Problem = $"Couldn't save {what}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// What a slide comes in with when it has no transition of its own, in words: "Presentation
    /// Default (Push ← 0.8 s)" or "Room Default (Dissolve 0.4 s)".
    /// </summary>
    public string SlideDefaultLabel(Presentation? presentation) =>
        presentation is not null && PresentationTransitionChoice(presentation) is { } deck
            ? $"Presentation default ({WindowsTransitions.Describe(deck)})"
            : RoomDefaultLabel(media: false);

    public string RoomDefaultLabel(bool media) => $"Room default ({WindowsTransitions.Describe(RoomTransitionChoice(media))})";
}
