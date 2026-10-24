using MxU.Slides.Core.Scene;
using Point = MxU.Slides.Core.Scene.EditorGeometry.Point;

namespace MxU.Slides.Core.Outputs;

/// <summary>
/// The pen in the output mask editor, without any drawing code: where the
/// points are, which one is being dragged, and what a click, drag, key or
/// hover does. Points live in the screen's unit square (0–1 across and
/// down), the space <see cref="OutputMask.PathData"/> is stored in; the view
/// size is only needed to know how close a click must be to grab a point.
/// Port of MaskPenNSView in the Mac's MaskEditor.swift.
/// <para>
/// Drawing: click to place points, drag while placing to pull out a curve,
/// then click the first point, double-click, or press Return to close the
/// shape (it needs three points); Escape gives up. Editing: drag a point to
/// move it (its handles come with it) or drag a handle to bend the curve;
/// every release saves the shape.
/// </para>
/// </summary>
public sealed class OutputMaskPen
{
    /// <summary>How near (in view pixels) a click must be to grab a point or handle.</summary>
    public const double GrabRadius = 7;

    private enum DragKind { Anchor, HandleIn, HandleOut, PenHandles }

    private readonly List<PathAnchor> anchors = [];
    private (DragKind Kind, int Index)? drag;

    /// <summary>The shape so far (drawing) or the shape being edited.</summary>
    public IReadOnlyList<PathAnchor> Anchors => anchors;

    /// <summary>Whether the edited shape is closed (drawn shapes always close when finished).</summary>
    public bool Closed { get; private set; }

    /// <summary>True while placing points for a new shape; false while editing an existing one.</summary>
    public bool IsDrawing { get; private set; } = true;

    /// <summary>Where the pointer is while drawing, for the rubber-band line to the next point.</summary>
    public Point? Hover { get; private set; }

    /// <summary>True between a press that grabbed something and its release.</summary>
    public bool DragInProgress => drag is not null;

    /// <summary>A shape was finished (drawing) or changed (editing): its points and whether it is closed.</summary>
    public event Action<IReadOnlyList<PathAnchor>, bool>? Completed;

    /// <summary>Escape was pressed while drawing.</summary>
    public event Action? Cancelled;

    /// <summary>Starts a new, empty shape.</summary>
    public void Draw()
    {
        anchors.Clear();
        Closed = false;
        IsDrawing = true;
        Hover = null;
        drag = null;
    }

    /// <summary>Edits an existing shape's points.</summary>
    public void Edit(IEnumerable<PathAnchor> shape, bool closed)
    {
        anchors.Clear();
        anchors.AddRange(shape);
        Closed = closed;
        IsDrawing = false;
        Hover = null;
        drag = null;
    }

    /// <summary>Edits a stored mask path; false (and nothing loaded) when it can't be read as one shape.</summary>
    public bool Edit(string pathData)
    {
        if (PathAnchorCodec.Parse(pathData, new SceneRect(0, 0, 1, 1)) is not { } parsed) return false;
        Edit(parsed.Anchors, parsed.Closed);
        return true;
    }

    /// <summary>A view position turned into the unit square, clamped to its edges.</summary>
    public static Point Unit(double viewX, double viewY, SceneSize view) =>
        view.Width <= 0 || view.Height <= 0
            ? new Point(0, 0)
            : new Point(Math.Clamp(viewX / view.Width, 0, 1), Math.Clamp(viewY / view.Height, 0, 1));

    /// <summary>A unit-square point in view pixels.</summary>
    public static Point ToView(Point unit, SceneSize view) => new(unit.X * view.Width, unit.Y * view.Height);

    private static bool Hits(Point a, Point b, SceneSize view)
    {
        var p = ToView(a, view);
        var q = ToView(b, view);
        return Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y)) <= GrabRadius;
    }

    /// <summary>A mouse press at <paramref name="unit"/>; <paramref name="clickCount"/> is 2 for a double-click.</summary>
    public void Press(Point unit, SceneSize view, int clickCount = 1)
    {
        if (IsDrawing)
        {
            // Clicking the first point again (or double-clicking) closes the shape.
            if (anchors.Count >= 3 && Hits(anchors[0].Point, unit, view))
            {
                Finish(close: true);
                return;
            }
            if (clickCount == 2 && anchors.Count >= 3)
            {
                Finish(close: true);
                return;
            }
            anchors.Add(new PathAnchor(unit));
            drag = (DragKind.PenHandles, anchors.Count - 1);
            return;
        }

        // Handles first: they sit on top of the points they belong to.
        for (var i = 0; i < anchors.Count; i++)
        {
            if (anchors[i].HandleIn is { } hIn && Hits(hIn, unit, view))
            {
                drag = (DragKind.HandleIn, i);
                return;
            }
            if (anchors[i].HandleOut is { } hOut && Hits(hOut, unit, view))
            {
                drag = (DragKind.HandleOut, i);
                return;
            }
        }
        for (var i = 0; i < anchors.Count; i++)
        {
            if (!Hits(anchors[i].Point, unit, view)) continue;
            drag = (DragKind.Anchor, i);
            return;
        }
    }

    /// <summary>The pointer moved with the button down.</summary>
    public void Drag(Point unit)
    {
        if (drag is not { } d || d.Index >= anchors.Count) return;
        var anchor = anchors[d.Index];
        switch (d.Kind)
        {
            case DragKind.Anchor:
                // A point carries its handles with it.
                var dx = unit.X - anchor.Point.X;
                var dy = unit.Y - anchor.Point.Y;
                anchors[d.Index] = new PathAnchor(unit,
                    anchor.HandleIn is { } hIn ? new Point(hIn.X + dx, hIn.Y + dy) : null,
                    anchor.HandleOut is { } hOut ? new Point(hOut.X + dx, hOut.Y + dy) : null);
                break;
            case DragKind.HandleIn:
                anchors[d.Index] = anchor with { HandleIn = unit };
                break;
            case DragKind.HandleOut:
                anchors[d.Index] = anchor with { HandleOut = unit };
                break;
            case DragKind.PenHandles:
                // Pulling away from a just-placed point makes a smooth curve: the out handle
                // follows the pointer and the in handle mirrors it.
                anchors[d.Index] = anchor with
                {
                    HandleOut = unit,
                    HandleIn = new Point(anchor.Point.X * 2 - unit.X, anchor.Point.Y * 2 - unit.Y),
                };
                break;
        }
    }

    /// <summary>The button came up. While editing, the shape is saved after every gesture.</summary>
    public void Release()
    {
        drag = null;
        if (!IsDrawing) Completed?.Invoke(anchors.ToList(), Closed);
    }

    /// <summary>The pointer moved with no button down (only matters while drawing).</summary>
    public void Move(Point unit)
    {
        if (IsDrawing) Hover = unit;
    }

    /// <summary>The pointer left the drawing area.</summary>
    public void Leave() => Hover = null;

    /// <summary>Return: closes a drawing with three or more points. True when it was used.</summary>
    public bool PressReturn()
    {
        if (!IsDrawing || anchors.Count < 3) return false;
        Finish(close: true);
        return true;
    }

    /// <summary>Escape: gives up the drawing. True when it was used.</summary>
    public bool PressEscape()
    {
        if (!IsDrawing) return false;
        anchors.Clear();
        Hover = null;
        drag = null;
        Cancelled?.Invoke();
        return true;
    }

    /// <summary>
    /// Removes an edited shape's point (not on the Mac, where a mask is redrawn
    /// instead). A shape keeps at least three. True when it was removed.
    /// </summary>
    public bool RemovePoint(Point unit, SceneSize view)
    {
        if (IsDrawing || anchors.Count <= 3) return false;
        var index = anchors.FindIndex(a => Hits(a.Point, unit, view));
        if (index < 0) return false;
        anchors.RemoveAt(index);
        Completed?.Invoke(anchors.ToList(), Closed);
        return true;
    }

    private void Finish(bool close)
    {
        if (anchors.Count < 3) return;
        Closed = close;
        drag = null;
        Completed?.Invoke(anchors.ToList(), close);
        if (IsDrawing) anchors.Clear();
        Hover = null;
    }

    /// <summary>One piece of the outline to draw: a straight line, or a curve with two control points.</summary>
    public readonly record struct Segment(Point From, Point To, Point? Control1, Point? Control2)
    {
        public bool IsLine => Control1 is null;
    }

    /// <summary>The outline as it should be drawn now (an unfinished drawing isn't closed).</summary>
    public IReadOnlyList<Segment> Segments()
    {
        var segments = new List<Segment>();
        if (anchors.Count == 0) return segments;
        var count = Closed && !IsDrawing ? anchors.Count : anchors.Count - 1;
        for (var i = 0; i < count; i++)
        {
            var from = anchors[i];
            var to = anchors[(i + 1) % anchors.Count];
            segments.Add(from.HandleOut is null && to.HandleIn is null
                ? new Segment(from.Point, to.Point, null, null)
                : new Segment(from.Point, to.Point, from.HandleOut ?? from.Point, to.HandleIn ?? to.Point));
        }
        return segments;
    }

    /// <summary>The faint line from the last point to the pointer while drawing, or null.</summary>
    public Segment? RubberBand() =>
        IsDrawing && Hover is { } hover && anchors.Count > 0
            ? anchors[^1].HandleOut is { } handle
                ? new Segment(anchors[^1].Point, hover, handle, hover)
                : new Segment(anchors[^1].Point, hover, null, null)
            : null;
}

/// <summary>
/// Changes to a screen's mask library, as the Mac's OutputMaskEditor makes
/// them: each returns a new list for <see cref="OutputConfiguration.SetMasks"/>
/// (which saves it and redraws the outputs).
/// </summary>
public static class OutputMaskLibrary
{
    /// <summary>The feather slider's range: none up to a quarter of the screen's shorter side.</summary>
    public const double MaxFeather = 0.25;

    /// <summary>A new mask from a drawn shape, named "Mask N" and blacking out what's inside, on every output of the screen.</summary>
    public static List<OutputMask> Add(IReadOnlyList<OutputMask> library, string pathData) =>
        [.. library, new OutputMask($"Mask {library.Count + 1}", pathData)];

    /// <summary>Changes one mask (found by id); the others are untouched.</summary>
    public static List<OutputMask> Change(IReadOnlyList<OutputMask> library, Guid id, Func<OutputMask, OutputMask> change) =>
        library.Select(mask => mask.Id == id ? change(mask) : mask).ToList();

    public static List<OutputMask> Remove(IReadOnlyList<OutputMask> library, Guid id) =>
        library.Where(mask => mask.Id != id).ToList();

    /// <summary>A shape for a mask's path: the pen's points written in the screen's unit square. Null for fewer than two points.</summary>
    public static string? PathData(IReadOnlyList<PathAnchor> anchors, bool closed) => PathAnchorCodec.EncodeAbsolute(anchors, closed);

    public static double ClampFeather(double feather) => double.IsFinite(feather) ? Math.Clamp(feather, 0, MaxFeather) : 0;

    /// <summary>
    /// A rectangle mask's path, for the editor's quick shapes; corners are
    /// clamped into the screen. Null when it has no area.
    /// </summary>
    public static string? Rectangle(double x, double y, double width, double height)
    {
        var left = Math.Clamp(Math.Min(x, x + width), 0, 1);
        var right = Math.Clamp(Math.Max(x, x + width), 0, 1);
        var top = Math.Clamp(Math.Min(y, y + height), 0, 1);
        var bottom = Math.Clamp(Math.Max(y, y + height), 0, 1);
        if (right - left <= 0 || bottom - top <= 0) return null;
        return PathData([new(new Point(left, top)), new(new Point(right, top)), new(new Point(right, bottom)), new(new Point(left, bottom))], closed: true);
    }

    /// <summary>
    /// Restricts a mask to one output of its screen, or (null) lets it cover them all.
    /// The screen's own id stands for its first output.
    /// </summary>
    public static OutputMask Covering(OutputMask mask, Guid? sliceId) =>
        mask with { SliceIds = sliceId is { } id ? [id] : null };

    /// <summary>"All outputs", one output's name, or "3 outputs" for a mask that names several.</summary>
    public static string CoversLabel(OutputMask mask, Func<Guid, string?> sliceName) => mask.SliceIds switch
    {
        null => "All outputs",
        { Count: 0 } => "No outputs",
        { Count: 1 } ids => sliceName(ids[0]) ?? "An output that's gone",
        var ids => $"{ids.Count} outputs",
    };
}
