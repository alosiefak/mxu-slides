using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

// Ported from the Mac's PresenterCore/Tests/PresenterCoreTests/DesignActionsTests.swift.
public class DesignActionsTests
{
    private static SlideAction Preset(string id, string presetId) => new() { Id = id, Kind = SlideActionKind.SwitchOutputPreset, PresetId = presetId };

    private static Slide Design(string name, List<SlideAction>? actions = null) => new() { Id = name, Name = name, Objects = [], Actions = actions };

    private static Theme Theme(string id, params Slide[] designs) => new()
    {
        Id = id, Name = id, FontFamily = "H", FontSize = 60, TextColorHex = "#fff", BackgroundColorHex = "#000", Slides = [.. designs],
    };

    private static List<string?> Presets(Slide slide) => [.. (slide.Actions ?? []).Select(a => a.PresetId).Where(p => p is not null)];

    [Fact]
    public void AThemeResolvesADesignByNameElseItsFirst()
    {
        var worship = Theme("worship", Design("Lyrics"), Design("Verse"));
        Assert.Equal("Verse", worship.Design("verse")?.Name);
        Assert.Equal("Lyrics", worship.Design(null)?.Name);
        Assert.Equal("Lyrics", worship.Design("Gone")?.Name);
        Assert.Null(Theme("empty").Design("Verse"));
    }

    [Fact]
    public void TakingADesignCopiesItsActionsWithFreshIds()
    {
        var ids = 0;
        var slide = new Slide { Id = "s", Name = "", Objects = [], Actions = [Preset("mine", "wide")] };
        slide.AdoptActions(Design("Verse", [Preset("v1", "sideThird")]), newId: () => $"n{++ids}");
        Assert.Equal(["mine", "n1"], slide.Actions!.Select(a => a.Id)); // the slide's own actions stay, the copy gets a fresh id
        Assert.Equal("sideThird", slide.Actions!.Last().PresetId);

        slide.AdoptActions(Design("Verse", [Preset("v1", "sideThird")]));
        Assert.Equal(2, slide.Actions!.Count); // taking it again adds nothing it already has

        var bare = new Slide { Id = "b", Name = "", Objects = [] };
        bare.AdoptActions(Design("Plain"));
        Assert.Null(bare.Actions); // no actions stores as absent
    }

    [Fact]
    public void SwitchingDesignsSwapsTheOldDesignsActions()
    {
        var verse = Design("Verse", [Preset("v1", "sideThird"), new SlideAction { Id = "v2", Kind = SlideActionKind.ClearAll }]);
        var point = Design("Point", [Preset("p1", "lowerThird"), new SlideAction { Id = "p2", Kind = SlideActionKind.ClearAll }]);
        var slide = new Slide { Id = "s", Name = "", Objects = [] };
        slide.AdoptActions(verse);
        slide.Actions!.Add(Preset("mine", "wide"));
        var kept = slide.Actions[1].Id;

        slide.AdoptActions(point, verse);
        Assert.Equal(["wide", "lowerThird"], Presets(slide)); // the verse preset leaves, the point preset arrives
        Assert.Equal(kept, slide.Actions!.First().Id); // an action both designs give stays put

        var edited = new Slide { Id = "e", Name = "", Objects = [], Actions = [Preset("x", "sideThird-edited")] };
        edited.AdoptActions(point, Design("Verse", [Preset("v1", "sideThird")]));
        Assert.Equal("sideThird-edited", edited.Actions!.First().PresetId); // an action changed since it came stays
    }

    [Fact]
    public void ApplyingADeckThemeSwapsEachSlidesDesignActions()
    {
        var old = Theme("old", Design("Verse", [Preset("o", "sideThird")]));
        var @new = Theme("new", Design("Verse", [Preset("n", "lowerThird")]));
        var other = Theme("other", Design("Verse", [Preset("x", "full")]));
        var follows = new Slide { Id = "a", Name = "", Objects = [], ThemeSlideName = "Verse" };
        follows.AdoptActions(old.Slides![0]);
        var own = new Slide { Id = "b", Name = "", Objects = [], ThemeSlideName = "Verse", ThemeId = "other" };
        own.AdoptActions(other.Slides![0]);
        var blank = new Slide { Id = "c", Name = "", Objects = [], Unthemed = true };
        var deck = new Presentation { Id = "p", Name = "Deck", PresentationKind = PresentationKind.Deck, ThemeId = "old", Slides = [follows, own, blank] };

        deck.AdoptDesignActions(@new, new Dictionary<string, Theme> { ["old"] = old, ["new"] = @new, ["other"] = other });
        Assert.Equal(["lowerThird"], Presets(deck.Slides[0]));
        Assert.Equal(["lowerThird"], Presets(deck.Slides[1])); // its own theme's design leaves
        Assert.Null(deck.Slides[2].Actions); // a Blank slide takes none
    }

    [Fact]
    public void ApplyingAThemeToSomeSlidesStylesOnlyThoseSlides()
    {
        var deckTheme = Theme("deck", Design("Verse", [Preset("d", "sideThird")]));
        var scripture = Theme("scripture", Design("Verse", [Preset("s", "lowerThird")]));
        var verse = new Slide { Id = "a", Name = "", Objects = [], ThemeSlideName = "Verse" };
        verse.AdoptActions(deckTheme.Slides![0]);
        var blank = new Slide { Id = "b", Name = "", Objects = [], Unthemed = true };
        var untouched = new Slide { Id = "c", Name = "", Objects = [] };
        var deck = new Presentation { Id = "p", Name = "Deck", PresentationKind = PresentationKind.Deck, ThemeId = "deck", Slides = [verse, blank, untouched] };
        var themes = new Dictionary<string, Theme> { ["deck"] = deckTheme, ["scripture"] = scripture };

        deck.ApplyTheme(scripture, new HashSet<string> { "a", "b" }, null, themes);
        Assert.True(deck.Slides[0].ThemeId == "scripture" && deck.Slides[0].ThemeSlideName == "Verse"); // keeps its design, follows the new theme
        Assert.Equal(["lowerThird"], Presets(deck.Slides[0])); // the old design's actions swap for the new one's
        Assert.True(deck.Slides[1].ThemeId == "scripture" && deck.Slides[1].Unthemed is null); // a Blank slide takes the theme
        Assert.True(deck.Slides[2].ThemeId is null && deck.ThemeId == "deck"); // unselected slides and the deck keep theirs
        Assert.True(deck.SlidesAllFollow(new HashSet<string> { "a", "b" }, "scripture"));
        Assert.False(deck.SlidesAllFollow(new HashSet<string> { "a", "c" }, "scripture"));

        deck.ApplyTheme(deckTheme, new HashSet<string> { "a" }, null, themes);
        Assert.Null(deck.Slides[0].ThemeId); // the deck's own theme clears the slide's
        Assert.Equal(["sideThird"], Presets(deck.Slides[0]));
    }

    [Fact]
    public void PickingADesignPutsEverySlideOnIt()
    {
        var deckTheme = Theme("deck", Design("Verse", [Preset("d", "sideThird")]));
        var scripture = Theme("scripture", Design("Verse"), Design("Reference", [Preset("r", "lowerThird")]));
        var verse = new Slide { Id = "a", Name = "", Objects = [], ThemeSlideName = "Verse" };
        verse.AdoptActions(deckTheme.Slides![0]);
        var deck = new Presentation
        {
            Id = "p", Name = "Deck", PresentationKind = PresentationKind.Deck, ThemeId = "deck",
            Slides = [verse, new Slide { Id = "b", Name = "", Objects = [] }],
        };

        deck.ApplyTheme(scripture, new HashSet<string> { "a", "b" }, "Reference", new Dictionary<string, Theme> { ["deck"] = deckTheme, ["scripture"] = scripture });
        Assert.All(deck.Slides, s => Assert.True(s.ThemeId == "scripture" && s.ThemeSlideName == "Reference"));
        Assert.Equal(["lowerThird"], Presets(deck.Slides[0])); // the picked design's actions replace the old design's
        Assert.True(deck.SlidesAllFollow(new HashSet<string> { "a", "b" }, "scripture", "reference"));
        Assert.False(deck.SlidesAllFollow(new HashSet<string> { "a", "b" }, "scripture", "Verse"));
    }
}
