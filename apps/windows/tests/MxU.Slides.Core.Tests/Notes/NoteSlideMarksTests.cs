using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;

namespace MxU.Slides.Core.Tests.Notes;

/// <summary>Builders the note tests share, so the C# reads like the Mac's tests.</summary>
internal static class N
{
    public static NoteRun Run(string text, string? group = null, string? type = null, string? slideId = null) =>
        new() { Text = text, SlideGroup = group, SlideType = type, SlideId = slideId };

    public static NoteBlock Para(params NoteRun[] runs) => new() { Type = NoteBlockType.Paragraph, Runs = [.. runs] };

    public static NoteBlock Para(string text) => Para(Run(text));

    public static NoteBlock Heading(string text, int level = 2) => new() { Type = NoteBlockType.Heading, Level = level, Runs = [Run(text)] };

    public static NoteBlock List(params string[] items) =>
        new() { Type = NoteBlockType.List, Ordered = false, Items = items.Select(i => new NoteListItem { Runs = [Run(i)] }).ToList() };

    public static NoteBlock Card(string presentationId, string slideId, bool? byHand = null) =>
        new() { Type = NoteBlockType.SlideRef, Slide = new NoteSlideRef { PresentationId = presentationId, SlideId = slideId, PlacedByHand = byHand } };

    public static NoteBody Body(params NoteBlock[] blocks) => new() { Blocks = [.. blocks], PlainText = "" };

    public static SlideObject Text(string name, string text) => new() { Id = Guid.NewGuid().ToString(), ObjectKind = SlideObjectKind.Text, Name = name, Text = text };

    public static Slide Slide(string id, params SlideObject[] objects) => new() { Id = id, Name = "", Objects = [.. objects] };

    public static Presentation Deck(string id, params Slide[] slides) =>
        new() { Id = id, Name = "Deck", PresentationKind = PresentationKind.Deck, ThemeId = "", Slides = [.. slides] };

    public static IReadOnlyDictionary<string, SlideRowOverride> NoOverrides { get; } = new Dictionary<string, SlideRowOverride>();
}

/// <summary>Port of the Mac's NoteSlideMarksTests.swift.</summary>
public class NoteSlideMarksTests
{
    private static NoteRun Run(string text, string? group = null, string? type = null, string? slideId = null) => N.Run(text, group, type, slideId);

    [Fact]
    public void GroupsFollowTheRunsAcrossBlocksInOrder()
    {
        var body = N.Body(
            new NoteBlock { Type = NoteBlockType.Heading, Level = 2, Runs = [Run("Point one", "a", "title")] },
            N.Para(Run("Plain "), Run("For God so loved ", "b"), Run("the world", "b", slideId: "s1")),
            new NoteBlock
            {
                Type = NoteBlockType.List, Ordered = false, Items =
                [
                    new NoteListItem { Runs = [Run("First", "c", "points")] },
                    new NoteListItem { Runs = [Run("Second", "c")], Blocks = [N.Para(Run("  nested  ", "c"))] },
                ],
            },
            new NoteBlock { Type = NoteBlockType.Quote, Blocks = [N.Para(Run("John 3:16", "b"))] },
            new NoteBlock { Type = NoteBlockType.Rule });

        var groups = NoteSlideMarks.Groups(body);
        Assert.Equal(["a", "b", "c"], groups.Select(g => g.Id));
        Assert.Equal(new NoteSlideMarks.Group { Id = "a", Type = "title", BlockIndexes = [0], Text = "Point one", Shape = NoteSlideMarks.Shape.Heading }, groups[0]);
        Assert.Equal("For God so loved the world\n\nJohn 3:16", groups[1].Text);
        Assert.True(groups[1].BlockIndexes.SequenceEqual([1, 3]) && groups[1].SlideId == "s1" && groups[1].Type is null);
        Assert.True(groups[2].Text == "First\nSecond\nnested" && groups[2].BlockIndexes.SequenceEqual([2]) && groups[2].Type == "points");
        Assert.Equal(1, NoteSlideMarks.BlockIndexOfGroup("b", body));
        Assert.Null(NoteSlideMarks.BlockIndexOfGroup("zz", body));
    }

    [Fact]
    public void MadeSlidesLandAfterTheirPassagesAndStampTheRuns()
    {
        var body = N.Body(
            N.Para(Run("Intro")),
            N.Para(Run("First ", "a"), Run("passage", "a")),
            N.Para(Run("Between")),
            new NoteBlock { Type = NoteBlockType.List, Ordered = false, Items = [new NoteListItem { Runs = [Run("Point", "b", "points")] }] });
        static NoteSlideRef Ref(string id) => new() { PresentationId = "p", SlideId = id, SlideIndex = 0, SlideText = id };
        var placed = NoteSlideMarks.PlacingMadeSlides([("a", [Ref("s1"), Ref("s2")]), ("b", [Ref("s3")]), ("zz", [Ref("s9")])], body);

        Assert.Equal([NoteBlockType.Paragraph, NoteBlockType.Paragraph, NoteBlockType.SlideRef, NoteBlockType.SlideRef, NoteBlockType.Paragraph, NoteBlockType.List, NoteBlockType.SlideRef],
            placed.Blocks.Select(b => b.Type));
        Assert.True(placed.Blocks[2].Slide?.SlideId == "s1" && placed.Blocks[3].Slide?.SlideId == "s2" && placed.Blocks[6].Slide?.SlideId == "s3");
        Assert.Equal(["s1", "s1"], placed.Blocks[1].Runs!.Select(r => r.SlideId));
        Assert.True(placed.Blocks[5].Items![0].Runs[0].SlideId == "s3" && placed.Blocks[5].Items![0].Runs[0].SlideType == "points");
        Assert.Null(placed.Blocks[0].Runs![0].SlideId);
        Assert.Equal("Intro\nFirst passage\ns1\ns2\nBetween\nPoint\ns3", placed.PlainText);
        Assert.Equal(["s1", "s3"], NoteSlideMarks.Groups(placed).Select(g => g.SlideId));
        Assert.Null(body.Blocks[1].Runs![0].SlideId); // the caller's note is untouched, as a Swift value would be
    }

    [Fact]
    public void MarkByStructureTakesEveryContentBlockOnceAndLyricsAsOne()
    {
        var body = N.Body(
            N.Heading("Point one"),
            N.Para("Body text"),
            new NoteBlock { Type = NoteBlockType.Rule },
            N.List("First", "Second"),
            N.Para(Run("already", "keep")),
            N.Para("   "),
            N.Card("p", "s"));
        var marked = NoteSlideMarks.MarkingByStructure(body);
        var groups = NoteSlideMarks.Groups(marked);
        Assert.Equal(4, groups.Count);
        Assert.Equal(["Point one", "Body text", "First\nSecond", "already"], groups.Select(g => g.Text));
        Assert.Equal([NoteSlideMarks.Shape.Heading, NoteSlideMarks.Shape.Paragraph, NoteSlideMarks.Shape.List, NoteSlideMarks.Shape.Paragraph], groups.Select(g => g.Shape));
        Assert.Equal("keep", groups[3].Id); // an existing mark stays as it was
        Assert.Null(marked.Blocks[5].Runs![0].SlideGroup); // a blank paragraph is no passage

        var lyrics = N.Body(N.Para("Verse 1"), N.Para("Amazing grace"), N.Para("Chorus"), N.Para("My chains are gone"));
        var song = NoteSlideMarks.Groups(NoteSlideMarks.MarkingByStructure(lyrics));
        Assert.True(song.Count == 1 && song[0].Text == "Verse 1\nAmazing grace\nChorus\nMy chains are gone");

        var unmarked = NoteSlideMarks.Unmarking(groups[2].Id, marked);
        Assert.Equal(["Point one", "Body text", "already"], NoteSlideMarks.Groups(unmarked).Select(g => g.Text));
    }

    [Fact]
    public void SlideMarkersMakePassagesAndTheirCardsTakeTheMarkersPlace()
    {
        var body = N.Body(
            N.Para("Hey everyone, this is the list for Sunday."), N.Para("[slide]"),
            N.Para("What passage impacted you most?"), N.Para("[slide]"),
            N.Para("1. Many voices"), N.Para(""), N.Para("2. Not a given"), N.Para("[slide]"),
            N.Para("Psalm 1:1-6"));
        var marked = NoteSlideMarks.MarkingByStructure(body);
        var groups = NoteSlideMarks.Groups(marked);
        Assert.Equal(["What passage impacted you most?", "1. Many voices\n\n2. Not a given", "Psalm 1:1-6"], groups.Select(g => g.Text));
        Assert.Null(marked.Blocks[0].Runs![0].SlideGroup); // the greeting never reaches a slide
        Assert.Null(marked.Blocks[1].Runs![0].SlideGroup); // markers stay markers

        static NoteSlideRef Ref(string id) => new() { PresentationId = "p", SlideId = id, SlideText = id };
        var placed = NoteSlideMarks.PlacingMadeSlides([(groups[0].Id, [Ref("s1")]), (groups[1].Id, [Ref("s2"), Ref("s3")])], marked);
        Assert.Equal([NoteBlockType.Paragraph, NoteBlockType.SlideRef, NoteBlockType.Paragraph, NoteBlockType.SlideRef, NoteBlockType.SlideRef,
            NoteBlockType.Paragraph, NoteBlockType.Paragraph, NoteBlockType.Paragraph, NoteBlockType.Paragraph, NoteBlockType.Paragraph], placed.Blocks.Select(b => b.Type));
        Assert.True(placed.Blocks[1].Slide?.SlideId == "s1" && placed.Blocks[3].Slide?.SlideId == "s2" && placed.Blocks[4].Slide?.SlideId == "s3");
        Assert.Equal(["s1", "s2", null], NoteSlideMarks.Groups(placed).Select(g => g.SlideId));
        Assert.True(NoteSlides.IsMarker(placed.Blocks[8], "[slide]"));
    }

    [Fact]
    public void ACardTakesItsMarkersPlacePastAnAside()
    {
        var body = N.Body(N.Para("Slide 2:"), N.Para("(Snoop Dogg video - attached)"), N.Para(Run("Snoop.mp4", "v")));
        var placed = NoteSlideMarks.PlacingMadeSlides([("v", [new NoteSlideRef { PresentationId = "p", SlideId = "s1" }])], body);
        Assert.Equal([NoteBlockType.Paragraph, NoteBlockType.SlideRef, NoteBlockType.Paragraph], placed.Blocks.Select(b => b.Type));
        Assert.Equal("(Snoop Dogg video - attached)", NoteReading.Text(placed.Blocks[0]));
    }

    [Fact]
    public void APassageRewrittenFromTheSheetLandsInItsFirstBlockAndDropsTheRest()
    {
        var body = N.Body(
            N.Para(Run("Keep "), Run("old first", "a", "points", "s1"), Run(" tail")),
            N.Para(Run("old second", "a")),
            new NoteBlock { Type = NoteBlockType.List, Ordered = false, Items = [new NoteListItem { Runs = [Run("old item", "a")] }, new NoteListItem { Runs = [Run("other item", "b")] }] },
            N.Para("After"));
        var edited = NoteSlideMarks.ReplacingText("a", "New line one\nNew line two", body);
        Assert.Equal(3, edited.Blocks.Count); // the emptied paragraph is gone; the list keeps its other item
        Assert.Equal([Run("Keep "), Run("New line one\nNew line two", "a", "points", "s1"), Run(" tail")], edited.Blocks[0].Runs!);
        Assert.Equal(["other item"], edited.Blocks[1].Items!.Select(i => i.Runs[0].Text));
        Assert.Equal(["New line one\nNew line two", "other item"], NoteSlideMarks.Groups(edited).Select(g => g.Text));
        Assert.Equal(body, NoteSlideMarks.ReplacingText("zz", "x", body));
    }

    [Fact]
    public void DeclinedReadsAsUnmarkedAndSuggestionsCountBesideMarks()
    {
        static NoteRun State(string text, string group, string? state) => new() { Text = text, SlideGroup = group, SlideState = state };
        var body = N.Body(
            N.Para(State("Welcome", "d", NoteSlideMarks.Declined)),
            N.Para(State("Grace is a gift", "s1", NoteSlideMarks.Suggested)),
            N.Para(State("Romans 5:8", "s2", NoteSlideMarks.Suggested)),
            N.Para("Closing"));
        Assert.Equal(["s1", "s2"], NoteSlideMarks.Groups(body).Select(g => g.Id));
        Assert.All(NoteSlideMarks.Groups(body), g => Assert.Equal(NoteSlideMarks.Suggested, g.State));

        var structured = NoteSlideMarks.MarkingByStructure(body);
        Assert.Equal(body.Blocks[0].Runs![0], structured.Blocks[0].Runs![0]);
        Assert.NotNull(structured.Blocks[3].Runs![0].SlideGroup);

        body.Blocks[3].Runs = [Run("Closing", "m")];
        Assert.Equal(["s1", "s2", "m"], NoteSlideMarks.Groups(body).Select(g => g.Id));
        Assert.Equal([NoteSlideMarks.Suggested, NoteSlideMarks.Suggested, null], NoteSlideMarks.Groups(body).Select(g => g.State));
        Assert.Equal(["s1", "s2", "m"], NoteSlideBuilder.Groups(body).Select(g => g.Id));

        var accepted = NoteSlideMarks.AcceptingSuggestions(body);
        Assert.Equal([NoteSlideMarks.Declined], accepted.Blocks.Select(b => b.Runs?[0].SlideState).OfType<string>());
        Assert.Equal(["s1", "s2", "m"], NoteSlideMarks.Groups(accepted).Select(g => g.Id));
        var stamped = NoteSlideMarks.Stamping("x", "s1", body);
        Assert.True(stamped.Blocks[1].Runs![0].SlideState is null && stamped.Blocks[1].Runs![0].SlideId == "x");
        Assert.Equal(Run("Welcome"), NoteSlideMarks.Unmarking("d", body).Blocks[0].Runs![0]);
        Assert.Equal(Run("Romans 5:8", "n"), NoteSlideMarks.Marking([2], "n", null, body).Blocks[2].Runs![0]);
    }

    [Fact]
    public void UnmarkedBodiesHaveNoGroupsAndIdsMintShort()
    {
        var body = new NoteBody { Blocks = [N.Para(Run("hi"), Run("", ""))], PlainText = "hi" };
        Assert.Empty(NoteSlideMarks.Groups(body));
        var id = NoteSlideMarks.MintGroupId();
        Assert.True(id.Length == 6 && id.All(char.IsLetterOrDigit));
        Assert.True(NoteSlideMarks.MintGroupId() != id || NoteSlideMarks.MintGroupId() != id);
        Assert.Equal("Scripture", NoteSlideMarks.SlideType.Scripture.Label());
    }

    [Fact]
    public void AGroupKeepsItsUnderlinedWordsOncePerRunInOrder()
    {
        var under = new NoteRun { Text = "reverence", Underline = true, SlideGroup = "a" };
        var body = N.Body(N.Para(
            Run("So we worship with ", "a"), under, new NoteRun { Text = " ", Underline = true, SlideGroup = "a" },
            new NoteRun { Text = "and awe", Underline = true, SlideGroup = "a" }, Run(" and ", "a"),
            new NoteRun { Text = "joy", Underline = true, SlideGroup = "a" },
            new NoteRun { Text = "outside", Underline = true }));
        Assert.Equal(["reverence and awe", "joy"], NoteSlideMarks.Groups(body)[0].Underlines);
        var plain = under.Copy();
        plain.Underline = null;
        Assert.Empty(NoteSlideMarks.Groups(N.Body(N.Para(plain)))[0].Underlines);
    }
}

/// <summary>Port of the reading parts of the Mac's NoteDocumentTests.swift, and LocalNotesTests.swift.</summary>
public class NoteDocumentTests
{
    private static readonly NoteBody ReadingBody = new()
    {
        Blocks =
        [
            N.Heading("Grace", 1),
            N.Para(new NoteRun { Text = "Grace upon " }, new NoteRun { Text = "grace", Bold = true }),
            N.Heading("  ", 2),
            new NoteBlock
            {
                Type = NoteBlockType.List, Ordered = false,
                Items = [new NoteListItem { Runs = [new NoteRun { Text = "Gráce here" }], Blocks = [N.Para("and grace")] }],
            },
            N.Heading("Close", 3),
        ],
        PlainText = "",
    };

    [Fact]
    public void OutlineListsNonEmptyHeadings() =>
        Assert.Equal([new NoteReading.OutlineEntry(0, 1, "Grace"), new NoteReading.OutlineEntry(4, 3, "Close")], NoteReading.Outline(ReadingBody));

    [Fact]
    public void FindMatchesEveryOccurrenceInsensitively()
    {
        Assert.Equal([new NoteReading.Match(0, 0), new NoteReading.Match(1, 0), new NoteReading.Match(1, 11), new NoteReading.Match(3, 0), new NoteReading.Match(3, 15)],
            NoteReading.Matches(ReadingBody, "grace"));
        Assert.Empty(NoteReading.Matches(ReadingBody, "  "));
    }

    private static Presentation Deck() => new()
    {
        Id = "p1", Name = "Sermon", PresentationKind = PresentationKind.Deck, ThemeId = "",
        Slides = [new Slide { Id = "s1", Name = "One", Objects = [] }, new Slide { Id = "s2", Name = "Two", Objects = [] }],
    };

    [Fact]
    public void SlideRefsResolveByIdThenIndexAndNameWhatIsMissing()
    {
        var deck = Deck();
        Presentation? Lookup(string id) => id == "p1" ? deck : null;
        var reference = new NoteSlideRef { PresentationId = "p1", SlideId = "s2", SlideIndex = 0 };
        Assert.Equal(new NoteSlides.Resolution.Found(deck, deck.Slides[1], 1), NoteSlides.Resolve(reference, Lookup));
        reference.SlideId = "gone";
        Assert.Equal(new NoteSlides.Resolution.Found(deck, deck.Slides[0], 0), NoteSlides.Resolve(reference, Lookup));
        reference.SlideIndex = 9;
        Assert.Equal(new NoteSlides.Resolution.MissingSlide(deck), NoteSlides.Resolve(reference, Lookup));
        reference.PresentationId = "nope";
        Assert.IsType<NoteSlides.Resolution.MissingPresentation>(NoteSlides.Resolve(reference, Lookup));
        Assert.IsType<NoteSlides.Resolution.NotPresenter>(NoteSlides.Resolve(new NoteSlideRef { PpIntegrationHexId = "i1" }, Lookup));
    }

    [Fact]
    public void LiveSlideFindsItsBlock()
    {
        var body = N.Body(N.Para("Read"), N.Card("p1", "s1"), N.Card("p1", "s2"));
        Assert.Equal(2, NoteSlides.BlockIndexOfLiveSlide("s2", body));
        Assert.Null(NoteSlides.BlockIndexOfLiveSlide("s9", body));
    }

    [Fact]
    public void ADroppedTileBecomesAFullRef()
    {
        var presentation = Deck();
        presentation.Slides[1].Objects = [N.Text("", "Grace upon grace")];
        var reference = NoteSlides.DropRef("p1", "s2", id => id == "p1" ? presentation : null);
        Assert.True(reference?.SlideIndex == 1 && reference.PresentationName == "Sermon" && reference.SlideLabel == "Two");
        Assert.True(reference.SlideText == "Grace upon grace" && reference.ShowThumbnail == true && reference.ShowNumber == true);
        Assert.Null(NoteSlides.DropRef("p1", "nope", _ => presentation));
    }

    [Fact]
    public void SlideMarkersBecomeTheDeckSlidesInOrder()
    {
        var body = N.Body(N.Para(" [Slide] "), N.Para("Luke 11:27"), N.Para("[slide]"), N.Para("[slide]"), N.Heading("[slide]"));
        Assert.Equal([0, 2, 3], NoteSlides.MarkerBlockIndexes(body));
        var placed = NoteSlides.PlaceSlides(body, Deck());
        Assert.True(placed.Blocks[0].Type == NoteBlockType.SlideRef && placed.Blocks[0].Slide?.SlideId == "s1");
        Assert.Equal([new NoteRun { Text = "Luke 11:27" }], placed.Blocks[1].Runs!);
        Assert.True(placed.Blocks[2].Slide?.SlideId == "s2" && placed.Blocks[2].Slide?.SlideIndex == 1);
        Assert.Equal(NoteBlockType.Paragraph, placed.Blocks[3].Type); // more markers than slides: the marker stays
        Assert.Equal(NoteBlockType.Heading, placed.Blocks[4].Type); // a heading is never a marker
        Assert.Equal("Sermon\nLuke 11:27\nSermon\n[slide]\n[slide]", placed.PlainText);
    }

    [Fact]
    public void ANewNoteIsEmptyAndLocal()
    {
        var note = LocalNotes.Note("Sunday", null);
        Assert.Equal(LocalNotes.Origin, note.Origin);
        Assert.Equal("Sunday", note.Name);
        Assert.Equal(new NoteBody { Blocks = [], PlainText = "" }, note.Body);
        Assert.NotEqual(note.Id, LocalNotes.Note("A", null).Id);
    }

    [Fact]
    public void APastedNoteIsNamedAfterItsFirstLine()
    {
        Assert.Equal("Unshakable", LocalNotes.PastedName("\n  Unshakable \nHebrews 12"));
        Assert.Equal("Untitled Note", LocalNotes.PastedName(" \n\n"));
        Assert.Equal(new string('a', 59) + "…", LocalNotes.PastedName(new string('a', 80)));
    }
}
