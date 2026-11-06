using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// What the overlay, theme and stage layout editors do to the objects of the one composition each
/// edits (an overlay, a theme design, a stage layout): add the usual boxes, delete, copy, bring to
/// the front or send to the back, and align. Each changes the composition in place; the editor
/// redraws and saves.
/// </summary>
public static class CompositionObjects
{
    /// <summary>A text box, placed where a lower third's (or a design's) words usually go.</summary>
    public static SlideObject Text(IAnimatedComposition c, double y = 840, double fontSize = 60) => new()
    {
        Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Text,
        Name = c.Objects.Count(o => o.ObjectKind == SlideObjectKind.Text) is var count && count == 0 ? "Text" : $"Text {count + 1}", Text = "Text",
        X = 160, Y = y - Math.Min(c.Objects.Count(o => o.ObjectKind == SlideObjectKind.Text), 4) * 110, Width = 1000, Height = 110,
        TextStyle = new TextStyle
        {
            FontSize = fontSize, HorizontalAlignment = TextHorizontalAlignment.Left, VerticalAlignment = TextVerticalAlignment.Middle,
            ColorHex = "#FFFFFFFF", AutoShrink = true,
        },
    };

    /// <summary>A plate to put words on.</summary>
    public static SlideObject Box() => new()
    {
        Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Shape, Name = "Plate", Text = "",
        X = 120, Y = 800, Width = 1100, Height = 190, ShapeKind = ShapeKind.RoundedRectangle, CornerRadius = 12,
        Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#0F172AE6" },
    };

    public static SlideObject Circle() => new()
    {
        Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Shape, Name = "Circle", Text = "",
        X = 1640, Y = 80, Width = 200, Height = 200, ShapeKind = ShapeKind.Ellipse,
        Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#FFFFFFFF" },
    };

    /// <summary>A picture box showing a media item, in the top-right corner.</summary>
    public static SlideObject Media(MediaItem item, SceneSize canvas)
    {
        var w = Math.Min(640.0, canvas.Width / 3);
        var h = item is { PixelWidth: > 0 and var pw, PixelHeight: > 0 and var ph } ? w * ph / pw : w * 9 / 16;
        return new SlideObject
        {
            Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Shape, Name = item.Name, Text = "",
            X = canvas.Width - w - 80, Y = 80, Width = w, Height = h, ShapeKind = ShapeKind.Rectangle,
            Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = item.Id, MediaScaleMode = MediaScaleMode.Fit, Loops = item.Loops },
        };
    }

    /// <summary>Removes objects, and with them their steps from the play order and any mask naming them.</summary>
    public static void Delete(IAnimatedComposition c, IReadOnlyList<SlideObject> selected)
    {
        var stepIds = selected.SelectMany(o => o.AnimationSteps ?? []).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var o in selected) c.Objects.Remove(o);
        if (c.AnimationOrder is { } order)
        {
            var kept = order.Where(id => !stepIds.Contains(id)).ToList();
            c.AnimationOrder = kept.Count == 0 ? null : kept;
        }
        var gone = selected.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var o in c.Objects.Where(o => o.MaskObjectId is { } m && gone.Contains(m))) ObjectMasks.SetMask(o, null);
    }

    /// <summary>
    /// Copies objects 40 units down and right (named "… Copy"). The copies' steps get new ids and play
    /// just after the originals', so they land in the same group. Returns the copies.
    /// </summary>
    public static List<SlideObject> Duplicate(IAnimatedComposition c, IReadOnlyList<SlideObject> selected, Func<SlideObject, SceneRect> frameOf)
    {
        var copies = new List<SlideObject>();
        var order = c.AnimationOrder is { Count: > 0 } existing ? existing.ToList() : null;
        foreach (var o in selected)
        {
            var frame = frameOf(o);
            var copy = MxuJson.Clone(o);
            copy.Id = LibraryStore.NewId();
            copy.Name = string.IsNullOrWhiteSpace(o.Name) ? o.Name : $"{o.Name} Copy";
            copy.X = frame.X + 40;
            copy.Y = frame.Y + 40;
            copy.Width = frame.Width;
            copy.Height = frame.Height;
            foreach (var step in copy.AnimationSteps ?? [])
            {
                var originalId = step.Id;
                step.Id = Guid.NewGuid().ToString().ToUpperInvariant();
                if (order is not null && order.IndexOf(originalId) is >= 0 and var at) order.Insert(at + 1, step.Id);
            }
            copies.Add(copy);
        }
        c.Objects.AddRange(copies);
        if (order is not null) c.AnimationOrder = order;
        return copies;
    }

    /// <summary>Brings objects to the front (drawn last) or sends them to the back.</summary>
    public static void Reorder(IAnimatedComposition c, IReadOnlyList<SlideObject> selected, bool toFront)
    {
        foreach (var o in selected) c.Objects.Remove(o);
        if (toFront) c.Objects.AddRange(selected);
        else c.Objects.InsertRange(0, selected);
    }

    /// <summary>Aligns objects to each other (two or more) or to the canvas (one). False when nothing moved.</summary>
    public static bool Align(IReadOnlyList<SlideObject> selected, EditorGeometry.AlignEdge edge, SceneSize canvas, Func<SlideObject, SceneRect> frameOf)
    {
        var units = selected.Select(o => new EditorGeometry.AlignUnit([o.Id], frameOf(o))).ToList();
        if (units.Count == 0) return false;
        var reference = units.Count == 1
            ? new SceneRect(canvas)
            : units.Skip(1).Aggregate(units[0].Frame, (a, u) =>
            {
                var x = Math.Min(a.X, u.Frame.X);
                var y = Math.Min(a.Y, u.Frame.Y);
                return new SceneRect(x, y, Math.Max(a.Right, u.Frame.Right) - x, Math.Max(a.Bottom, u.Frame.Bottom) - y);
            });
        var deltas = EditorGeometry.AlignDeltas(units, edge, reference);
        if (deltas.Count == 0) return false;
        foreach (var o in selected.Where(o => deltas.ContainsKey(o.Id)))
        {
            var f = frameOf(o);
            var d = deltas[o.Id];
            o.X = f.X + d.Dx;
            o.Y = f.Y + d.Dy;
            o.Width = f.Width;
            o.Height = f.Height;
        }
        return true;
    }
}
