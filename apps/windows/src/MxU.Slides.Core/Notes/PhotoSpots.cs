using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Notes;

/// <summary>
/// Photo spots: shapes in a theme design marked "a picture goes here". A picture slide made from
/// notes fills them in reading order. Port of PhotoSpots.swift; drawing them is the engine's
/// (SlideSceneBuilder, ported in the engine catch-up).
/// </summary>
public static class PhotoSpots
{
    /// <summary>A design's photo spots, left to right and top to bottom.</summary>
    public static List<SlideObject> Of(Slide slide)
    {
        var spots = slide.Objects.Where(IsPhotoSpot).ToList();
        // A stable sort, as Swift's sorted(by:) is: spots that read the same keep their order.
        return spots.Select((o, i) => (o, i)).OrderBy(p => p, Comparer<(SlideObject o, int i)>.Create((a, b) =>
            ReadsBefore(a.o, b.o) ? -1 : ReadsBefore(b.o, a.o) ? 1 : a.i.CompareTo(b.i))).Select(p => p.o).ToList();
    }

    public static bool IsPhotoSpot(SlideObject o) => o.PhotoSpot == true && o.ObjectKind == SlideObjectKind.Shape;

    /// <summary>Scale and Blur means "fill" inside a spot: the spot is the picture's frame already.</summary>
    public static MediaScaleMode InPhotoSpot(this MediaScaleMode mode) => mode == MediaScaleMode.ScaleAndBlur ? MediaScaleMode.Fill : mode;

    /// <summary>Same row (their heights overlap): the one further left first. Otherwise the higher one.</summary>
    internal static bool ReadsBefore(SlideObject a, SlideObject b)
    {
        double ay = a.Y ?? 0, ah = a.Height ?? 0, by = b.Y ?? 0, bh = b.Height ?? 0;
        var sameRow = ay < by + bh && by < ay + ah;
        return sameRow ? (a.X ?? 0) < (b.X ?? 0) : ay < by;
    }

    /// <summary>A picture for a spot: a shape filled with the media, marked as a photo spot.</summary>
    public static SlideObject Photo(string mediaId, MediaScaleMode? scale) => new()
    {
        Id = SwiftString.Uuid(), ObjectKind = SlideObjectKind.Shape, Name = "Photo", Text = "",
        Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = mediaId, MediaScaleMode = scale == MediaScaleMode.Fill ? null : scale },
        PhotoSpot = true,
    };

    /// <summary>The design's spot with a slide's picture in it: the spot's look, the picture's fill (and its place, when it has its own).</summary>
    public static SlideObject Filled(SlideObject spot, SlideObject photo)
    {
        var merged = Json.MxuJson.Clone(spot);
        merged.Id = photo.Id;
        merged.Name = photo.Name;
        merged.Fill = photo.Fill is null ? null : Json.MxuJson.Clone(photo.Fill);
        merged.PhotoSpot = true;
        if (photo is { X: not null, Y: not null, Width: not null, Height: not null })
            (merged.X, merged.Y, merged.Width, merged.Height) = (photo.X, photo.Y, photo.Width, photo.Height);
        if (photo.AnimationSteps is { Count: > 0 } steps) merged.AnimationSteps = Json.MxuJson.Clone(steps);
        return merged;
    }
}

/// <summary>A picture much taller or wider than its spot, which Scale and Blur shows whole on a blurred copy of itself.</summary>
public enum PictureShape { Tall, Wide }

public static class PictureShapes
{
    /// <summary>How far apart the shapes must be (in aspect ratio) before filling the spot would crop too much.</summary>
    public const double Limit = 1.6;

    public static PictureShape? Of(double? picture, double spot)
    {
        if (picture is not { } aspect || aspect <= 0 || spot <= 0) return null;
        if (spot / aspect > Limit) return PictureShape.Tall;
        if (aspect / spot > Limit) return PictureShape.Wide;
        return null;
    }

    public static string Raw(this PictureShape shape) => shape == PictureShape.Tall ? "tall" : "wide";

    public static string Caption(this PictureShape shape) => $"Scale and Blur ({shape.Raw()} picture)";
}

/// <summary>Picture and video slides: showing media full screen, and the placeholder a slide is until its media arrives.</summary>
public static class SlideMediaFacts
{
    /// <summary>How a slide's notes start when it waits for media someone has to add.</summary>
    public const string NeedsMediaNote = "Needs media:";

    /// <summary>The media as a whole-slide cue (as one dropped onto the grid as a new slide), with the video's ending.</summary>
    public static void ShowMedia(this Slide slide, string mediaId, MediaKind kind, MediaScaleMode? scale, SlideOptions.VideoEnds videoEnd)
    {
        var cue = new CueMedia
        {
            MediaId = mediaId,
            Layer = kind == MediaKind.Image ? CueMediaLayer.StillGraphics : CueMediaLayer.Videos,
            Loops = kind == MediaKind.Video ? false : null,
            Classification = MediaClassification.Foreground,
        };
        if (kind == MediaKind.Image) cue.ScaleMode = scale == MediaScaleMode.Fill ? null : scale;
        slide.AutoAdvance = null;
        if (kind == MediaKind.Video)
        {
            switch (videoEnd)
            {
                case SlideOptions.VideoEnds.Next: slide.AutoAdvance = new AutoAdvance { DelaySeconds = 0, AfterPlayback = true }; break;
                case SlideOptions.VideoEnds.Loop: cue.Loops = true; break;
                case SlideOptions.VideoEnds.FadeToBlack: cue.EndFade = MediaEndFade.Black; break;
                case SlideOptions.VideoEnds.FadeToClear: cue.EndFade = MediaEndFade.Clear; break;
            }
        }
        slide.Background = cue;
    }

    /// <summary>A slide that is only a link or "Needs media:" note, waiting for its picture or video.</summary>
    public static bool WaitsForMedia(this Slide slide) =>
        slide.Objects.Count == 0 && slide.Background is null && slide.Notes is { } notes
        && (notes.StartsWith("http://", StringComparison.Ordinal) || notes.StartsWith("https://", StringComparison.Ordinal)
            || notes.StartsWith(NeedsMediaNote, StringComparison.Ordinal));

    /// <summary>Gives a waiting slide its media, dropping the "Needs media" note.</summary>
    public static void FillPlaceholder(this Slide slide, MediaItem item, SlideOptions options)
    {
        slide.ShowMedia(item.Id, item.MediaKind, options.PictureScale, options.VideoEnd);
        if (slide.Notes?.StartsWith(NeedsMediaNote, StringComparison.Ordinal) == true) slide.Notes = null;
    }
}
