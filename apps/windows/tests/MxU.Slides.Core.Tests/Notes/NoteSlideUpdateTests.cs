using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;

namespace MxU.Slides.Core.Tests.Notes;

/// <summary>Port of the Mac's NoteSlideUpdateTests.swift.</summary>
public class NoteSlideUpdateTests
{
    private static SlideObject TextObject(string name, string text, string? id = null, double? w = null, double? h = null) =>
        new() { Id = id ?? Guid.NewGuid().ToString(), ObjectKind = SlideObjectKind.Text, Name = name, Text = text, Width = w, Height = h };

    private static readonly Theme ScriptureTheme = new()
    {
        Id = "t", Name = "Sermon", FontFamily = "Helvetica", FontSize = 60, TextColorHex = "#fff", BackgroundColorHex = "#000",
        Slides = [new Slide { Id = "d", Name = "Scripture", Objects = [TextObject("Verse", "", w: 1600, h: 500), TextObject("Reference", "", w: 800, h: 80)] }],
    };

    private static NoteBlock Marked(string text, string group, string slideId) => N.Para(new NoteRun { Text = text, SlideGroup = group, SlideId = slideId });

    private static NoteSlideRef Ref(string id, bool? byHand = null) => new() { PresentationId = "p", SlideId = id, PlacedByHand = byHand };

    private static List<string?> Ids(NoteBody body) => body.Blocks.Select(b => b.Slide?.SlideId).Where(s => s is not null).ToList();

    [Fact]
    public void CardsAfterAPassageAreItsSlides()
    {
        var body = N.Body(Marked("Verse", "a", "s1"), N.Card("p", "s1"), N.Card("p", "s2"), N.Para("Between"), N.Card("p", "s9"));
        Assert.Equal(["s1", "s2"], NoteSlideUpdate.CardsAfterGroup("a", body).Select(c => c.SlideId));
        Assert.Empty(NoteSlideUpdate.CardsAfterGroup("none", body));

        var synced = NoteSlideUpdate.SyncingCards("a", [Ref("s3")], new HashSet<string> { "s2" }, body);
        Assert.Equal(["s1", "s3", "s9"], Ids(synced));
        Assert.Equal(NoteBlockType.Paragraph, synced.Blocks[3].Type);

        var hand = body.Copy();
        hand.Blocks.Insert(3, new NoteBlock { Type = NoteBlockType.SlideRef, Slide = Ref("h1", true) });
        Assert.Equal(["s1", "s2"], NoteSlideUpdate.CardsAfterGroup("a", hand).Select(c => c.SlideId));
        Assert.Equal(["s1", "s3", "h1", "s9"], Ids(NoteSlideUpdate.SyncingCards("a", [Ref("s3")], new HashSet<string> { "s2" }, hand)));

        var above = N.Body(N.Card("p", "s1"), Marked("Verse", "a", "s1"), N.Para("After"));
        Assert.Equal(["s1"], NoteSlideUpdate.CardsAfterGroup("a", above).Select(c => c.SlideId));
        var grown = NoteSlideUpdate.SyncingCards("a", [Ref("s2")], new HashSet<string>(), above);
        Assert.Equal([NoteBlockType.SlideRef, NoteBlockType.SlideRef, NoteBlockType.Paragraph, NoteBlockType.Paragraph], grown.Blocks.Select(b => b.Type));

        var stacked = N.Body(N.Card("p", "s1"), Marked("First", "a", "s1"), N.Card("p", "s2"), Marked("Second", "b", "s2"));
        Assert.Equal(["s1"], NoteSlideUpdate.CardsAfterGroup("a", stacked).Select(c => c.SlideId));
        Assert.Equal(["s2"], NoteSlideUpdate.CardsAfterGroup("b", stacked).Select(c => c.SlideId));
        Assert.Equal(["s1", "s1b", "s2"], Ids(NoteSlideUpdate.SyncingCards("a", [Ref("s1b")], new HashSet<string>(), stacked)));
    }

    [Fact]
    public void ASlideIsBehindWhenThePassageChangedAndUpdatesInPlace()
    {
        var group = new MarkedGroup("a", "For God so loved the world\nJohn 3:16");
        var slide = new Slide
        {
            Id = "s1", Name = "", ThemeSlideName = "Scripture",
            Objects = [TextObject("Verse", "For God so loved the world", "v"), TextObject("Reference", "John 3:16", "r"), new SlideObject { Id = "shape", ObjectKind = SlideObjectKind.Shape, Name = "Plate", Text = "" }],
        };
        slide.Objects[0].X = 100;
        Assert.False(NoteSlideUpdate.IsBehind(group, [slide], ScriptureTheme));

        var edited = new MarkedGroup("a", "For God so loved the whole world\nJohn 3:16-17");
        Assert.True(NoteSlideUpdate.IsBehind(edited, [slide], ScriptureTheme));
        var result = NoteSlideUpdate.Updating([slide], NoteSlideUpdate.Replan(edited, [slide], ScriptureTheme)!);
        Assert.True(result.Appended.Count == 0 && result.RemovedIds.Count == 0);
        var updated = result.Updated[0];
        Assert.Equal(["v", "r", "shape"], updated.Objects.Select(o => o.Id));
        Assert.True(updated.Objects[0].Text == "For God so loved the whole world" && updated.Objects[0].X == 100);
        Assert.Equal("John 3:16-17", updated.Objects[1].Text);
        Assert.False(NoteSlideUpdate.IsBehind(edited, [updated], ScriptureTheme));
        Assert.Equal("For God so loved the world", slide.Objects[0].Text); // the deck's own slide is untouched
    }

    [Fact]
    public void MoreOrFewerPartsAppendOrDropSlides()
    {
        var first = new Slide { Id = "s1", Name = "", Objects = [TextObject("Verse", "Line 1"), TextObject("Reference", "Ps 1")], ThemeSlideName = "Scripture", ThemeId = "other" };
        var lines = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"Line {i}"));
        var grown = NoteSlideUpdate.Updating([first], NoteSlideUpdate.Replan(new MarkedGroup("a", lines + "\nPsalm 1:1-8"), [first], ScriptureTheme)!);
        Assert.True(grown.Updated.Count == 1 && grown.Appended.Count == 1 && grown.RemovedIds.Count == 0);
        Assert.True(grown.Appended[0].ThemeSlideName == "Scripture" && grown.Appended[0].ThemeId == "other");
        Assert.Equal(["Verse", "Reference"], grown.Appended[0].Objects.Select(o => o.Name));

        var second = new Slide { Id = "s2", Name = "", Objects = [TextObject("Verse", "Line 7")] };
        var kept = new Slide { Id = "s3", Name = "", ThemeSlideName = "Scripture", Objects = [TextObject("Verse", "x"), new SlideObject { Id = "m", ObjectKind = SlideObjectKind.Media, Name = "Video", Text = "" }] };
        var shrunk = NoteSlideUpdate.Updating([first, second, kept], NoteSlideUpdate.Replan(new MarkedGroup("a", "Only one line\nPsalm 1:1"), [first], ScriptureTheme)!);
        Assert.Equal(["s1", "s3"], shrunk.Updated.Select(s => s.Id)); // a slide holding more than text stays
        Assert.Equal(["s2"], shrunk.RemovedIds);
    }

    [Fact]
    public void NewPartsCarryTheFirstSlidesActionsAndTheRowsSwitchReplacesPresetSwitches()
    {
        var slide = new Slide
        {
            Id = "s1", Name = "", ThemeSlideName = "Scripture", Objects = [TextObject("Verse", "Short", "v"), TextObject("Reference", "John 3:16", "r")],
            Actions =
            [
                new SlideAction { Id = "a1", Kind = SlideActionKind.SwitchOutputPreset, PresetId = "side" },
                new SlideAction { Id = "a2", Kind = SlideActionKind.ClearLayer, Layer = "videos" },
                new SlideAction { Id = "a3", Kind = SlideActionKind.FireCombo, ComboId = "mine" },
            ],
        };
        var lines = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"Line {i}"));
        var result = NoteSlideUpdate.Updating([slide], NoteSlideUpdate.Replan(new MarkedGroup("a", lines + "\nJohn 3:16"), [slide], ScriptureTheme)!);
        Assert.NotEmpty(result.Appended); // the passage grew past one slide
        var appended = result.Appended[0];
        Assert.Equal([SlideActionKind.SwitchOutputPreset, SlideActionKind.ClearLayer, SlideActionKind.FireCombo], appended.Actions!.Select(a => a.Kind));
        Assert.NotEqual(slide.Actions.Select(a => a.Id), appended.Actions!.Select(a => a.Id)); // fresh ids
        Assert.True(MxuJson.StructurallyEqual(slide.Actions, result.Updated[0].Actions)); // the kept slide keeps its own

        foreach (var s in NoteSlideUpdate.ApplyingSwitch(SlidePresetChoice.Preset("lower"), [.. result.Updated, .. result.Appended]))
            Assert.Equal(["videos", "mine", "lower"], s.Actions!.Select(a => a.PresetId ?? a.ComboId ?? a.Layer ?? "")); // the old switch goes, the rest stay, the new one lands
        Assert.Equal([SlideActionKind.ClearLayer, SlideActionKind.FireCombo], NoteSlideUpdate.ApplyingSwitch(SlidePresetChoice.Combo("mine"), [slide])[0].Actions!.Select(a => a.Kind)); // not added twice
        Assert.Equal([SlideActionKind.ClearLayer, SlideActionKind.FireCombo], NoteSlideUpdate.ApplyingSwitch(null, [slide])[0].Actions!.Select(a => a.Kind));
        var bare = MxuJson.Clone(slide);
        bare.Actions = [new SlideAction { Id = "x", Kind = SlideActionKind.SwitchOutputPreset, PresetId = "side" }];
        Assert.Null(NoteSlideUpdate.ApplyingSwitch(null, [bare])[0].Actions); // nothing left = no list
    }

    [Fact]
    public void MadeSlidesLiveOnlyWhileTheirDeckHoldsThem()
    {
        var deck = N.Deck("p", N.Slide("s1"), N.Slide("s2"));
        Presentation? Lookup(string id) => id == "p" ? deck : null;
        var living = NoteSlideUpdate.MadeSlidesOf(["s2", "gone"], "p", Lookup);
        Assert.True(living.IsMade);
        Assert.Equal(new Dictionary<string, int> { ["s2"] = 2 }, living.Positions);
        Assert.Equal(["gone"], living.Dead);
        var orphaned = NoteSlideUpdate.MadeSlidesOf(["s1", "s2"], "missing", Lookup);
        Assert.False(orphaned.IsMade);
        Assert.Equal(new HashSet<string> { "s1", "s2" }, orphaned.Dead);
        Assert.False(NoteSlideUpdate.MadeSlidesOf(["s1"], null, Lookup).IsMade);
    }
}

/// <summary>Port of the Mac's NoteSlideMatchTests.swift.</summary>
public class NoteSlideMatchTests
{
    private static NoteBlock OrderedList(params string[] items) =>
        new() { Type = NoteBlockType.List, Ordered = true, Items = items.Select(i => new NoteListItem { Runs = [new NoteRun { Text = i }] }).ToList() };

    private static NoteBody Body(params NoteBlock[] blocks) => new() { Blocks = [.. blocks], PlainText = string.Join("\n", blocks.Select(NoteReading.Text)) };

    internal static (NoteBody Body, Presentation Deck, List<NoteMadePassage> Memory) Sunday()
    {
        var first = NoteSlides.Make(Body(
            N.Heading("Waiting well"),
            N.Para("Wait for the LORD; be strong and take heart and wait for the LORD.\nPsalm 27:14"),
            OrderedList("Waiting is trusting God's timing", "Waiting is strengthening your heart", "Waiting is refusing to run ahead"),
            N.Para("Most of us treat waiting like a waiting room, a place you sit until the real thing starts."),
            N.Para("Be strong while you wait.")), "", new SlideDesignMap(), _ => null);
        var deck = new Presentation { Id = "d1", Name = "Waiting well", PresentationKind = PresentationKind.Deck, ThemeId = "", Slides = first.NewSlides };
        return (first.Body, deck, NoteLiveUpdates.Memory(first.Body, deck, first.GroupBySlideId));
    }

    [Fact]
    public void MemoryRemembersEveryMadePassage()
    {
        var (placed, deck, memory) = Sunday();
        Assert.Equal(5, memory.Count);
        Assert.Equal(NoteSlideMarks.Groups(placed).Select(g => g.Id), memory.Select(m => m.GroupId));
        Assert.All(memory, m => Assert.NotEmpty(m.Slides));
        Assert.Contains(memory[1].Slides[0].Boxes, b => b.Text.Contains("Psalm 27:14") || b.Text.Contains("Wait for the LORD"));
        Assert.Equal(deck.Slides.Select(s => s.Id).ToHashSet(), memory.SelectMany(m => m.Slides.Select(s => s.SlideId)).ToHashSet());
    }

    [Fact]
    public void APasteOverKeepsSlidesByWordsReferenceTitleAndSimilarity()
    {
        var (_, _, memory) = Sunday();
        var fresh = Body(
            N.Heading("Waiting Well"),
            OrderedList("1. Waiting is trusting in God's timing", "2. Waiting is strengthening your own heart", "3. Waiting is refusing to run on ahead"),
            N.Para("Wait patiently for the LORD. Be brave and courageous. Yes, wait patiently for the LORD.\nPsalm 27:14"),
            N.Para("Nothing about Tuesday afternoon feels like progress."),
            N.Para("So this week, sit in it."));
        var result = NoteSlideMatch.MatchPassages(fresh, memory);
        var rungs = result.Matches.GroupBy(m => m.GroupId).ToDictionary(g => g.Key, g => g.First().Rung);
        Assert.Equal(NoteSlideMatch.Rung.Text, rungs[memory[0].GroupId]); // the heading matched by its words
        Assert.Equal(NoteSlideMatch.Rung.Scripture, rungs[memory[1].GroupId]); // the verse matched by its reference
        Assert.Contains(rungs[memory[2].GroupId], new[] { NoteSlideMatch.Rung.Similar, NoteSlideMatch.Rung.Title }); // the points matched by their words
        Assert.True(!rungs.ContainsKey(memory[3].GroupId) && !rungs.ContainsKey(memory[4].GroupId));
        Assert.Equal([memory[3].GroupId, memory[4].GroupId], result.Orphans.Select(o => o.GroupId));

        var groups = NoteSlideMarks.Groups(result.Body);
        Assert.Equal(3, groups.Count); // only recognized passages are marked; new text stays prose
        Assert.Equal([memory[0].GroupId, memory[2].GroupId, memory[1].GroupId], groups.Select(g => g.Id)); // marks follow the new order
        Assert.All(groups, g => Assert.Equal(memory.First(m => m.GroupId == g.Id).Slides[0].SlideId, g.SlideId));
    }

    [Fact]
    public void PassagesStillMarkedKeepTheirMarksAndTwoAlikePointsDoNotSwap()
    {
        var (placed, _, memory) = Sunday();
        var typed = NoteSlideMatch.MatchPassages(placed, memory);
        Assert.All(typed.Matches, m => Assert.Equal(NoteSlideMatch.Rung.Mark, m.Rung));
        Assert.Empty(typed.Orphans);
        Assert.Equal(placed, typed.Body);

        var a = new NoteMadePassage { GroupId = "ga", Text = "Grace is a gift you cannot earn", Slides = [new NoteMadeSlide { SlideId = "sa", Boxes = [] }] };
        var b = new NoteMadePassage { GroupId = "gb", Text = "Grace is a gift you cannot lose", Slides = [new NoteMadeSlide { SlideId = "sb", Boxes = [] }] };
        var swapped = NoteSlideMatch.MatchPassages(Body(N.Para("Grace is a gift you cannot lose"), N.Para("Grace is a gift you cannot earn")), [a, b]);
        Assert.Equal(["gb", "ga"], NoteSlideMarks.Groups(swapped.Body).Select(g => g.Id));
        Assert.All(swapped.Matches, m => Assert.Equal(NoteSlideMatch.Rung.Text, m.Rung));
    }

    [Fact]
    public void RungsAndNormalization()
    {
        static NoteSlideMarks.Group Candidate(string text, NoteSlideMarks.Shape shape = NoteSlideMarks.Shape.Paragraph) =>
            new() { Id = "c", BlockIndexes = [0], Text = text, Shape = shape };
        static NoteMadePassage Passage(string text) => new() { GroupId = "m", Text = text, Slides = [] };
        Assert.Equal(NoteSlideMatch.Rung.Text, NoteSlideMatch.RungOf(Candidate("“Be strong,” he said."), Passage("\"Be strong\", he said")));
        Assert.Equal(NoteSlideMatch.Rung.Text, NoteSlideMatch.RungOf(Candidate("1. Trust\n2. Wait"), Passage("Trust\nWait"))); // list numbers do not count
        Assert.Equal(NoteSlideMatch.Rung.Scripture, NoteSlideMatch.RungOf(Candidate("For God so loved the world.\nJohn 3:16"), Passage("God loved the world this way.\nJohn 3:16")));
        Assert.Equal(NoteSlideMatch.Rung.Title, NoteSlideMatch.RungOf(Candidate("Waiting well\nA whole new subtitle here", NoteSlideMarks.Shape.Heading), Passage("Waiting well\nThe old subtitle")));
        Assert.Null(NoteSlideMatch.RungOf(Candidate("Waiting well\nA whole new subtitle here"), Passage("Waiting well\nThe old subtitle"))); // a paragraph's first line is not a title
        Assert.Equal(NoteSlideMatch.Rung.Similar, NoteSlideMatch.RungOf(Candidate("The kingdom of heaven is like a mustard seed planted in a field"), Passage("The kingdom of heaven is like a mustard seed a man planted in his field")));
        Assert.Null(NoteSlideMatch.RungOf(Candidate("Completely different words about something else entirely"), Passage("The kingdom of heaven is like a mustard seed")));
        Assert.Empty(NoteSlideMatch.MatchPassages(Body(N.Para("x")), []).Matches);
    }

    [Fact]
    public void UntouchedIsExactlyWhatTheRunMade()
    {
        var (_, deck, memory) = Sunday();
        var passage = memory[1];
        var made = passage.Slides[0];
        var slide = deck.Slides.First(s => s.Id == made.SlideId);
        Assert.True(NoteSlideMatch.IsUntouched(slide, made, passage));

        Slide Changed(Action<Slide> change)
        {
            var copy = MxuJson.Clone(slide);
            change(copy);
            return copy;
        }
        Assert.True(NoteSlideMatch.IsUntouched(Changed(s =>
        {
            s.Actions = [new SlideAction { Id = "a", Kind = SlideActionKind.SwitchOutputPreset }];
            s.Notes = "the pastor's aside";
        }), made, passage)); // the run's own switch and notes do not count
        Assert.False(NoteSlideMatch.IsUntouched(Changed(s => s.Objects[0].X = 120), made, passage));
        Assert.False(NoteSlideMatch.IsUntouched(Changed(s => s.Objects[0].Text += " (NIV)"), made, passage));
        Assert.False(NoteSlideMatch.IsUntouched(Changed(s => s.Objects.Add(new SlideObject { Id = "p", ObjectKind = SlideObjectKind.Shape, Name = "Picture", Text = "" })), made, passage));
        Assert.False(NoteSlideMatch.IsUntouched(Changed(s => s.ThemeSlideName = "Quote"), made, passage));
        Assert.False(NoteSlideMatch.IsUntouched(Changed(s => s.Objects[0].TextStyle = new TextStyle()), made, passage));
    }
}
