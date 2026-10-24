using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Scene;

// Ported from SlideScene/Tests/SlideSceneBuilderTests.swift: live inputs and screen mirrors as an
// object's fill (a picture-in-picture camera on a slide).
public class LiveInputFillTests
{
    private static ShapeStyle ShapeOf(SlideObject o) => Assert.IsType<ItemContent.Shape>(SlideSceneBuilder.RenderItemFor(o, null).Content).Style;

    [Fact]
    public void ShapeLiveVideoFillMapsToInputEngineId()
    {
        var o = new SlideObject
        {
            Id = "shape", ObjectKind = SlideObjectKind.Shape, Name = "PIP", Text = "", ShapeKind = ShapeKind.RoundedRectangle,
            Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = "clip", CaptureSourceKind = CaptureSourceKind.Camera, CaptureSourceId = "cam-1" },
        };
        Assert.Equal(new SceneFill.Media("input::camera::cam-1", SceneMediaScaleMode.Fill), ShapeOf(o).Fill);
        Assert.Equal("input::camera::cam-1", SlideSceneBuilder.FillLiveInputId(o));
        Assert.Null(SlideSceneBuilder.FillMediaId(o)); // the library want-list must not open input ids
    }

    [Fact]
    public void ScreenMirrorWinsOverCaptureOverMedia()
    {
        var shape = new SlideObject
        {
            Id = "shape", ObjectKind = SlideObjectKind.Shape, Name = "Mirror", Text = "", ShapeKind = ShapeKind.Rectangle,
            Fill = new ObjectFill
            {
                FillKind = FillKind.Media, MediaId = "clip", MediaScaleMode = MediaScaleMode.Fit,
                CaptureSourceKind = CaptureSourceKind.Camera, CaptureSourceId = "cam-1", ScreenSourceId = "screen-a",
            },
        };
        Assert.Equal(new SceneFill.Media("screen::screen-a", SceneMediaScaleMode.Fit), ShapeOf(shape).Fill);
        Assert.Equal("screen::screen-a", SlideSceneBuilder.FillScreenMirrorId(shape));
        Assert.Null(SlideSceneBuilder.FillLiveInputId(shape)); // screen wins over capture
        Assert.Null(SlideSceneBuilder.FillMediaId(shape)); // the library want-list must not open screen ids

        var input = new SlideObject
        {
            Id = "live", ObjectKind = SlideObjectKind.LiveInput, Name = "Mon", Text = "",
            CaptureSourceKind = CaptureSourceKind.Camera, CaptureSourceId = "cam-1", ScreenSourceId = "screen-b",
        };
        Assert.Equal(new SceneFill.Media("screen::screen-b", SceneMediaScaleMode.Fill), ShapeOf(input).Fill);
        Assert.Equal("screen::screen-b", SlideSceneBuilder.FillScreenMirrorId(input));
    }

    [Fact]
    public void MediaWantsCollectScreenMirrors()
    {
        var mirrorObject = new SlideObject { Id = "m", ObjectKind = SlideObjectKind.Media, Name = "Mon", Text = "", MediaId = "media-1", ScreenSourceId = "screen-a" };
        var mirrorFill = new SlideObject
        {
            Id = "s", ObjectKind = SlideObjectKind.Shape, Name = "Badge", Text = "",
            Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = "media-2", ScreenSourceId = "screen-b" },
        };
        var wants = SlideSceneBuilder.MediaWants([mirrorObject, mirrorFill]);
        Assert.Contains("screen::screen-a", wants.Keys);
        Assert.Contains("screen::screen-b", wants.Keys);
        Assert.DoesNotContain("media-1", wants.Keys);
        Assert.DoesNotContain("media-2", wants.Keys);
    }

    [Fact]
    public void NamedInputWinsOverLegacyDeviceReference()
    {
        var o = new SlideObject { Id = "live", ObjectKind = SlideObjectKind.LiveInput, Name = "Cam", Text = "", CaptureSourceKind = CaptureSourceKind.Camera, CaptureSourceId = "cam-1" };
        Assert.Equal("input::camera::cam-1", SlideSceneBuilder.LiveInputEngineId(o));
        o.LiveInputId = "item-1";
        Assert.Equal("input::item::item-1", SlideSceneBuilder.LiveInputEngineId(o));
        var wants = SlideSceneBuilder.MediaWants([o]);
        Assert.Contains("input::item::item-1", wants.Keys);
        Assert.DoesNotContain("input::camera::cam-1", wants.Keys);
    }

    [Fact]
    public void NamedInputFillResolvesAndSuppressesLibraryWant()
    {
        var fill = new ObjectFill { FillKind = FillKind.Media, MediaId = "clip", LiveInputId = "item-2" };
        var shape = new SlideObject { Id = "shape", ObjectKind = SlideObjectKind.Shape, Name = "Fill", Text = "", ShapeKind = ShapeKind.Rectangle, Fill = fill };
        Assert.Equal(new SceneFill.Media("input::item::item-2", SceneMediaScaleMode.Fill), ShapeOf(shape).Fill);
        Assert.Equal("input::item::item-2", SlideSceneBuilder.FillLiveInputId(shape));
        Assert.Null(SlideSceneBuilder.FillMediaId(shape));

        fill.ScreenSourceId = "screen-a";
        Assert.Null(SlideSceneBuilder.FillLiveInputId(shape)); // screen wins over a named input
    }

    [Fact]
    public void UnassignedLiveInputObjectStillWantsItsItemId()
    {
        var o = new SlideObject { Id = "live", ObjectKind = SlideObjectKind.LiveInput, Name = "Cam", Text = "", LiveInputId = "item-3" };
        Assert.Contains("input::item::item-3", SlideSceneBuilder.MediaWants([o]).Keys);
    }

    [Fact]
    public void FullyUnassignedLiveInputRendersPlaceholderButWantsNothing()
    {
        var o = new SlideObject { Id = "live", ObjectKind = SlideObjectKind.LiveInput, Name = "Live Input", Text = "" };
        Assert.Equal(new SceneFill.Media("input::camera::", SceneMediaScaleMode.Fill), ShapeOf(o).Fill);
        Assert.Empty(SlideSceneBuilder.MediaWants([o]));
    }

    [Fact]
    public void ALiveInputFillReachesTheSlideScene()
    {
        // The whole way: a picture-in-picture camera on a slide is a media fill in the scene, which
        // the renderer draws from the input's newest frame.
        var pip = new SlideObject
        {
            Id = "pip", ObjectKind = SlideObjectKind.Shape, Name = "Camera", Text = "", X = 1200, Y = 600, Width = 640, Height = 360,
            Fill = new ObjectFill { FillKind = FillKind.Media, LiveInputId = "pulpit" },
        };
        var scene = SlideSceneBuilder.Scene(new Slide { Id = "s", Name = "1", Objects = [pip] }, null);
        Assert.Contains("input::item::pulpit", scene.MediaIds);
    }
}
