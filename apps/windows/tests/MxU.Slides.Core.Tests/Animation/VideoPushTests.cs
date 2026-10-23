using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Animation;

// Port of RenderEngineTests/VideoPushTests.swift (the Metal pixel case keeps its clip assertions).
public class VideoPushTests
{
    private static readonly SceneSize Canvas = SceneSize.Hd;

    private static ItemContent Solid(SceneColor color) => new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect, new SceneFill.Solid(color)));

    private static AnimatedItem Panel(
        string id = "panel", SceneVideoPush? push = null, SceneVideoPush? exitPush = null, AnimationContext? context = null)
    {
        List<SceneAnimationStep> steps =
        [
            new() { Id = $"{id}-in", Kind = SceneAnimationKind.Enter, Animation = SceneStepAnimation.Move, Group = SceneAnimationGroup.Auto, Duration = 1, Ramp = SceneAnimationRamp.None, Edge = SceneAnimationEdge.Right, VideoPush = push },
        ];
        if (exitPush is not null)
            steps.Add(new() { Id = $"{id}-out", Kind = SceneAnimationKind.Exit, Animation = SceneStepAnimation.Move, Group = SceneAnimationGroup.Exit, Duration = 1, Ramp = SceneAnimationRamp.None, Edge = SceneAnimationEdge.Right, VideoPush = exitPush });
        return new AnimatedItem
        {
            Render = new RenderItem { Id = id, Frame = new SceneRect(1280, 0, 640, 1080), Content = Solid(SceneColor.White) },
            AnimationSteps = steps,
            AnimationContext = context ?? new AnimationContext(0),
        };
    }

    private static AnimatedItem Video(string id = "cam") =>
        new RenderItem { Id = id, Frame = new SceneRect(Canvas), Content = Solid(new SceneColor(1, 0, 0)) };

    [Fact]
    public void FreeRectPicksTheLargestSideBand()
    {
        Assert.Equal(new SceneRect(0, 0, 1280, 1080), VideoPushEvaluator.FreeRect(new SceneRect(1280, 0, 640, 1080), Canvas, 0)); // side third → the left band
        Assert.Equal(new SceneRect(40, 40, 1840, 680), VideoPushEvaluator.FreeRect(new SceneRect(0, 760, 1920, 240), Canvas, 40)); // lower band → above it, margin inset
        Assert.Equal(new SceneRect(Canvas), VideoPushEvaluator.FreeRect(new SceneRect(Canvas), Canvas, 0)); // full-frame object degrades to the canvas
    }

    [Fact]
    public void TransformFillFitAlignmentAndZoom()
    {
        var target = new SceneRect(0, 0, 1280, 1080);
        var state = new VideoPushEvaluator.State(new SceneVideoPush(), 1, target);

        var fill = VideoPushEvaluator.TransformFor(Canvas, state);
        Assert.Equal(1, fill.Scale, 0.0001);
        Assert.Equal((1280 - 1920) / 2.0, fill.Offset.Dx, 0.001);
        Assert.Equal(target, fill.Clip);

        var left = state with { Push = state.Push with { Alignment = SceneVideoPushAlignment.Left } };
        Assert.Equal(0, VideoPushEvaluator.TransformFor(Canvas, left).Offset.Dx, 0.001); // left keeps the left of the picture

        var fit = state with { Push = state.Push with { Mode = SceneVideoPushMode.Fit } };
        Assert.Equal(1280.0 / 1920.0, VideoPushEvaluator.TransformFor(Canvas, fit).Scale, 0.0001); // fit letterboxes

        var zoomed = state with { Push = state.Push with { Zoom = 1.2 } };
        Assert.Equal(1.2, VideoPushEvaluator.TransformFor(Canvas, zoomed).Scale, 0.0001);

        var mid = state with { Amount = 0.5 };
        Assert.Equal(new SceneRect(0, 0, 1600, 1080), VideoPushEvaluator.TransformFor(Canvas, mid).Clip);

        var blur = state with { Push = new SceneVideoPush(Mode: SceneVideoPushMode.BlurBackground, Zoom: 1.3, BlurRadius: 40), Amount = 0.5 };
        var blurT = VideoPushEvaluator.TransformFor(Canvas, blur);
        Assert.Equal(1.15, blurT.Scale, 0.0001);
        Assert.Null(blurT.Clip);
        Assert.Equal(20, blurT.BlurRadius, 0.001);
    }

    [Fact]
    public void InPushesSettledHoldsOutReturnsAndRecencyWins()
    {
        AnimatedScene Scene(double? dismissAt = null)
        {
            var scene = new AnimatedScene(Canvas);
            scene.AddItem(Video(), LayerKind.VideoInput);
            scene.AddItem(Panel(push: new SceneVideoPush(), exitPush: new SceneVideoPush(),
                context: dismissAt is null ? null : new AnimationContext(0, dismissHostTime: dismissAt)), LayerKind.Overlays);
            return scene;
        }
        MotionClip? Clip(double at, double? dismissAt = null) =>
            AnimationEvaluator.Resolve(Scene(dismissAt), at).Motions.GetValueOrDefault("cam")?.Clip;

        var mid = Clip(0.5)!;
        Assert.Equal(1760.0 / 1920.0, mid.MaxU, 0.001);

        var settled = Clip(10)!;
        Assert.Equal(320.0 / 1920.0, settled.MinU, 0.001);
        Assert.Equal(1600.0 / 1920.0, settled.MaxU, 0.001);

        var returning = Clip(10.5, dismissAt: 10)!;
        Assert.Equal(1760.0 / 1920.0, returning.MaxU, 0.001);

        Assert.Null(Clip(12, dismissAt: 10));

        var two = Scene();
        var second = Panel("late", push: new SceneVideoPush(Margin: 100), context: new AnimationContext(100));
        two.AddItem(second with { Render = second.Render with { Frame = new SceneRect(0, 760, 1920, 240) } }, LayerKind.Slide);
        var winner = AnimationEvaluator.Resolve(two, 200).Motions["cam"].Clip!;
        Assert.Equal(0.7894, winner.MaxV, 0.001); // the later lower-band push owns the picture
    }

    [Fact]
    public void BackdropCloneAndBlurBackground()
    {
        var scene = new AnimatedScene(Canvas);
        scene.AddItem(Video(), LayerKind.VideoInput);
        scene.AddItem(Panel(push: new SceneVideoPush(Zoom: 1.05, Backdrop: true, BlurRadius: 30)), LayerKind.Overlays);
        var resolved = AnimationEvaluator.Resolve(scene, 10);
        var layer = resolved[LayerKind.VideoInput];
        Assert.Equal(["cam#pushbg", "cam"], layer.Items.Select(r => r.Item.Id)); // the blurred copy rides under the pushed picture
        Assert.Equal(new AnimationEffect.Blur(30), layer.Items[0].Item.Effects[0]);
        Assert.Null(resolved.Motions["cam#pushbg"].Clip); // the backdrop is unclipped
        Assert.NotNull(resolved.Motions["cam"].Clip);

        var blurScene = new AnimatedScene(Canvas);
        blurScene.AddItem(Video(), LayerKind.VideoInput);
        blurScene.AddItem(Panel(push: new SceneVideoPush(Mode: SceneVideoPushMode.BlurBackground, Zoom: 1.25, BlurRadius: 44)), LayerKind.Overlays);
        var blurred = AnimationEvaluator.Resolve(blurScene, 10);
        var cam = blurred[LayerKind.VideoInput].Items[0];
        Assert.Equal(new AnimationEffect.Blur(44), cam.Item.Effects[0]);
        Assert.Equal(1.25, blurred.Motions["cam"].Scale, 0.0001);
        Assert.Null(blurred.Motions["cam"].Clip);
    }

    [Fact]
    public void MidPushClipsTheVideoToTheWalkingWindow()
    {
        // testMidPushRendersTheVideoClippedToTheWalkingWindow, as clip geometry instead of pixels.
        var scene = new AnimatedScene(Canvas);
        scene.AddItem(Video(), LayerKind.VideoInput);
        var ghost = Panel(push: new SceneVideoPush());
        scene.AddItem(ghost with { Render = ghost.Render with { Content = Solid(SceneColor.Clear) } }, LayerKind.Overlays);

        // The visible window in canvas x (the fill push keeps scale 1 here); the Mac samples a 480-wide render.
        (double Left, double Right) Window(AnimationMotion m) => (m.Translate.Dx + m.Clip!.MinU * Canvas.Width, m.Translate.Dx + m.Clip.MaxU * Canvas.Width);
        double Px(double x) => x / 480 * Canvas.Width;

        var settled = Window(AnimationEvaluator.Resolve(scene, 10).Motions["cam"]);
        Assert.InRange(Px(160), settled.Left, settled.Right); // picture inside the window
        Assert.True(Px(350) > settled.Right); // clipped outside the settled window

        var mid = Window(AnimationEvaluator.Resolve(scene, 0.5).Motions["cam"]);
        Assert.InRange(Px(395), mid.Left, mid.Right); // window still wide mid-push
        Assert.True(Px(470) > mid.Right); // already clipped past the walking edge
    }
}
