using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.Views.Transitions;

/// <summary>
/// The two pictures the transition picker's previews (and the self-test's pictures) go between: a
/// deep-blue slide with "Amazing Grace" and a warm sunset one with "How Sweet the Sound", each a
/// background with words over it, as a lyric slide over a background usually is. Both layers change,
/// so a preview shows exactly what the outputs do when a slide brings its own background.
/// </summary>
public static class TransitionSamples
{
    public static AnimatedScene Before(SceneSize? size = null) => Sample(size ?? SceneSize.Hd, "Amazing Grace",
        [new SceneColor(0.04, 0.10, 0.30), new SceneColor(0.10, 0.36, 0.68)], new SceneColor(0.55, 0.80, 1.0, 0.85), left: true);

    public static AnimatedScene After(SceneSize? size = null) => Sample(size ?? SceneSize.Hd, "How Sweet the Sound",
        [new SceneColor(0.42, 0.10, 0.36), new SceneColor(0.98, 0.52, 0.22)], new SceneColor(1.0, 0.86, 0.45, 0.9), left: false);

    private static AnimatedScene Sample(SceneSize size, string words, SceneColor[] sky, SceneColor sun, bool left)
    {
        var scene = new AnimatedScene(size);
        var w = size.Width;
        var h = size.Height;
        var id = left ? "before" : "after";
        scene.AddItem(new RenderItem
        {
            Id = $"sample-{id}-sky",
            Frame = new SceneRect(size),
            Content = new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect,
                new SceneFill.LinearGradient(left ? 70 : 110, [new SceneGradientStop(sky[0], 0), new SceneGradientStop(sky[1], 1)]))),
        }, LayerKind.StillGraphics);
        // A bright disc and a band, so a move, a zoom or a turn is easy to follow.
        scene.AddItem(new RenderItem
        {
            Id = $"sample-{id}-sun",
            Frame = new SceneRect(left ? w * 0.08 : w * 0.66, h * 0.1, h * 0.38, h * 0.38),
            Content = new ItemContent.Shape(new ShapeStyle(new SceneShapeKind.Ellipse(), new SceneFill.Solid(sun))),
        }, LayerKind.StillGraphics);
        scene.AddItem(new RenderItem
        {
            Id = $"sample-{id}-band",
            Frame = new SceneRect(0, h * 0.78, w, h * 0.06),
            Content = new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect, new SceneFill.Solid(new SceneColor(1, 1, 1, 0.18)))),
        }, LayerKind.StillGraphics);
        scene.AddItem(new RenderItem
        {
            Id = $"sample-{id}-words",
            Frame = new SceneRect(w * 0.08, h * 0.42, w * 0.84, h * 0.3),
            Content = new ItemContent.Text(new StyledText
            {
                String = words, FontName = "Segoe UI Semibold", FontSize = h * 0.12, Color = SceneColor.White,
                Shadow = new TextShadow(new SceneColor(0, 0, 0, 0.45), h * 0.02, 0, h * 0.008),
            }),
        }, LayerKind.Slide);
        return scene;
    }
}
