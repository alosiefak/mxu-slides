using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;
using Item = MxU.Slides.Core.Notes.NoteFollowAlong.Item;
using Slot = MxU.Slides.Core.Notes.NoteFollowAlong.Slot;

namespace MxU.Slides.Core.Tests.Notes;

/// <summary>Port of the Mac's NoteFollowAlongTests.swift.</summary>
public class NoteFollowAlongTests
{
    private static NoteBlock Marked(string text, string group, string? slideId = null, string? type = null) => N.Para(N.Run(text, group, type, slideId));

    private static NoteBlock Card(string slideId, bool byHand = false) => N.Card("deck", slideId, byHand ? true : null);

    private static Slide Slide(string id, params string[] texts) =>
        new() { Id = id, Name = "", Objects = texts.Select((t, i) => new SlideObject { Id = $"{id}-{i}", ObjectKind = SlideObjectKind.Text, Name = $"Box {i}", Text = t }).ToList() };

    private static Presentation Deck(params Slide[] slides) =>
        new() { Id = "deck", Name = "Unshakable", PresentationKind = PresentationKind.Deck, ThemeId = "t", Slides = [.. slides] };

    private static List<Item> Items(NoteBlock[] blocks, Presentation? deck, List<NoteMadePassage>? memory = null, Dictionary<string, string[]>? parts = null) =>
        NoteFollowAlong.Items(new NoteBody { Blocks = [.. blocks], PlainText = "" }, deck, memory ?? [],
            slot => deck?.Slides.FirstOrDefault(s => s.Id == slot.SlideId),
            (group, _) => new SlideRow
            {
                GroupId = group.Id, Kind = SlideKind.Scripture, LinesPerSlide = 4,
                Parts = (parts?.GetValueOrDefault(group.Id) ?? [group.Text]).Select(t => new List<SlideBox> { new("Box 0", t) }).ToList(),
            });

    private static List<NoteFollowAlong.Row> Rows(Item item) => item is Item.Passage p ? p.Rows : [];

    [Fact]
    public void FollowAlongReadsProseAndPassagesInNoteOrder()
    {
        var result = Items([N.Para("Intro"), N.Para("Story"), Marked("For God so loved the world", "g1", "s1", "scripture"), Card("s1"), N.Para("Close")],
            Deck(Slide("s1", "For God so loved the world")));
        Assert.Equal(["prose:0", "passage:g1", "prose:4"], result.Select(i => i.Id));
        Assert.Equal([0, 1], result[0].BlockIndexes);
        var passage = Assert.IsType<Item.Passage>(result[1]);
        Assert.True(passage.GroupId == "g1" && passage.Kind == "scripture" && passage.FirstBlock == 2 && passage.LastBlock == 2);
        Assert.Equal([new NoteFollowAlong.Row("s1", "scripture", "For God so loved the world", [new Slot("deck", "s1")])], passage.Rows);
    }

    [Fact]
    public void FollowAlongPutsEachSlideOnThePartItShowsAndBlanksOnTheirOwnRows()
    {
        var result = Items([Marked("27 This phrase 28 Therefore", "g", "v28"), Card("v28"), Card("v27"), Card("blank"), Card("x")],
            Deck(Slide("v28", "28 Therefore"), Slide("v27", "27 This phrase"), Slide("blank"), Slide("x", "Edited by hand")),
            parts: new() { ["g"] = ["27 This phrase", "28 Therefore", "29 For our God"] });
        var passage = Rows(result[0]);
        Assert.Equal(["27 This phrase", "28 Therefore", null, "29 For our God"], passage.Select(r => r.Words));
        Assert.Equal([["v27"], ["v28"], ["blank"], ["x"]], passage.Select(r => r.Slides.Select(s => s.SlideId).ToList()));
        Assert.Equal(["scripture", "scripture", "blank", "scripture"], passage.Select(r => r.Kind));
        Assert.Equal(["v27", "v28", "blank", "x"], passage.Select(r => r.Id));
    }

    [Fact]
    public void FollowAlongPutsASlideMatchingNoFreePartUnderTheFirstRow()
    {
        var result = Items([Marked("One Two", "g", "a"), Card("a"), Card("b"), Card("c")],
            Deck(Slide("a", "One"), Slide("b", "Two"), Slide("c", "Three")), parts: new() { ["g"] = ["One", "Two"] });
        Assert.Equal([["a", "c"], ["b"]], Rows(result[0]).Select(r => r.Slides.Select(s => s.SlideId).ToList()));
    }

    [Fact]
    public void FollowAlongPlacesSlidesTheNoteNeverMadeWhereTheyFallInTheDeck()
    {
        var result = Items([
            N.Para("Opening"), Marked("Title", "g1", "s1"), Card("s1"), N.Para("Middle"), Marked("Point", "g2", "s2"), Card("s2"), Card("hand", true),
        ], Deck(Slide("welcome"), Slide("s1", "Title"), Slide("between"), Slide("s2", "Point"), Slide("hand"), Slide("end")));
        Assert.Equal(["prose:0", "slide:welcome", "passage:g1", "slide:between", "prose:3", "passage:g2", "slide:hand", "slide:end"], result.Select(i => i.Id));
        Assert.Equal(new Item.SlideItem(new Slot("deck", "welcome"), false), result[1]);
        Assert.Equal(new Item.SlideItem(new Slot("deck", "hand"), true), result[6]);
    }

    [Fact]
    public void FollowAlongPutsEveryDeckSlideAtTheEndOfANoteHoldingNone()
    {
        Assert.Equal(["prose:0", "slide:a", "slide:b"], Items([N.Para("Just words")], Deck(Slide("a"), Slide("b"))).Select(i => i.Id));
        Assert.Equal(["prose:0"], Items([N.Para("Just words")], null).Select(i => i.Id));
    }

    [Fact]
    public void FollowAlongReadsThePassagesMemoryWhenItsCardsAreGone()
    {
        var memory = new List<NoteMadePassage> { new() { GroupId = "g", Text = "Remembered", Slides = [new NoteMadeSlide { SlideId = "s1", Boxes = [] }] } };
        var result = Items([Marked("Remembered", "g")], Deck(Slide("s1", "Remembered")), memory);
        Assert.Equal(["passage:g"], result.Select(i => i.Id));
        Assert.Equal(["s1"], Rows(result[0])[0].Slides.Select(s => s.SlideId));
    }

    [Fact]
    public void FollowAlongReadsPassagesWithNoLivingSlideAsProseAndShowsACardOnce()
    {
        var result = Items([Marked("Not made yet", "g0"), Marked("Gone", "g1", "dead"), Card("dead"), Marked("Kept", "g2", "s1"), Card("s1"), Card("s1")],
            Deck(Slide("s1", "Kept")));
        Assert.Equal(["prose:0", "passage:g2"], result.Select(i => i.Id));
        Assert.Equal([0, 1], result[0].BlockIndexes);
        Assert.Equal(["s1"], Rows(result[1]).SelectMany(r => r.Slides).Select(s => s.SlideId));
    }

    [Fact]
    public void FollowAlongRowsFollowTheRealPlannersVerseSplit()
    {
        var options = new SlideOptions { ScriptureSplit = SlideOptions.ScriptureSplits.OneVerse };
        var group = new MarkedGroup("g", "Hebrews 12:27–28\n27 This phrase signifies the removing. 28 Therefore, receiving a Kingdom.", "scripture");
        var row = new DeterministicSlidePlanner(options).Plan([group], null, N.NoOverrides).Rows[0];
        var made = row.Parts.Select((boxes, index) => new Slide
        {
            Id = $"v{index}", Name = "",
            Objects = boxes.Select(b => new SlideObject { Id = Guid.NewGuid().ToString(), ObjectKind = SlideObjectKind.Text, Name = b.Name, Text = b.Text }).ToList(),
        }).ToList();
        Assert.Equal(2, made.Count);
        var result = NoteFollowAlong.Items(
            N.Body(N.Para(new NoteRun { Text = group.Text, SlideGroup = "g", SlideType = "scripture", SlideId = "v0" }), Card("v1"), Card("v0")),
            Deck([.. made]), [], slot => made.FirstOrDefault(s => s.Id == slot.SlideId),
            (g, slides) => NoteSlideUpdate.Replan(g, slides, null, options));
        var passage = Rows(result[0]);
        Assert.Equal([["v0"], ["v1"]], passage.Select(r => r.Slides.Select(s => s.SlideId).ToList()));
        Assert.StartsWith("27 This phrase", passage[0].Words);
        Assert.StartsWith("28 Therefore", passage[1].Words);
    }

    [Fact]
    public void FollowAlongLaneNarrowsWithThePanelAndStopsAtItsLimits()
    {
        Assert.Equal(160, NoteFollowAlong.LaneWidth(0));
        Assert.Equal(80, NoteFollowAlong.LaneWidth(200));
        Assert.Equal(130, NoteFollowAlong.LaneWidth(420));
        Assert.Equal(220, NoteFollowAlong.LaneWidth(1400));
    }

    [Fact]
    public void FollowAlongLinesFlattenPassagesIntoOutlinedRowsAndIndexThem()
    {
        var result = Items([N.Para("Intro"), Marked("One Two", "g", "a"), Card("a"), Card("b")],
            Deck(Slide("welcome"), Slide("a", "One"), Slide("b", "Two")), parts: new() { ["g"] = ["One", "Two"] });
        var lines = NoteFollowAlong.Lines(result);
        Assert.Equal(["prose:0", "slide:welcome", "row:a", "row:b"], lines.Select(l => l.Id));
        var row = Assert.IsType<NoteFollowAlong.LineContent.RowLine>(lines[2].Content);
        var second = Assert.IsType<NoteFollowAlong.LineContent.RowLine>(lines[3].Content);
        Assert.True(row.PassageKind == "slide" && row.First && !row.Last && !second.First && second.Last);
        var (bySlide, byBlock) = NoteFollowAlong.LineIndex(result);
        Assert.Equal(new Dictionary<string, string> { ["welcome"] = "slide:welcome", ["a"] = "row:a", ["b"] = "row:b" }, bySlide);
        Assert.Equal(new Dictionary<int, string> { [0] = "prose:0", [1] = "row:a" }, byBlock);
    }

    [Fact]
    public void FollowAlongCallsOnlyEmptyTextSlidesBlank()
    {
        Assert.True(NoteFollowAlong.IsBlank(Slide("b")));
        Assert.True(NoteFollowAlong.IsBlank(Slide("b", " ")));
        Assert.False(NoteFollowAlong.IsBlank(Slide("t", "Welcome")));
        var video = Slide("v");
        video.Objects.Add(new SlideObject { Id = "m", ObjectKind = SlideObjectKind.Media, Name = "Video", Text = "" });
        Assert.False(NoteFollowAlong.IsBlank(video));
    }
}

/// <summary>Port of the Mac's SlideBuildingSettingsTests.swift (its UserDefaults, Library and Mac-source checks have no Windows twin).</summary>
public class SlideBuildingSettingsTests
{
    private static SlideDesignMap Map(string theme)
    {
        var map = new SlideDesignMap();
        map.Set(new SlideDesignMap.Entry(theme, "Scripture"), SlideKind.Scripture);
        return map;
    }

    [Fact]
    public void TheDrivesDocumentWinsOverThisComputersOwnOnceItIsHere()
    {
        var own = new SlideBuildingSettings { Id = SlideBuildingSettings.WellKnownId, LyricsImportThemeId = "mine" };
        var drive = new SlideBuildingSettings { Id = SlideBuildingSettings.WellKnownId, LyricsImportThemeId = "booth" };
        Assert.Equal("booth", SlideBuildingSettingsLogic.Effective(drive, own).LyricsImportThemeId);
        Assert.Equal("mine", SlideBuildingSettingsLogic.Effective(null, own).LyricsImportThemeId);
        Assert.Null(SlideBuildingSettingsLogic.Effective(SlideBuildingSettingsLogic.New(), own).LyricsImportThemeId); // once the document exists, its unset fields stay unset
    }

    [Fact]
    public void NotesFallBackToTheLyricThemeThenTheStarter()
    {
        var settings = SlideBuildingSettingsLogic.New();
        Assert.Equal("pack", settings.NotesThemeId("pack"));
        Assert.Equal("", settings.NotesThemeId(null)); // no starter in the library: no theme
        settings.LyricsImportThemeId = "lyrics";
        Assert.Equal("lyrics", settings.NotesThemeId("pack"));
        var none = SlideBuildingSettingsLogic.New();
        none.SetLyricsLook("", null);
        Assert.Equal("", none.NotesThemeId("pack"));
        none.StartOnTheme("pack");
        Assert.Equal("", none.LyricsImportThemeId); // onboarding's starter never overrides it either
    }

    [Fact]
    public void AFreshLibraryStartsOnTheStarterThemeOnlyWhileNoneIsSet()
    {
        var fresh = SlideBuildingSettingsLogic.New();
        fresh.StartOnTheme("pack.cleanGeometric.theme");
        Assert.Equal("pack.cleanGeometric.theme", fresh.LyricsImportThemeId);
        var chosen = new SlideBuildingSettings { Id = SlideBuildingSettings.WellKnownId, LyricsImportThemeId = "church", LyricsImportDesign = "Lyrics Lower" };
        chosen.StartOnTheme("pack.cleanGeometric.theme");
        Assert.True(chosen.LyricsImportThemeId == "church" && chosen.LyricsImportDesign == "Lyrics Lower");
    }

    [Fact]
    public void TheDesignMapRoundTripsAsStableText()
    {
        var settings = SlideBuildingSettingsLogic.New();
        var design = Map("t1");
        design.Set(new SlideDesignMap.Entry("t2", "Point"), SlideKind.Points);
        design.Blanks = BlankPlacement.AfterPassage;
        settings.SetSlideDesignMap(design);
        Assert.Equal(design, settings.SlideDesignMap());
        var first = settings.DesignMap;
        settings.SetSlideDesignMap(design);
        Assert.Equal(first, settings.DesignMap); // the same map is the same text, so saving it again changes nothing
    }

    [Fact]
    public void SeedAdoptOrWaitFromTheDrivesIndex()
    {
        var own = new SlideBuildingSettings { Id = SlideBuildingSettings.WellKnownId, LyricsImportThemeId = "mine" };
        var none = SlideBuildingSettingsLogic.New();
        Assert.Equal(SlideBuildingSeed.Step.None, SlideBuildingSeed.StepFor(true, true, false, own)); // already here
        Assert.Equal(SlideBuildingSeed.Step.None, SlideBuildingSeed.StepFor(false, false, false, own)); // never before the index is read
        Assert.Equal(SlideBuildingSeed.Step.Adopt, SlideBuildingSeed.StepFor(false, true, true, own));
        Assert.Equal(SlideBuildingSeed.Step.Seed, SlideBuildingSeed.StepFor(false, true, false, own));
        Assert.Equal(SlideBuildingSeed.Step.None, SlideBuildingSeed.StepFor(false, true, false, none)); // nothing of its own to give
    }

    [Fact]
    public void AFirstCopyTakesOnlyWhatThisComputerSet()
    {
        var document = new SlideBuildingSettings { Id = SlideBuildingSettings.WellKnownId, LyricsImportThemeId = "edited" };
        document.FillUnset(new SlideBuildingSettings { Id = SlideBuildingSettings.WellKnownId, MessageNotesThemeId = "notes", LyricsImportThemeId = "old" });
        Assert.Equal("notes", document.MessageNotesThemeId);
        Assert.Equal("edited", document.LyricsImportThemeId); // a value already there is kept
        Assert.Null(document.DesignMap);
    }

    [Fact]
    public void TheLyricThemeAndItsDesignArePickedTogether()
    {
        var settings = SlideBuildingSettingsLogic.New();
        Assert.Equal("Lyrics", settings.LyricsDesign()); // no pick = the theme's Lyrics design
        settings.SetLyricsLook("sunday", "Lyrics (Lower Third)");
        Assert.True(settings.LyricsImportThemeId == "sunday" && settings.LyricsDesign() == "Lyrics (Lower Third)" && !settings.IsUnset());
        settings.SetLyricsLook("sunday", null);
        Assert.Equal("Lyrics (Lower Third)", settings.LyricsDesign()); // a theme-only pick of the same theme keeps the design
        settings.SetLyricsLook("youth", null);
        Assert.True(settings.LyricsImportThemeId == "youth" && settings.LyricsImportDesign is null); // another theme drops a design its names may not have
        settings.SetLyricsLook("youth", "Big Words");
        settings.SetLyricsLook("youth", "");
        Assert.Equal("Lyrics", settings.LyricsDesign()); // Use "Theme" goes back to Lyrics
        settings.SetLyricsLook("youth", "Big Words");
        settings.SetLyricsLook("", "");
        Assert.True(settings.LyricsImportThemeId == "" && settings.LyricsImportDesign is null); // None is a choice, kept, with no design
    }
}
