using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// The overlay editor's pictures: an overlay drawn the way the overlay layer
/// draws it (masks wired, mattes hidden), still or playing its animations. The
/// items are built exactly as <see cref="ShowState.AnimatedScene"/> builds a
/// live overlay's, so what the editor plays is what the audience will see.
/// </summary>
public static class OverlayPreview
{
    /// <summary>The id prefix ShowState gives a live overlay's items ("overlay-&lt;id&gt;-").</summary>
    public static string ItemPrefix(Overlay overlay) => $"overlay-{overlay.Id}-";

    /// <summary>Adds an overlay's items with their animation steps, against <paramref name="context"/> (null: at rest).</summary>
    public static void AddItems(AnimatedScene scene, Overlay overlay, AnimationContext? context, double anchorHostTime, LayerKind? layer = null)
    {
        var target = layer ?? LayerKinds.Parse(overlay.Layer) ?? LayerKind.Overlays;
        var canvas = scene.CanvasSize;
        var wiring = SlideSceneBuilder.MaskWiring(overlay.Objects);
        var steps = AnimationSequence.SceneSteps(overlay.Objects, overlay.AnimationOrder);
        foreach (var o in overlay.Objects)
        {
            var items = SlideSceneBuilder.RenderItems(o, null, null, canvas, ItemPrefix(overlay) + o.Id,
                wiring.MaskedBy.GetValueOrDefault(o.Id), wiring.Mattes.Contains(o.Id) ? o.Id : null);
            foreach (var item in AnimatedItems.For(o, items, steps.GetValueOrDefault(o.Id) ?? [], null, null, canvas,
                         steps.Count == 0 ? null : context, anchorHostTime))
                scene.AddItem(item, target);
        }
    }

    /// <summary>How long the play-through lasts: going up, any clicks, a short hold, then coming down.</summary>
    public static double Duration(Overlay overlay) =>
        AnimationSequence.PreviewTimeline(overlay.Objects, overlay.AnimationOrder).Duration;

    /// <summary>
    /// The overlay playing through from <paramref name="startedAt"/> (a <see cref="ShowState.HostClock"/>
    /// time): its In steps, each click group in turn, a hold, then its Outs. Given
    /// <paramref name="time"/> (seconds into it), the picture is frozen there, for scrubbing.
    /// <paramref name="backdrop"/>'s items go underneath.
    /// </summary>
    public static AnimatedScene PlayThrough(Overlay overlay, SceneSize canvas, double startedAt, double? time = null, RenderScene? backdrop = null)
    {
        var scene = backdrop is null ? new AnimatedScene(canvas) : AnimatedScene.From(backdrop);
        var context = AnimationSequence.PreviewContext(overlay.Objects, overlay.AnimationOrder, time, startedAt);
        AddItems(scene, overlay, context, startedAt);
        return scene;
    }

    /// <summary>
    /// The still picture the editor's canvas shows: the overlay at rest over
    /// <paramref name="backdrop"/>, with its mattes (invisible on outputs) shown as faint
    /// blue shapes when <paramref name="showMattes"/> is on, so they can be found and moved.
    /// </summary>
    public static RenderScene Still(Overlay overlay, RenderScene backdrop, bool showMattes = true)
    {
        var target = LayerKinds.Parse(overlay.Layer) ?? LayerKind.Overlays;
        SlideSceneBuilder.AddObjects(backdrop, overlay.Objects, target, backdrop.CanvasSize, ItemPrefix(overlay));
        if (!showMattes) return backdrop;
        var mattes = SlideSceneBuilder.MaskWiring(overlay.Objects).Mattes;
        foreach (var matte in overlay.Objects.Where(o => mattes.Contains(o.Id) && o.Hidden != true))
            backdrop.AddItem(SlideSceneBuilder.RenderItemFor(Ghost(matte), null, null, backdrop.CanvasSize), target);
        return backdrop;
    }

    /// <summary>A see-through stand-in for a matte: same place and shape, pale blue with a dashed edge.</summary>
    private static SlideObject Ghost(SlideObject matte)
    {
        var frame = SlideSceneBuilder.Frame(matte);
        var shapeKind = matte.ObjectKind == SlideObjectKind.Shape ? matte.ShapeKind : ShapeKind.Rectangle;
        return new SlideObject
        {
            Id = $"{matte.Id}.matte-ghost",
            ObjectKind = SlideObjectKind.Shape,
            Name = matte.Name,
            Text = "",
            X = frame.X, Y = frame.Y, Width = frame.Width, Height = frame.Height,
            RotationDegrees = matte.RotationDegrees,
            ShapeKind = shapeKind,
            CornerRadius = matte.CornerRadius,
            PathData = shapeKind == ShapeKind.Path ? matte.PathData : null,
            Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#60A5FA26" },
            Stroke = new ObjectStroke { ColorHex = "#60A5FAB3", Width = 2, DashKind = StrokeDashKind.Dashed },
        };
    }

    /// <summary>A scene with nothing but a background colour, for the editor's plain backdrops.</summary>
    public static RenderScene Plain(SceneSize canvas, SceneColor color) => new(canvas, color);

    /// <summary>
    /// A copy of the live audience picture without one overlay's items (the editor draws that
    /// overlay itself on top, as you edit it).
    /// </summary>
    public static RenderScene Without(RenderScene live, Overlay overlay)
    {
        var prefix = ItemPrefix(overlay);
        var copy = new RenderScene(live.CanvasSize, live.Background);
        foreach (var layer in live.Layers)
            foreach (var item in layer.Items.Where(i => !i.Id.StartsWith(prefix, StringComparison.Ordinal)))
                copy.AddItem(item, layer.Kind);
        return copy;
    }
}
