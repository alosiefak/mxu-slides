using System.Text.Json.Nodes;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;

namespace MxU.Slides.Core.Tests.Notes;

/// <summary>Port of the Mac's NoteLiveUpdatesTests.swift, on the shared slide-markers fixture.</summary>
public class NoteLiveUpdatesTests
{
    private sealed class Made
    {
        public required NoteBody Body { get; init; }
        public required Presentation Deck { get; set; }
        public required List<NoteMadePassage> Memory { get; set; }
        public required string Mode { get; set; }
    }

    private static Made FirstDeck()
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(MakeSlidesFixturesTests.Folder, "slide-markers.json")))!.AsObject();
        var email = MakeSlidesFixturesTests.Body(json);
        var made = NoteSlides.Make(email, "", new SlideDesignMap(), _ => null);
        var deck = new Presentation { Id = "deck", Name = "Message", PresentationKind = PresentationKind.Deck, ThemeId = "", Slides = made.NewSlides };
        return new Made { Body = made.Body, Deck = deck, Memory = NoteLiveUpdates.Memory(made.Body, deck, made.GroupBySlideId), Mode = NoteLiveUpdates.BuildMode(email) };
    }

    private static NoteLiveUpdates.Found Changes(Made made, NoteBody body, IReadOnlyList<NoteSkippedUpdate>? skipped = null) =>
        NoteLiveUpdates.Changes(body, made.Deck, made.Memory, made.Mode, skipped ?? [], new SlideDesignMap(), _ => null);

    private static int IndexOf(string text, NoteBody body)
    {
        var index = body.Blocks.FindIndex(b => NoteReading.Text(b) == text);
        Assert.True(index >= 0, text);
        return index;
    }

    private static NoteBody Retyping(string text, string typed, NoteBody body)
    {
        var copy = body.Copy();
        var at = IndexOf(text, copy);
        copy.Blocks[at].Runs = copy.Blocks[at].Runs?.Select(r => r.WithText(r.Text.Replace(text, typed))).ToList();
        return copy;
    }

    private const string Question = "What is it about prosperity that can cause us to forget God?";
    private const string Talk = "That's when I think about my grandfather's farm, and the summer the creek ran dry.";

    private static List<string> Texts(IEnumerable<NoteMadeBox>? boxes) => boxes?.Select(b => b.Text).ToList() ?? [];

    [Fact]
    public void TheFirstDeckIsRememberedPassageByPassageWithNoCardsInTheNote()
    {
        var made = FirstDeck();
        Assert.Equal(NoteSlideMarks.Groups(made.Body).Count, made.Memory.Count);
        Assert.Equal(made.Deck.Slides.Count, made.Memory.SelectMany(m => m.Slides).Count());
        Assert.DoesNotContain(made.Body.Blocks, b => b.Type == NoteBlockType.SlideRef); // the note holds no cards
        Assert.Empty(Changes(made, made.Body).Updates); // an untouched note waits on nothing
    }

    [Fact]
    public void WhatTheFirstDeckLeftOutWaitsForNoOneUntilItsWordsChange()
    {
        var made = FirstDeck();
        var marked = made.Body.Copy();
        marked.Blocks.Insert(IndexOf(Question, marked) + 1, N.Para("Next step: Pray for one neighbour."));
        var found = Changes(made, marked);
        Assert.NotEmpty(found.Updates);
        var skipped = NoteLiveUpdates.Baseline(found);
        Assert.Empty(Changes(made, marked, skipped).Updates);
        var edited = Retyping("Next step: Pray for one neighbour.", "Next step: Pray for two neighbours.", marked);
        Assert.Single(Changes(made, edited, skipped).Updates);
    }

    [Fact]
    public void OneChangedWordIsAnUpdateOnThatSlideAlone()
    {
        var made = FirstDeck();
        const string comfort = "What is it about comfort that can cause us to forget God?";
        var edited = Retyping(Question, comfort, made.Body);
        var found = Changes(made, edited);
        var update = Assert.Single(found.Updates);
        Assert.Equal(NotePendingUpdates.Words, update.Kind);
        Assert.Equal([comfort], Texts(update.After));
        Assert.Equal([Question], Texts(NoteSlideMatch.Boxes(made.Deck.Slides.First(s => s.Id == update.SlideId))));

        var applied = NoteLiveUpdates.Applying(update, made.Deck, made.Memory, edited, made.Mode, "now");
        Assert.Null(applied.Body); // an update never writes the note
        Assert.Equal(made.Deck.Slides.Count, applied.Deck.Slides.Count);
        var slide = applied.Deck.Slides.First(s => s.Id == update.SlideId);
        Assert.Equal([comfort], Texts(NoteSlideMatch.Boxes(slide)));
        Assert.Equal(NoteSlides.UpdatedReason, slide.History?[0].Reason);
        made.Deck = applied.Deck;
        made.Memory = applied.Memory;
        Assert.Empty(Changes(made, edited).Updates); // applied: nothing waits

        made.Memory = NoteLiveUpdates.SettlingTexts(made.Memory, edited, []);
        var back = Changes(made, made.Body);
        Assert.Equal([update.Id], back.Updates.Select(u => u.Id));
        Assert.Equal([Question], Texts(back.Updates[0].After));
    }

    [Fact]
    public void ANewLabelledLineUnderAPassageIsAnAddAfterItsSlides()
    {
        var made = FirstDeck();
        var edited = made.Body.Copy();
        edited.Blocks.Insert(IndexOf(Question, edited) + 1, N.Para("Next step: Write down one idol you'll hand back to God this week."));
        var update = Assert.Single(Changes(made, edited).Updates);
        Assert.Equal(NotePendingUpdates.New, update.Kind);
        var questionGroup = NoteSlideMarks.Groups(made.Body).First(g => g.Text.StartsWith("What is it about", StringComparison.Ordinal));
        var questionSlide = made.Memory.First(m => m.GroupId == questionGroup.Id).Slides[^1].SlideId;
        Assert.Equal(questionSlide, update.AfterSlideId); // the Add lands after the passage before it

        var applied = NoteLiveUpdates.Applying(update, made.Deck, made.Memory, edited, made.Mode, "now");
        var body = applied.Body;
        Assert.NotNull(body); // the new words take a mark: a marks-only write
        Assert.Contains(NoteSlideMarks.Groups(body), g => g.Id == update.GroupId && g.Text.StartsWith("Next step", StringComparison.Ordinal));
        var at = applied.Deck.Slides.FindIndex(s => s.Id == questionSlide);
        Assert.Equal(update.Proposed?.Id, applied.Deck.Slides[at + 1].Id);
        made.Deck = applied.Deck;
        made.Memory = applied.Memory;
        Assert.Empty(Changes(made, body).Updates);
    }

    [Fact]
    public void ALabelledLineOutsideTheMarkersIsAnAddToo()
    {
        var made = FirstDeck();
        var edited = made.Body.Copy();
        edited.Blocks.InsertRange(IndexOf(Question, edited) + 1,
            [new NoteBlock { Type = NoteBlockType.Paragraph, Runs = [] }, N.Para("Next step: Write down one idol you'll hand back to God this week.")]);
        var found = Changes(made, edited);
        var update = Assert.Single(found.Updates);
        Assert.Equal(NotePendingUpdates.New, update.Kind);
        var questionGroup = NoteSlideMarks.Groups(made.Body).First(g => g.Text.StartsWith("What is it about", StringComparison.Ordinal));
        Assert.Equal(made.Memory.First(m => m.GroupId == questionGroup.Id).Slides[^1].SlideId, update.AfterSlideId);
        Assert.StartsWith("Next step", found.Texts[update.Id]);
    }

    [Fact]
    public void PlainTalkAfterAPassageStaysNotes()
    {
        var made = FirstDeck();
        var edited = made.Body.Copy();
        edited.Blocks.Insert(IndexOf(Talk, edited) + 1, N.Para("And it rained the next week, which nobody expected."));
        Assert.Empty(Changes(made, edited).Updates);
    }

    [Fact]
    public void ADeletedPassageIsARemoveOfEachOfItsSlides()
    {
        var made = FirstDeck();
        var edited = made.Body.Copy();
        edited.Blocks.RemoveRange(IndexOf("Slide 3:", edited), 2);
        var found = Changes(made, edited);
        Assert.Equal([NotePendingUpdates.Remove], found.Updates.Select(u => u.Kind));
        var update = found.Updates[0];
        var applied = NoteLiveUpdates.Applying(update, made.Deck, made.Memory, edited, made.Mode, "now");
        Assert.DoesNotContain(applied.Deck.Slides, s => s.Id == update.SlideId);
        Assert.Equal(made.Memory.Count - 1, applied.Memory.Count);
        made.Deck = applied.Deck;
        made.Memory = applied.Memory;
        Assert.Empty(Changes(made, edited).Updates);
    }

    [Fact]
    public void ASkipStaysSkippedUntilThePassageChangesAgain()
    {
        var made = FirstDeck();
        const string comfort = "What is it about comfort that can cause us to forget God?";
        var edited = Retyping(Question, comfort, made.Body);
        var found = Changes(made, edited);
        var update = found.Updates[0];
        var skipped = NoteLiveUpdates.Skipping(update.Id, found.Texts[update.Id], []);
        Assert.Empty(Changes(made, edited, skipped).Updates);
        var again = Retyping(comfort, "What is it about success that can cause us to forget God?", edited);
        Assert.Equal([update.Id], Changes(made, again, skipped).Updates.Select(u => u.Id)); // new words: the change is back
    }

    [Fact]
    public void AListItemTypedIntoAPassageIsItsWords()
    {
        var made = FirstDeck();
        var edited = made.Body.Copy();
        var list = edited.Blocks.FindIndex(b => b.Type == NoteBlockType.List && NoteReading.Text(b).Contains("The idol of comfort"));
        edited.Blocks[list].Items!.Add(new NoteListItem { Runs = [new NoteRun { Text = "The idol of approval" }] });
        var found = Changes(made, edited);
        Assert.NotEmpty(found.Updates);
        Assert.All(found.Updates, u => Assert.NotEqual(NotePendingUpdates.Remove, u.Kind)); // the passage is still there
        Assert.Contains(found.Updates, u => (u.After ?? (u.Proposed is { } p ? NoteSlideMatch.Boxes(p) : [])).Any(b => b.Text.Contains("The idol of approval")));
    }

    [Fact]
    public void APasteThatLostTheMarksFindsItsPassagesByTheirWords()
    {
        var made = FirstDeck();
        var found = Changes(made, NoteLiveUpdates.Unmarked(made.Body));
        var worded = made.Memory.Where(m => m.Text.Length > 0).ToList();
        Assert.All(found.Updates, u => Assert.DoesNotContain(worded, m => m.GroupId == u.GroupId));
        Assert.NotNull(found.Remarked);
        Assert.True(NoteSlideMarks.Groups(found.Remarked).Select(g => g.Id).ToHashSet().IsSupersetOf(worded.Select(m => m.GroupId)));
    }

    [Fact]
    public void ALiveNotesDeletedMarkIsARemove()
    {
        var made = FirstDeck();
        var found = NoteLiveUpdates.Changes(NoteLiveUpdates.Unmarked(made.Body), made.Deck, made.Memory, made.Mode, [], new SlideDesignMap(), _ => null, refind: false);
        Assert.Null(found.Remarked);
        Assert.All(made.Memory.Where(m => m.Text.Length > 0),
            passage => Assert.Contains(found.Updates, u => u.GroupId == passage.GroupId && u.Kind == NotePendingUpdates.Remove));
    }

    [Fact]
    public void TheReaderShowsMadeSlidesUnderTheirWordsAndSavesNone()
    {
        var made = FirstDeck();
        var shown = NoteLiveUpdates.DisplayBody(made.Body, made.Memory, made.Deck);
        var remembered = made.Memory.SelectMany(m => m.Slides.Select(s => s.SlideId)).ToHashSet();
        Assert.Equal(made.Deck.Slides.Count(s => remembered.Contains(s.Id)), shown.Blocks.Count(b => b.Type == NoteBlockType.SlideRef));

        var marker = IndexOf("Slide 3:", shown);
        Assert.Equal(Question, NoteReading.Text(shown.Blocks[marker + 1]));
        Assert.Equal(NoteBlockType.SlideRef, shown.Blocks[marker + 2].Type);
        Assert.Equal(made.Memory.Count, NoteSlideMarks.Groups(shown).Count(g => g.SlideId is not null)); // Make Slides reads them as made
        Assert.Equal(made.Body.Blocks, NoteLiveUpdates.SavedBody(shown, made.Deck.Id).Blocks);

        var hand = NoteSlides.HandRef("deck", made.Deck.Slides[0].Id, _ => made.Deck)!;
        var withCards = made.Body.Copy();
        withCards.Blocks.Add(new NoteBlock { Type = NoteBlockType.SlideRef, Slide = hand });
        withCards.Blocks.Insert(0, new NoteBlock { Type = NoteBlockType.SlideRef, Slide = NoteSlides.DropRef("deck", made.Deck.Slides[1].Id, _ => made.Deck) });
        var stripped = NoteLiveUpdates.SavedBody(withCards, "deck");
        Assert.Equal([true], stripped.Blocks.Where(b => b.Type == NoteBlockType.SlideRef).Select(b => b.Slide?.PlacedByHand));
    }

    [Fact]
    public void AMarkedPassageWithNoSlidesIsAnAddAndANoteMarkedByHandProposesNothingElse()
    {
        var made = FirstDeck();
        made.Mode = NoteLiveUpdates.ByHand;
        var edited = made.Body.Copy();
        var talk = IndexOf(Talk, edited);
        Assert.Empty(Changes(made, edited).Updates);
        edited.Blocks[talk] = NoteSlideMarks.Marking(edited.Blocks[talk], "pastor", "quote");
        var found = Changes(made, edited);
        Assert.Equal([NotePendingUpdates.New], found.Updates.Select(u => u.Kind));
        Assert.Equal("pastor", found.Updates[0].GroupId);
    }

    [Fact]
    public void APassageWhoseSlidesTheOperatorDeletedIsNoAdd()
    {
        var made = FirstDeck();
        var gone = made.Memory.First(m => m.Text.StartsWith("What is it about", StringComparison.Ordinal));
        made.Deck = MxuJson.Clone(made.Deck);
        made.Deck.Slides.RemoveAll(s => gone.Slides.Any(g => g.SlideId == s.Id));
        Assert.Empty(Changes(made, made.Body).Updates);
    }

    [Fact]
    public void AddsSitInTheGridAfterTheSlideTheyFollow()
    {
        NotePendingUpdate[] updates =
        [
            new() { Id = "new:g:0", Kind = NotePendingUpdates.New, GroupId = "g", AfterSlideId = "b", Proposed = N.Slide("n1") },
            new() { Id = "new:g:1", Kind = NotePendingUpdates.New, GroupId = "g", AfterSlideId = "n1", Proposed = N.Slide("n2") },
            new() { Id = "new:h:0", Kind = NotePendingUpdates.New, GroupId = "h", Proposed = N.Slide("n3") },
            new() { Id = "words:a", Kind = NotePendingUpdates.Words, GroupId = "a", SlideId = "a" },
        ];
        NoteLiveUpdates.Cell Add(string id) => new NoteLiveUpdates.Cell.AddCell(id);
        NoteLiveUpdates.Cell At(int index) => new NoteLiveUpdates.Cell.SlideCell(index);
        Assert.Equal([Add("new:h:0"), At(0), At(1), Add("new:g:0"), Add("new:g:1"), At(2), At(3)], NoteLiveUpdates.Cells(["a", "b", "c", "b"], updates));
    }

    [Fact]
    public void TheWordDiffMarksWhatChanged()
    {
        var diff = NoteLiveUpdates.WordDiff("What are some ways God provided", "What are three ways God provided");
        Assert.Equal(["What are", "some", "three", "ways God provided"], diff.Select(d => d.Text));
        Assert.Equal([NoteLiveUpdates.Change.Kept, NoteLiveUpdates.Change.Removed, NoteLiveUpdates.Change.Added, NoteLiveUpdates.Change.Kept], diff.Select(d => d.Change));
    }

    [Fact]
    public void TheModeIsTheSuggestersPick()
    {
        var plain = N.Body(N.Para("Grace is a gift"));
        Assert.Equal("structure", NoteLiveUpdates.BuildMode(plain));
        var bold = N.Body(
            N.Para(new NoteRun { Text = "Grace is a gift", Bold = true }),
            N.Para("And we tell the story of a long week at the lake house."),
            N.Para(new NoteRun { Text = "Mercy is new every morning", Bold = true }),
            N.Para("The kids asked for pancakes again on Saturday."));
        Assert.Equal("boldLines", NoteLiveUpdates.BuildMode(bold));
        Assert.Equal("boldLines", NoteLiveUpdates.BuildMode(plain, NoteSlideMarks.SuggestMode.BoldLines));
    }
}
