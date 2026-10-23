using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Scene;

/// <summary>Which layer a media item lands on when fired. Port of MediaCueRouting.swift.</summary>
public static class MediaCueRouting
{
    public static CueMedia Dropped(MediaItem item)
    {
        var layer = FiredLayer(item.MediaKind, item.Classification);
        return new CueMedia
        {
            MediaId = item.Id,
            Layer = layer,
            Classification = item.Classification,
            Mode = layer == CueMediaLayer.LoopingVideos ? CueMediaMode.UntilReplaced : null,
            Loops = item.MediaKind == MediaKind.Video ? item.Loops : null,
        };
    }

    public static CueMediaLayer FiredLayer(MediaKind kind, MediaClassification classification) =>
        classification == MediaClassification.Background
            ? CueMediaLayer.LoopingVideos
            : kind == MediaKind.Image ? CueMediaLayer.StillGraphics : CueMediaLayer.Videos;

    public static MediaClassification ResolvedClassification(this CueMedia cue) =>
        cue.Classification ?? (cue.Layer == CueMediaLayer.Videos ? MediaClassification.Foreground : MediaClassification.Background);

    public static bool IsLiveInput(this CueMedia cue) => cue.MediaId.StartsWith("input::", StringComparison.Ordinal);

    public static CueMedia DroppedOnSlide(MediaItem item) => new()
    {
        MediaId = item.Id,
        Mode = CueMediaMode.UntilReplaced,
        Layer = CueMediaLayer.LoopingVideos,
        Loops = item.MediaKind == MediaKind.Video ? true : null,
        Classification = MediaClassification.Background,
    };

    public static CueMedia DroppedAsNewSlide(MediaItem item) => new()
    {
        MediaId = item.Id,
        Layer = item.MediaKind == MediaKind.Image ? CueMediaLayer.StillGraphics : CueMediaLayer.Videos,
        Loops = item.MediaKind == MediaKind.Video ? false : null,
        Classification = MediaClassification.Foreground,
    };
}
