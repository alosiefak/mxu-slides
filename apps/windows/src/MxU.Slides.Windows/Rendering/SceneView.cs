using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.Rendering;

public enum TransitionStyle { Cut, Dissolve, FadeThroughColor }

public readonly record struct SceneTransition(TransitionStyle Style, double Seconds = 0, Color? ThroughColor = null)
{
    public static readonly SceneTransition Cut = new(TransitionStyle.Cut);
}

/// <summary>
/// Draws a <see cref="RenderScene"/> scaled to fit (letterboxed) with
/// cut/dissolve/fade-through transitions. Each item is its own visual, so
/// shadows, blur, rotation and opacity compose on the GPU. A single bad item
/// is skipped and logged rather than taking the frame down.
/// </summary>
public sealed class SceneView : FrameworkElement
{
    private sealed class ItemVisual : DrawingVisual
    {
        public ItemVisual()
        {
            VisualTextRenderingMode = TextRenderingMode.Grayscale;
            VisualTextHintingMode = TextHintingMode.Animated;
            VisualBitmapScalingMode = BitmapScalingMode.HighQuality;
            VisualEdgeMode = EdgeMode.Unspecified;
        }
    }

    private sealed class Root(RenderScene scene, ContainerVisual visual, HashSet<string> keys)
    {
        public RenderScene Scene { get; set; } = scene;
        public ContainerVisual Visual { get; set; } = visual;
        public HashSet<string> Keys { get; set; } = keys;
    }

    private readonly VisualCollection children;
    private readonly DrawingVisual throughColor = new();
    private Root? current;
    private Root? outgoing;
    private SceneTransition activeTransition;
    private readonly Stopwatch transitionClock = new();
    private bool animating;
    private MediaHub? media;

    public SceneView()
    {
        children = new VisualCollection(this);
        SnapsToDevicePixels = false;
        ClipToBounds = true;
        if (DefaultMedia is not null) Media = DefaultMedia;
        Unloaded += (_, _) => ReleaseAll();
    }

    /// <summary>The hub views created in XAML (thumbnails, previews) use unless given their own.</summary>
    public static MediaHub? DefaultMedia { get; set; }

    /// <summary>Shared images and video players. Required before presenting media.</summary>
    public MediaHub? Media
    {
        get => media;
        set
        {
            if (media is not null) media.MediaReady -= OnMediaReady;
            media = value;
            if (media is not null) media.MediaReady += OnMediaReady;
        }
    }

    /// <summary>When false (thumbnails, editor), videos draw as a still placeholder and no players start.</summary>
    public bool PlaysVideo { get; set; }

    /// <summary>Videos with the same scope and item id share one player (audience output and its preview).</summary>
    public string PlaybackScope { get; set; } = "live";

    /// <summary>Which media layers may play sound. Background loops stay silent.</summary>
    public Func<string, bool> IsAudible { get; set; } = itemId => itemId == "live-media-videos";

    /// <summary>Colour outside the scene when the view's shape differs from the canvas.</summary>
    public Brush Letterbox { get; set; } = Brushes.Black;

    public RenderScene? Scene => current?.Scene;

    public static readonly DependencyProperty StaticSceneProperty = DependencyProperty.Register(
        nameof(StaticScene), typeof(RenderScene), typeof(SceneView),
        new FrameworkPropertyMetadata(null, (d, e) => ((SceneView)d).Present((RenderScene?)e.NewValue, SceneTransition.Cut)));

    /// <summary>Bindable scene for thumbnails and previews (always a cut).</summary>
    public RenderScene? StaticScene
    {
        get => (RenderScene?)GetValue(StaticSceneProperty);
        set => SetValue(StaticSceneProperty, value);
    }

    public void Present(RenderScene? scene, SceneTransition transition)
    {
        var next = scene is null ? null : Build(scene);
        if (next is not null) Retain(next.Keys);

        if (transition.Style == TransitionStyle.Cut || transition.Seconds <= 0.01 || !IsVisible)
        {
            FinishTransition();
            Detach(current);
            current = next;
            Attach(current, opacity: 1);
            return;
        }

        FinishTransition();
        outgoing = current;
        current = next;
        activeTransition = transition;
        if (transition.Style == TransitionStyle.FadeThroughColor)
        {
            using (var dc = throughColor.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(transition.ThroughColor ?? Colors.Black), null, new Rect(RenderSize));
            if (!children.Contains(throughColor)) children.Insert(0, throughColor);
        }
        Attach(current, opacity: 0);
        transitionClock.Restart();
        if (!animating)
        {
            animating = true;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    /// <summary>Re-renders the current scene in place (clock ticks, edits) without a transition.</summary>
    public void Update(RenderScene scene)
    {
        if (current is null)
        {
            Present(scene, SceneTransition.Cut);
            return;
        }
        var rebuilt = Build(scene);
        Retain(rebuilt.Keys);
        var index = children.IndexOf(current.Visual);
        rebuilt.Visual.Opacity = current.Visual.Opacity;
        rebuilt.Visual.Transform = FitTransform(scene.CanvasSize);
        if (index >= 0)
        {
            children.RemoveAt(index);
            children.Insert(index, rebuilt.Visual);
        }
        else
        {
            children.Add(rebuilt.Visual);
        }
        Release(current.Keys);
        current = rebuilt;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var t = Math.Clamp(transitionClock.Elapsed.TotalSeconds / Math.Max(activeTransition.Seconds, 0.01), 0, 1);
        var eased = t * t * (3 - 2 * t);
        if (activeTransition.Style == TransitionStyle.FadeThroughColor)
        {
            if (outgoing is not null) outgoing.Visual.Opacity = Math.Clamp(1 - eased * 2, 0, 1);
            if (current is not null) current.Visual.Opacity = Math.Clamp(eased * 2 - 1, 0, 1);
        }
        else if (current is not null)
        {
            current.Visual.Opacity = eased;
        }
        if (t >= 1) FinishTransition();
    }

    private void FinishTransition()
    {
        if (animating)
        {
            CompositionTarget.Rendering -= OnFrame;
            animating = false;
        }
        Detach(outgoing);
        outgoing = null;
        if (children.Contains(throughColor)) children.Remove(throughColor);
        if (current is not null) current.Visual.Opacity = 1;
    }

    private void Attach(Root? root, double opacity)
    {
        if (root is null) return;
        root.Visual.Opacity = opacity;
        root.Visual.Transform = FitTransform(root.Scene.CanvasSize);
        children.Add(root.Visual);
    }

    private void Detach(Root? root)
    {
        if (root is null) return;
        children.Remove(root.Visual);
        Release(root.Keys);
    }

    private void ReleaseAll()
    {
        FinishTransition();
        Detach(current);
        current = null;
    }

    private void Retain(IEnumerable<string> keys)
    {
        foreach (var key in keys) media?.Retain(key);
    }

    private void Release(IEnumerable<string> keys)
    {
        foreach (var key in keys) media?.Release(key);
    }

    private void OnMediaReady(string mediaId)
    {
        if (current?.Scene.MediaIds.Contains(mediaId) == true) Update(current.Scene);
    }

    protected override int VisualChildrenCount => children.Count;

    protected override Visual GetVisualChild(int index) => children[index];

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

    protected override void OnRender(DrawingContext dc) => dc.DrawRectangle(Letterbox, null, new Rect(RenderSize));

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (current is not null) current.Visual.Transform = FitTransform(current.Scene.CanvasSize);
        if (outgoing is not null) outgoing.Visual.Transform = FitTransform(outgoing.Scene.CanvasSize);
        if (children.Contains(throughColor))
            using (var dc = throughColor.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(activeTransition.ThroughColor ?? Colors.Black), null, new Rect(RenderSize));
    }

    private Transform FitTransform(SceneSize canvas)
    {
        var size = RenderSize;
        if (canvas.Width <= 0 || canvas.Height <= 0 || size.Width <= 0 || size.Height <= 0) return Transform.Identity;
        var scale = Math.Min(size.Width / canvas.Width, size.Height / canvas.Height);
        var transform = new MatrixTransform(scale, 0, 0, scale,
            (size.Width - canvas.Width * scale) / 2, (size.Height - canvas.Height * scale) / 2);
        transform.Freeze();
        return transform;
    }

    // MARK: Building

    private Root Build(RenderScene scene)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var root = new ContainerVisual { Clip = new RectangleGeometry(new Rect(0, 0, scene.CanvasSize.Width, scene.CanvasSize.Height)) };
        var background = new ItemVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(Painting.Brush(scene.Background), null, new Rect(0, 0, scene.CanvasSize.Width, scene.CanvasSize.Height));
        root.Children.Add(background);

        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var layer in scene.Layers)
        {
            foreach (var item in layer.Items)
            {
                try
                {
                    root.Children.Add(BuildItem(item, scene, ppd, keys));
                }
                catch (Exception ex)
                {
                    Log.Error($"Skipped an item that failed to draw ({item.Id})", ex);
                }
            }
        }
        return new Root(scene, root, keys);
    }

    private ItemVisual BuildItem(RenderItem item, RenderScene scene, double ppd, HashSet<string> keys)
    {
        var visual = new ItemVisual { Opacity = Math.Clamp(item.Opacity, 0, 1) };
        var frame = new Rect(item.Frame.X, item.Frame.Y, Math.Max(0, item.Frame.Width), Math.Max(0, item.Frame.Height));
        using (var dc = visual.RenderOpen())
        {
            switch (item.Content)
            {
                case ItemContent.Text text:
                    DrawText(dc, text.Styled, item.Frame, ppd);
                    visual.Effect = ShadowEffect(text.Styled.Shadow);
                    break;
                case ItemContent.Shape shape:
                    DrawShape(dc, shape.Style, frame, item, keys);
                    visual.Effect = ShadowEffect(shape.Style.Shadow);
                    break;
                case ItemContent.Media mediaContent:
                    DrawMedia(dc, mediaContent.Id, mediaContent.ScaleMode, frame, item, keys, new RectangleGeometry(frame));
                    break;
            }
        }
        if (item.BlurRadius > 0.5) visual.Effect = new BlurEffect { Radius = item.BlurRadius, KernelType = KernelType.Gaussian };

        if (item.RotationDegrees != 0 || item.FlipHorizontal || item.FlipVertical || item.SkewX != 0 || item.SkewY != 0)
        {
            var center = new Point(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
            var group = new TransformGroup();
            if (item.SkewX != 0 || item.SkewY != 0)
                group.Children.Add(new SkewTransform(-Math.Clamp(item.SkewX, -85, 85), Math.Clamp(item.SkewY, -85, 85), center.X, center.Y));
            if (item.FlipHorizontal || item.FlipVertical)
                group.Children.Add(new ScaleTransform(item.FlipHorizontal ? -1 : 1, item.FlipVertical ? -1 : 1, center.X, center.Y));
            if (item.RotationDegrees != 0) group.Children.Add(new RotateTransform(item.RotationDegrees, center.X, center.Y));
            group.Freeze();
            visual.Transform = group;
        }
        return visual;
    }

    private static Effect? ShadowEffect(TextShadow? shadow)
    {
        if (shadow is null || shadow.Color.Alpha <= 0.001) return null;
        var depth = Math.Sqrt(shadow.OffsetX * shadow.OffsetX + shadow.OffsetY * shadow.OffsetY);
        var effect = new DropShadowEffect
        {
            Color = Painting.ToColor(shadow.Color with { Alpha = 1 }),
            Opacity = shadow.Color.Alpha,
            BlurRadius = shadow.BlurRadius * 2,
            ShadowDepth = depth,
            Direction = depth > 0 ? Math.Atan2(-shadow.OffsetY, shadow.OffsetX) * 180 / Math.PI : 315,
            RenderingBias = RenderingBias.Quality,
        };
        effect.Freeze();
        return effect;
    }

    private static void DrawText(DrawingContext dc, StyledText text, SceneRect frame, double ppd)
    {
        if (string.IsNullOrEmpty(text.String)) return;
        var layout = TextLayout.Build(text, frame, ppd);
        if (layout.Lines.Count == 0) return;

        if (text.LineFill is { } lineFill && Painting.FillBrush(lineFill.Fill, Bounds(layout)) is { } fillBrush)
        {
            var maxWidth = layout.Lines.Max(l => l.Width);
            foreach (var line in layout.Lines.Where(l => l.Text.Text.Trim().Length > 0))
            {
                var (x, width) = lineFill.WidthMode switch
                {
                    LineFillWidthMode.LineWidth => (line.X - lineFill.HorizontalPadding, line.Width + 2 * lineFill.HorizontalPadding),
                    LineFillWidthMode.MaxLineWidth => (layout.Bounds.X - lineFill.HorizontalPadding, maxWidth + 2 * lineFill.HorizontalPadding),
                    _ => (frame.X, frame.Width),
                };
                var rect = new Rect(x + lineFill.HorizontalOffset, line.Y - lineFill.VerticalPadding + lineFill.VerticalOffset,
                    Math.Max(0, width), line.Height + 2 * lineFill.VerticalPadding);
                dc.DrawRoundedRectangle(fillBrush, null, rect, lineFill.CornerRadius, lineFill.CornerRadius);
            }
        }

        var textBrush = text.Fill is { } fill and not SceneFill.NoFill ? Painting.FillBrush(fill, Bounds(layout)) : null;
        Pen? outlinePen = null;
        if (text.Outline is { Width: > 0 } outline)
        {
            outlinePen = new Pen(Painting.Brush(outline.Color), outline.Width * 2) { LineJoin = PenLineJoin.Round };
            outlinePen.Freeze();
        }
        foreach (var line in layout.Lines)
        {
            var pieces = line.Glyphs ?? [(line.Text, 0.0)];
            foreach (var (piece, offset) in pieces)
            {
                if (textBrush is not null) piece.SetForegroundBrush(textBrush);
                var origin = new Point(line.X + offset, line.Y);
                if (outlinePen is not null) dc.DrawGeometry(null, outlinePen, piece.BuildGeometry(origin));
                if (text.Fill is SceneFill.NoFill) continue;
                dc.DrawText(piece, origin);
            }
        }
        foreach (var chord in layout.Chords) dc.DrawText(chord.Text, new Point(chord.X, chord.Y));
    }

    private static Rect Bounds(TextLayout layout) =>
        new(layout.Bounds.X, layout.Bounds.Y, Math.Max(1, layout.Bounds.Width), Math.Max(1, layout.Bounds.Height));

    private void DrawShape(DrawingContext dc, ShapeStyle style, Rect frame, RenderItem item, HashSet<string> keys)
    {
        Geometry geometry = style.Kind switch
        {
            SceneShapeKind.RoundedRectangle rounded => new RectangleGeometry(frame,
                Math.Min(rounded.CornerRadius, Math.Min(frame.Width, frame.Height) / 2),
                Math.Min(rounded.CornerRadius, Math.Min(frame.Width, frame.Height) / 2)),
            SceneShapeKind.Ellipse => new EllipseGeometry(frame),
            SceneShapeKind.Path path => PathGeometry(path.Data, frame),
            _ => new RectangleGeometry(frame),
        };
        geometry.Freeze();

        if (style.Fill is SceneFill.Media mediaFill)
            DrawMedia(dc, mediaFill.Id, mediaFill.ScaleMode, frame, item, keys, geometry);
        else if (Painting.FillBrush(style.Fill, frame) is { } brush)
            dc.DrawGeometry(brush, null, geometry);

        if (style.Stroke is { Width: > 0 } stroke)
        {
            var pen = new Pen(Painting.Brush(stroke.Color), stroke.Width)
            {
                DashStyle = stroke.Dash switch
                {
                    StrokeDash.Dashed => new DashStyle([3, 2], 0),
                    StrokeDash.Dotted => new DashStyle([0, 2], 0),
                    _ => DashStyles.Solid,
                },
                DashCap = stroke.Dash == StrokeDash.Dotted ? PenLineCap.Round : PenLineCap.Flat,
                LineJoin = PenLineJoin.Miter,
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }

    private static Geometry PathGeometry(string data, Rect frame)
    {
        if (string.IsNullOrWhiteSpace(data)) return new RectangleGeometry(frame);
        try
        {
            var geometry = Geometry.Parse(data);
            geometry.Transform = new TranslateTransform(frame.X, frame.Y);
            return geometry;
        }
        catch
        {
            return new RectangleGeometry(frame);
        }
    }

    private void DrawMedia(DrawingContext dc, string mediaId, SceneMediaScaleMode mode, Rect frame, RenderItem item, HashSet<string> keys, Geometry clip)
    {
        if (media is null || frame.Width <= 0 || frame.Height <= 0) return;
        dc.PushClip(clip);
        try
        {
            switch (media.KindOf(mediaId))
            {
                case Core.Model.MediaKind.Image:
                    var pixelWidth = (int)Math.Ceiling(frame.Width * Math.Max(1, RenderScale()));
                    if (media.Image(mediaId, pixelWidth) is { } image)
                        dc.DrawImage(image, Place(new Size(image.Width, image.Height), frame, mode));
                    break;
                case Core.Model.MediaKind.Video when PlaysVideo:
                    var key = $"{PlaybackScope}:{item.Id}:{mediaId}";
                    var player = media.Video(key, mediaId, item.Loops, IsAudible(item.Id));
                    keys.Add(key);
                    if (player is null || media.IsVideoFailed(key))
                    {
                        DrawPlaceholder(dc, frame, media.NameOf(mediaId), failed: true);
                        break;
                    }
                    var natural = media.NaturalSize(mediaId) ?? frame.Size;
                    dc.DrawVideo(player, Place(natural, frame, mode));
                    break;
                case Core.Model.MediaKind.Video:
                    DrawPlaceholder(dc, frame, media.NameOf(mediaId), failed: false);
                    break;
                default:
                    DrawPlaceholder(dc, frame, "Missing media", failed: true);
                    break;
            }
        }
        finally
        {
            dc.Pop();
        }
    }

    private double RenderScale()
    {
        var canvas = current?.Scene.CanvasSize ?? SceneSize.Hd;
        var size = RenderSize;
        if (size.Width <= 0) return 1;
        return Math.Min(size.Width / canvas.Width, size.Height / canvas.Height) * VisualTreeHelper.GetDpi(this).DpiScaleX;
    }

    private static readonly Brush PlaceholderFill = Frozen(new LinearGradientBrush(Color.FromRgb(0x1E, 0x29, 0x3B), Color.FromRgb(0x0B, 0x12, 0x20), 90));
    private static readonly Brush FailedFill = Frozen(new SolidColorBrush(Color.FromRgb(0x3B, 0x1E, 0x1E)));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static void DrawPlaceholder(DrawingContext dc, Rect frame, string? name, bool failed)
    {
        dc.DrawRectangle(failed ? FailedFill : PlaceholderFill, null, frame);
        var size = Math.Max(12, Math.Min(frame.Width, frame.Height) * 0.08);
        var label = new FormattedText((failed ? "⚠ " : "▶ ") + (name ?? "Video"), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, Brushes.White, 1.0)
        { MaxTextWidth = Math.Max(1, frame.Width * 0.9), MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
        dc.PushOpacity(0.7);
        dc.DrawText(label, new Point(frame.X + frame.Width * 0.05, frame.Y + (frame.Height - label.Height) / 2));
        dc.Pop();
    }

    /// <summary>Where content of <paramref name="natural"/> size lands in <paramref name="frame"/> for a scale mode.</summary>
    public static Rect Place(Size natural, Rect frame, SceneMediaScaleMode mode)
    {
        if (mode == SceneMediaScaleMode.Stretch || natural.Width <= 0 || natural.Height <= 0) return frame;
        var scale = mode == SceneMediaScaleMode.Fit
            ? Math.Min(frame.Width / natural.Width, frame.Height / natural.Height)
            : Math.Max(frame.Width / natural.Width, frame.Height / natural.Height);
        var w = natural.Width * scale;
        var h = natural.Height * scale;
        return new Rect(frame.X + (frame.Width - w) / 2, frame.Y + (frame.Height - h) / 2, w, h);
    }
}
