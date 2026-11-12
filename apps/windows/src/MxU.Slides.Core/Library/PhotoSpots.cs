using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// Photo spots: shapes in a theme design that a slide's pictures fill ("Photo", "2 Photos",
/// "Photo + Text (Lower Third)"…). A slide made from the design carries one photo object per
/// spot; the scene builder lays each over its spot, in reading order (<see cref="PhotoSpotsOf"/>).
/// Port of the Mac's PresenterCore/Library/PhotoSpots.swift.
/// </summary>
public static class PhotoSpots
{
    /// <summary>True for a shape marked as a photo spot (the shared <c>photoSpot</c> field).</summary>
    public static bool IsPhotoSpot(this SlideObject o) => o.PhotoSpot == true && o.ObjectKind == SlideObjectKind.Shape;

    /// <summary>The slide's photo spots in reading order: left to right along a row, then down.</summary>
    public static List<SlideObject> PhotoSpotsOf(this Slide slide)
    {
        var spots = slide.Objects.Where(IsPhotoSpot).ToList();
        // A stable insertion sort, as Swift's sort(by:) with this rule keeps ties in order.
        var sorted = new List<SlideObject>();
        foreach (var spot in spots)
        {
            var at = sorted.FindIndex(placed => ReadsBefore(spot, placed));
            if (at < 0) sorted.Add(spot);
            else sorted.Insert(at, spot);
        }
        return sorted;
    }

    /// <summary>Two spots overlapping vertically share a row and read left to right; otherwise the higher reads first.</summary>
    internal static bool ReadsBefore(SlideObject a, SlideObject b)
    {
        var (ay, ah, by, bh) = (a.Y ?? 0, a.Height ?? 0, b.Y ?? 0, b.Height ?? 0);
        var sameRow = ay < by + bh && by < ay + ah;
        return sameRow ? (a.X ?? 0) < (b.X ?? 0) : ay < by;
    }

    /// <summary>
    /// The scale a picture takes inside a spot. Scale and Blur means "fill the screen with a
    /// blurred copy behind it", which a small spot has no room for, so a spot just fills.
    /// </summary>
    public static MediaScaleMode InPhotoSpot(this MediaScaleMode mode) => mode == MediaScaleMode.ScaleAndBlur ? MediaScaleMode.Fill : mode;

    /// <summary>A photo for a spot: the picture as a media fill (Fill is the default, so it isn't written).</summary>
    public static SlideObject Photo(string mediaId, MediaScaleMode? scale) => new()
    {
        Id = Guid.NewGuid().ToString().ToUpperInvariant(), ObjectKind = SlideObjectKind.Shape, Name = "Photo", Text = "",
        Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = mediaId, MediaScaleMode = scale == MediaScaleMode.Fill ? null : scale },
        PhotoSpot = true,
    };

    /// <summary>
    /// The design's spot filled with a slide's photo: the spot's look (corners, stroke, shadow)
    /// with the photo's id, name and picture, and the photo's own frame and steps when it has them.
    /// </summary>
    public static SlideObject FilledWith(this SlideObject spot, SlideObject photo)
    {
        var merged = Json.MxuJson.Clone(spot);
        merged.Id = photo.Id;
        merged.Name = photo.Name;
        merged.Fill = photo.Fill is null ? null : Json.MxuJson.Clone(photo.Fill);
        merged.PhotoSpot = true;
        if (photo.X is not null && photo.Y is not null && photo.Width is not null && photo.Height is not null)
            (merged.X, merged.Y, merged.Width, merged.Height) = (photo.X, photo.Y, photo.Width, photo.Height);
        if (photo.AnimationSteps is { Count: > 0 } steps) merged.AnimationSteps = Json.MxuJson.Clone(steps);
        return merged;
    }
}

/// <summary>
/// A picture whose shape is far from its spot's: much taller or much wider. Make Slides gives such
/// a picture Scale and Blur, so it isn't cropped to a sliver. Port of the Mac's PictureShape.
/// </summary>
public enum PictureShape { Tall, Wide }

public static class PictureShapes
{
    /// <summary>How far apart the two aspect ratios (width ÷ height) must be.</summary>
    internal const double Limit = 1.6;

    public static PictureShape? Of(double? picture, double spot)
    {
        if (picture is not { } p || p <= 0 || spot <= 0) return null;
        if (spot / p > Limit) return PictureShape.Tall;
        if (p / spot > Limit) return PictureShape.Wide;
        return null;
    }

    public static string Raw(this PictureShape shape) => shape == PictureShape.Tall ? "tall" : "wide";

    public static string Caption(this PictureShape shape) => $"Scale and Blur ({shape.Raw()} picture)";
}
