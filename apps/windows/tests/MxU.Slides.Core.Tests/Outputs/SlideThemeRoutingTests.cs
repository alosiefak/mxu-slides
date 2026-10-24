using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Tests.Outputs.OutputFixtures;

namespace MxU.Slides.Core.Tests.Outputs;

// Ported from OutputEngineTests/SlideThemeRoutingTests.swift.
public class SlideThemeRoutingTests
{
    private static RenderScene Tagged(double side) => new(new SceneSize(side, side));

    [Fact]
    public void ThemeRoutedTargetCompositesItsVariant()
    {
        var outputs = new OutputConfiguration();
        var stream = SwiftUuid.Format(Guid.NewGuid());
        var projector = SwiftUuid.Format(Guid.NewGuid());
        outputs.SceneProvider = () => Tagged(100);
        outputs.VariantSceneProvider = themeId => themeId == "lower-third" ? Tagged(200) : null;
        outputs.SetSlideThemeRouting(new Dictionary<string, string> { [stream] = "lower-third" });

        Assert.Equal(200, outputs.PreviewProvider(stream)().CanvasSize.Width); // the stream feed composites the variant
        Assert.Equal(100, outputs.PreviewProvider(projector)().CanvasSize.Width); // unrouted outputs keep the base scene

        var provider = outputs.PreviewProvider(stream);
        outputs.SetSlideThemeRouting(new Dictionary<string, string>());
        Assert.Equal(100, provider().CanvasSize.Width); // clearing the routing returns the output to the base scene
    }

    [Fact]
    public void MissingVariantFallsBackToBaseNeverBlank()
    {
        var outputs = new OutputConfiguration();
        var stream = SwiftUuid.Format(Guid.NewGuid());
        outputs.SceneProvider = () => Tagged(100);
        outputs.VariantSceneProvider = _ => null;
        outputs.SetSlideThemeRouting(new Dictionary<string, string> { [stream] = "lower-third" });
        Assert.Equal(100, outputs.PreviewProvider(stream)().CanvasSize.Width);
    }

    [Fact]
    public void LayerRoutingAppliesToTheVariantScene()
    {
        var outputs = new OutputConfiguration();
        var stream = SwiftUuid.Format(Guid.NewGuid());
        var variant = Tagged(200);
        variant.AddItem(Solid("x", default, SceneColor.White), LayerKind.Alerts);
        variant.AddItem(Solid("y", default, SceneColor.White), LayerKind.Slide);
        outputs.SceneProvider = () => Tagged(100);
        outputs.VariantSceneProvider = _ => variant;
        outputs.SetSlideThemeRouting(new Dictionary<string, string> { [stream] = "lower-third" });
        outputs.SetLayerRouting(new Dictionary<string, IReadOnlySet<string>> { [stream] = new HashSet<string> { LayerKind.Slide.Raw() } });

        var composited = outputs.PreviewProvider(stream)();
        Assert.Equal(200, composited.CanvasSize.Width);
        // RenderLayer has no hidden flag on Windows, so non-routed layers come back empty.
        Assert.Empty(composited[LayerKind.Alerts].Items); // non-routed layers hide on the variant exactly like the base
        Assert.Equal("y", Assert.Single(composited[LayerKind.Slide].Items).Id);
    }

    [Fact]
    public void LayerRoutingHidesAnimatedLayers()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Stream", 100, 100)!;
        var key = SwiftUuid.Format(screen.Id);
        outputs.SetLayerRouting(new Dictionary<string, IReadOnlySet<string>> { [key] = new HashSet<string> { LayerKind.Slide.Raw() } });
        var scene = new AnimatedScene(new SceneSize(100, 100));
        scene.AddItem(Solid("y", default, SceneColor.White), LayerKind.Slide);
        var routed = outputs.Program(outputs.Feed(key), scene);
        Assert.True(routed[LayerKind.Alerts].IsHidden);
        Assert.False(routed[LayerKind.Slide].IsHidden);
        Assert.False(scene[LayerKind.Alerts].IsHidden); // the caller's scene is left alone
    }

    [Fact]
    public void SliceTargetsRouteUnderTheirScreen()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Wall", 100, 100)!;
        var slice = outputs.AddSlice(screen.Id, "Lobby")!;
        var feed = outputs.Feed(SwiftUuid.Format(slice.Id).ToLowerInvariant());
        Assert.Equal(screen.Id, feed.ScreenId);
        Assert.Equal(SwiftUuid.Format(screen.Id), feed.RoutingKey);
        Assert.Equal(new SceneSize(100, 100), feed.CanvasSize);
        Assert.Equal(OutputFeedKind.Program, feed.Kind);
        Assert.Equal("display:X", outputs.Feed("display:X").RoutingKey); // a direct display routes by its own id
    }
}
