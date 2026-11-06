using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;

namespace MxU.Slides.Core.Tests.Notes;

/// <summary>Port of the Mac's NoteDeckCardsTests.swift.</summary>
public class NoteDeckCardsTests
{
    private static Slide Slide(string id, string text = "") =>
        new() { Id = id, Name = "", Objects = text.Length == 0 ? [] : [new SlideObject { Id = $"{id}-t", ObjectKind = SlideObjectKind.Text, Name = "Text", Text = text }] };

    private static NoteBlock Card(string slideId, bool? hand = null, string deck = "p") => N.Card(deck, slideId, hand);

    private static Presentation Deck() => new()
    {
        Id = "p", Name = "Message", PresentationKind = PresentationKind.Deck, ThemeId = "t", Slides = [Slide("s1", "One"), Slide("s2", "Two"), Slide("s3", "Three")],
    };

    private static NoteBody Body() => new()
    {
        Blocks = [N.Para("Intro"), N.Para(N.Run("Point one", "a", slideId: "s1")), Card("s1"), N.Para(N.Run("Point two", "b", slideId: "s3")), Card("s3")],
        PlainText = "Intro\nPoint one\nOne\nPoint two\nThree",
    };

    private static List<string?> Ids(NoteBody body) => body.Blocks.Select(b => b.Slide?.SlideId).Where(s => s is not null).ToList();

    [Fact]
    public void ASlideAddedBetweenMadeSlidesGetsItsCardAfterTheEarlierOne()
    {
        var synced = NoteDeckCards.Syncing(Deck(), new HashSet<string> { "s2" }, new HashSet<string>(), Body());
        Assert.Equal(["s1", "s2", "s3"], Ids(synced));
        var placed = synced.Blocks[3].Slide;
        Assert.True(placed?.PlacedByHand == true && placed.SlideText == "Two" && placed.SlideIndex == 1);
        Assert.Contains("Two", synced.PlainText);
        Assert.Equal(["s1"], NoteSlideUpdate.CardsAfterGroup("a", synced).Select(c => c.SlideId));
    }

    [Fact]
    public void ASlideAheadOfEveryCardedSlideLandsBeforeTheDecksFirstCard()
    {
        var grown = Deck();
        grown.Slides.Insert(0, Slide("s0", "Title"));
        var synced = NoteDeckCards.Syncing(grown, new HashSet<string> { "s0" }, new HashSet<string>(), Body());
        Assert.Equal(["s0", "s1", "s3"], Ids(synced));
        Assert.Equal("s0", synced.Blocks[2].Slide?.SlideId);
    }

    [Fact]
    public void ANoteWithNoCardFromTheDeckTakesTheCardAtTheEnd()
    {
        var plain = new NoteBody { Blocks = [N.Para("Just text")], PlainText = "Just text" };
        var synced = NoteDeckCards.Syncing(Deck(), new HashSet<string> { "s2" }, new HashSet<string>(), plain);
        Assert.True(synced.Blocks.Count == 2 && synced.Blocks[^1].Slide?.SlideId == "s2");
        Assert.Equal(["s2", "s3"], Ids(NoteDeckCards.Syncing(Deck(), new HashSet<string> { "s2", "s3" }, new HashSet<string>(), plain)));
    }

    [Fact]
    public void ARemovedSlideTakesOnlyItsHandCardAndMadeCardsStay()
    {
        var withHand = Body();
        withHand.Blocks.Insert(3, Card("s2", true));
        var shrunk = Deck();
        shrunk.Slides.RemoveAll(s => s.Id is "s2" or "s3");
        Assert.Equal(["s1", "s3"], Ids(NoteDeckCards.Syncing(shrunk, new HashSet<string>(), new HashSet<string> { "s2", "s3" }, withHand)));
        var other = Body();
        other.Blocks.Add(Card("s2", true, "q"));
        Assert.Equal(other, NoteDeckCards.Syncing(shrunk, new HashSet<string>(), new HashSet<string> { "s2" }, other));
    }

    [Fact]
    public void TheShelfIsTheDecksSlidesWithNoCardInDeckOrder()
    {
        Assert.Equal(["s2"], NoteDeckCards.Unplaced(Deck(), Body()).Select(s => s.Id));
        var other = Body();
        other.Blocks.Add(Card("s2", true, "q"));
        Assert.Equal(["s2"], NoteDeckCards.Unplaced(Deck(), other).Select(s => s.Id));
        Assert.Empty(NoteDeckCards.Unplaced(Deck(), NoteDeckCards.Syncing(Deck(), new HashSet<string> { "s2" }, new HashSet<string>(), Body())));
    }

    [Fact]
    public void NothingToPlaceReturnsTheBodyUntouched()
    {
        Assert.Equal(Body(), NoteDeckCards.Syncing(Deck(), new HashSet<string> { "s1", "zz" }, new HashSet<string>(), Body()));
        Assert.Equal(Body(), NoteDeckCards.Syncing(Deck(), new HashSet<string>(), new HashSet<string> { "zz" }, Body()));
    }
}

/// <summary>Port of the Mac's NotePanelsTests.swift and NotePassagesTests.swift.</summary>
public class NotePanelsTests
{
    private static NoteBlock Marked(string text, string group, string? slideId = null, string? type = null) => N.Para(N.Run(text, group, type, slideId));

    private static NoteBlock Card(string slideId, int index, bool byHand = false) =>
        new() { Type = NoteBlockType.SlideRef, Slide = new NoteSlideRef { PresentationId = "deck", SlideId = slideId, SlideIndex = index, PlacedByHand = byHand ? true : null } };

    [Fact]
    public void PanelsPairEachPassageWithTheCardsAfterIt()
    {
        var note = N.Body(N.Para("Intro"), Marked("For God so loved", "g1", "s1"), Card("s1", 0), Card("s2", 1), N.Para("Between"), Marked("Point one", "g2", type: "points"));
        var panels = NotePanels.Panels(note);
        Assert.Equal(2, panels.Count);
        Assert.Equal(new NotePanels.Panel("g1", "g1", null, true, (1, 1), [2, 3], 1), panels[0]);
        Assert.Equal(new NotePanels.Panel("g2", "g2", "points", false, (5, 5), [], 1), panels[1]);
    }

    [Fact]
    public void HandPlacedCardsStandAloneAndNeverJoinAPassage()
    {
        var panels = NotePanels.Panels(N.Body(Marked("Words", "g1", "s1"), Card("s1", 0), Card("s9", 8, true), N.Para("Prose"), Card("s5", 4)));
        Assert.Equal(["g1", "card:s9", "card:s5"], panels.Select(p => p.Id));
        Assert.Equal([1], panels[0].Cards);
        Assert.True(panels[1].PlacedByHand && panels[1].Words is null && panels[1].Cards.SequenceEqual([2]));
        Assert.Equal([4], panels[2].Cards);
    }

    [Fact]
    public void CardsUnderAMarkerBelongToThePassageBelowThemAndNeverTwice()
    {
        var panels = NotePanels.Panels(N.Body(Marked("First passage", "g1", "s1"), Card("s1", 0), Card("s2", 1), Marked("Second passage", "g2", "s2")));
        Assert.Equal([1], panels[0].Cards);
        Assert.Equal([2], panels[1].Cards);
        Assert.True(panels[1].FirstBlock == 2 && panels[1].LastBlock == 3);
    }

    [Fact]
    public void PanelLinesCountEveryMarkedLine()
    {
        var note = N.Body(new NoteBlock
        {
            Type = NoteBlockType.List, Ordered = true,
            Items = new[] { "One", "Two", "Three" }.Select(t => new NoteListItem { Runs = [N.Run(t, "g")] }).ToList(),
        });
        Assert.Equal(3, NotePanels.Panels(note)[0].Lines);
        Assert.Equal("3 lines", NotePanels.FoldedTitle(3));
        Assert.Equal("1 line", NotePanels.FoldedTitle(1));
    }

    [Fact]
    public void PanelTitlesReadAsRangesWhenTheNumbersRun()
    {
        Assert.Null(NotePanels.Title([]));
        Assert.Equal("Slide 3", NotePanels.Title([3]));
        Assert.Equal("Slides 3 to 5", NotePanels.Title([5, 3, 4]));
        Assert.Equal("Slides 3, 7", NotePanels.Title([3, 7]));
    }

    private static NoteBody Stacked() => N.Body(
        N.Card("p", "s1"), Marked("First", "a", "s1"), N.Card("p", "s2"), Marked("Second", "b", "s2"), Marked("Third", "c"),
        N.Card("p", "s3"), N.Card("p", "h1", true));

    [Fact]
    public void PassagesMatchThePerGroupReads()
    {
        var stacked = Stacked();
        var passages = new NotePassages(stacked);
        Assert.Equal(NoteSlideBuilder.Groups(stacked), passages.Groups);
        foreach (var group in passages.Groups)
            Assert.Equal(NoteSlideUpdate.CardsAfterGroup(group.Id, stacked), passages.Cards[group.Id]);
        Assert.Equal(["s3"], passages.Cards["c"].Select(c => c.SlideId)); // a hand-placed card is not the passage's
    }

    [Fact]
    public void ACardFindsItsPassage()
    {
        var passages = new NotePassages(Stacked());
        Assert.Equal("b", passages.PassageContainingSlide("s2")?.Group.Id);
        Assert.Equal(["s3"], passages.PassageContainingSlide("s3")?.Cards.Select(c => c.SlideId));
        Assert.Null(passages.PassageContainingSlide("h1"));
        Assert.Null(passages.PassageContainingSlide("gone"));
    }

    [Fact]
    public void TheMemoParsesOncePerBody()
    {
        var memo = new NotePassagesMemo();
        var stacked = Stacked();
        for (var i = 0; i < 20; i++) memo.Passages(stacked);
        Assert.Equal(1, memo.Parses);
        memo.Passages(stacked.Copy());
        Assert.Equal(1, memo.Parses);
        var edited = stacked.Copy();
        edited.Blocks[4].Runs = [N.Run("Third, edited", "c")];
        Assert.Equal("Third, edited", memo.Passages(edited).Groups[^1].Text);
        Assert.Equal(2, memo.Parses);
    }

    [Fact]
    public void PanelsReadEachPassagesCardsFromTheOneParse()
    {
        var panels = NotePanels.Panels(Stacked());
        Assert.Equal([0], panels.First(p => p.GroupId == "a").Cards);
        Assert.Equal([2], panels.First(p => p.GroupId == "b").Cards);
        Assert.Equal([5], panels.First(p => p.GroupId == "c").Cards);
    }
}

/// <summary>Port of the Mac's NoteSlidesMadeTests.swift.</summary>
public class NoteSlidesMadeTests
{
    private static NoteBody Body(params string[] lines) => new() { Blocks = lines.Select(N.Para).ToList(), PlainText = string.Join("\n", lines) };

    [Fact]
    public void FirstRunMarksByStructureAndMakesEverySlide()
    {
        var made = NoteSlides.Make(Body("Grace is a gift.", "Romans 5:8 For God so loved"), "", new SlideDesignMap(), _ => null);
        Assert.Equal(2, made.NewSlides.Count);
        Assert.True(made.MarkedByStructure); // the first run marked the note itself
        Assert.Equal(2, NoteSlideMarks.Groups(made.Body).Count); // every paragraph became a passage
        Assert.Equal(made.GroupOrder, made.NewSlides.Select(s => made.GroupBySlideId[s.Id]));
        Assert.DoesNotContain(made.Body.Blocks, b => b.Type == NoteBlockType.SlideRef); // the note takes marks, never cards
    }

    [Fact]
    public void ANoteMarkedByHandMakesNothingExtraOnAFirstRun()
    {
        var body = Body("Intro", "Point one", "Aside", "Point two", "Closing");
        body.Blocks[1].Runs![0].SlideGroup = "g1";
        body.Blocks[3].Runs![0].SlideGroup = "g2";
        var first = NoteSlides.Make(body, "", new SlideDesignMap(), _ => null);
        Assert.True(first.NewSlides.Count == 2 && first.MarkedByStructure is null); // marks by hand: nothing else is marked
    }

    [Fact]
    public void AFirstRunUsesTheNotesStoredSuggestionsInsteadOfMarkingOnItsOwn()
    {
        var body = Body("Welcome everyone", "Grace is a gift.", "An aside", "Romans 5:8 For God so loved");
        void Mark(int block, string group, string? state)
        {
            body.Blocks[block].Runs![0].SlideGroup = group;
            body.Blocks[block].Runs![0].SlideState = state;
        }
        Mark(0, "d", NoteSlideMarks.Declined);
        Mark(1, "s1", NoteSlideMarks.Suggested);
        Mark(3, "s2", NoteSlideMarks.Suggested);
        var made = NoteSlides.Make(body, "", new SlideDesignMap(), _ => null);
        Assert.True(made.NewSlides.Count == 2 && made.GroupOrder.SequenceEqual(["s1", "s2"]));
        Assert.Null(made.MarkedByStructure); // the suggestions stood in for marking by structure
        Assert.Null(made.Body.Blocks[2].Runs![0].SlideGroup); // unsuggested prose stays prose
        Assert.Equal(NoteSlideMarks.Declined, made.Body.Blocks[0].Runs![0].SlideState);
        Assert.Null(made.Body.Blocks[1].Runs![0].SlideState); // the first run confirmed the suggestions it used

        body.Blocks[2].Runs![0].SlideGroup = "m";
        var marked = NoteSlides.Make(body, "", new SlideDesignMap(), _ => null);
        Assert.True(marked.NewSlides.Count == 3 && marked.GroupOrder.SequenceEqual(["s1", "m", "s2"]));
        Assert.Null(marked.Body.Blocks[1].Runs![0].SlideState);
    }
}
