using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Editing;

/// <summary>
/// Where a drop on the slide grid lands. A port of the Mac's SlideGridDrop (SlideGridDrop.swift):
/// the gap between two tiles inserts between them; past a row's last tile lands after it; the gap
/// below a row belongs to its nearer half; below every row lands after the last slide.
/// </summary>
public static class SlideGridDrop
{
    /// <summary>Before or after a tile (by its index in the grid).</summary>
    public readonly record struct Spot(int Index, bool After)
    {
        public static Spot Before(int index) => new(index, false);

        public static Spot AfterTile(int index) => new(index, true);

        /// <summary>The index a drop here inserts at.</summary>
        public int InsertionIndex => After ? Index + 1 : Index;
    }

    public static Spot SpotAt(EditorGeometry.Point point, IReadOnlyDictionary<int, SceneRect> tileFrames, int count, double rowGap)
    {
        var atOrBelow = tileFrames.Where(t => point.Y <= t.Value.Bottom + rowGap / 2).ToList();
        if (atOrBelow.Count > 0)
        {
            var rowTop = atOrBelow.Min(t => t.Value.Y);
            var row = atOrBelow.Where(t => Math.Abs(t.Value.Y - rowTop) < 1).ToList();
            var before = row.Where(t => point.X < t.Value.X + t.Value.Width / 2).Select(t => t.Key).DefaultIfEmpty(-1).Min();
            return before >= 0 ? Spot.Before(before) : Spot.AfterTile(row.Count > 0 ? row.Max(t => t.Key) : count - 1);
        }
        return count > 0 ? Spot.AfterTile(count - 1) : Spot.Before(0);
    }
}

/// <summary>Where on a tile or a row a drop lands (the Mac's SlideDropZone).</summary>
public enum SlideDropZone
{
    Before,
    On,
    After,
}

public static class SlideDropZones
{
    /// <summary>The width of the strip at a tile's edges that inserts beside it rather than dropping on it.</summary>
    public const double Gutter = 26;

    /// <summary>
    /// A drop on a slide tile: a slide being reordered goes before or after it by half; anything else
    /// dropped in the edge strips inserts beside it, and in the middle lands on it (a background).
    /// </summary>
    public static SlideDropZone Tile(EditorGeometry.Point point, double width, bool reorder, bool insertOnly)
    {
        if (reorder) return width > 0 && point.X > width / 2 ? SlideDropZone.After : SlideDropZone.Before;
        if (point.X < Gutter) return SlideDropZone.Before;
        if (width > Gutter * 3 && point.X > width - Gutter) return SlideDropZone.After;
        return insertOnly ? SlideDropZone.Before : SlideDropZone.On;
    }

    /// <summary>A drop on a list row (the service list): its top and bottom bands insert around it.</summary>
    public static SlideDropZone Row(EditorGeometry.Point point, SceneSize size, bool insertOnly)
    {
        var band = Math.Min(10, size.Height / 4);
        if (point.X < Gutter || point.Y < band) return SlideDropZone.Before;
        if (size.Height > 0 && point.Y > size.Height - band) return SlideDropZone.After;
        return insertOnly ? SlideDropZone.Before : SlideDropZone.On;
    }
}

/// <summary>Something dragged out of one of the main window's bins: a song or presentation, media, an overlay, music or a playlist.</summary>
public sealed record LibraryDrag(Type Kind, string Id)
{
    /// <summary>The drag-and-drop format it travels as (inside this app only).</summary>
    public const string Format = "mxu/library-item";

    private const string Prefix = "mxuitem::";

    public string Encode() => $"{Prefix}{Kind.Name}::{Id}";

    public static LibraryDrag? Decode(string? payload)
    {
        if (payload is null || !payload.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var parts = payload[Prefix.Length..].Split("::", 2);
        if (parts.Length != 2 || parts[1].Length == 0) return null;
        Type? kind = parts[0] switch
        {
            nameof(Presentation) => typeof(Presentation),
            nameof(MediaItem) => typeof(MediaItem),
            nameof(Overlay) => typeof(Overlay),
            nameof(AudioItem) => typeof(AudioItem),
            nameof(Playlist) => typeof(Playlist),
            _ => null,
        };
        return kind is null ? null : new LibraryDrag(kind, parts[1]);
    }
}

/// <summary>Slides dragged from the grid: which presentation, and which slides (in its order).</summary>
public sealed record SlideDrag(string PresentationId, IReadOnlyList<string> SlideIds)
{
    public const string Format = "mxu/slides";

    // The Mac's drag payload for one slide is "mxuslide::<presentation>::<slide>" (SlideBulkEdit.slideDragPrefix).
    private const string Prefix = "mxuslide::";

    public string Encode() => $"{Prefix}{PresentationId}::{string.Join(",", SlideIds)}";

    public static SlideDrag? Decode(string? payload)
    {
        if (payload is null || !payload.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var parts = payload[Prefix.Length..].Split("::", 2);
        if (parts.Length != 2 || parts[0].Length == 0) return null;
        var ids = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
        return ids.Length == 0 ? null : new SlideDrag(parts[0], ids);
    }
}

/// <summary>What a drop does to the documents. Kept apart from the mouse, so it can be tested.</summary>
public static class DropRules
{
    /// <summary>
    /// Moves the slides to just before <paramref name="beforeSlideId"/> (null: the end), keeping
    /// their order. Like the Mac's SlideBulkEdit.moveSlide, each joins the group of the slide it lands
    /// before (or the last slide's, at the end). Returns whether anything moved.
    /// </summary>
    public static bool MoveSlides(Presentation presentation, IReadOnlyCollection<string> slideIds, string? beforeSlideId)
    {
        if (beforeSlideId is not null && slideIds.Contains(beforeSlideId))
        {
            // Dropped on one of the slides being moved: land before the first slide after them that isn't.
            var at = presentation.Slides.FindIndex(s => s.Id == beforeSlideId);
            beforeSlideId = presentation.Slides.Skip(at).FirstOrDefault(s => !slideIds.Contains(s.Id))?.Id;
        }
        var moving = presentation.Slides.Where(s => slideIds.Contains(s.Id)).ToList();
        if (moving.Count == 0) return false;
        var rest = presentation.Slides.Where(s => !slideIds.Contains(s.Id)).ToList();
        var index = beforeSlideId is null ? rest.Count : rest.FindIndex(s => s.Id == beforeSlideId);
        if (index < 0) index = rest.Count;
        var neighbour = index < rest.Count ? rest[index] : rest.LastOrDefault();
        var order = rest.ToList();
        order.InsertRange(index, moving);
        // Dropped back where they were: nothing moves, and no slide changes group by accident.
        if (order.Select(s => s.Id).SequenceEqual(presentation.Slides.Select(s => s.Id))) return false;
        foreach (var slide in moving) slide.SectionId = neighbour is null ? slide.SectionId : neighbour.SectionId;
        presentation.Slides = order;
        return true;
    }

    /// <summary>The service row a library item makes (as the Mac's addServiceItem), or null for one a service can't hold (an overlay).</summary>
    public static ServiceItem? ServiceItemFor(LibraryDrag drag, string name) =>
        ServiceRunOrder.ItemKindAdding(drag.Kind) is { } kind
            ? new ServiceItem { Id = LibraryStore.NewId(), ItemKind = kind, Name = name, RefId = drag.Id }
            : null;

    /// <summary>Inserts a row at <paramref name="index"/> (clamped). Returns it.</summary>
    public static ServiceItem InsertRow(Service service, ServiceItem item, int index)
    {
        service.Items.Insert(Math.Clamp(index, 0, service.Items.Count), item);
        return item;
    }

    /// <summary>
    /// Media dropped on the canvas: onto a shape, it becomes that shape's fill (a picture in a frame);
    /// anywhere else, a new picture or video box centred where it was dropped. Returns the object
    /// changed or made, and whether it's new.
    /// </summary>
    public static (SlideObject Object, bool Made) DropMediaOnCanvas(Slide slide, Slide? template, MediaItem item, EditorGeometry.Point at, SceneSize canvas)
    {
        if (EditorGeometry.HitObject(at, slide.Objects, template) is { ObjectKind: SlideObjectKind.Shape } shape)
        {
            shape.Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = item.Id, MediaScaleMode = MediaScaleMode.Fill, Loops = item.Loops };
            return (shape, false);
        }
        var made = EditorClipboard.MediaObject(item, canvas, at);
        slide.Objects.Add(made);
        return (made, true);
    }

    /// <summary>Media dropped on a slide tile: the slide's background (MediaCueRouting.DroppedOnSlide, as the Mac does).</summary>
    public static void DropMediaAsBackground(Slide slide, MediaItem item) => slide.Background = MediaCueRouting.DroppedOnSlide(item);
}
