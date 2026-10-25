using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// The "room default" transitions: what a layer does when nothing it shows names a
/// transition of its own. On the Mac these are two app settings (slides, media); on
/// Windows the slide one is Settings › Transition.
/// </summary>
/// <param name="Slide">The slide layer's default; null or a cut means slides cut.</param>
/// <param name="Media">The background, still and video layers' default; null or a cut means they cut.</param>
public sealed record RoomTransitions(Transition? Slide, Transition? Media)
{
    /// <summary>The Mac's out-of-the-box defaults: slides cut, media dissolve over 0.7 s.</summary>
    public static readonly RoomTransitions MacDefault = new(null, new Transition { TransitionKind = TransitionKind.Dissolve, DurationSeconds = 0.7 });

    /// <summary>The slide layer's default when it's a kind only Windows has (a room default is this PC's own setting, so it can be).</summary>
    public WindowsTransition? SlideWindows { get; init; }

    /// <summary>The media layers' default when it's a kind only Windows has.</summary>
    public WindowsTransition? MediaWindows { get; init; }

    /// <summary>Room defaults as the Windows app holds them: any kind; null or a cut means that layer cuts.</summary>
    public static RoomTransitions Of(WindowsTransition? slide, WindowsTransition? media) => new(slide?.ToShared(), media?.ToShared())
    {
        SlideWindows = slide is { IsShared: false } ? slide : null,
        MediaWindows = media is { IsShared: false } ? media : null,
    };
}

/// <summary>
/// What is live, as far as choosing transitions goes: the slide (and the transitions it carries) and each
/// media layer's cue. <see cref="SlideWindows"/> and <see cref="PresentationWindows"/> are the Windows-only
/// transitions kept beside the library for them, when they still go with the shared ones (<see cref="TransitionExtras"/>).
/// </summary>
public sealed record LiveCues(string? SlideId, Transition? SlideTransition, Transition? PresentationTransition, IReadOnlyDictionary<LayerKind, string> Media)
{
    public static readonly LiveCues Nothing = new(null, null, null, new Dictionary<LayerKind, string>());

    public WindowsTransition? SlideWindows { get; init; }
    public WindowsTransition? PresentationWindows { get; init; }

    public static LiveCues Of(ShowState show, TransitionExtras? extras = null) => new(
        show.Live?.Slide.Id,
        show.Live?.Slide.Transition,
        show.Live?.Presentation?.Transition,
        show.LiveMedia.ToDictionary(pair => pair.Key, pair => pair.Value.MediaId))
    {
        SlideWindows = show.Live?.Slide is { } slide ? extras?.ForSlide(slide) : null,
        PresentationWindows = show.Live?.Presentation is { } presentation ? extras?.ForPresentation(presentation) : null,
    };
}

/// <summary>
/// How each output layer changes when the show changes, decided exactly as the Mac decides it
/// (RenderContext.sceneTransition / roomDefault and ServiceControls' nextLiveTransitions):
/// <list type="bullet">
/// <item>A slide that fires uses its own transition, else its presentation's, else the room default.</item>
/// <item>Media a slide brings (its background) uses the media item's own transition, else the presentation's.</item>
/// <item>Media fired on its own uses its own transition, and so does media cleared off (its exit).</item>
/// <item>Everything else uses the room default: slides cut and media dissolve unless set otherwise;
/// overlays, alerts and live inputs always cut (overlays play their own In and Out animations).</item>
/// </list>
/// Only layers whose items actually change transition at all: a clock ticking or a build playing
/// never fades (see <see cref="SceneTransitionEngine"/>).
/// </summary>
public static class LiveTransitions
{
    /// <summary>A stored transition as the renderer plays it; null when there is none. Port of RenderContext.sceneTransition(from:).</summary>
    public static SceneTransition? From(Transition? transition)
    {
        if (transition is null) return null;
        var duration = transition.DurationSeconds ?? TransitionEdit.DefaultDuration;
        // Shorter than a frame or two is a cut.
        if (duration < 0.05) return SceneTransition.Cut;
        return transition.TransitionKind switch
        {
            TransitionKind.Cut => SceneTransition.Cut,
            TransitionKind.Dissolve => new SceneTransition(SceneTransitionKind.Dissolve, duration),
            TransitionKind.FadeBlack => SceneTransition.Fade(SceneColor.Black, duration),
            TransitionKind.FadeWhite => SceneTransition.Fade(SceneColor.White, duration),
            TransitionKind.FadeColor => SceneTransition.Fade(ColorHex.Color(transition.ColorHex) ?? SceneColor.Black, duration),
            TransitionKind.BlurDissolve => new SceneTransition(SceneTransitionKind.BlurDissolve, duration),
            TransitionKind.FilmBurn => new SceneTransition(SceneTransitionKind.FilmBurn, duration),
            _ => SceneTransition.Cut,
        };
    }

    /// <summary>A Windows transition as the renderer plays it: one of the Mac's kinds exactly as the Mac draws it, else a picture transition.</summary>
    public static SceneTransition? FromWindows(WindowsTransition? transition)
    {
        if (transition is null) return null;
        if (transition.IsShared) return From(transition.ToShared());
        return transition.DurationSeconds < 0.05 ? SceneTransition.Cut : SceneTransition.Of(transition);
    }

    /// <summary>
    /// What a view that can't draw picture transitions (no pixel shaders at all) plays instead: the
    /// nearest of the Mac's kinds, at the same length. Anything else plays as it is.
    /// </summary>
    public static SceneTransition? Fallback(SceneTransition? transition) =>
        transition is { Kind: SceneTransitionKind.Picture, Picture: { } picture } ? From(picture.ToShared()) : transition;

    /// <summary>A cue's own transition: the Windows one kept beside the library when there is one, else the shared one.</summary>
    private static SceneTransition? Own(Transition? shared, WindowsTransition? windows) => windows is not null ? FromWindows(windows) : From(shared);

    /// <summary>What a layer does when nothing it shows names a transition. Port of RenderContext.roomDefault(for:).</summary>
    public static SceneTransition? RoomDefault(LayerKind layer, RoomTransitions room) => layer switch
    {
        LayerKind.Slide => Own(room.Slide, room.SlideWindows),
        LayerKind.LoopingVideos or LayerKind.StillGraphics or LayerKind.Videos
            when room.MediaWindows is not null || room.Media is { TransitionKind: not TransitionKind.Cut } => Own(room.Media, room.MediaWindows),
        _ => null,
    };

    /// <summary>
    /// The transition for each layer when the outputs go from showing <paramref name="before"/> to
    /// <paramref name="after"/>. <paramref name="mediaTransition"/> looks up a media item's own
    /// transition by id, and <paramref name="mediaWindows"/> the Windows-only one kept beside it.
    /// <paramref name="clearAll"/> is Clear All (F1), which leaves every layer to the room default as on the Mac.
    /// </summary>
    public static Func<LayerKind, SceneTransition?> For(
        LiveCues before, LiveCues after, Func<string, Transition?> mediaTransition, RoomTransitions room, bool clearAll = false,
        Func<string, WindowsTransition?>? mediaWindows = null)
    {
        var chosen = new Dictionary<LayerKind, SceneTransition>();
        var slideFired = after.SlideId is not null && after.SlideId != before.SlideId;
        // A cue's own transition wins (even a cut), else its presentation's: TransitionEdit.Entrance, with the Windows kinds.
        SceneTransition? Entrance(Transition? own, WindowsTransition? ownWindows) =>
            own is not null || ownWindows is not null ? Own(own, ownWindows) : Own(after.PresentationTransition, after.PresentationWindows);
        SceneTransition? MediaOwn(string id) => Own(mediaTransition(id), mediaWindows?.Invoke(id));
        if (!clearAll)
        {
            if (slideFired && Entrance(after.SlideTransition, after.SlideWindows) is { } entrance)
                chosen[LayerKind.Slide] = entrance;
            foreach (var layer in before.Media.Keys.Union(after.Media.Keys))
            {
                if (layer == LayerKind.Slide) continue;
                var old = before.Media.GetValueOrDefault(layer);
                var now = after.Media.GetValueOrDefault(layer);
                if (old == now) continue;
                SceneTransition? stored = null;
                if (now is not null)
                {
                    // Arriving: its own transition; one a slide brings falls back to the presentation's.
                    stored = slideFired ? Entrance(mediaTransition(now), mediaWindows?.Invoke(now)) : MediaOwn(now);
                }
                else if (!slideFired && old is not null)
                {
                    // Cleared off: the leaving media's own transition (a slide sweeping it uses the room default).
                    stored = MediaOwn(old);
                }
                if (stored is { } transition) chosen[layer] = transition;
            }
        }
        return layer => chosen.TryGetValue(layer, out var transition) ? transition : RoomDefault(layer, room);
    }

    /// <summary>Every layer cuts.</summary>
    public static readonly Func<LayerKind, SceneTransition?> AllCut = _ => SceneTransition.Cut;
}
