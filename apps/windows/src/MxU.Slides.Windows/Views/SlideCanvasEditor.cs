using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using static MxU.Slides.Core.Scene.EditorGeometry;
using Point = System.Windows.Point;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Edit-mode canvas: the slide drawn by the real renderer, with an
/// interaction layer on top. Click to select (Shift adds), drag to move with
/// snapping to the canvas and other objects (hold Alt to stop snapping), drag
/// a handle to resize, drag on empty space to select several. Arrow keys
/// nudge (Shift ×10), Delete removes, Ctrl+D duplicates, Ctrl+A selects all.
/// </summary>
public sealed class SlideCanvasEditor : Grid
{
    private readonly SceneView view = new() { PlaysVideo = false, Letterbox = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)) };
    private readonly Overlay overlay;

    public SlideCanvasEditor()
    {
        overlay = new Overlay(this);
        Children.Add(view);
        Children.Add(overlay);
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
    }

    public Slide? Slide { get; private set; }
    public Theme? Theme { get; private set; }
    public Presentation? Presentation { get; private set; }
    public HashSet<string> Selection { get; } = new(StringComparer.Ordinal);

    public event Action? SelectionChanged;

    /// <summary>Objects moved or resized; <c>true</c> once the gesture ends (time to save).</summary>
    public event Action<bool>? Edited;

    public event Action<string>? TextEditRequested;
    public event Action? DeleteRequested;
    public event Action? DuplicateRequested;

    public SceneSize Canvas => SlideSceneBuilder.CanvasSizeFor(Presentation);

    private Slide? Template => Slide is null ? null : SlideSceneBuilder.ThemeSlide(Slide, Theme);

    private IReadOnlyList<SlideObject> Objects => Slide?.Objects ?? [];

    public void Load(Slide? slide, Theme? theme, Presentation? presentation)
    {
        var changedSlide = slide?.Id != Slide?.Id;
        Slide = slide;
        Theme = theme;
        Presentation = presentation;
        if (changedSlide)
        {
            Selection.Clear();
            SelectionChanged?.Invoke();
        }
        Refresh();
    }

    public void Refresh()
    {
        if (Slide is null)
        {
            view.Present((RenderScene?)null, SceneTransition.Cut);
        }
        else
        {
            var scene = SlideSceneBuilder.Scene(Slide, Theme, Presentation);
            if (view.Scene is null) view.Present(scene, SceneTransition.Cut);
            else view.Update(scene);
        }
        overlay.InvalidateVisual();
    }

    public void Select(IEnumerable<string> ids)
    {
        Selection.Clear();
        Selection.UnionWith(ids);
        SelectionChanged?.Invoke();
        overlay.InvalidateVisual();
    }

    /// <summary>Where an object sits on the canvas (its own frame, or its theme placeholder's).</summary>
    public SceneRect FrameOf(SlideObject o)
    {
        var placeholders = SlideSceneBuilder.PlaceholderAssignments(Objects, Template);
        return SlideSceneBuilder.Frame(o, placeholders.GetValueOrDefault(o.Id), Canvas);
    }

    private static void SetFrame(SlideObject o, SceneRect frame)
    {
        o.X = Math.Round(frame.X, 1);
        o.Y = Math.Round(frame.Y, 1);
        o.Width = Math.Round(frame.Width, 1);
        o.Height = Math.Round(frame.Height, 1);
    }

    // MARK: Coordinates

    private (double Scale, EditorGeometry.Point Origin) Fit() =>
        Letterbox(Canvas, new SceneSize(ActualWidth, ActualHeight));

    private EditorGeometry.Point ToScene(Point p)
    {
        var (scale, origin) = Fit();
        return new((p.X - origin.X) / scale, (p.Y - origin.Y) / scale);
    }

    private Point ToView(EditorGeometry.Point p)
    {
        var (scale, origin) = Fit();
        return new(origin.X + p.X * scale, origin.Y + p.Y * scale);
    }

    private Rect ToView(SceneRect r)
    {
        var (scale, origin) = Fit();
        return new Rect(origin.X + r.X * scale, origin.Y + r.Y * scale, Math.Max(0, r.Width * scale), Math.Max(0, r.Height * scale));
    }

    // MARK: Gestures

    private enum Gesture { None, Move, Resize, Marquee }

    private Gesture gesture;
    private EditorGeometry.Point start;
    private Handle handle;
    private string? primaryId;
    private readonly Dictionary<string, SceneRect> originals = new(StringComparer.Ordinal);
    private HashSet<string> marqueeBase = [];
    private SceneRect? marquee;
    private IReadOnlyList<Guide> guides = [];
    private bool moved;
    private string? hoverId;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Slide is null) return;
        Focus();
        var p = ToScene(e.GetPosition(this));
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        start = p;
        moved = false;
        guides = [];

        if (Selection.Count == 1 && Objects.FirstOrDefault(o => o.Id == Selection.First()) is { } only
            && (only.RotationDegrees ?? 0) == 0 && HandleAt(e.GetPosition(this), FrameOf(only)) is { } grabbed)
        {
            gesture = Gesture.Resize;
            handle = grabbed;
            primaryId = only.Id;
            originals.Clear();
            originals[only.Id] = FrameOf(only);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        var target = PressTarget(p, Objects, Selection, Template);
        if (target is not null)
        {
            if (e.ClickCount == 2 && target.ObjectKind == SlideObjectKind.Text)
            {
                TextEditRequested?.Invoke(target.Id);
                e.Handled = true;
                return;
            }
            if (shift)
            {
                if (!Selection.Remove(target.Id)) Selection.Add(target.Id);
            }
            else if (!Selection.Contains(target.Id))
            {
                Selection.Clear();
                Selection.Add(target.Id);
            }
            var expanded = ExpandSelectionToGroups(Selection, Objects);
            Selection.UnionWith(expanded);
            SelectionChanged?.Invoke();
            if (Selection.Contains(target.Id))
            {
                gesture = Gesture.Move;
                primaryId = target.Id;
                originals.Clear();
                foreach (var o in Objects.Where(o => Selection.Contains(o.Id))) originals[o.Id] = FrameOf(o);
                CaptureMouse();
            }
        }
        else
        {
            if (!shift && Selection.Count > 0)
            {
                Selection.Clear();
                SelectionChanged?.Invoke();
            }
            gesture = Gesture.Marquee;
            marqueeBase = shift ? Selection.ToHashSet() : [];
            marquee = new SceneRect(p.X, p.Y, 0, 0);
            CaptureMouse();
        }
        overlay.InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Slide is null) return;
        var p = ToScene(e.GetPosition(this));
        var noSnap = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var delta = new EditorGeometry.Vector(p.X - start.X, p.Y - start.Y);
        switch (gesture)
        {
            case Gesture.Move when primaryId is not null && originals.TryGetValue(primaryId, out var primary):
            {
                if (!moved && Math.Abs(delta.Dx) < 2 && Math.Abs(delta.Dy) < 2) return;
                moved = true;
                var proposed = primary with { X = primary.X + delta.Dx, Y = primary.Y + delta.Dy };
                var others = Objects.Where(o => !Selection.Contains(o.Id) && o.Hidden != true).Select(FrameOf).ToList();
                var snap = noSnap ? new SnapResult(proposed, []) : SnapMove(proposed, others, Canvas, 8 / Fit().Scale);
                var dx = snap.Frame.X - primary.X;
                var dy = snap.Frame.Y - primary.Y;
                guides = snap.Guides;
                foreach (var o in Objects.Where(o => originals.ContainsKey(o.Id)))
                {
                    var f = originals[o.Id];
                    SetFrame(o, f with { X = f.X + dx, Y = f.Y + dy });
                }
                Edited?.Invoke(false);
                Refresh();
                break;
            }
            case Gesture.Resize when primaryId is not null && originals.TryGetValue(primaryId, out var frame):
            {
                moved = true;
                var resized = Resize(frame, handle, delta);
                var others = Objects.Where(o => o.Id != primaryId && o.Hidden != true).Select(FrameOf).ToList();
                var snap = noSnap ? new SnapResult(resized, []) : SnapResize(resized, handle, others, 0, Canvas, 8 / Fit().Scale);
                guides = snap.Guides;
                if (Objects.FirstOrDefault(o => o.Id == primaryId) is { } target) SetFrame(target, snap.Frame);
                Edited?.Invoke(false);
                Refresh();
                break;
            }
            case Gesture.Marquee:
            {
                marquee = new SceneRect(Math.Min(start.X, p.X), Math.Min(start.Y, p.Y), Math.Abs(p.X - start.X), Math.Abs(p.Y - start.Y));
                var swept = SweptObjectIds(marquee.Value, EditorGeometry.ComposedOrder(Objects, Template), FrameOf);
                Selection.Clear();
                Selection.UnionWith(marqueeBase);
                Selection.UnionWith(ExpandSelectionToGroups(swept, Objects));
                SelectionChanged?.Invoke();
                overlay.InvalidateVisual();
                break;
            }
            default:
            {
                var hover = HitObject(p, Objects, Template)?.Id;
                if (hover != hoverId)
                {
                    hoverId = hover;
                    overlay.InvalidateVisual();
                }
                Cursor = Selection.Count == 1 && Objects.FirstOrDefault(o => o.Id == Selection.First()) is { } sel
                    && (sel.RotationDegrees ?? 0) == 0 && HandleAt(e.GetPosition(this), FrameOf(sel)) is { } h
                        ? CursorFor(h)
                        : hover is null ? Cursors.Arrow : Cursors.SizeAll;
                break;
            }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var finished = gesture is Gesture.Move or Gesture.Resize && moved;
        gesture = Gesture.None;
        marquee = null;
        guides = [];
        ReleaseMouseCapture();
        if (finished) Edited?.Invoke(true);
        overlay.InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        hoverId = null;
        overlay.InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Slide is null) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        (double Dx, double Dy)? nudge = e.Key switch
        {
            Key.Left => (-step, 0),
            Key.Right => (step, 0),
            Key.Up => (0, -step),
            Key.Down => (0, step),
            _ => null,
        };
        if (nudge is { } n && Selection.Count > 0)
        {
            foreach (var o in Objects.Where(o => Selection.Contains(o.Id)))
            {
                var f = FrameOf(o);
                SetFrame(o, f with { X = f.X + n.Dx, Y = f.Y + n.Dy });
            }
            Edited?.Invoke(true);
            Refresh();
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Delete or Key.Back when Selection.Count > 0:
                DeleteRequested?.Invoke();
                e.Handled = true;
                break;
            case Key.D when ctrl && Selection.Count > 0:
                DuplicateRequested?.Invoke();
                e.Handled = true;
                break;
            case Key.A when ctrl:
                Select(AllObjectIds(EditorGeometry.ComposedOrder(Objects, Template), includingHidden: false));
                e.Handled = true;
                break;
            case Key.Escape:
                Select([]);
                e.Handled = true;
                break;
        }
    }

    private Handle? HandleAt(Point viewPoint, SceneRect frame)
    {
        foreach (var h in Enum.GetValues<Handle>())
        {
            var hp = ToView(HandlePosition(h, frame));
            if (Math.Abs(hp.X - viewPoint.X) <= 7 && Math.Abs(hp.Y - viewPoint.Y) <= 7) return h;
        }
        return null;
    }

    private static Cursor CursorFor(Handle h) => h switch
    {
        Handle.TopLeft or Handle.BottomRight => Cursors.SizeNWSE,
        Handle.TopRight or Handle.BottomLeft => Cursors.SizeNESW,
        Handle.Top or Handle.Bottom => Cursors.SizeNS,
        _ => Cursors.SizeWE,
    };

    // MARK: Drawing the selection

    private sealed class Overlay(SlideCanvasEditor editor) : FrameworkElement
    {
        private static readonly Pen Faint = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = DashStyles.Dash });
        private static readonly Pen Hover = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0x60, 0xA5, 0xFA)), 1));
        private static readonly Pen Selected = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), 1.5));
        private static readonly Pen GuidePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0xB6)), 1));
        private static readonly Brush HandleFill = Brushes.White;
        private static readonly Brush MarqueeFill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x3B, 0x82, 0xF6)));

        private static T Frozen<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (editor.Slide is null) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            var objects = EditorGeometry.ComposedOrder(editor.Objects, editor.Template);
            foreach (var o in objects.Where(o => o.Hidden != true))
            {
                var frame = editor.FrameOf(o);
                var pen = editor.Selection.Contains(o.Id) ? Selected : o.Id == editor.hoverId ? Hover : Faint;
                DrawFrame(dc, frame, o.RotationDegrees ?? 0, pen);
            }
            if (editor.Selection.Count == 1 && objects.FirstOrDefault(o => o.Id == editor.Selection.First()) is { } only && (only.RotationDegrees ?? 0) == 0)
            {
                var frame = editor.FrameOf(only);
                foreach (var h in Enum.GetValues<Handle>())
                {
                    var p = editor.ToView(HandlePosition(h, frame));
                    dc.DrawRectangle(HandleFill, Selected, new Rect(p.X - 4, p.Y - 4, 8, 8));
                }
            }
            foreach (var guide in editor.guides)
            {
                switch (guide)
                {
                    case Guide.Vertical v:
                        var x = editor.ToView(new EditorGeometry.Point(v.X, 0)).X;
                        dc.DrawLine(GuidePen, new Point(x, 0), new Point(x, ActualHeight));
                        break;
                    case Guide.Horizontal hz:
                        var y = editor.ToView(new EditorGeometry.Point(0, hz.Y)).Y;
                        dc.DrawLine(GuidePen, new Point(0, y), new Point(ActualWidth, y));
                        break;
                }
            }
            if (editor.marquee is { } band) dc.DrawRectangle(MarqueeFill, Selected, editor.ToView(band));
        }

        private void DrawFrame(DrawingContext dc, SceneRect frame, double rotation, Pen pen)
        {
            var rect = editor.ToView(frame);
            if (rotation != 0) dc.PushTransform(new RotateTransform(rotation, rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
            dc.DrawRectangle(null, pen, rect);
            if (rotation != 0) dc.Pop();
        }
    }
}
