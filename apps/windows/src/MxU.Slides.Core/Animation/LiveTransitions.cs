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
}

/// <summary>What is live, as far as choosing transitions goes: the slide (and the transitions it carries) and each media layer's cue.</summary>
public sealed record LiveCues(string? SlideId, Transition? SlideTransition, Transition? PresentationTransition, IReadOnlyDictionary<LayerKind, string> Media)
{
    public static readonly LiveCues Nothing = new(null, null, null, new Dictionary<LayerKind, string>());

    public static LiveCues Of(ShowState show) => new(
        show.Live?.Slide.Id,
        show.Live?.Slide.Transition,
        show.Live?.Presentation?.Transition,
        show.LiveMedia.ToDictionary(pair => pair.Key, pair => pair.Value.MediaId));
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

    /// <summary>What a layer does when nothing it shows names a transition. Port of RenderContext.roomDefault(for:).</summary>
    public static SceneTransition? RoomDefault(LayerKind layer, RoomTransitions room) => layer switch
    {
        LayerKind.Slide => From(room.Slide),
        LayerKind.LoopingVideos or LayerKind.StillGraphics or LayerKind.Videos
            when room.Media is { TransitionKind: not TransitionKind.Cut } media => From(media),
        _ => null,
    };

    /// <summary>
    /// The transition for each layer when the outputs go from showing <paramref name="before"/> to
    /// <paramref name="after"/>. <paramref name="mediaTransition"/> looks up a media item's own
    /// transition by id. <paramref name="clearAll"/> is Clear All (F1), which leaves every layer to
    /// the room default as on the Mac.
    /// </summary>
    public static Func<LayerKind, SceneTransition?> For(
        LiveCues before, LiveCues after, Func<string, Transition?> mediaTransition, RoomTransitions room, bool clearAll = false)
    {
        var chosen = new Dictionary<LayerKind, SceneTransition>();
        var slideFired = after.SlideId is not null && after.SlideId != before.SlideId;
        if (!clearAll)
        {
            if (slideFired && From(TransitionEdit.Entrance(after.SlideTransition, after.PresentationTransition)) is { } entrance)
                chosen[LayerKind.Slide] = entrance;
            foreach (var layer in before.Media.Keys.Union(after.Media.Keys))
            {
                if (layer == LayerKind.Slide) continue;
                var old = before.Media.GetValueOrDefault(layer);
                var now = after.Media.GetValueOrDefault(layer);
                if (old == now) continue;
                Transition? stored = null;
                if (now is not null)
                {
                    // Arriving: its own transition; one a slide brings falls back to the presentation's.
                    var own = mediaTransition(now);
                    stored = slideFired ? TransitionEdit.Entrance(own, after.PresentationTransition) : own;
                }
                else if (!slideFired && old is not null)
                {
                    // Cleared off: the leaving media's own transition (a slide sweeping it uses the room default).
                    stored = mediaTransition(old);
                }
                if (From(stored) is { } transition) chosen[layer] = transition;
            }
        }
        return layer => chosen.TryGetValue(layer, out var transition) ? transition : RoomDefault(layer, room);
    }

    /// <summary>Every layer cuts.</summary>
    public static readonly Func<LayerKind, SceneTransition?> AllCut = _ => SceneTransition.Cut;
}
