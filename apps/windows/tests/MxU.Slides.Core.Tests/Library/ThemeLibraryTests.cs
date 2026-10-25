using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Library;

// Making and organising themes and their designs, and giving a deck (or some of its slides) a
// theme. The Mac's rules (AppModel, its slide editor's theme host) have no tests of their own there.
public class ThemeLibraryTests : ScratchFolderTest
{
    private static SlideObject Text(string id, string name = "Lyrics") => new() { Id = id, ObjectKind = SlideObjectKind.Text, Name = name, Text = "Words" };

    private static Theme Theme(string id, params Slide[] designs) => new()
    {
        Id = id, Name = id, FontFamily = "Helvetica Neue", FontSize = 96, TextColorHex = "#FFFFFF", BackgroundColorHex = "#000000", Slides = [.. designs],
    };

    private static Slide Design(string id, string name) => new() { Id = id, Name = name, Objects = [Text(id + ".text")] };

    [Fact]
    public void ANewThemeIsTheMacsStarter()
    {
        var theme = ThemeLibrary.Create();
        Assert.Equal(("Untitled Theme", "Helvetica Neue", 96.0, "#FFFFFF", "#000000"), (theme.Name, theme.FontFamily, theme.FontSize, theme.TextColorHex, theme.BackgroundColorHex));
        Assert.Equal(["Lyrics", "Bible", "Announcements", "Sermon Points"], theme.Slides!.Select(d => d.Name));
        Assert.All(theme.Slides!, d => Assert.Equal(ThemeLibrary.PlaceholderName, Assert.Single(d.Objects).Name));
        Assert.Equal("Sample Bible", theme.Slides![1].Objects[0].Text);
        Assert.False(ThemeLibrary.IsStarter(theme));
        Assert.True(ThemeLibrary.IsStarter(StarterPacks.All[0].MakeTheme()));
    }

    [Fact]
    public void ACopyGetsFreshIdsInsideAndRemembersThem()
    {
        var design = Design("d1", "Lyrics");
        design.Objects.Add(new SlideObject { Id = "matte", ObjectKind = SlideObjectKind.Shape, Name = "Mask", Text = "" });
        design.Objects[0].MaskObjectId = "matte";
        design.Objects[0].AnimationSteps = [new AnimationStep { Id = "in", Kind = AnimationKind.In, Animation = StepAnimation.Fade, Trigger = AnimationTrigger.WithPrevious, DurationSeconds = 0.5 }];
        design.AnimationOrder = ["in"];
        design.Actions = [new SlideAction { Id = "act", Kind = SlideActionKind.ClearAll }];
        var theme = Theme("t", design);

        var (copy, ids) = ThemeLibrary.Duplicate(theme);
        Assert.Equal("t Copy", copy.Name);
        Assert.NotEqual(theme.Id, copy.Id);
        var copied = copy.Slides![0];
        Assert.Equal(ids.Designs["d1"], copied.Id);
        Assert.Equal(ids.Objects["d1.text"], copied.Objects[0].Id);
        Assert.Equal(copied.Objects[1].Id, copied.Objects[0].MaskObjectId); // the mask follows its matte
        Assert.Equal("in", copied.Objects[0].AnimationSteps![0].Id); // steps keep theirs: the play order still names them
        Assert.Equal(["in"], copied.AnimationOrder);
        Assert.NotEqual("act", copied.Actions![0].Id);
        Assert.Equal("d1.text", theme.Slides![0].Objects[0].Id); // the original is untouched
    }

    [Fact]
    public void DesignsAreAddedCopiedRenamedAndKeptUnique()
    {
        var theme = Theme("t", Design("a", "Lyrics"), Design("b", "Bible"));
        var added = ThemeLibrary.AddDesign(theme, "a");
        Assert.Equal("Category 3", added.Name);
        Assert.Equal(["Lyrics", "Category 3", "Bible"], theme.Slides!.Select(d => d.Name));
        Assert.Equal("Sample Text", added.Objects[0].Text);

        var copy = ThemeLibrary.DuplicateDesign(theme, "a")!.Value;
        Assert.Equal("Lyrics Copy", copy.Design.Name);
        Assert.Equal(1, theme.Slides!.IndexOf(copy.Design));
        Assert.Equal("Lyrics Copy 2", ThemeLibrary.DuplicateDesign(theme, "a")!.Value.Design.Name);

        Assert.True(ThemeLibrary.RenameDesign(theme, "b", "lyrics"));
        Assert.Equal("lyrics 2", theme.Slides!.Single(d => d.Id == "b").Name); // slides find designs by name, any case
        Assert.False(ThemeLibrary.RenameDesign(theme, "b", "  "));
        Assert.True(ThemeLibrary.SetDesignFolder(theme, "b", " Lower Thirds "));
        Assert.Equal("Lower Thirds", theme.Slides!.Single(d => d.Id == "b").Folder);
        Assert.True(ThemeLibrary.SetDesignFolder(theme, "b", ""));
        Assert.Null(theme.Slides!.Single(d => d.Id == "b").Folder);
    }

    [Fact]
    public void AThemeKeepsItsLastDesignAndDesignsMove()
    {
        var theme = Theme("t", Design("a", "Lyrics"), Design("b", "Bible"));
        Assert.True(ThemeLibrary.MoveDesign(theme, "b", -1));
        Assert.Equal(["Bible", "Lyrics"], theme.Slides!.Select(d => d.Name));
        Assert.False(ThemeLibrary.MoveDesign(theme, "b", -1));
        Assert.True(ThemeLibrary.DeleteDesign(theme, "a"));
        Assert.False(ThemeLibrary.DeleteDesign(theme, "b")); // a theme keeps at least one design
    }

    [Fact]
    public void UsesCountDeckThemesAndSlidesThatChoseIt()
    {
        var deck = new Presentation
        {
            Id = "p1", Name = "Song", PresentationKind = PresentationKind.Song, ThemeId = "t",
            Slides = [new Slide { Id = "s1", Name = "", Objects = [], ThemeSlideName = "Lyrics" }, new Slide { Id = "s2", Name = "", Objects = [], Unthemed = true }],
        };
        var other = new Presentation
        {
            Id = "p2", Name = "Talk", PresentationKind = PresentationKind.Deck, ThemeId = "x",
            Slides = [new Slide { Id = "s3", Name = "", Objects = [], ThemeId = "t", ThemeSlideName = "lyrics" }, new Slide { Id = "s4", Name = "", Objects = [] }],
        };
        var uses = ThemeLibrary.Uses([deck, other], "t");
        Assert.Equal([("p1", 1, true), ("p2", 1, false)], uses.Select(u => (u.Presentation.Id, u.Slides, u.IsDeckTheme)));
        Assert.Equal(["s1", "s3"], ThemeLibrary.DesignUses([deck, other], "t", "LYRICS").Select(u => u.Slide.Id));

        var changed = ThemeLibrary.RenameDesignUses([deck, other], "t", "Lyrics", "Words");
        Assert.Equal(2, changed.Count);
        Assert.Equal("Words", deck.Slides[0].ThemeSlideName);
        Assert.Equal("Words", other.Slides[0].ThemeSlideName);
        Assert.Null(other.Slides[1].ThemeSlideName);
    }

    // MARK: Giving a deck a theme

    private static Dictionary<string, Theme> Themes(params Theme[] themes) => themes.ToDictionary(t => t.Id);

    private static Presentation Deck(string themeId, params Slide[] slides) => new() { Id = "p", Name = "Deck", PresentationKind = PresentationKind.Deck, ThemeId = themeId, Slides = [.. slides] };

    [Fact]
    public void ANewDeckThemeReplacesSlidesOwnThemesOrKeepsThem()
    {
        var themes = Themes(Theme("a", Design("ad", "Lyrics")), Theme("b", Design("bd", "Lyrics")), Theme("s", Design("sd", "Scripture")));
        Presentation Fresh() => Deck("a", new Slide { Id = "1", Name = "", Objects = [] }, new Slide { Id = "2", Name = "", Objects = [], ThemeId = "s" });

        Assert.Equal(1, ThemeApplication.SlidesWithOwnTheme(Fresh(), "b"));
        Assert.Equal(0, ThemeApplication.SlidesWithOwnTheme(Fresh(), "s")); // the new theme is the one it chose

        var replaced = Fresh();
        Assert.True(ThemeApplication.ApplyToDeck(replaced, "b", null, themes));
        Assert.Equal("b", replaced.ThemeId);
        Assert.False(replaced.HasSlideThemes()); // as on the Mac

        var kept = Fresh();
        Assert.True(ThemeApplication.ApplyToDeck(kept, "b", null, themes, ThemeApplication.OwnThemes.Keep));
        Assert.Equal("b", kept.ThemeId);
        Assert.Equal("s", kept.Slides[1].ThemeId);

        var adopting = Fresh();
        ThemeApplication.ApplyToDeck(adopting, "s", null, themes, ThemeApplication.OwnThemes.Keep);
        Assert.Null(adopting.Slides[1].ThemeId); // it now just follows the deck
    }

    [Fact]
    public void PickingTheDeckThemeAgainPutsTheTextBack()
    {
        var themes = Themes(Theme("a", Design("ad", "Lyrics")));
        var moved = new SlideObject { Id = "o", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = "x", X = 5, TextStyle = new TextStyle { FontSize = 20 } };
        var deck = Deck("a", new Slide { Id = "1", Name = "", Objects = [moved] });
        Assert.True(ThemeApplication.ApplyToDeck(deck, "a", null, themes));
        Assert.Null(moved.X);
        Assert.Null(moved.TextStyle);
        Assert.False(ThemeApplication.ApplyToDeck(Deck(""), "", null, themes)); // no theme stays no theme
    }

    [Fact]
    public void ADesignForEverySlide()
    {
        var themes = Themes(Theme("a", Design("ad", "Lyrics")), Theme("b", Design("b1", "Lyrics"), Design("b2", "Lower Third")));
        var deck = Deck("a", new Slide { Id = "1", Name = "", Objects = [], ThemeSlideName = "Lyrics" }, new Slide { Id = "2", Name = "", Objects = [], ThemeId = "a" });
        Assert.True(ThemeApplication.ApplyToDeck(deck, "b", "Lower Third", themes));
        Assert.Equal("b", deck.ThemeId);
        Assert.All(deck.Slides, s => Assert.True(s.ThemeId is null && s.ThemeSlideName == "Lower Third"));
    }

    [Fact]
    public void ApplyThemeToSelectedSlidesStylesOnlyThem()
    {
        var themes = Themes(Theme("a", Design("ad", "Lyrics")), Theme("s", Design("sd", "Scripture")));
        var deck = Deck("a", new Slide { Id = "1", Name = "", Objects = [] }, new Slide { Id = "2", Name = "", Objects = [] });
        Assert.True(ThemeApplication.ApplyToSlides(deck, ["2"], "s", "Scripture", themes));
        Assert.Equal("a", deck.ThemeId);
        Assert.Null(deck.Slides[0].ThemeId);
        Assert.Equal(("s", "Scripture"), (deck.Slides[1].ThemeId, deck.Slides[1].ThemeSlideName));
        Assert.False(ThemeApplication.ApplyToSlides(deck, ["2"], "gone", null, themes));
        Assert.False(ThemeApplication.ApplyToSlides(deck, ["9"], "s", null, themes));
    }

    // MARK: Kinetic builds on copies

    [Fact]
    public void ACopiedPlaceholderTakesItsKineticBuild()
    {
        var library = NewLibrary();
        var kinetic = new KineticExtras(library.Extras);
        var placeholder = Text("pack.x.lyrics");
        placeholder.AnimationSteps = [new AnimationStep { Id = "in", Kind = AnimationKind.In, Animation = StepAnimation.Fade, Trigger = AnimationTrigger.WithPrevious, DurationSeconds = 0.5 }];
        kinetic.Save(placeholder, new KineticBuild(KineticSplit.Lines, 0.2, []));
        var copy = Core.Json.MxuJson.Clone(placeholder);
        copy.Id = "copy";
        kinetic.Copied(placeholder, copy);
        Assert.Equal(KineticSplit.Lines, kinetic.For(copy)?.Split);
        kinetic.Remove(copy);
        Assert.Null(kinetic.For(copy));
        Assert.NotNull(kinetic.For(placeholder));
    }

    [Fact]
    public void SeedingLeavesABuildSavedForChangedStepsAlone()
    {
        var library = NewLibrary();
        var kinetic = new KineticExtras(library.Extras);
        var starter = Text("pack.x.lyrics");
        starter.AnimationSteps = [new AnimationStep { Id = "in", Kind = AnimationKind.In, Animation = StepAnimation.Fade, Trigger = AnimationTrigger.WithPrevious, DurationSeconds = 0.5 }];
        var edited = Core.Json.MxuJson.Clone(starter);
        edited.AnimationSteps![0].Animation = StepAnimation.Blur;
        kinetic.Save(edited, new KineticBuild(KineticSplit.Words, 0.1, [])); // the theme editor's own build
        Assert.Equal(0, kinetic.Seed([(starter, new KineticBuild(KineticSplit.Lines, 0.2, []))]));
        Assert.Equal(KineticSplit.Words, kinetic.For(edited)?.Split);
    }
}
