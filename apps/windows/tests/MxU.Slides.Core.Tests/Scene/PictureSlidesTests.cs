using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Scene;

// Ports of SlideSceneTests/PhotoSpotSceneTests.swift, the cue half of PictureVideoSceneTests.swift
// and RenderEngineTests/MediaLooksTests.swift (photo spots, Scale and Blur, video end fades).
public class PictureSlidesTests
{
    private static SlideObject Spot(string id, double x, double y, double w = 400, double h = 300) => new()
    {
        Id = id, ObjectKind = SlideObjectKind.Shape, Name = $"Shape {id}", Text = "", X = x, Y = y, Width = w, Height = h,
        ShapeKind = ShapeKind.RoundedRectangle, CornerRadius = 12, Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#333333FF" },
        Stroke = new ObjectStroke { ColorHex = "#FFFFFFFF", Width = 4 }, PhotoSpot = true,
    };

    private static Slide Design() => new()
    {
        Id = "d", Name = "3 Photos",
        Objects =
        [
            Spot("right", 1000, 100), Spot("low", 100, 700), Spot("left", 100, 120),
            new SlideObject { Id = "plate", ObjectKind = SlideObjectKind.Shape, Name = "Plate", Text = "", X = 0, Y = 0, Width = 1920, Height = 80 },
        ],
    };

    [Fact]
    public void PicturesFillTheSpotsInReadingOrder()
    {
        var design = Design();
        Assert.Equal(["left", "right", "low"], design.PhotoSpotsOf().Select(o => o.Id));
        var first = PhotoSpots.Photo("m1", MediaScaleMode.ScaleAndBlur);
        var second = PhotoSpots.Photo("m2", MediaScaleMode.Fit);
        var stack = SlideSceneBuilder.ComposedStack([first, second], design);
        var photos = stack.Where(e => !e.FromTheme).Select(e => e.Object).ToDictionary(o => o.Id);
        Assert.Equal(new[] { first.Id, second.Id }.Order(StringComparer.Ordinal), photos.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((100.0, 120.0), (photos[first.Id].X, photos[first.Id].Y)); // the first picture takes the left spot
        Assert.Equal(1000, photos[second.Id].X); // the second takes the right one
        Assert.Equal("m1", photos[first.Id].Fill?.MediaId);
        Assert.Equal(4, photos[first.Id].Stroke?.Width);
        Assert.Equal(12, photos[first.Id].CornerRadius);
        Assert.DoesNotContain(stack, e => e.Object.Id == "low");
        Assert.Contains(stack, e => e.FromTheme && e.Object.Id == "plate");
    }

    [Fact]
    public void APictureMovedByHandKeepsItsFrame()
    {
        var photo = PhotoSpots.Photo("m1", null);
        (photo.X, photo.Y, photo.Width, photo.Height) = (5, 6, 7, 8);
        var merged = SlideSceneBuilder.ComposedStack([photo], Design()).First(e => !e.FromTheme).Object;
        Assert.Equal((5.0, 8.0), (merged.X, merged.Height));
    }

    [Fact]
    public void AFilledSpotDrawsAsAMediaFilledShape()
    {
        var slide = new Slide { Id = "s", Name = "", Objects = [PhotoSpots.Photo("m1", MediaScaleMode.Fit)], ThemeSlideName = "3 Photos" };
        var theme = new Theme { Id = "t", Name = "T", FontFamily = "Helvetica", FontSize = 72, TextColorHex = "#FFFFFFFF", BackgroundColorHex = "#000000FF", Slides = [Design()] };
        var items = SlideSceneBuilder.Scene(slide, theme)[LayerKind.Slide].Items;
        var shape = items.First(i => i.Content is ItemContent.Shape { Style.Fill: SceneFill.Media });
        Assert.Equal(new SceneRect(100, 120, 400, 300), shape.Frame);
        var fill = Assert.IsType<SceneFill.Media>(((ItemContent.Shape)shape.Content).Style.Fill);
        Assert.Equal(("m1", SceneMediaScaleMode.Fit), (fill.Id, fill.ScaleMode));
    }

    [Fact]
    public void ACueCarriesItsScaleAndEndFade()
    {
        var cue = new CueMedia { MediaId = "v", Layer = CueMediaLayer.Videos, ScaleMode = MediaScaleMode.ScaleAndBlur, EndFade = MediaEndFade.Black };
        var item = SlideSceneBuilder.MediaItem("x", cue, SceneSize.Hd);
        Assert.Equal(SceneMediaScaleMode.ScaleAndBlur, Assert.IsType<ItemContent.Media>(item.Content).ScaleMode);
        Assert.Equal(MediaEndFade.Black, item.EndFade);
        Assert.Equal(SceneMediaScaleMode.Fill, Assert.IsType<ItemContent.Media>(SlideSceneBuilder.MediaItem("y", new CueMedia { MediaId = "v" }, SceneSize.Hd).Content).ScaleMode);
    }

    // MARK: MediaLooks

    private static readonly SceneSize Canvas = SceneSize.Hd;

    private static AnimatedScene SceneOf(RenderItem item)
    {
        var scene = new AnimatedScene(Canvas);
        scene.AddItem(item, LayerKind.StillGraphics);
        return scene;
    }

    private static List<RenderItem> Items(AnimatedScene scene) => [.. scene.AllItems.Select(i => i.Render)];

    [Fact]
    public void ScaleAndBlurIsABlurredFillUnderTheFitPicture()
    {
        var item = new RenderItem { Id = "p", Frame = new SceneRect(Canvas), Content = new ItemContent.Media("m", SceneMediaScaleMode.ScaleAndBlur) };
        var output = Items(MediaLooks.Expanding(SceneOf(item), _ => null));
        Assert.Equal(["p#scalebg", "p"], output.Select(i => i.Id));
        Assert.Equal(new ItemContent.Media("m", SceneMediaScaleMode.Fill, MediaLooks.Zoomed(null)), output[0].Content);
        Assert.Equal(48, output[0].BlurRadius, 1e-9);
        // Windows only (the Mac keeps item.Frame): grown by the blur past the canvas, so WPF's faded rim isn't seen.
        Assert.Equal(new SceneRect(-48, -48, Canvas.Width + 96, Canvas.Height + 96), output[0].Frame);
        Assert.Equal(item.Frame, output[1].Frame);
        // A side that nothing clips stays where it is: the blur mustn't spill over the slide.
        Assert.Equal(new SceneRect(100, -48, 400, 348), MediaLooks.Spread(new SceneRect(100, 0, 400, 300), 48, Canvas));
        var zoom = MediaLooks.Zoomed(new SceneSourceRect(0.5, 0, 0.5, 1));
        Assert.Equal(0.5 / MediaLooks.BackdropZoom, zoom.Width, 1e-9);
        Assert.Equal(0.75, zoom.X + zoom.Width / 2, 1e-9);
        Assert.Equal(new ItemContent.Media("m", SceneMediaScaleMode.Fit), output[1].Content);
    }

    [Fact]
    public void ASceneWithNothingToExpandIsUntouched()
    {
        var plain = SceneOf(new RenderItem { Id = "p", Frame = default, Content = new ItemContent.Media("m", SceneMediaScaleMode.Fill) });
        Assert.Same(plain, MediaLooks.Expanding(plain, _ => 0));
    }

    [Fact]
    public void TheEndFadeRunsOverTheLastSecond()
    {
        Assert.Equal(0, MediaLooks.EndFadeAmount(null));
        Assert.Equal(0, MediaLooks.EndFadeAmount(5));
        Assert.Equal(0.75, MediaLooks.EndFadeAmount(0.25));
        Assert.Equal(1, MediaLooks.EndFadeAmount(0));
    }

    [Fact]
    public void FadeToClearFadesTheVideoOut()
    {
        var item = new RenderItem { Id = "v", Frame = new SceneRect(Canvas), Content = new ItemContent.Media("m", SceneMediaScaleMode.Fill), EndFade = MediaEndFade.Clear };
        Assert.Equal([1.0], Items(MediaLooks.Expanding(SceneOf(item), _ => 9)).Select(i => i.Opacity));
        var fading = Items(MediaLooks.Expanding(SceneOf(item), _ => 0.5));
        Assert.Equal(0.5, Assert.Single(fading).Opacity);
    }

    [Fact]
    public void FadeToBlackCoversTheVideoAndHolds()
    {
        var item = new RenderItem { Id = "v", Frame = new SceneRect(Canvas), Content = new ItemContent.Media("m", SceneMediaScaleMode.ScaleAndBlur), EndFade = MediaEndFade.Black };
        var ended = Items(MediaLooks.Expanding(SceneOf(item), id => id == "m" ? 0 : null));
        Assert.Equal(["v#scalebg", "v", "v#endfade"], ended.Select(i => i.Id));
        Assert.Equal(1, ended[2].Opacity);
        Assert.Equal(new SceneFill.Solid(SceneColor.Black), Assert.IsType<ItemContent.Shape>(ended[2].Content).Style.Fill);
    }

    [Fact]
    public void ScaleAndBlurInAShapeIsClippedToItsOutline()
    {
        var style = new ShapeStyle(new SceneShapeKind.RoundedRectangle(20), new SceneFill.Media("m", SceneMediaScaleMode.ScaleAndBlur), new SceneStroke(SceneColor.White, 4));
        var item = new RenderItem { Id = "spot", Frame = new SceneRect(100, 100, 400, 300), Content = new ItemContent.Shape(style) };
        var output = Items(MediaLooks.Expanding(SceneOf(item), _ => null));
        Assert.Equal(["spot#spotmatte", "spot#scalebg", "spot"], output.Select(i => i.Id));
        Assert.Equal("spot#spotmatte", output[0].MatteGroup);
        Assert.Equal("spot#spotmatte", output[1].MaskedBy);
        var matte = ((ItemContent.Shape)output[0].Content).Style;
        var blurred = ((ItemContent.Shape)output[1].Content).Style;
        var picture = ((ItemContent.Shape)output[2].Content).Style;
        Assert.Equal(new SceneFill.Solid(SceneColor.White), matte.Fill);
        Assert.Null(matte.Stroke);
        Assert.Equal(new SceneFill.Media("m", SceneMediaScaleMode.Fill, MediaLooks.Zoomed(null)), blurred.Fill);
        Assert.Null(blurred.Stroke);
        Assert.Equal(48, output[1].BlurRadius, 1e-9);
        Assert.Equal(new SceneRect(52, 52, 496, 396), output[1].Frame); // grown under the matte (Windows only)
        Assert.Equal(item.Frame, output[0].Frame);
        Assert.Equal(new SceneFill.Media("m", SceneMediaScaleMode.Fit), picture.Fill);
        Assert.NotNull(picture.Stroke); // the picture keeps the border
    }
}
