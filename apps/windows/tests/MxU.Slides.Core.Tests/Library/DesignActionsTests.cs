using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;

namespace MxU.Slides.Core.Tests.Library;

/// <summary>
/// Port of the Mac's DesignActionsTests.swift, for the parts of DesignActions the Windows app has:
/// finding a design by name, a slide taking on its design's actions, and Make Slides giving each
/// made slide its design's actions. The Mac's three deck-wide tests (applying a theme to a deck or
/// to some of its slides) wait for the C# port of <c>Presentation.applyTheme</c>.
/// </summary>
public class DesignActionsTests
{
    private static SlideAction Preset(string id, string presetId) =>
        new() { Id = id, Kind = SlideActionKind.SwitchOutputPreset, PresetId = presetId };

    private static Slide Design(string name, List<SlideAction>? actions = null) =>
        new() { Id = name, Name = name, Objects = [], Actions = actions };

    private static Theme Theme(string id, params Slide[] designs) => new()
    {
        Id = id, Name = id, FontFamily = "H", FontSize = 60, TextColorHex = "#fff", BackgroundColorHex = "#000",
        Slides = [.. designs],
    };

    private static List<string?> Presets(Slide slide) =>
        (slide.Actions ?? []).Select(a => a.PresetId).Where(p => p is not null).ToList();

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
        // The slide's own actions stay, the copy gets a fresh id.
        Assert.Equal(["mine", "n1"], slide.Actions!.Select(a => a.Id));
        Assert.Equal("sideThird", slide.Actions![^1].PresetId);

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

        slide.AdoptActions(point, previous: verse);
        // The verse preset leaves, the point preset arrives.
        Assert.Equal(["wide", "lowerThird"], Presets(slide));
        Assert.Equal(kept, slide.Actions![0].Id); // an action both designs give stays put

        var edited = new Slide { Id = "e", Name = "", Objects = [], Actions = [Preset("x", "sideThird-edited")] };
        edited.AdoptActions(point, previous: Design("Verse", [Preset("v1", "sideThird")]));
        Assert.Equal("sideThird-edited", edited.Actions![0].PresetId); // an action changed since it came stays
    }

    [Fact]
    public void MadeSlidesTakeTheirNamedDesignsActions()
    {
        var themes = new Dictionary<string, Theme>
        {
            ["worship"] = Theme("worship", Design("Lyrics", [Preset("l", "full")]), Design("Verse", [Preset("v", "sideThird")])),
            ["scripture"] = Theme("scripture", Design("Verse", [Preset("s", "lowerThird")])),
        };
        var deckVerse = new Slide { Id = "a", Name = "", Objects = [], Actions = [Preset("switch", "sideThird")], ThemeSlideName = "Verse" };
        var fromOther = new Slide { Id = "b", Name = "", Objects = [], ThemeSlideName = "Verse", ThemeId = "scripture" };
        var media = new Slide { Id = "c", Name = "", Objects = [] };
        var built = new NoteSlideBuilder.Built { Slides = [deckVerse, fromOther, media] };

        NoteSlideBuilder.AttachingDesignActions(built, "worship", id => themes.GetValueOrDefault(id));
        Assert.Single(built.Slides[0].Actions!); // the type's switch already gives the same preset
        Assert.Equal(["lowerThird"], Presets(built.Slides[1])); // read through the slide's own theme
        Assert.Null(built.Slides[2].Actions); // a slide naming no design takes nothing
    }
}
