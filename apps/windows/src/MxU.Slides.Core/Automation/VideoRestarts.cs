using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// Which videos go back to the start when something fires. A video player is shared and kept
/// for a moment after its video leaves the screen, so firing the same video again (or a slide
/// that brings it back) can find the player paused at its end. As on the Mac, those videos are
/// restarted: from the start of their pass (the in point), playing. Port of the restart rules
/// in Sources/ServiceControls.swift (land, fire(mediaItem:), fire(overlay:), resolvedLoops).
/// </summary>
public static class VideoRestarts
{
    /// <summary>Whether a video loops: its own setting where it's placed (a fill, a cue), else the media item's, else yes.</summary>
    public static bool ResolvedLoops(bool? own, MediaItem? item) => own ?? item?.Loops ?? true;

    /// <summary>Firing media restarts it, unless it's background media (a loop that carries on under the slides).</summary>
    public static bool OnMediaFired(MediaItem item) => item.Classification != MediaClassification.Background;

    /// <summary>
    /// The videos to restart when a slide lands: its own non-looping videos (shapes filled with a
    /// video), and its foreground video background if that one has played out (a background
    /// still playing from the slide before carries on).
    /// </summary>
    public static List<string> OnSlideLanded(Slide slide, Presentation? presentation, string? arrangementId,
        Func<string, MediaItem?> media, Func<string, MediaTransportState?> transport, DateTimeOffset now)
    {
        var restart = new List<string>();
        foreach (var o in slide.Objects.Select(SlideObjectNormalization.Normalized))
            if (SlideSceneBuilder.FillMediaId(o) is { } id && !ResolvedLoops(o.Fill?.Loops, media(id)) && !restart.Contains(id))
                restart.Add(id);
        if (SlideSceneBuilder.EffectiveBackground(slide, presentation, arrangementId) is { } background
            && SlideSceneBuilder.LayerKindOf(background.Layer) == LayerKind.Videos
            && !ResolvedLoops(background.Loops, media(background.MediaId))
            && transport(background.MediaId) is { } state && state.IsFinished(now)
            && !restart.Contains(background.MediaId))
            restart.Add(background.MediaId);
        return restart;
    }

    /// <summary>The videos to restart when an overlay goes up: its non-looping video fills.</summary>
    public static List<string> OnOverlayFired(Overlay overlay, Func<string, MediaItem?> media) =>
        [.. overlay.Objects.Select(SlideObjectNormalization.Normalized)
            .Where(o => SlideSceneBuilder.FillMediaId(o) is { } id && !ResolvedLoops(o.Fill?.Loops, media(id)))
            .Select(o => SlideSceneBuilder.FillMediaId(o)!).Distinct()];
}
