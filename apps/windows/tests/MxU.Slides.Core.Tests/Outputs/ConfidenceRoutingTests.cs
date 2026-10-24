using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Outputs;

// Ported from OutputEngineTests/ConfidenceRoutingTests.swift.
public class ConfidenceRoutingTests
{
    private static RenderScene Tagged(double side) => new(new SceneSize(side, side));

    [Fact]
    public void ConfidenceScreensCompositePerScreenScenes()
    {
        var outputs = new OutputConfiguration();
        var band = Guid.NewGuid();
        var speaker = Guid.NewGuid();
        var audience = Guid.NewGuid();
        outputs.SetRole(band, ScreenRole.Confidence);
        outputs.SetRole(speaker, ScreenRole.Confidence);
        outputs.SceneProvider = () => Tagged(100);
        outputs.ConfidenceSceneProvider = screenId => Tagged(screenId == band ? 200 : 300);

        Assert.Equal(200, outputs.PreviewProvider(SwiftUuid.Format(band))().CanvasSize.Width); // the band screen composites ITS layout
        Assert.Equal(300, outputs.PreviewProvider(SwiftUuid.Format(speaker))().CanvasSize.Width); // the speaker screen a DIFFERENT one
        Assert.Equal(100, outputs.PreviewProvider(SwiftUuid.Format(audience))().CanvasSize.Width); // audience targets composite the program
    }

    [Fact]
    public void RoleFlipReachesTheProviderOnItsNextPull()
    {
        var outputs = new OutputConfiguration();
        var screen = Guid.NewGuid();
        outputs.SceneProvider = () => Tagged(100);
        outputs.ConfidenceSceneProvider = _ => Tagged(200);

        var provider = outputs.PreviewProvider(SwiftUuid.Format(screen));
        Assert.Equal(100, provider().CanvasSize.Width);
        outputs.SetRole(screen, ScreenRole.Confidence);
        Assert.Equal(200, provider().CanvasSize.Width); // an already-made provider honours the flip per frame
        outputs.SetRole(screen, ScreenRole.Audience);
        Assert.Equal(100, provider().CanvasSize.Width);
    }

    [Fact]
    public void ScaleModeShapesTheProgramForItsScreen()
    {
        var outputs = new OutputConfiguration();
        outputs.SceneProvider = () => new RenderScene(new SceneSize(1024, 768));
        var screen = outputs.AddScreen("Wide", 192, 108)!;
        var provider = outputs.PreviewProvider(SwiftUuid.Format(screen.Id));
        Assert.Equal(OutputScaleMode.Fit, outputs.ScaleMode(screen.Id));
        Assert.Equal(768, provider().CanvasSize.Height);
        Assert.Equal(768.0 * 16 / 9, provider().CanvasSize.Width, 0.001);
        outputs.SetScaleMode(screen.Id, OutputScaleMode.Fill);
        Assert.Equal(1024, provider().CanvasSize.Width);
        Assert.Equal(576, provider().CanvasSize.Height, 0.001);
        outputs.AddSlice(screen.Id, sourceRect: new SceneRect(0, 0, 0.5, 1));
        Assert.Equal(new SceneSize(1024, 768), provider().CanvasSize); // a spanned screen is never reshaped
        outputs.SetScaleMode(screen.Id, OutputScaleMode.Fit);
        Assert.Empty(outputs.ScaleModes);
        outputs.RemoveScreen(screen.Id);
    }
}
