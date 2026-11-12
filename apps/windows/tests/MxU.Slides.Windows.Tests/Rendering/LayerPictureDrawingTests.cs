using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using ViewTransition = MxU.Slides.Windows.Rendering.SceneTransition;

namespace MxU.Slides.Windows.Tests.Rendering;

// A layer drawn as one picture (SceneView.Layers.cs): a Screen Map placement and a dissolve's two stacks.
public class LayerPictureDrawingTests
{
    private static void OnWpfThread(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "drawing didn't finish in two minutes");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static byte[] Drawn(AnimatedScene scene, int width, int height)
    {
        var view = new SceneView { PlaysVideo = false, Width = width, Height = height };
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.Present(scene, ViewTransition.Cut);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static (byte R, byte G, byte B) At(byte[] pixels, int width, int x, int y)
    {
        var i = (y * width + x) * 4;
        return (pixels[i + 2], pixels[i + 1], pixels[i]);
    }

    private static RenderItem Block(string id, SceneRect frame, SceneColor color) => new()
    {
        Id = id, Frame = frame, Content = new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect, new SceneFill.Solid(color))),
    };

    [Fact]
    public void APlacedLayerDrawsItsRoomPictureIntoItsFrame()
    {
        OnWpfThread(() =>
        {
            // The room picture: red on its left half, green on its right. Placed across two screens
            // (frame 0..2 screens wide, filling), this screen is the left one: it shows red only.
            var scene = new AnimatedScene(new SceneSize(160, 90), SceneColor.Black);
            scene.AddItem(Block("left", new SceneRect(0, 0, 80, 90), new SceneColor(1, 0, 0)), LayerKind.Slide);
            scene.AddItem(Block("right", new SceneRect(80, 0, 80, 90), new SceneColor(0, 1, 0)), LayerKind.Slide);
            scene[LayerKind.Slide].Placed = new PlacedLayer(new SceneSize(160, 90), [new PlacedLayer.Frame(new SceneRect(0, 0, 320, 90), Fill: true)]);
            var pixels = Drawn(scene, 160, 90);
            var (r, g, _) = At(pixels, 160, 120, 45);
            Assert.True(r > 200 && g < 60, $"the left screen shows the picture's left half ({r},{g})");
        });
    }

    [Fact]
    public void ADissolveMixesTheTwoStacksAsPictures()
    {
        OnWpfThread(() =>
        {
            var old = new AnimatedScene(new SceneSize(160, 90), SceneColor.Black);
            old.AddItem(Block("backdrop", new SceneRect(0, 0, 160, 90), SceneColor.White), LayerKind.Slide);
            old.AddItem(Block("picture", new SceneRect(0, 0, 160, 90), new SceneColor(1, 0, 0)), LayerKind.Slide);
            var engine = new SceneTransitionEngine(old);
            engine.Push(new AnimatedScene(new SceneSize(160, 90), SceneColor.Black), new MxU.Slides.Core.Animation.SceneTransition(SceneTransitionKind.Dissolve, 1), now: 0);
            var (r, g, b) = At(Drawn(engine.Scene(0.5), 160, 90), 160, 80, 45);
            Assert.True(Math.Abs(r - 128) < 24 && g < 20 && b < 20, $"half the picture, none of the backdrop ({r},{g},{b})");
        });
    }
}
