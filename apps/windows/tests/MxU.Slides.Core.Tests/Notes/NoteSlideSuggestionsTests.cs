using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;
using Mode = MxU.Slides.Core.Notes.NoteSlideMarks.SuggestMode;

namespace MxU.Slides.Core.Tests.Notes;

/// <summary>Port of the Mac's NoteSlideSuggestionsTests.swift.</summary>
public class NoteSlideSuggestionsTests
{
    private static NoteBlock Para(string text, bool? bold = null, string? group = null, string? state = null) =>
        new() { Type = NoteBlockType.Paragraph, Runs = [new NoteRun { Text = text, Bold = bold, SlideGroup = group, SlideState = state }] };

    private static List<NoteSlideMarks.Group> Suggested(IEnumerable<NoteBlock> blocks, Mode mode = Mode.PickForMe) =>
        NoteSlideMarks.Groups(NoteSlideMarks.Suggesting(new NoteBody { Blocks = blocks.ToList(), PlainText = "" }, mode));

    [Fact]
    public void RunSheetAndMediaLinesAreNotSuggested()
    {
        foreach (var line in new[] { "Intro: the earthquake", "Introduction", "Illustration – the bridge", "Offering", "Closing prayer, band up",
                     "Opening Prayer", "Video: Sarah (2:40)", "Pictures of the build", "Photos of camp", "Announcements" })
            Assert.True(NoteSlideMarks.IsRunSheet(line), line);
        foreach (var line in new[] { "Prayer is our lifeline", "Introspection matters", "Videos teach us" })
            Assert.False(NoteSlideMarks.IsRunSheet(line), line);
        Assert.Empty(Suggested([Para("Video: Sarah"), Para("(Stand for the reading)"), Para("https://example.com/clip")]));
    }

    [Fact]
    public void SuggestionsAreSuggestedWithTheirKindAndNeverTouchAMarkedBlock()
    {
        var groups = Suggested([Para("“Be still.” — Psalm 46"), Para("A thought", group: "x", state: NoteSlideMarks.Declined), Para("God is faithful.")]);
        Assert.Equal(["“Be still.” — Psalm 46", "God is faithful."], groups.Select(g => g.Text));
        Assert.Equal(["quote", null], groups.Select(g => g.Type));
        Assert.All(groups, g => Assert.Equal(NoteSlideMarks.Suggested, g.State));

        var once = NoteSlideMarks.Suggesting(new NoteBody { Blocks = [Para("God is faithful.")], PlainText = "" });
        Assert.Equal(once, NoteSlideMarks.Suggesting(once));
    }

    [Fact]
    public void BoldLinesOnlyWhenTheNoteMixesThemUnlessTheModeSays()
    {
        NoteBlock[] mixed = [Para("Here is the plan."), Para("One", bold: true), Para("talk"), Para("Two", bold: true), Para("John 3:16 – For God so loved"), Para("more talk")];
        Assert.Equal(["One", "Two", "John 3:16 – For God so loved"], Suggested(mixed).Select(g => g.Text));
        Assert.Equal(6, Suggested(mixed, Mode.Structure).Count);

        NoteBlock[] oneBold = [Para("Here is the plan."), Para("One", bold: true), Para("talk"), Para("more talk")];
        Assert.Equal(4, Suggested(oneBold).Count);
        Assert.Equal(["One"], Suggested(oneBold, Mode.BoldLines).Select(g => g.Text));
    }

    [Fact]
    public void BoldLinesOnlyKeepsTheTitleWithItsPlainSubtitle()
    {
        var groups = Suggested([
            new NoteBlock { Type = NoteBlockType.Heading, Level = 1, Runs = [new NoteRun { Text = "Unshakable", Bold = true }] },
            Para("Hebrews 12:25–29 · Kingdom Come, week 4"),
            Para("Intro: the earthquake"),
            Para("Everything that can be shaken will be shaken", bold: true),
            Para("Talk about 2020"),
            Para("What God builds can’t be shaken", bold: true),
        ]);
        Assert.Equal("title", groups[0].Type);
        Assert.Equal("Unshakable\nHebrews 12:25–29 · Kingdom Come, week 4", groups[0].Text);
        Assert.Equal(["Everything that can be shaken will be shaken", "What God builds can’t be shaken"], groups.Skip(1).Select(g => g.Text));
    }

    [Fact]
    public void AReferenceJoinsItsVerseAndATitleItsShortLine()
    {
        var groups = Suggested([N.Heading("Rooted", 1), Para("Colossians 2 · week 3"), Para("For God so loved the world"), Para("John 3:16"), Para("Romans 8:28")]);
        Assert.Equal(["Rooted\nColossians 2 · week 3", "For God so loved the world\nJohn 3:16", "Romans 8:28"], groups.Select(g => g.Text));
        Assert.Equal(["title", "scripture", "scripture"], groups.Select(g => g.Type));
    }

    [Fact]
    public void SlideMarkersMakeTheSuggestions()
    {
        var groups = Suggested([Para("Hi team, here are Sunday's slides"), Para("[slide]"), Para("Grace is enough"), Para("It always was"), Para("[slide]"), Para("John 3:16 – For God so loved")]);
        Assert.Equal(["Grace is enough\nIt always was", "John 3:16 – For God so loved"], groups.Select(g => g.Text));
        Assert.Equal([null, "scripture"], groups.Select(g => g.Type));
    }

    [Fact]
    public void SlideLabelLinesAreMarkers()
    {
        string[] markers = ["Slide", "Slide:", "Slide 1", "slide 3:", "SLIDE 4", "Slide #5 -", "Slides:", "Slide 10: (photo attached)", "[slide]"];
        Assert.All(markers, m => Assert.True(NoteSlides.IsMarker(Para(m), NoteSlides.DefaultMarker), m));
        Assert.DoesNotContain(new[] { "Slide 1: Welcome", "Slides are ready", "Slideshow" }, m => NoteSlides.IsMarker(Para(m), NoteSlides.DefaultMarker));
        Assert.False(NoteSlideMarks.IsVideoFileLink(Para("Snoop.mp4")));
        Assert.Equal([null], Suggested([Para("Slide 2:"), Para("Snoop.mp4")]).Select(g => g.Type));
    }

    [Fact]
    public void OneLineMarkersMakeSlidesOfTheirWords()
    {
        Assert.Equal(9, NoteSlides.InlineMarkerLabelLength(Para("Slide 1: Welcome to Grace Church")));
        Assert.Equal(7, NoteSlides.InlineMarkerLabelLength(Para("SLIDE: God is good")));
        Assert.All(new[] { "Slide into home plate", "the slide 3 we saw", "Slide 1 Welcome", "Slide 10: (photo attached)", "Slide 2:" },
            w => Assert.Null(NoteSlides.InlineMarkerLabelLength(Para(w))));

        var marked = NoteSlideMarks.Suggesting(new NoteBody
        {
            Blocks =
            [
                Para("Hi team"),
                N.Para(new NoteRun { Text = "Slide 1: " }, new NoteRun { Text = "Welcome", Bold = true }, new NoteRun { Text = " to Grace Church" }),
                Para("Slide 2: Grace is enough"),
            ],
            PlainText = "",
        });
        Assert.Equal(["Welcome to Grace Church", "Grace is enough"], NoteSlideMarks.Groups(marked).Select(g => g.Text));
        Assert.Null(marked.Blocks[1].Runs![0].SlideGroup); // the label stays the speaker's
        Assert.Equal(["Slide 2: ", "Grace is enough"], marked.Blocks[2].Runs!.Select(r => r.Text));
    }

    [Fact]
    public void PastedNotesGetTheirTitleAndTheirNumberedPoints()
    {
        var groups = Suggested([Para("Unshakable"), Para("Hebrews 12 · week 4"), Para("1. Everything that can be shaken will be"), Para("2. What God builds can’t be shaken"), Para("God is faithful.")]);
        Assert.Equal(["Unshakable\nHebrews 12 · week 4", "1. Everything that can be shaken will be\n2. What God builds can’t be shaken", "God is faithful."], groups.Select(g => g.Text));
        Assert.Equal(["title", "points", null], groups.Select(g => g.Type));
        Assert.Equal([null], Suggested([Para("Today we begin a new series.")]).Select(g => g.Type));
    }

    [Fact]
    public void TheSelectionBarGuessesByTheSuggestersLadder()
    {
        Assert.Equal("scripture", NoteSlideMarks.GuessedType("Psalm 46:1 – God is our refuge and strength", NoteSlideMarks.Shape.Paragraph));
        Assert.Equal("quote", NoteSlideMarks.GuessedType("“Faith is trusting God.” — Corrie ten Boom", NoteSlideMarks.Shape.Paragraph));
        Assert.Equal("title", NoteSlideMarks.GuessedType("Unshakable", NoteSlideMarks.Shape.Heading));
        Assert.Equal("points", NoteSlideMarks.GuessedType("God is our refuge\nGod is our strength", NoteSlideMarks.Shape.List));
        Assert.Null(NoteSlideMarks.GuessedType("We were living in Santa Cruz, and the first thing I remember is the sound of the sea.", NoteSlideMarks.Shape.Paragraph));
        Assert.Equal("media", NoteSlideMarks.GuessedType("https://example.com/clip", NoteSlideMarks.Shape.Paragraph));
    }

    [Fact]
    public void TheSelectionBarPutsTheGuessFirstAndNeverTwice()
    {
        var scripture = NoteSlideMarks.SelectionBarKinds("scripture");
        Assert.Equal(["scripture", "points", "quote", "title"], scripture.Shown);
        Assert.Equal(["lyrics", "body", "media"], scripture.More);
        var slide = NoteSlideMarks.SelectionBarKinds(null);
        Assert.Equal([null, "points", "scripture", "quote", "title"], slide.Shown);
        Assert.Equal(["lyrics", "body", "media"], slide.More);
        Assert.Equal(["lyrics", "body"], NoteSlideMarks.SelectionBarKinds("media").More);
        Assert.Equal(["Slide", "Point", "Scripture", "Statement"], new[] { null, "points", "scripture", "body" }.Select(NoteSlideMarks.SelectionBarLabel));
        Assert.True(NoteSlideMarks.SlideType.Body.Label() == "Statement" && NoteSlideMarks.TextKinds[0] == NoteSlideMarks.SlideType.Body);
        Assert.Equal(SlideKind.Body, DeterministicSlidePlanner.KindOfDesignNamed("Statement"));
    }

    [Fact]
    public void AutoMarkWritesItsModesSuggestionsOrNothing()
    {
        var body = new NoteBody { Blocks = [Para("Here is the plan."), Para("One", bold: true), Para("talk"), Para("Two", bold: true)], PlainText = "" };
        Assert.Equal(body, NoteSlideMarks.AutoMark.Nothing.Marking(body));
        foreach (var (choice, mode) in new[] { (NoteSlideMarks.AutoMark.PickForMe, Mode.PickForMe), (NoteSlideMarks.AutoMark.BoldLines, Mode.BoldLines), (NoteSlideMarks.AutoMark.Structure, Mode.Structure) })
        {
            Assert.Equal(NoteSlideMarks.Groups(NoteSlideMarks.Suggesting(body, mode)).Select(g => g.Text),
                NoteSlideMarks.Groups(choice.Marking(body)).Select(g => g.Text));
        }
        Assert.Equal(["Pick for me", "Bold lines only", "By structure", "Nothing"], Enum.GetValues<NoteSlideMarks.AutoMark>().Select(m => m.Title()));
    }

    [Fact]
    public void ALineLabelledWithAnOwnTypesNameIsThatType()
    {
        string[] own = ["Announcement", "Video"];
        var groups = NoteSlideMarks.Groups(NoteSlideMarks.Suggesting(new NoteBody
        {
            Blocks =
            [
                new NoteBlock { Type = NoteBlockType.Heading, Runs = [new NoteRun { Text = "Family Sunday" }] },
                Para("announcement: Camp signups close Friday"),
                Para("Video: Sarah (2:40)"),
                Para("Psalm 46:1"),
                Para("Announcement: Bring a friend"),
                Para("Next step: Write it down."),
            ],
            PlainText = "",
        }, ownTypes: own));
        Assert.Equal(["title", "Announcement", "Video", "scripture", "Announcement", "points"], groups.Select(g => g.Type));
        Assert.Equal([[0], [1], [2], [3], [4], [5]], groups.Select(g => g.BlockIndexes)); // never a title's subtitle, never a reference's verse
        Assert.Empty(Suggested([Para("Video: Sarah (2:40)")])); // a run-sheet line without the type
        Assert.Equal("Announcement", NoteSlideMarks.GuessedType("Announcement: Camp", NoteSlideMarks.Shape.Paragraph, own));
        Assert.Equal("points", NoteSlideMarks.GuessedType("Announcement: Camp", NoteSlideMarks.Shape.Paragraph));
        Assert.Equal(["lyrics", "body", "media", "Video"], NoteSlideMarks.SelectionBarKinds("Announcement", own).More);
        Assert.True(NoteSlideMarks.SelectionBarLabel("Announcement") == "Announcement" && NoteSlideMarks.SelectionBarLabel("body") == "Statement");
    }
}

/// <summary>Port of the Mac's SlideDesignMapTests.swift.</summary>
public class SlideDesignMapTests
{
    [Fact]
    public void TheDesignMapRoundTripsAndFallsBackToNameMatching()
    {
        var map = new SlideDesignMap();
        Assert.Null(map.EntryFor(SlideKind.Scripture)); // absent = match by name
        map.Set(new SlideDesignMap.Entry("scripture-theme", "Verse + Reference"), SlideKind.Scripture);
        map.Set(new SlideDesignMap.Entry("deck", "Points"), SlideKind.Points);
        var back = SlideDesignMap.Decode(map.Encoded());
        Assert.Equal(map, back);
        Assert.Equal("Verse + Reference", back.EntryFor(SlideKind.Scripture)?.ThemeSlideName);
        map.Set(null, SlideKind.Scripture);
        Assert.True(map.EntryFor(SlideKind.Scripture) is null && map.EntryFor(SlideKind.Points) is not null);
        Assert.True(SlideDesignMap.Decode(null).Equals(new SlideDesignMap()) && SlideDesignMap.Decode("junk").Equals(new SlideDesignMap()));
        Assert.True(SlideDesignMap.Kinds[0] == SlideKind.Title && !SlideDesignMap.Kinds.Contains(SlideKind.Media) && !SlideDesignMap.Kinds.Contains(SlideKind.Body));
    }

    [Fact]
    public void PresetSwitchesAndBlanksRoundTripAndOldSettingsStillDecode()
    {
        var map = new SlideDesignMap();
        Assert.True(map.PresetFor(SlideKind.Scripture) is null && map.Blanks == BlankPlacement.None);
        map.SetPreset(SlidePresetChoice.Preset("side"), SlideKind.Scripture);
        map.OtherPreset = SlidePresetChoice.Preset("full");
        map.Blanks = BlankPlacement.AfterPassage;
        map.SetPreset(SlidePresetChoice.Combo("clear-stream"), SlideKind.Blank);
        Assert.Equal(SlidePresetChoice.Preset("side"), map.PresetFor(SlideKind.Scripture)); // the type's own
        Assert.Equal(SlidePresetChoice.Preset("full"), map.PresetFor(SlideKind.Points)); // all other slides
        Assert.Null(map.OwnPresetFor(SlideKind.Points));
        Assert.Equal(map, SlideDesignMap.Decode(map.Encoded()));

        var old = SlideDesignMap.Decode("""{"entries":{"scripture":{"themeId":"t","themeSlideName":"Verse"}}}""");
        Assert.True(old.EntryFor(SlideKind.Scripture)?.ThemeSlideName == "Verse" && old.Presets.Count == 0 && old.Blanks == BlankPlacement.None && old.OtherPreset is null);

        var migrated = SlideDesignMap.Decode("""{"blankPreset":{"kind":"outputPreset","id":"full"}}""");
        Assert.True(migrated.PresetFor(SlideKind.Blank) == SlidePresetChoice.Preset("full") && SlideDesignMap.Kinds.Contains(SlideKind.Blank));

        var action = SlidePresetChoice.Preset("side").Action();
        Assert.True(action.Kind == SlideActionKind.SwitchOutputPreset && action.PresetId == "side" && SlidePresetChoice.Preset("side").Matches(action));
        var combo = SlidePresetChoice.Combo("c").Action();
        Assert.True(combo.Kind == SlideActionKind.FireCombo && combo.ComboId == "c" && !SlidePresetChoice.Preset("c").Matches(combo));
    }

    [Fact]
    public void SlideOptionsRoundTripAndMissingKeysAreDefaults()
    {
        var map = new SlideDesignMap();
        Assert.Equal(new SlideOptions(), map.Options);
        Assert.True(!map.Options.RevealPoints && map.Options.ScriptureSplit == SlideOptions.ScriptureSplits.FitToBox && map.Options.VerseNumbers);
        Assert.True(!map.Options.BlanksBetweenVerses && map.Options.KeepUnderlines && map.Options.ParenthesesToNotes);
        map.Options = map.Options with { RevealPoints = true, ScriptureSplit = SlideOptions.ScriptureSplits.OneVerse, VerseNumbers = false };
        Assert.Equal(map, SlideDesignMap.Decode(map.Encoded()));

        Assert.Equal(new SlideOptions(), SlideDesignMap.Decode("""{"entries":{}}""").Options);
        var partial = SlideDesignMap.Decode("""{"options":{"revealPoints":true,"scriptureSplit":"byPhrase"}}""");
        Assert.True(partial.Options.RevealPoints && partial.Options.ScriptureSplit == SlideOptions.ScriptureSplits.FitToBox && partial.Options.ParenthesesToNotes);
    }

    [Fact]
    public void OwnTypesLiveInTheOptionsWithTheirDesignAndSwitchUnderTheirName()
    {
        var map = new SlideDesignMap();
        Assert.True(map.CanAddOwnType("Announcement") && !map.CanAddOwnType("Scripture") && !map.CanAddOwnType(" "));
        map.Options = map.Options with { OwnTypes = [new SlideOptions.OwnType("Announcement", SlideOptions.OwnType.Splits.Line, true)] };
        Assert.False(map.CanAddOwnType("announcement")); // names are one case
        map.SetForType(new SlideDesignMap.Entry("t", "Notice"), "Announcement");
        map.SetPresetForType(SlidePresetChoice.Preset("lower"), "Announcement");
        var back = SlideDesignMap.Decode(map.Encoded());
        Assert.True(back.Equals(map) && back.Options.OwnTypeNamed("announcement")?.Split == SlideOptions.OwnType.Splits.Line);
        Assert.True(back.EntryForType("Announcement")?.ThemeSlideName == "Notice" && back.PresetForType("Announcement") == SlidePresetChoice.Preset("lower"));
        Assert.True(back.PresetForType("Other") is null && back.EntryFor(SlideKind.Scripture) is null);

        var partial = SlideDesignMap.Decode("""{"options":{"ownTypes":[{"name":"Notice","split":"byWord"}]}}""");
        Assert.Equal([new SlideOptions.OwnType("Notice")], partial.Options.OwnTypes);
        Assert.Empty(SlideDesignMap.Decode("""{"options":{}}""").Options.OwnTypes);

        map.RenameOwnType("Announcement", "Notice");
        Assert.True(map.Options.OwnTypes.Select(t => t.Name).SequenceEqual(["Notice"]) && map.EntryForType("Notice")?.ThemeSlideName == "Notice");
        Assert.True(map.EntryForType("Announcement") is null && map.PresetForType("Notice") == SlidePresetChoice.Preset("lower"));
        map.RemoveOwnType("Notice");
        Assert.True(map.Options.OwnTypes.Count == 0 && map.Entries.Count == 0 && map.Presets.Count == 0);
    }

    [Fact]
    public void TheMapIsWrittenWithSortedKeysAsTheMacWritesIt()
    {
        var map = new SlideDesignMap();
        map.Set(new SlideDesignMap.Entry("t", "Verse"), SlideKind.Scripture);
        var json = map.Encoded();
        Assert.StartsWith("""{"blanks":"none","entries":{"scripture":{"themeId":"t","themeSlideName":"Verse"}},"options":{"blanksBetweenVerses":false""", json);
        Assert.EndsWith("\"verseNumbers\":true,\"videoEnd\":\"next\"},\"presets\":{}}", json);
    }
}
