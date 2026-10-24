using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Scene;

// SlideSceneBuilderTests.clippedTextObjectIDsFlagOverflowingBoxes, with the measuring done by a
// stand-in (the real one is WPF's, in TextLayout.Overflows, which the self-test checks on real text).
public class ClippedTextTests
{
    /// <summary>A crude measure: about 0.55 em a character, lines of 1.2 em, wrapping at the box's width.</summary>
    private static bool RoughlyOverflows(StyledText text, SceneSize frame)
    {
        var perLine = Math.Max(1, (int)(frame.Width / (text.FontSize * 0.55)));
        var lines = text.String.Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / (double)perLine)));
        return lines * text.FontSize * 1.2 > frame.Height;
    }

    private static SlideObject Text(string id, string text, double y, double width, double height) => new()
    {
        Id = id, ObjectKind = SlideObjectKind.Text, Name = "T", Text = text, X = 0, Y = y, Width = width, Height = height,
        TextStyle = new TextStyle { FontSize = 96 },
    };

    private static readonly string Long = string.Join("\n", Enumerable.Repeat("Amazing grace how sweet the sound", 12));

    [Fact]
    public void ClippedTextObjectIdsFlagOverflowingBoxes()
    {
        var slide = new Slide { Id = "s", Name = "1", Objects = [Text("big", Long, 0, 400, 100), Text("fits", "Hi", 300, 800, 400)] };
        var scene = SlideSceneBuilder.Scene(slide, null);
        Assert.Equal(["big"], SlideSceneBuilder.ClippedTextObjectIds(slide, scene, RoughlyOverflows));
    }

    [Fact]
    public void TextThatShowsAllItsWordsItsOwnWayIsNeverClipped()
    {
        var onAPath = Text("path", Long, 0, 400, 100);
        onAPath.TextStyle!.PathData = PathPresets.Line;
        var ticker = Text("ticker", Long, 100, 400, 100);
        ticker.TextStyle!.TickerSpeed = 40;
        var credits = Text("credits", Long, 200, 400, 100);
        credits.TextStyle!.Scroll = new BlockScroll { Speed = 30 };
        var slide = new Slide { Id = "s", Name = "1", Objects = [onAPath, ticker, credits] };
        Assert.Empty(SlideSceneBuilder.ClippedTextObjectIds(slide, SlideSceneBuilder.Scene(slide, null), RoughlyOverflows));
    }

    [Fact]
    public void AShapesTextCountsAsTheShapeAndTheDesignsOwnTextDoesnt()
    {
        var band = new SlideObject
        {
            Id = "band", ObjectKind = SlideObjectKind.Shape, Name = "Band", Text = Long, X = 0, Y = 0, Width = 300, Height = 80,
            TextStyle = new TextStyle { FontSize = 96 },
        };
        var design = new Slide
        {
            Id = "d", Name = "Lyrics",
            Objects = [new SlideObject { Id = "decor", ObjectKind = SlideObjectKind.Shape, Name = "Tag", Text = Long, X = 0, Y = 900, Width = 100, Height = 40 }],
        };
        var theme = new Theme { Id = "t", Name = "T", FontFamily = "H", FontSize = 96, TextColorHex = "#FFFFFFFF", BackgroundColorHex = "#000000FF", Slides = [design] };
        var slide = new Slide { Id = "s", Name = "1", ThemeSlideName = "Lyrics", Objects = [band] };
        var scene = SlideSceneBuilder.Scene(slide, theme);
        Assert.Equal(["band"], SlideSceneBuilder.ClippedTextObjectIds(slide, scene, RoughlyOverflows));
    }
}
