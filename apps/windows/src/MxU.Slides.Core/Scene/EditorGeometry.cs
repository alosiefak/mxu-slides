using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Scene;

/// <summary>
/// The slide editor's geometry: what's under the pointer, resize handles,
/// snapping to the canvas and other objects, and align/distribute. Port of
/// SlideScene/EditorGeometry.swift (perspective helpers left out).
/// </summary>
public static class EditorGeometry
{
    public readonly record struct Point(double X, double Y);

    public readonly record struct Vector(double Dx, double Dy);

    public static SlideObject? HitObject(Point point, IReadOnlyList<SlideObject> objects, Slide? template = null) =>
        ObjectsUnder(point, objects, template).FirstOrDefault();

    /// <summary>The object a press lands on, preferring one already selected (so overlapping selections stay draggable).</summary>
    public static SlideObject? PressTarget(Point point, IReadOnlyList<SlideObject> objects, ISet<string> selected, Slide? template = null)
    {
        var under = ObjectsUnder(point, objects, template).ToList();
        return under.FirstOrDefault(o => selected.Contains(o.Id)) ?? under.FirstOrDefault();
    }

    private static IEnumerable<SlideObject> ObjectsUnder(Point point, IReadOnlyList<SlideObject> objects, Slide? template)
    {
        var placeholders = SlideSceneBuilder.PlaceholderAssignments(objects, template);
        return ComposedOrder(objects, template).AsEnumerable().Reverse().Where(o =>
        {
            var frame = SlideSceneBuilder.Frame(o, placeholders.GetValueOrDefault(o.Id));
            var local = Unrotate(point, new Point(frame.X + frame.Width / 2, frame.Y + frame.Height / 2), o.RotationDegrees ?? 0);
            return Contains(frame, local);
        });
    }

    /// <summary>The slide's own objects in drawing order (theme decor excluded).</summary>
    public static List<SlideObject> ComposedOrder(IReadOnlyList<SlideObject> objects, Slide? template) =>
        SlideSceneBuilder.ComposedStack(objects, template).Where(e => !e.FromTheme).Select(e => e.Object).ToList();

    private static bool Contains(SceneRect r, Point p) => p.X >= r.X && p.X <= r.Right && p.Y >= r.Y && p.Y <= r.Bottom;

    public static Point Unrotate(Point point, Point center, double degrees)
    {
        if (degrees == 0) return point;
        var radians = -degrees * Math.PI / 180;
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        return new Point(center.X + dx * Math.Cos(radians) - dy * Math.Sin(radians), center.Y + dx * Math.Sin(radians) + dy * Math.Cos(radians));
    }

    public static SceneRect RotatedBounds(SceneRect frame, double degrees)
    {
        if (degrees % 360 == 0) return frame;
        var center = new Point(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
        var corners = new[] { new Point(frame.X, frame.Y), new Point(frame.Right, frame.Y), new Point(frame.Right, frame.Bottom), new Point(frame.X, frame.Bottom) }
            .Select(c => Unrotate(c, center, -degrees)).ToList();
        var minX = corners.Min(c => c.X);
        var minY = corners.Min(c => c.Y);
        return new SceneRect(minX, minY, corners.Max(c => c.X) - minX, corners.Max(c => c.Y) - minY);
    }

    public const double PasteboardInset = 32;

    public static (double Scale, Point Origin) Letterbox(SceneSize canvas, SceneSize view, double inset = 0)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0 || view.Width <= 0 || view.Height <= 0) return (1, new Point(0, 0));
        var scale = Math.Min(Math.Max(view.Width - 2 * inset, 1) / canvas.Width, Math.Max(view.Height - 2 * inset, 1) / canvas.Height);
        return (scale, new Point((view.Width - canvas.Width * scale) / 2, (view.Height - canvas.Height * scale) / 2));
    }

    public static Point ScenePoint(Point fromView, SceneSize viewSize, SceneSize canvas, double inset = 0)
    {
        var (scale, origin) = Letterbox(canvas, viewSize, inset);
        return new Point((fromView.X - origin.X) / scale, (fromView.Y - origin.Y) / scale);
    }

    public static HashSet<string> SweptObjectIds(SceneRect band, IEnumerable<SlideObject> objects, Func<SlideObject, SceneRect> frame) =>
        objects.Where(o => o.Hidden != true && Intersects(RotatedBounds(frame(o), o.RotationDegrees ?? 0), band)).Select(o => o.Id).ToHashSet();

    private static bool Intersects(SceneRect a, SceneRect b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    public static HashSet<string> AllObjectIds(IEnumerable<SlideObject> objects, bool includingHidden) =>
        objects.Where(o => includingHidden || o.Hidden != true).Select(o => o.Id).ToHashSet();

    public static HashSet<string> ExpandSelectionToGroups(IReadOnlySet<string> ids, IReadOnlyList<SlideObject> objects)
    {
        static string? Group(SlideObject o) => string.IsNullOrEmpty(o.GroupId) ? null : o.GroupId;
        var groups = objects.Where(o => ids.Contains(o.Id)).Select(Group).OfType<string>().ToHashSet();
        var result = ids.ToHashSet();
        if (groups.Count == 0) return result;
        result.UnionWith(objects.Where(o => Group(o) is { } g && groups.Contains(g)).Select(o => o.Id));
        return result;
    }

    // MARK: Handles and resizing

    public enum Handle { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

    public static bool ResizesX(this Handle h) => h is not (Handle.Top or Handle.Bottom);

    public static bool ResizesY(this Handle h) => h is not (Handle.Left or Handle.Right);

    private static bool Leading(Handle h) => h is Handle.TopLeft or Handle.Left or Handle.BottomLeft;

    private static bool Upper(Handle h) => h is Handle.TopLeft or Handle.Top or Handle.TopRight;

    public static Point HandlePosition(Handle handle, SceneRect f) => handle switch
    {
        Handle.TopLeft => new(f.X, f.Y),
        Handle.Top => new(f.X + f.Width / 2, f.Y),
        Handle.TopRight => new(f.Right, f.Y),
        Handle.Right => new(f.Right, f.Y + f.Height / 2),
        Handle.BottomRight => new(f.Right, f.Bottom),
        Handle.Bottom => new(f.X + f.Width / 2, f.Bottom),
        Handle.BottomLeft => new(f.X, f.Bottom),
        _ => new(f.X, f.Y + f.Height / 2),
    };

    public static SceneRect Resize(SceneRect frame, Handle handle, Vector delta, double minSize = 16)
    {
        double x = frame.X, y = frame.Y, w = frame.Width, h = frame.Height;
        if (handle.ResizesX())
        {
            if (Leading(handle))
            {
                var dx = Math.Min(delta.Dx, frame.Width - minSize);
                x += dx;
                w -= dx;
            }
            else
            {
                w = Math.Max(minSize, frame.Width + delta.Dx);
            }
        }
        if (handle.ResizesY())
        {
            if (Upper(handle))
            {
                var dy = Math.Min(delta.Dy, frame.Height - minSize);
                y += dy;
                h -= dy;
            }
            else
            {
                h = Math.Max(minSize, frame.Height + delta.Dy);
            }
        }
        return new SceneRect(x, y, w, h);
    }

    // MARK: Snapping

    public abstract record Guide
    {
        private Guide() { }
        public sealed record Vertical(double X) : Guide;
        public sealed record Horizontal(double Y) : Guide;
    }

    public sealed record SnapResult(SceneRect Frame, IReadOnlyList<Guide> Guides);

    private static (List<double> X, List<double> Y) Targets(SceneSize canvas, IEnumerable<SceneRect> others)
    {
        var xs = new List<double> { 0, canvas.Width / 2, canvas.Width };
        var ys = new List<double> { 0, canvas.Height / 2, canvas.Height };
        foreach (var r in others)
        {
            xs.AddRange([r.X, r.X + r.Width / 2, r.Right]);
            ys.AddRange([r.Y, r.Y + r.Height / 2, r.Bottom]);
        }
        return (xs, ys);
    }

    public static SnapResult SnapMove(SceneRect frame, IEnumerable<SceneRect> others, SceneSize? canvasSize = null, double threshold = 8)
    {
        var (xs, ys) = Targets(canvasSize ?? SlideSceneBuilder.CanvasSize, others);
        double x = frame.X, y = frame.Y;
        var guides = new List<Guide>();
        if (BestSnap([frame.X, frame.X + frame.Width / 2, frame.Right], xs, threshold) is { } bx)
        {
            x += bx.Correction;
            guides.Add(new Guide.Vertical(bx.Target));
        }
        if (BestSnap([frame.Y, frame.Y + frame.Height / 2, frame.Bottom], ys, threshold) is { } by)
        {
            y += by.Correction;
            guides.Add(new Guide.Horizontal(by.Target));
        }
        return new SnapResult(frame with { X = x, Y = y }, guides);
    }

    public static SnapResult SnapResize(SceneRect frame, Handle handle, IEnumerable<SceneRect> others, double rotationDegrees = 0,
        SceneSize? canvasSize = null, double threshold = 8)
    {
        if (rotationDegrees % 360 != 0) return new SnapResult(frame, []);
        var (xs, ys) = Targets(canvasSize ?? SlideSceneBuilder.CanvasSize, others);
        double x = frame.X, y = frame.Y, w = frame.Width, h = frame.Height;
        var guides = new List<Guide>();
        if (handle.ResizesX())
        {
            var leading = Leading(handle);
            if (BestSnap([leading ? frame.X : frame.Right], xs, threshold) is { } best)
            {
                if (leading)
                {
                    x += best.Correction;
                    w -= best.Correction;
                }
                else
                {
                    w += best.Correction;
                }
                guides.Add(new Guide.Vertical(best.Target));
            }
        }
        if (handle.ResizesY())
        {
            var leading = Upper(handle);
            if (BestSnap([leading ? frame.Y : frame.Bottom], ys, threshold) is { } best)
            {
                if (leading)
                {
                    y += best.Correction;
                    h -= best.Correction;
                }
                else
                {
                    h += best.Correction;
                }
                guides.Add(new Guide.Horizontal(best.Target));
            }
        }
        return new SnapResult(new SceneRect(x, y, w, h), guides);
    }

    private static (double Correction, double Target)? BestSnap(IEnumerable<double> candidates, IReadOnlyList<double> targets, double threshold)
    {
        (double Correction, double Target)? best = null;
        foreach (var candidate in candidates)
        {
            foreach (var target in targets)
            {
                var correction = target - candidate;
                if (Math.Abs(correction) <= threshold && Math.Abs(correction) < Math.Abs(best?.Correction ?? double.MaxValue))
                    best = (correction, target);
            }
        }
        return best;
    }

    // MARK: Align and distribute

    public enum AlignEdge { Left, CenterX, Right, Top, CenterY, Bottom }

    public sealed record AlignUnit(IReadOnlyList<string> Ids, SceneRect Frame);

    public static Dictionary<string, Vector> AlignDeltas(IEnumerable<AlignUnit> units, AlignEdge edge, SceneRect reference)
    {
        var deltas = new Dictionary<string, Vector>();
        foreach (var unit in units)
        {
            var f = unit.Frame;
            var delta = edge switch
            {
                AlignEdge.Left => new Vector(reference.X - f.X, 0),
                AlignEdge.CenterX => new Vector(reference.X + reference.Width / 2 - (f.X + f.Width / 2), 0),
                AlignEdge.Right => new Vector(reference.Right - f.Right, 0),
                AlignEdge.Top => new Vector(0, reference.Y - f.Y),
                AlignEdge.CenterY => new Vector(0, reference.Y + reference.Height / 2 - (f.Y + f.Height / 2)),
                _ => new Vector(0, reference.Bottom - f.Bottom),
            };
            if (delta.Dx == 0 && delta.Dy == 0) continue;
            foreach (var id in unit.Ids) deltas[id] = delta;
        }
        return deltas;
    }

    public static Dictionary<string, Vector> DistributeDeltas(IReadOnlyList<AlignUnit> units, bool horizontally)
    {
        var deltas = new Dictionary<string, Vector>();
        if (units.Count < 3) return deltas;
        var sorted = units.OrderBy(u => horizontally ? u.Frame.X : u.Frame.Y).ToList();
        var first = sorted[0].Frame;
        var last = sorted[^1].Frame;
        var span = horizontally ? last.Right - first.X : last.Bottom - first.Y;
        var extent = sorted.Sum(u => horizontally ? u.Frame.Width : u.Frame.Height);
        var gap = (span - extent) / (sorted.Count - 1);
        var cursor = horizontally ? first.X : first.Y;
        foreach (var unit in sorted)
        {
            var position = horizontally ? unit.Frame.X : unit.Frame.Y;
            var offset = cursor - position;
            if (offset != 0)
            {
                var delta = horizontally ? new Vector(offset, 0) : new Vector(0, offset);
                foreach (var id in unit.Ids) deltas[id] = delta;
            }
            cursor += (horizontally ? unit.Frame.Width : unit.Frame.Height) + gap;
        }
        return deltas;
    }
}
