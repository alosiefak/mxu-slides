using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Tests.Outputs.OutputFixtures;

namespace MxU.Slides.Core.Tests.Outputs;

// Ported from RenderEngineTests/OutputScalingTests.swift, plus the same cases for AnimatedScene.
public class OutputScalingTests
{
    private static readonly SceneSize FourThree = new(1024, 768);
    private static readonly SceneSize Wide = new(1920, 1080);

    private static RenderScene MakeScene()
    {
        var scene = new RenderScene(FourThree);
        var full = new SceneRect(FourThree);
        scene.AddItem(new RenderItem { Id = "bg", Frame = full, Content = new ItemContent.Media("loop", SceneMediaScaleMode.Fill) }, LayerKind.LoopingVideos);
        scene.AddItem(Solid("s-backdrop", full, SceneColor.White), LayerKind.Slide);
        scene.AddItem(Solid("lyric", new SceneRect(100, 600, 824, 100), SceneColor.White), LayerKind.Slide);
        scene.AddItem(new RenderItem { Id = "still", Frame = full, Content = new ItemContent.Media("png", SceneMediaScaleMode.Fit) }, LayerKind.StillGraphics);
        return scene;
    }

    private static SceneRect? Frame(string id, RenderScene scene) => scene.AllItems.FirstOrDefault(item => item.Id == id)?.Frame;

    private static SceneRect? Frame(string id, AnimatedScene scene) => scene.AllItems.FirstOrDefault(item => item.Id == id)?.Render.Frame;

    [Fact]
    public void FitGrowsTheCanvasAndBackgroundsCoverIt()
    {
        var fitted = MakeScene().Scaled(Wide, OutputScaleMode.Fit);
        Assert.Equal(768, fitted.CanvasSize.Height);
        Assert.Equal(768.0 * 16 / 9, fitted.CanvasSize.Width, 0.001);
        var grown = new SceneRect(fitted.CanvasSize);

        Assert.Equal(grown, Frame("bg", fitted));
        Assert.Equal(grown, Frame("s-backdrop", fitted));

        var dx = (fitted.CanvasSize.Width - 1024) / 2;
        Assert.Equal(new SceneRect(100 + dx, 600, 824, 100), Frame("lyric", fitted));
        Assert.Equal(new SceneRect(dx, 0, 1024, 768), Frame("still", fitted)); // a still graphic keeps its shape
    }

    [Fact]
    public void FillCropsTheMiddle()
    {
        var filled = MakeScene().Scaled(Wide, OutputScaleMode.Fill);
        Assert.Equal(1024, filled.CanvasSize.Width);
        Assert.Equal(576, filled.CanvasSize.Height, 0.001);
        Assert.Equal(new SceneRect(100, 504, 824, 100), Frame("lyric", filled));
        Assert.Equal(new SceneRect(0, -96, 1024, 768), Frame("bg", filled));
    }

    [Fact]
    public void TallScreenGrowsTheCanvasDown()
    {
        var fitted = MakeScene().Scaled(new SceneSize(1080, 1920), OutputScaleMode.Fit);
        Assert.Equal(1024, fitted.CanvasSize.Width);
        Assert.Equal(1024.0 * 16 / 9, fitted.CanvasSize.Height, 0.001);
        Assert.Equal(100, Frame("lyric", fitted)?.X);
    }

    [Fact]
    public void SameShapeIsUnchanged()
    {
        var original = MakeScene();
        Assert.Same(original, original.Scaled(new SceneSize(2048, 1536), OutputScaleMode.Fill));
        Assert.Same(original, original.Scaled(new SceneSize(0, 0), OutputScaleMode.Fit));
    }

    [Fact]
    public void ScalingLeavesTheOriginalAndItsBackgroundAlone()
    {
        var original = MakeScene();
        original.Background = new SceneColor(0.1, 0.2, 0.3);
        var fitted = original.Scaled(Wide, OutputScaleMode.Fit);
        Assert.Equal(original.Background, fitted.Background);
        Assert.Equal(FourThree, original.CanvasSize);
        Assert.Equal(new SceneRect(FourThree), Frame("bg", original));
    }

    // ── AnimatedScene ───────────────────────────────────────────────────────

    private static AnimatedScene MakeAnimated()
    {
        var animated = AnimatedScene.From(MakeScene());
        animated[LayerKind.Alerts].IsHidden = true;
        return animated;
    }

    [Fact]
    public void AnimatedFitMovesItemsAndGrowsBackgrounds()
    {
        var fitted = MakeAnimated().Scaled(Wide, OutputScaleMode.Fit);
        var grown = new SceneRect(fitted.CanvasSize);
        var dx = (fitted.CanvasSize.Width - 1024) / 2;
        Assert.Equal(grown, Frame("bg", fitted));
        Assert.Equal(grown, Frame("s-backdrop", fitted));
        Assert.Equal(new SceneRect(100 + dx, 600, 824, 100), Frame("lyric", fitted));
        Assert.True(fitted[LayerKind.Alerts].IsHidden); // hidden layers stay hidden
    }

    [Fact]
    public void AnimatedFillAndSameShape()
    {
        var animated = MakeAnimated();
        var filled = animated.Scaled(Wide, OutputScaleMode.Fill);
        Assert.Equal(new SceneSize(1024, 576), new SceneSize(filled.CanvasSize.Width, Math.Round(filled.CanvasSize.Height, 6)));
        Assert.Equal(new SceneRect(0, -96, 1024, 768), Frame("bg", filled));
        Assert.Same(animated, animated.Scaled(new SceneSize(4, 3), OutputScaleMode.Fit));
    }

    [Fact]
    public void ScaleModeRawNamesMatchTheMac()
    {
        Assert.Equal("fit", OutputScaleMode.Fit.Raw());
        Assert.Equal(OutputScaleMode.Fill, OutputScaleModes.Parse("fill"));
        Assert.Null(OutputScaleModes.Parse("stretch"));
        Assert.True(OutputScaling.Bleeds("x", LayerKind.LoopingVideos));
        Assert.False(OutputScaling.Bleeds("x-backdrop", LayerKind.StillGraphics));
    }
}
