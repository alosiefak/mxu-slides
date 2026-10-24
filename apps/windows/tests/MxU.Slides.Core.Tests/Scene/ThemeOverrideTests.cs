using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Scene;

// Port of SlideSceneTests/ThemeOverrideTests.swift: a slide's text restyled by a look
// (ThemeOverride.overrideObjects, PlaceholderAnimations.OverrideObjects here). The Mac reads
// bold and italic from Core Text; here PlaceholderAnimations.FontTraits reads them from the name.
public class ThemeOverrideTests
{
    private static readonly Theme OriginalTheme = new()
    {
        Id = "orig", Name = "Original", FontFamily = "Helvetica", FontSize = 60, TextColorHex = "#FFFFFFFF", BackgroundColorHex = "#000000FF",
    };

    private static Theme OverrideTheme(KeepFromSlideOptions? keep = null)
    {
        var placeholder = new SlideObject
        {
            Id = "ph", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = "Sample",
            X = 0, Y = 800, Width = 1920, Height = 280, TextStyle = new TextStyle { FontSize = 42 }, KeepFromSlide = keep,
        };
        return new Theme
        {
            Id = "ov", Name = "Stream Lower Third", FontFamily = "Arial", FontSize = 40, TextColorHex = "#FFFFFFFF", BackgroundColorHex = "#00000000",
            Slides = [new Slide { Id = "ov-lyrics", Name = "Lyrics", Objects = [placeholder] }],
        };
    }

    private static SlideObject LyricsObject(List<TextStyleRun>? runs = null, TextStyle? style = null) => new()
    {
        Id = "lyrics", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = "Amazing grace how sweet the sound",
        X = 100, Y = 100, Width = 800, Height = 400, TextStyle = style, StyleRuns = runs,
    };

    private static SlideObject Transformed(SlideObject o, KeepFromSlideOptions? keep = null)
    {
        var slide = new Slide { Id = "s", Name = "Verse 1", Objects = [o] };
        return PlaceholderAnimations.OverrideObjects(slide, slide.Objects, OriginalTheme, OverrideTheme(keep))[0];
    }

    [Fact]
    public void OverrideStripsGeometryAndBaseStyle()
    {
        var result = Transformed(LyricsObject(style: new TextStyle { FontSize = 80, ColorHex = "#FF0000FF" }));
        Assert.True(result.X is null && result.Y is null && result.Width is null && result.Height is null);
        Assert.Null(result.TextStyle);
        Assert.Null(result.StyleRuns);
    }

    [Fact]
    public void NonTextObjectsPassThroughUntouched()
    {
        var shape = new SlideObject { Id = "pic", ObjectKind = SlideObjectKind.Shape, Name = "Photo", Text = "", X = 10, Y = 20 };
        var slide = new Slide { Id = "s", Name = "", Objects = [shape] };
        Assert.Same(shape, PlaceholderAnimations.OverrideObjects(slide, slide.Objects, OriginalTheme, OverrideTheme())[0]);
    }

    [Fact]
    public void BoldWordCarriesAsTheThemeFontsBoldFace()
    {
        var run = new TextStyleRun { Line = 0, Column = 0, Length = 7, FontName = "Helvetica-Bold" };
        var carried = Assert.Single(Transformed(LyricsObject([run])).StyleRuns!);
        Assert.NotNull(carried.FontName);
        Assert.True(PlaceholderAnimations.FontTraits(carried.FontName!).Bold);
        Assert.DoesNotContain("helvetica", carried.FontName!.ToLowerInvariant());
        Assert.True(carried.Line == 0 && carried.Column == 0 && carried.Length == 7);
        Assert.True(carried.FontSize is null && carried.ColorHex is null);
    }

    [Fact]
    public void AllBoldBaseCarriesNothing()
    {
        var run = new TextStyleRun { Line = 0, Column = 0, Length = 7, FontName = "Helvetica-Bold" };
        Assert.Null(Transformed(LyricsObject([run], new TextStyle { FontName = "Helvetica-Bold" })).StyleRuns);
    }

    [Fact]
    public void BoldToggleOffDropsTheDelta()
    {
        var run = new TextStyleRun { Line = 0, Column = 0, Length = 7, FontName = "Helvetica-Bold" };
        Assert.Null(Transformed(LyricsObject([run]), new KeepFromSlideOptions { Bold = false }).StyleRuns);
    }

    [Fact]
    public void SizeEmphasisCarriesAsARatio()
    {
        var run = new TextStyleRun { Line = 0, Column = 0, Length = 7, FontSize = 80 };
        var carried = Assert.Single(Transformed(LyricsObject([run])).StyleRuns!);
        Assert.Equal(56, carried.FontSize!.Value, 2); // 80/60 of the look's 42
    }

    [Fact]
    public void DecorationsCarryAndColorsDontByDefault()
    {
        var run = new TextStyleRun { Line = 0, Column = 8, Length = 5, Underline = true, ColorHex = "#FF0000FF", HighlightColorHex = "#FFFF00FF" };
        var carried = Assert.Single(Transformed(LyricsObject([run])).StyleRuns!);
        Assert.True(carried.Underline);
        Assert.Null(carried.ColorHex);
        Assert.Null(carried.HighlightColorHex);

        var kept = Assert.Single(Transformed(LyricsObject([run]), new KeepFromSlideOptions { Highlight = true, TextColor = true }).StyleRuns!);
        Assert.Equal("#FF0000FF", kept.ColorHex);
        Assert.Equal("#FFFF00FF", kept.HighlightColorHex);
    }

    [Fact]
    public void WordTrackingCarriesScaledToTheThemeSize()
    {
        var run = new TextStyleRun { Line = 0, Column = 0, Length = 7, Tracking = 10 };
        var carried = Assert.Single(Transformed(LyricsObject([run])).StyleRuns!);
        Assert.Equal(7, carried.Tracking!.Value, 2);
    }

    [Fact]
    public void LineStylesCarryLikeRunsExceptIndentsAndTracking()
    {
        var style = new TextStyle
        {
            LineStyles = [new LineStyleOverride
            {
                LineIndex = 1, FontName = "Helvetica-Bold", FontSize = 90, ColorHex = "#FF0000FF", Tracking = 5, FirstLineIndent = 100, LeftIndent = 50,
            }],
        };
        var result = Transformed(LyricsObject(style: style));
        var carried = Assert.Single(result.TextStyle!.LineStyles!);
        Assert.Equal(1, carried.LineIndex);
        Assert.True(PlaceholderAnimations.FontTraits(carried.FontName ?? "").Bold);
        Assert.Equal(63, carried.FontSize!.Value, 2);
        Assert.Null(carried.ColorHex);
        Assert.Null(carried.Tracking);
        Assert.True(carried.FirstLineIndent is null && carried.LeftIndent is null);
        Assert.True(result.TextStyle.FontName is null && result.TextStyle.FontSize is null);
    }

    [Fact]
    public void VariantSceneReThemesWithStableItemIds()
    {
        var o = LyricsObject([new TextStyleRun { Line = 0, Column = 0, Length = 7, FontName = "Helvetica-Bold" }]);
        var slide = new Slide { Id = "s", Name = "Verse 1", Objects = [o] };
        var presentation = new Presentation { Id = "p", Name = "Song", PresentationKind = PresentationKind.Song, ThemeId = "orig", Slides = [slide] };
        var state = new ShowState();
        state.Fire(slide, presentation, OriginalTheme);

        var baseScene = state.AnimatedScene();
        var variant = state.AnimatedScene(slideThemeOverride: OverrideTheme());
        Assert.Equal(baseScene[LayerKind.Slide].Items.Select(i => i.Id), variant[LayerKind.Slide].Items.Select(i => i.Id));

        var variantItem = variant[LayerKind.Slide].Items.First(i => i.Id == "lyrics");
        Assert.Equal(new SceneRect(0, 800, 1920, 280), variantItem.Render.Frame);
        var styled = Assert.IsType<ItemContent.Text>(variantItem.Render.Content).Styled;
        Assert.Equal("Arial", styled.FontName);
        Assert.Equal(42, styled.FontSize);
        Assert.Single(styled.StyleRuns);
        Assert.Equal(new SceneRect(100, 100, 800, 400), baseScene[LayerKind.Slide].Items.First(i => i.Id == "lyrics").Render.Frame);
    }

    [Fact]
    public void OverrideThemeDecorMediaIsDrawnOnTheLookOnly()
    {
        // overrideThemeDecorMediaStaysWanted: the look's decor media is part of its scene (Windows plays
        // whatever a scene draws, so being drawn is what keeps it wanted).
        var theme = OverrideTheme();
        theme.Slides![0].Objects.Insert(0, new SlideObject
        {
            Id = "strip", ObjectKind = SlideObjectKind.Shape, Name = "Strip", Text = "", Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = "motion-strip" },
        });
        var state = new ShowState();
        state.Fire(new Slide { Id = "s", Name = "Verse 1", Objects = [LyricsObject()] }, theme: OriginalTheme);
        static bool Draws(AnimatedScene scene) => scene.AllItems.Any(i => i.Render.Content is ItemContent.Shape { Style.Fill: SceneFill.Media { Id: "motion-strip" } });
        Assert.False(Draws(state.AnimatedScene()));
        Assert.True(Draws(state.AnimatedScene(slideThemeOverride: theme)));
    }
}
