using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Library;

// Ported from PresenterCore/Tests/PerSlideThemeTests.swift, plus what the Windows renderer does with the answer.
public class PerSlideThemeTests
{
    private static Slide NewSlide(string id) => new() { Id = id, Name = "", Objects = [] };

    /// <summary>NewSlide.blank on the Mac: no objects, following no theme.</summary>
    private static Slide Blank(string id) => new() { Id = id, Name = "", Objects = [], Unthemed = true };

    [Fact]
    public void ASlideFollowsItsOwnThemeElseTheDecks()
    {
        var own = NewSlide("s1");
        own.ThemeId = "scripture";
        var plain = NewSlide("s2");
        var empty = NewSlide("s3");
        empty.ThemeId = "";
        var deck = new Presentation { Id = "p", Name = "Deck", PresentationKind = PresentationKind.Deck, ThemeId = "lyrics", Slides = [own, plain, empty] };

        Assert.Equal("scripture", deck.ThemeIdFor(own));
        Assert.Equal("lyrics", deck.ThemeIdFor(plain));
        Assert.Equal("lyrics", deck.ThemeIdFor(empty)); // an empty own theme is no theme
        Assert.True(deck.HasSlideThemes());

        deck.ClearSlideThemes();
        Assert.False(deck.HasSlideThemes());
        Assert.All(deck.Slides, s => Assert.Null(s.ThemeId));
        Assert.Equal("lyrics", deck.ThemeIdFor(deck.Slides[0]));
    }

    [Fact]
    public void AnOverrideOutputReadsTheSlideThroughItsChosenDesign()
    {
        var slide = NewSlide("s");
        slide.ThemeSlideName = "Scripture";
        var stream = new Theme { Id = "stream", Name = "Stream", FontFamily = "H", FontSize = 60, TextColorHex = "#fff", BackgroundColorHex = "#000" };
        Assert.Equal("Scripture", PlaceholderAnimations.RenderedThroughOverride(slide, stream).ThemeSlideName); // no choice = the same name

        slide.SetOverrideDesign("Scripture Wide", "stream");
        Assert.Equal("Scripture Wide", slide.OverrideDesignName("stream"));
        Assert.Equal("Scripture Wide", PlaceholderAnimations.RenderedThroughOverride(slide, stream).ThemeSlideName);
        Assert.Equal("Scripture", slide.ThemeSlideName); // the projector keeps its own

        slide.SetOverrideDesign("Reference Only", "stream");
        Assert.True(slide.OverrideDesigns?.Count == 1 && slide.OverrideDesignName("stream") == "Reference Only"); // one entry per theme
        slide.SetOverrideDesign(null, "stream");
        Assert.Null(slide.OverrideDesigns);
    }

    [Fact]
    public void ABlankSlideFollowsNoTheme()
    {
        var blank = Blank("b");
        blank.ThemeId = "scripture";
        var deck = new Presentation { Id = "p", Name = "Deck", PresentationKind = PresentationKind.Deck, ThemeId = "lyrics", Slides = [blank] };

        Assert.Equal("", deck.ThemeIdFor(blank)); // blank wins over the deck's and its own
        Assert.True(deck.HasSlideThemes()); // Apply Theme confirms: a slide does not follow the deck
    }

    [Fact]
    public void AMixedDeckDrawsEachSlideWithItsOwnTheme()
    {
        // What the outputs and the grid do with the answer: a scripture slide in a lyric deck
        // takes the scripture theme's font and backdrop; the slide after it the deck's.
        var lyrics = Theme("lyrics", "Avenir-Heavy", "#101010FF");
        var scripture = Theme("scripture", "Georgia", "#203040FF");
        var themes = new Dictionary<string, Theme> { ["lyrics"] = lyrics, ["scripture"] = scripture };
        var verse = new Slide { Id = "v", Name = "", Objects = [Text("t1", "Amazing grace")] };
        var reading = new Slide { Id = "r", Name = "", ThemeId = "scripture", Objects = [Text("t2", "John 3:16")] };
        var blank = new Slide { Id = "b", Name = "", Unthemed = true, Objects = [Text("t3", "Notes")] };
        var deck = new Presentation { Id = "p", Name = "Deck", PresentationKind = PresentationKind.Deck, ThemeId = "lyrics", Slides = [verse, reading, blank] };

        RenderScene SceneOf(Slide s) => SlideSceneBuilder.Scene(s, themes.GetValueOrDefault(deck.ThemeIdFor(s)), deck);
        static string FontOf(RenderScene scene) =>
            scene[LayerKind.Slide].Items.Select(i => i.Content).OfType<ItemContent.Text>().First().Styled.FontName;

        Assert.Equal("Avenir-Heavy", FontOf(SceneOf(verse)));
        Assert.Equal("Georgia", FontOf(SceneOf(reading)));
        Assert.Equal(ColorHex.Color("#203040FF"), SceneOf(reading).Background);
        // A Blank slide has no theme: the default font on black.
        Assert.Equal("HelveticaNeue-Bold", FontOf(SceneOf(blank)));
        Assert.Equal(SceneColor.Black, SceneOf(blank).Background);
    }

    private static Theme Theme(string id, string font, string background) => new()
    {
        Id = id, Name = id, FontFamily = font, FontSize = 72, TextColorHex = "#FFFFFFFF", BackgroundColorHex = background,
    };

    private static SlideObject Text(string id, string text) => new() { Id = id, ObjectKind = SlideObjectKind.Text, Name = "Text", Text = text };
}
