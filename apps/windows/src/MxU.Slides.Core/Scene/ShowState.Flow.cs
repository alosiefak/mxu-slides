using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Media;

namespace MxU.Slides.Core.Scene;

// What show flow (auto-advance and the video transport strip) asks of the live show.
// Ports of ShowState.slideStepsCompletedAt (SlideScene/ShowState.swift) and
// ShowState.transportSeeds (SlideScene/MediaTransportTarget.swift).
public sealed partial class ShowState
{
    /// <summary>Media layers whose cue was fired directly (a media item, a service row) rather than brought by a slide.</summary>
    public IReadOnlySet<LayerKind> DirectMediaFires => directMediaFires;

    /// <summary>
    /// When the live slide's builds will have finished (host time), or null while a press is still
    /// owed. An auto-advance never presses for the operator: it waits until every click, and the
    /// exit press if there is one, has been made and has played.
    /// </summary>
    public double? SlideStepsCompletedAt()
    {
        if (Live is not { } live) return null;
        var clicks = SlideClickCount;
        if (slideAdvanceClicks.Count < clicks) return null;
        var compositions = Compositions(live);
        if (SlideHasExitGroup)
        {
            if (slideExitAt is not { } exitAt) return null;
            return exitAt + compositions.Select(c => GroupDuration(SceneAnimationGroup.Exit, c)).DefaultIfEmpty(0).Max();
        }
        if (clicks <= 0) return live.FiredAtHostTime;
        var lastClick = slideAdvanceClicks[clicks - 1];
        return lastClick + compositions.Select(c => GroupDuration(SceneAnimationGroup.Click(clicks - 1), c)).DefaultIfEmpty(0).Max();
    }

    private static double GroupDuration(SceneAnimationGroup group, PlaceholderAnimations.Composition composition) =>
        AnimationTimeline.GroupDuration(group, AnimationSequence.SceneSteps(composition.Objects, composition.Order).Values.SelectMany(s => s));

    /// <summary>
    /// Every library video that could be live now, with its layer and where it came from, for the
    /// transport strip to read: the media layers, then the slide's (and its design's) media, then
    /// the overlays'. Windows' alerts carry no media, so they add none.
    /// </summary>
    public List<TransportSeed> TransportSeeds()
    {
        var seeds = new List<TransportSeed>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in new[] { LayerKind.VideoInput, LayerKind.LoopingVideos, LayerKind.StillGraphics, LayerKind.Videos })
        {
            if (!liveMedia.TryGetValue(layer, out var media) || media.MediaId.Length == 0 || !seen.Add(media.MediaId)) continue;
            var direct = directMediaFires.Contains(layer);
            var origin = layer == LayerKind.Videos
                ? direct ? TransportOrigin.ForegroundServiceItem : TransportOrigin.ForegroundCue
                : direct ? TransportOrigin.BackgroundServiceItem : TransportOrigin.BackgroundCue;
            seeds.Add(new TransportSeed(media.MediaId, layer, origin));
        }
        if (Live is { } live)
        {
            var objects = live.Slide.Objects.ToList();
            if (SlideSceneBuilder.ThemeSlide(live.Slide, live.Theme) is { } template) objects.AddRange(template.Objects);
            foreach (var o in objects)
                if (SlideSceneBuilder.FillMediaId(o) is { } id && seen.Add(id))
                    seeds.Add(new TransportSeed(id, LayerKind.Slide, TransportOrigin.Slides));
        }
        foreach (var overlay in liveOverlays)
        {
            var layer = LayerKinds.Parse(overlay.Layer) ?? LayerKind.Overlays;
            foreach (var o in overlay.Objects)
                if (SlideSceneBuilder.FillMediaId(o) is { } id && seen.Add(id))
                    seeds.Add(new TransportSeed(id, layer, TransportOrigin.Overlays));
        }
        return seeds;
    }
}
