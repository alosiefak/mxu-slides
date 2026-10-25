using System.Windows;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.Tests.Rendering;

// A picture's crop (a media item's Windows look, or a crop imported from ProPresenter or
// PowerPoint): only the part kept is fitted into the frame, and the rest is clipped away.
public class MediaCropTests
{
    private static readonly Rect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void WithoutACropThePictureIsPlacedAsBefore()
    {
        var (whole, kept) = SceneView.PlaceCropped(new Size(1000, 1000), Screen, SceneMediaScaleMode.Fit, null);
        Assert.Equal(new Rect(420, 0, 1080, 1080), whole);
        Assert.Null(kept);
        Assert.Null(SceneView.PlaceCropped(new Size(1000, 1000), Screen, SceneMediaScaleMode.Fit, new SceneSourceRect(0, 0, 1, 1)).Kept);
    }

    [Fact]
    public void TheKeptPartFillsTheScreenAndTheRestHangsOutside()
    {
        // A 16:9 picture with its left and right quarters cut off: the middle half (8:9) is fitted.
        var (whole, kept) = SceneView.PlaceCropped(new Size(1920, 1080), Screen, SceneMediaScaleMode.Fit, new SceneSourceRect(0.25, 0, 0.5, 1));
        Assert.Equal(new Rect(480, 0, 960, 1080), kept);
        Assert.Equal(new Rect(0, 0, 1920, 1080), whole); // drawn whole, clipped to the kept part
    }

    [Fact]
    public void FillingWithACropZoomsIntoTheKeptPart()
    {
        var (whole, kept) = SceneView.PlaceCropped(new Size(1920, 1080), Screen, SceneMediaScaleMode.Fill, new SceneSourceRect(0.5, 0.5, 0.5, 0.5));
        Assert.Equal(Screen, kept);
        Assert.Equal(new Rect(-1920, -1080, 3840, 2160), whole); // the bottom-right quarter fills the screen
    }
}
