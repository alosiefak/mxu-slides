using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;
using Ov = MxU.Slides.Core.Notes.SlideRowOverride;

namespace MxU.Slides.Core.Tests.Notes;

/// <summary>Port of the Mac's SlidePlannerTests.swift. The planner measures with its Helvetica estimate here (no WPF in Core).</summary>
public class SlidePlannerTests
{
    private static SlideObject Box(string name, double w, double h, double? size = null) => new()
    {
        Id = Guid.NewGuid().ToString(), ObjectKind = SlideObjectKind.Text, Name = name, Text = "", X = 0, Y = 0, Width = w, Height = h,
        TextStyle = size is { } s ? new TextStyle { FontSize = s } : null,
    };

    private static Slide Design(string id, string name, params SlideObject[] objects) => new() { Id = id, Name = name, Objects = [.. objects] };

    private static Theme MakeTheme(string id, string name, string font, double size, params Slide[] slides) =>
        new() { Id = id, Name = name, FontFamily = font, FontSize = size, TextColorHex = "#fff", BackgroundColorHex = "#000", Slides = [.. slides] };

    private static readonly Theme Theme = MakeTheme("t", "Sermon", "Helvetica", 60,
        Design("d1", "Points", Box("Points", 1600, 600)),
        Design("d2", "Scripture", Box("Verse", 1600, 500, 60), Box("Reference", 800, 80)),
        Design("d3", "Big Quote", Box("Left", 900, 700), Box("Right", 400, 100)),
        Design("d4", "Section Title", Box("Title", 1800, 300)),
        Design("d5", "Lyrics", Box("Lyrics", 1600, 400)));

    private static readonly Theme EmailTheme = MakeTheme("e", "Message", "Helvetica", 60,
        Design("e1", "Points", Box("Title", 1800, 160), Box("Points", 1600, 600, 60)),
        Design("e2", "Scripture", Box("Verse", 1600, 500, 60), Box("Reference", 800, 80)),
        Design("e3", "Body", Box("Text", 1600, 700, 60)));

    private static readonly Theme MessageSeries = MakeTheme("ms", "Message Series", "Gotham-Book", 50,
        Design("m1", "Note", Box("Default", 1575, 810, 55)),
        Design("m2", "Point", Box("Number", 751, 118, 53), Box("Point", 1575, 554, 55)),
        Design("m3", "List", Box("Default", 1575, 810, 50)),
        Design("m4", "Scripture", Box("Reference", 714, 118, 53), Box("Verse", 1575, 554, 50)));

    private static readonly NoTheme None = new();

    private sealed class NoTheme;

    private static List<SlideRow> Plan(IEnumerable<MarkedGroup> groups, object? theme = null, Dictionary<string, Ov>? overrides = null, SlideOptions? options = null) =>
        new DeterministicSlidePlanner(options).Plan(groups.ToList(), theme is NoTheme ? null : theme as Theme ?? Theme, overrides ?? []).Rows.ToList();

    private static MarkedGroup G(string id, string text, string? type = null, NoteSlideMarks.Shape shape = NoteSlideMarks.Shape.Paragraph,
        string[]? underlines = null, string[]? links = null) =>
        new(id, type, shape, text, underlines ?? [], [], links ?? []);

    private static SlideBox B(string name, string text) => new(name, text);

    private static void Same(IEnumerable<IEnumerable<SlideBox>> expected, IEnumerable<IEnumerable<SlideBox>> actual) =>
        Assert.Equal(expected.Select(p => p.Select(b => (b.Name, b.Text, string.Join(",", b.RevealLines ?? [])))).Select(p => string.Join("|", p)),
            actual.Select(p => p.Select(b => (b.Name, b.Text, string.Join(",", b.RevealLines ?? [])))).Select(p => string.Join("|", p)));

    private static SlideOptions Options(Func<SlideOptions, SlideOptions> change) => change(new SlideOptions());

    [Fact]
    public void ScriptureGoesToTheVerseAndReferenceBoxesByName()
    {
        var rows = Plan([G("a", "For God so loved the world\nJohn 3:16")]);
        Assert.True(rows[0].Kind == SlideKind.Scripture && rows[0].ThemeSlideName == "Scripture");
        Same([[B("Verse", "For God so loved the world"), B("Reference", "John 3:16")]], rows[0].Parts);
    }

    [Fact]
    public void UnnamedBoxesFillBySizeAndOneBoxStacksTheReference()
    {
        var sized = Plan([G("a", "The Lord is my shepherd (Psalm 23:1)", "scripture")], overrides: new() { ["a"] = new Ov(ThemeSlideName: "Big Quote") });
        Same([[B("Left", "The Lord is my shepherd"), B("Right", "Psalm 23:1")]], sized[0].Parts);
        var one = Plan([G("b", "The Lord is my shepherd (Psalm 23:1)")], overrides: new() { ["b"] = new Ov(ThemeSlideName: "Points") });
        Same([[B("Points", "The Lord is my shepherd\nPsalm 23:1")]], one[0].Parts);
    }

    [Fact]
    public void KindsFollowTheLadder()
    {
        var rows = Plan([
            G("typed", "Be the change\n- Gandhi", "quote", NoteSlideMarks.Shape.List),
            G("label", "Points: Love\nJoy\nPeace"),
            G("design", "Section Title: The Big Idea"),
            G("lyrics", "Verse 1\nAmazing grace\nChorus\nMy chains are gone"),
            G("heading", "Point One", shape: NoteSlideMarks.Shape.Heading),
            G("list", "First\nSecond", shape: NoteSlideMarks.Shape.List),
            G("body", "Just a paragraph of notes."),
        ]);
        Assert.Equal([SlideKind.Quote, SlideKind.Points, SlideKind.Title, SlideKind.Lyrics, SlideKind.Title, SlideKind.Points, SlideKind.Body], rows.Select(r => r.Kind));
        Same([[B("Left", "Be the change"), B("Right", "Gandhi")]], rows[0].Parts);
        Same([[B("Points", "Love\nJoy\nPeace")]], rows[1].Parts);
        Assert.Equal("Section Title", rows[2].ThemeSlideName);
        Same([[B("Title", "The Big Idea")]], rows[2].Parts);
        Assert.True(rows[3].Lyrics!.Sections.Select(s => s.Name).SequenceEqual(["Verse 1", "Chorus"]) && rows[3].Parts.Count == 0);
        Assert.Equal("Section Title", rows[4].ThemeSlideName);
        Assert.Null(rows[6].ThemeSlideName);
        Same([[B("Points", "Just a paragraph of notes.")]], rows[6].Parts);
    }

    [Fact]
    public void NamedLinesAndOverridesWin()
    {
        var rows = Plan([G("a", "Reference: Romans 8:28\nVerse: All things work together", "scripture")],
            overrides: new() { ["a"] = new Ov(SlideKind.Scripture, "Scripture", 1) });
        Same([[B("Verse", "All things work together"), B("Reference", "Romans 8:28")]], rows[0].Parts);
        Assert.Equal(1, rows[0].LinesPerSlide);
    }

    [Fact]
    public void LongTextSplitsAtTheMeasuredFitAndRepeatsTheReference()
    {
        Assert.Equal(new NoteSlideBuilder.FitInfo(6, 53), NoteSlideBuilder.Fit(Theme.Slides![1].Objects[0], Theme));
        var verse = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"Line {i}"));
        var rows = Plan([G("a", verse + "\nJohn 1:1-8")]);
        Assert.Equal(2, rows[0].Parts.Count);
        Assert.Equal(string.Join("\n", Enumerable.Range(1, 6).Select(i => $"Line {i}")), rows[0].Parts[0][0].Text);
        Same([[B("Verse", "Line 7\nLine 8"), B("Reference", "John 1:1-8")]], [rows[0].Parts[1]]);

        var wrapped = Plan([G("b", string.Concat(Enumerable.Repeat("word ", 40)))], overrides: new() { ["b"] = new Ov(ThemeSlideName: "Scripture", LinesPerSlide: 2) });
        Assert.True(wrapped[0].Parts.Count == 1 && wrapped[0].Kind == SlideKind.Body);
        Assert.Equal(["short", new string('x', 100), "tail"], DeterministicSlidePlanner.Chunk(["short", new string('x', 100), "tail"], 2, 50));

        const string pasted = "10 You, however, know all about my teaching, my way of life, my purpose, faith, patience, love, endurance, 11 persecutions, sufferings. 12 In fact, everyone who wants to live a godly life will be persecuted, 13 while evildoers go from bad to worse.\n2 Timothy 3:10-13";
        var passage = Plan([G("d", pasted)], overrides: new() { ["d"] = new Ov(LinesPerSlide: 2) });
        Assert.True(passage[0].Kind == SlideKind.Scripture && passage[0].Parts.Count >= 2);
        Assert.True(passage[0].Parts[0][0].Text.StartsWith("10 You, however", StringComparison.Ordinal) && passage[0].Parts[^1][0].Text.EndsWith("bad to worse.", StringComparison.Ordinal));
        Assert.All(passage[0].Parts, p => Assert.Contains(B("Reference", "2 Timothy 3:10-13"), p));
        Assert.Equal(["One.", "Two", "3 Three"], DeterministicSlidePlanner.Pieces("One. Two 3 Three"));

        var title = Plan([G("c", "One\nTwo\nThree", shape: NoteSlideMarks.Shape.Heading)], overrides: new() { ["c"] = new Ov(LinesPerSlide: 1) });
        Assert.Single(title[0].Parts);
    }

    [Fact]
    public void NoThemeStillBuildsPlainSlides()
    {
        var rows = Plan([G("a", "Grace upon grace (John 1:16)")], None);
        Assert.True(rows[0].Kind == SlideKind.Scripture && rows[0].ThemeSlideName is null);
        Same([[B("Text", "Grace upon grace\nJohn 1:16")]], rows[0].Parts);
        Assert.Equal(new NoteSlideBuilder.FitInfo(7, 32), NoteSlideBuilder.Fit(null, null));
    }

    [Fact]
    public void TheBuilderMakesThemeInheritingSlidesAndLyricsSections()
    {
        var rows = Plan([G("s", "For God so loved the world\nJohn 3:16"), G("l", "Verse 1\nAmazing grace\nChorus\nMy chains\nVerse 1")]);
        var built = NoteSlideBuilder.Build(new SlidePlan(rows));
        Assert.Equal(3, built.Slides.Count);
        var scripture = built.Slides[0];
        Assert.True(scripture.ThemeSlideName == "Scripture" && scripture.Objects.Select(o => o.Name).SequenceEqual(["Verse", "Reference"]));
        Assert.All(scripture.Objects, o => Assert.True(o.TextStyle is null && o.X is null && o.Width is null));
        Assert.Equal("s", built.GroupBySlideId[scripture.Id]);
        Assert.Equal(["Verse 1", "Chorus"], built.Sections.Select(s => s.Name));
        Assert.True(built.Slides[1].ThemeSlideName == "Lyrics" && built.Slides[1].SectionId == built.Sections[0].Id);
        Assert.Equal(3, built.Arrangement?.SectionIds.Count); // Verse 1 repeats
        Assert.Equal("l", built.GroupBySlideId[built.Slides[2].Id]);
    }

    [Fact]
    public void NumberedLinesArePointsAndAShortFirstLineIsTheTitle()
    {
        var rows = Plan([G("a", "Four Voices Influencing Your Life\n1. Mockers : Reject God\n2. Neutrals : Replace God")], EmailTheme);
        Assert.True(rows[0].Kind == SlideKind.Points && rows[0].ThemeSlideName == "Points");
        Same([[B("Title", "Four Voices Influencing Your Life"), B("Points", "1. Mockers : Reject God\n2. Neutrals : Replace God")]], rows[0].Parts);

        var bullets = Plan([G("b", "Remember\n- Pray\n- Read\n• Give")]);
        Assert.Equal(SlideKind.Points, bullets[0].Kind);
        Same([[B("Points", "Remember\n- Pray\n- Read\n• Give")]], bullets[0].Parts);

        var spaced = Plan([G("c", "1. Many voices\n\n2. Not a given\n\n3. What you believe")], EmailTheme);
        Same([[B("Points", "1. Many voices\n2. Not a given\n3. What you believe")]], spaced[0].Parts);
        Assert.True(DeterministicSlidePlanner.IsPointLine("10) Ten") && !DeterministicSlidePlanner.IsPointLine("2024 was a year") && !DeterministicSlidePlanner.IsPointLine("-no space"));
    }

    [Fact]
    public void StanzasAndColonHeadingsShapeTheSplitAndTheTitleRepeats()
    {
        const string stats = "Transformation in people who read the Bible 4x a week:\n\nUnwanted habits dropped:\nExcessive drinking: 62%\nGambling: 45%\nOvereating: 20%\n\nEmotional health improved:\nBitterness: 40%\nAnxiety and fear: 14%\n\nSpiritual practices increased:\nGiving to the church: 416%\nSharing faith: 228%";
        var rows = Plan([G("a", stats)], EmailTheme);
        Assert.True(rows[0].Kind == SlideKind.Body && rows[0].LinesPerSlide == 9);
        var texts = rows[0].Parts.Select(p => p[0].Text).ToList();
        Assert.Equal(2, texts.Count);
        Assert.StartsWith("Transformation in people who read the Bible 4x a week:\nUnwanted habits dropped:", texts[0]);
        Assert.EndsWith("Anxiety and fear: 14%", texts[0]);
        Assert.Equal("Spiritual practices increased:\nGiving to the church: 416%\nSharing faith: 228%", texts[1]);
        Assert.Equal(["a\nb", "c\nd"], DeterministicSlidePlanner.Chunk([["a", "b"], ["c", "d"]], 3, 50));
    }

    [Fact]
    public void InstructionsAndLinksStayOffTheScreen()
    {
        var rows = Plan([
            G("a", "2 Timothy 3:1-5 (bold “having a form of godliness, but denying its power”)"),
            G("b", "https://youtu.be/-BWqzQU0JL4?si=1Ebg0sRPzZfDeIcT"),
            G("c", "Psalm 1:1-6"),
        ], EmailTheme);
        Assert.Equal(SlideKind.Scripture, rows[0].Kind);
        Same([[B("Reference", "2 Timothy 3:1-5")]], rows[0].Parts);
        Assert.Equal("bold “having a form of godliness, but denying its power”", rows[0].Notes);
        Assert.True(rows[1].Kind == SlideKind.Media && rows[1].Parts.Count == 0 && rows[1].Notes!.StartsWith("https://youtu.be/", StringComparison.Ordinal));
        Assert.StartsWith("Needs media", rows[1].Warning);
        Assert.True(rows[2].Kind == SlideKind.Scripture && rows[2].Notes is null);
        Same([[B("Reference", "Psalm 1:1-6")]], rows[2].Parts);
        var built = NoteSlideBuilder.Build(new SlidePlan(rows));
        Assert.True(built.Slides[0].Notes == rows[0].Notes && built.Slides[1].Objects.Count == 0 && built.Slides[1].Notes == rows[1].Notes);
    }

    [Fact]
    public void ThePastorsEmailMakesHisSlides()
    {
        const string email = "Hey Everyone, this is the most up to date slide list for Sunday.\n\n[slide]\nWhat is the passage of scripture that has had the biggest impact on their life and why?\n\n[slide]\nPsalm 1:1-6\n\n[slide]\n1. There are many voices who’s words can impact your life.\n\n2. It’s not a given that you will choose God’s word above all other words.\n\n[slide]\nThe Bible is God’s inspired word given to lead us to Jesus.\n\n[slide]\nhttps://youtu.be/-BWqzQU0JL4\n\n[slide]\nFour Voices Influencing Your Life\n1. Mockers : Reject God\n2. Neutrals : Replace God\n\n[slide]\n2 Timothy 3:1-5 (bold “having a form of godliness”)";
        var blocks = email.Split('\n').Select(N.Para).ToList();
        var marked = NoteSlideMarks.MarkingByStructure(new NoteBody { Blocks = blocks, PlainText = email });
        var rows = Plan(NoteSlideBuilder.Groups(marked), EmailTheme);
        Assert.Equal([SlideKind.Body, SlideKind.Scripture, SlideKind.Points, SlideKind.Body, SlideKind.Media, SlideKind.Points, SlideKind.Scripture], rows.Select(r => r.Kind));
        Assert.Single(rows[2].Parts); // two numbered paragraphs, one slide
        Assert.Equal("Four Voices Influencing Your Life", rows[5].Parts[0][0].Text);
        Assert.Equal("bold “having a form of godliness”", rows[6].Notes);
        Assert.Equal(7, NoteSlideBuilder.Build(new SlidePlan(rows)).Slides.Count);
    }

    [Fact]
    public void TheMessageSeriesThemePicksListForManyPointsAndNumberPlusPointForOne()
    {
        var rows = Plan([
            G("many", "1. There are many voices.\n2. It’s not a given.\n3. What you believe matters."),
            G("one", "Four Voices Influencing Your Life\n1. Mockers : Reject God"),
            G("body", "The Bible is God’s inspired word."),
            G("ref", "Psalm 1:1-6"),
        ], MessageSeries);
        Assert.True(rows[0].Kind == SlideKind.Points && rows[0].ThemeSlideName == "List");
        Same([[B("Default", "1. There are many voices.\n2. It’s not a given.\n3. What you believe matters.")]], rows[0].Parts);
        Assert.Equal("Point", rows[1].ThemeSlideName);
        Same([[B("Number", "1."), B("Point", "Four Voices Influencing Your Life\nMockers : Reject God")]], rows[1].Parts);
        Assert.Equal("Note", rows[2].ThemeSlideName);
        Same([[B("Default", "The Bible is God’s inspired word.")]], rows[2].Parts);
        Assert.Equal("Scripture", rows[3].ThemeSlideName);
        Same([[B("Reference", "Psalm 1:1-6")]], rows[3].Parts);
        Assert.True(DeterministicSlidePlanner.SplitPointNumber("2) Second").Number == "2)" && DeterministicSlidePlanner.SplitPointNumber("- Dash").Number is null);
    }

    [Fact]
    public void APartTheBoxCantHoldAtItsRealFontSplitsAgain()
    {
        var narrow = MakeTheme("n", "Narrow", "Helvetica", 96, Design("d", "Body", Box("Text", 800, 300)));
        const string text = "WWWWWWWWWWWWWWW\nMMMMMMMMMMMMMMM";
        var box = narrow.Slides![0].Objects[0];
        Assert.Equal(new NoteSlideBuilder.FitInfo(2, 16), NoteSlideBuilder.Fit(box, narrow));
        Assert.False(NoteSlideBuilder.Fits(text, box, narrow));
        Assert.True(NoteSlideBuilder.Fits("WWWWWWWWWWWWWWW", box, narrow));
        Assert.Equal(["WWWWWWWWWWWWWWW", "MMMMMMMMMMMMMMM"], Plan([G("a", text)], narrow)[0].Parts.Select(p => p[0].Text));
        Assert.Single(Plan([G("a", text)], narrow, new() { ["a"] = new Ov(LinesPerSlide: 2) })[0].Parts);

        var wide = MakeTheme("w", "Wide", "Helvetica", 96, Design("d", "Body", Box("Text", 1600, 300)));
        var thin = new string('i', 40) + "\n" + new string('l', 40);
        Assert.Equal(2, DeterministicSlidePlanner.Chunk(thin.Split('\n').ToList(), 2, 33).Count);
        Assert.Equal([thin], Plan([G("t", thin)], wide)[0].Parts.Select(p => p[0].Text));

        const string rules = "Backyard Ultra - Rules:\nComplete 4.167 miles, every hour, on the hour.\nWarnings given 3, 2, and 1 minute prior to each loop. All competitors must be in the starting corral before the starting whistle.\nYou win by being the last person standing and must complete an additional loop in order to win outright.";
        var parts = Plan([G("r", rules, "points")], None)[0].Parts;
        Assert.True(parts.Count >= 2);
        Assert.All(parts, p => Assert.True(NoteSlideBuilder.Fits(p[0].Text, null, null)));
    }

    [Fact]
    public void ALinkedVideoFileNameIsAVideoPlaceholderHoweverItWasMarked()
    {
        const string link = "https://drive.google.com/file/d/1/view";
        var row = Plan([G("v", "Snoop.mp4", links: [link])], None)[0];
        Assert.True(row.Kind == SlideKind.Video && row.Notes == link && row.Warning is not null && row.Parts.Count == 0);
        Assert.Equal(SlideKind.Body, Plan([G("w", "Snoop.mp4")], None)[0].Kind);
    }

    [Fact]
    public void AColonLineOverAListIsThePointsHeading()
    {
        var warnings = G("w", "Three Warnings:\nDon’t forget Me\nDon’t test God", "points");
        var list = Plan([warnings], MessageSeries)[0];
        Assert.Equal("List", list.ThemeSlideName);
        Same([[B("Default", "Three Warnings\nDon’t forget Me\nDon’t test God")]], list.Parts);
        var point = Plan([warnings], MessageSeries, new() { ["w"] = new Ov(ThemeSlideName: "Point") })[0];
        Same([[B("Number", "1."), B("Point", "Three Warnings\nDon’t forget Me")], [B("Number", "2."), B("Point", "Three Warnings\nDon’t test God")]], point.Parts);
    }

    [Fact]
    public void PlannerReadsGroupsFromANote()
    {
        var body = N.Body(
            new NoteBlock { Type = NoteBlockType.Heading, Level = 2, Runs = [N.Run("Point one", "a")] },
            new NoteBlock { Type = NoteBlockType.List, Ordered = false, Items = [new NoteListItem { Runs = [N.Run("First", "b", "points")] }] });
        Assert.Equal([new MarkedGroup("a", "Point one", null, NoteSlideMarks.Shape.Heading), new MarkedGroup("b", "First", "points", NoteSlideMarks.Shape.List)],
            NoteSlideBuilder.Groups(body));
        Assert.Equal("Big Quote", NoteSlideBuilder.DesignName(SlideKind.Quote, Theme));
        Assert.Null(NoteSlideBuilder.DesignName(SlideKind.Body, Theme));
    }

    [Fact]
    public void BlanksFollowTheirPassageAndCarryTheBlankSwitch()
    {
        var rows = Plan([
            G("s", "For God so loved the world, that he gave his only begotten Son, that whosoever believeth in him should not perish, but have everlasting life. For God sent not his Son into the world to condemn the world; but that the world through him might be saved. He that believeth on him is not condemned: but he that believeth not is condemned already, because he hath not believed in the name of the only begotten Son of God.\nJohn 3:16-18"),
            G("p", "Points\n1. One\n2. Two"),
            G("q", "A short quote"),
        ]);
        var scriptureParts = rows[0].Parts.Count;
        Assert.True(scriptureParts > 1, "the passage splits");
        var built = NoteSlideBuilder.Build(new SlidePlan(rows),
            new Dictionary<string, BlankPlacement> { ["s"] = BlankPlacement.AfterPassage, ["p"] = BlankPlacement.AfterSlide },
            new NoteSlideBuilder.BlankStyle("logo-theme", "Logo", [SlidePresetChoice.Preset("full").Action()]));
        var isBlank = built.Slides.Select(s => built.BlankFollows.ContainsKey(s.Id)).ToList();
        Assert.Equal(Enumerable.Repeat(false, scriptureParts).Concat([true, false, true, false]), isBlank);
        var blank = built.Slides[scriptureParts];
        Assert.True(blank.Objects.Count == 0 && blank.Actions?[0].PresetId == "full" && !built.GroupBySlideId.ContainsKey(blank.Id));
        Assert.True(blank.ThemeSlideName == "Logo" && blank.ThemeId == "logo-theme", "a blank can wear a design");
        var logoTheme = MakeTheme("l", "Series", "Helvetica", 60, Design("x", "Scripture"), Design("y", "Logo"));
        Assert.Equal("Logo", NoteSlideBuilder.DesignName(SlideKind.Blank, logoTheme)); // matched by name
        Assert.True(built.Group(blank.Id) == "s" && built.BlankFollows.Count == 2);
        Assert.Empty(NoteSlideBuilder.Build(new SlidePlan(rows)).BlankFollows); // no blanks unless asked
    }

    [Fact]
    public void MadeSlidesTakeTheirTypesSwitchTheRowOverridingIt()
    {
        var rows = Plan([G("s", "For God so loved the world\nJohn 3:16"), G("p", "Points\n1. One\n2. Two"), G("q", "A short quote"), G("b", "Plain body text here")]);
        var settings = new SlideDesignMap();
        settings.SetPreset(SlidePresetChoice.Preset("side"), SlideKind.Scripture);
        settings.OtherPreset = SlidePresetChoice.Combo("full");
        var built = NoteSlideBuilder.Build(new SlidePlan(rows), new Dictionary<string, BlankPlacement> { ["s"] = BlankPlacement.AfterPassage }, new NoteSlideBuilder.BlankStyle());
        NoteSlideBuilder.AttachingSwitches(built, rows, settings, new Dictionary<string, NoteSlideBuilder.SwitchOverride>
        {
            ["q"] = new NoteSlideBuilder.SwitchOverride.None(),
            ["b"] = new NoteSlideBuilder.SwitchOverride.Chosen(SlidePresetChoice.Preset("lower")),
        });
        List<string> Switches(string group) => built.Slides.Where(s => built.GroupBySlideId.GetValueOrDefault(s.Id) == group)
            .SelectMany(s => (s.Actions ?? []).Select(a => a.PresetId ?? a.ComboId ?? "")).ToList();
        Assert.Equal(["side"], Switches("s")); // the type's own
        Assert.Equal(["full"], Switches("p")); // all other slides, through a combo
        Assert.Empty(Switches("q")); // the row said none
        Assert.Equal(["lower"], Switches("b")); // the row's own
        Assert.Null(built.Slides.First(s => built.BlankFollows.ContainsKey(s.Id)).Actions); // blanks take the blank switch only
        Assert.Equal(SlidePresetChoice.Combo("full"), NoteSlideBuilder.PresetChoice(SlideKind.Points, new NoteSlideBuilder.SwitchOverride.TypeDefault(), settings));
    }

    // The Mac's Theme.scoped(toSlideFolder:): the theme with only one folder's designs.
    private static Theme Scoped(Theme theme, string folder)
    {
        var scoped = MxuJson.Clone(theme);
        scoped.Slides = scoped.Slides?.Where(s => s.Folder == folder).ToList();
        return scoped;
    }

    [Fact]
    public void LookDesignsMatchTheKindInEachLookAndPicksWin()
    {
        var side = Scoped(StarterPack.Digital.MakeTheme(), "Side Thirds");
        var lower = Scoped(StarterPack.CleanGeometric.MakeTheme(), "Lower Thirds");
        Assert.Equal(new Dictionary<string, string> { [side.Id] = "Verse + Reference (Side Third)", [lower.Id] = "Verse + Reference (Lower Third)" },
            NoteSlideBuilder.LookDesigns(SlideKind.Scripture, [side, lower], new Dictionary<string, string>()));
        Assert.Equal(new Dictionary<string, string> { [side.Id] = "Point (Side Third)" }, NoteSlideBuilder.LookDesigns(SlideKind.Points, [side], new Dictionary<string, string>()));
        Assert.Empty(NoteSlideBuilder.LookDesigns(SlideKind.Body, [side], new Dictionary<string, string>())); // no Body design: the look's first
        Assert.Equal(new Dictionary<string, string> { [side.Id] = "Point (Side Third)" },
            NoteSlideBuilder.LookDesigns(SlideKind.Scripture, [side], new Dictionary<string, string> { [side.Id] = "Point (Side Third)" }));
    }

    [Fact]
    public void RevealOnePerClickGivesEachPointItsOwnFadeIn()
    {
        var group = G("a", "Remember\n- Pray\n- Read");
        Assert.Null(Plan([group])[0].Parts[0][0].RevealLines);
        var on = Plan([group], options: Options(o => o with { RevealPoints = true }))[0];
        Assert.Equal([1, 2], on.Parts[0][0].RevealLines!);
        var steps = NoteSlideBuilder.Build(new SlidePlan([on])).Slides[0].Objects[0].AnimationSteps!;
        Assert.All(steps, s => Assert.True(s.Trigger == AnimationTrigger.OnClick && s.Kind == AnimationKind.In && s.Animation == StepAnimation.Fade));
        Assert.Equal([(1, 0, 6), (2, 0, 6)], steps.Select(s => (s.Ranges![0].Line, s.Ranges[0].Column, s.Ranges[0].Length)));
        var one = Plan([G("b", "Big idea: Grace wins.", "points")], options: Options(o => o with { RevealPoints = true }))[0];
        Assert.Null(one.Parts[0][0].RevealLines);
    }

    [Fact]
    public void ALabelledLineIsAPointUnderItsLabel()
    {
        var rows = Plan([G("a", "Next step: Write it down."), G("b", "Chorus: Holy, holy")], EmailTheme);
        Assert.Equal(SlideKind.Points, rows[0].Kind);
        Same([[B("Title", "Next step"), B("Points", "Write it down.")]], rows[0].Parts);
        Assert.NotEqual(SlideKind.Points, rows[1].Kind);
        Assert.Null(DeterministicSlidePlanner.LabelledPoint("A very long label here: words"));
        Assert.Null(DeterministicSlidePlanner.LabelledPoint("Next step: one\ntwo"));
    }

    [Fact]
    public void AQuoteWithItsNameOnTheSameLineSplitsAtTheDash()
    {
        var row = Plan([G("a", "“Faith is the first step.” — Martin Luther King Jr.", "quote")])[0];
        Same([[B("Left", "“Faith is the first step.”"), B("Right", "Martin Luther King Jr.")]], row.Parts);
        Assert.True(DeterministicSlidePlanner.ReadsAsQuote("“Be still.”") && !DeterministicSlidePlanner.ReadsAsQuote("Be still."));
    }

    [Fact]
    public void OneVersePerSlideCarriesEachVersesReferenceAndTheFullOneLast()
    {
        var group = G("a", "Philippians 4:6–7 (ESV)\n6 Do not be anxious about anything. 7 And the peace of God will guard your hearts.");
        var split = Plan([group], options: Options(o => o with { ScriptureSplit = SlideOptions.ScriptureSplits.OneVerse }))[0];
        Same([[B("Verse", "6 Do not be anxious about anything."), B("Reference", "Philippians 4:6")],
            [B("Verse", "7 And the peace of God will guard your hearts."), B("Reference", "Philippians 4:6–7 (ESV)")]], split.Parts);
        var bare = Plan([group], options: Options(o => o with { ScriptureSplit = SlideOptions.ScriptureSplits.OneVerse, VerseNumbers = false }))[0];
        Assert.Equal(["Do not be anxious about anything.", "And the peace of God will guard your hearts."], bare.Parts.Select(p => p[0].Text));
        var fit = Plan([group], options: Options(o => o with { VerseNumbers = false }))[0];
        Assert.Equal(["Do not be anxious about anything. And the peace of God will guard your hearts."], fit.Parts.Select(p => p[0].Text));
        var single = Plan([G("b", "Psalm 46:1 – God is our refuge")], options: Options(o => o with { ScriptureSplit = SlideOptions.ScriptureSplits.OneVerse }))[0];
        Assert.Single(single.Parts);
    }

    [Fact]
    public void BlanksSkipTheVersesOfOnePassageUnlessAskedFor()
    {
        var rows = Plan([G("s", "John 1:1–2\n1 In the beginning was the Word. 2 The same was in the beginning with God."), G("p", "1. One\n2. Two")],
            options: Options(o => o with { ScriptureSplit = SlideOptions.ScriptureSplits.OneVerse }));
        var blanks = new Dictionary<string, BlankPlacement> { ["s"] = BlankPlacement.AfterSlide, ["p"] = BlankPlacement.AfterSlide };
        List<bool> Shape(SlideOptions options)
        {
            var built = NoteSlideBuilder.Build(new SlidePlan(rows), blanks, new NoteSlideBuilder.BlankStyle(), options);
            return built.Slides.Select(s => built.BlankFollows.ContainsKey(s.Id)).ToList();
        }
        Assert.Equal([false, false, true, false, true], Shape(new SlideOptions()));
        Assert.Equal([false, true, false, true, false, true], Shape(new SlideOptions { BlanksBetweenVerses = true }));
    }

    private static List<(int, int, int, bool?)> Runs(IEnumerable<TextStyleRun>? runs) => runs?.Select(r => (r.Line, r.Column, r.Length, r.Underline)).ToList() ?? [];

    [Fact]
    public void TheNotesUnderlinesStayOnTheSlideInOrder()
    {
        var group = G("a", "Everything that can be shaken will be shaken\nWhat God builds can’t be shaken", "points", underlines: ["shaken", "can’t", "missing"]);
        var row = Plan([group])[0];
        Assert.Equal([(0, 23, 6, true), (1, 16, 5, true)], Runs(row.Parts[0][0].StyleRuns));
        Assert.Equal(Runs(row.Parts[0][0].StyleRuns), Runs(NoteSlideBuilder.Build(new SlidePlan([row])).Slides[0].Objects[0].StyleRuns));
        Assert.Null(Plan([group], options: new SlideOptions { KeepUnderlines = false })[0].Parts[0][0].StyleRuns);
    }

    [Fact]
    public void WordsInParenthesesGoToTheNotesUnlessTurnedOff()
    {
        MarkedGroup[] groups = [G("a", "Grace is a gift (hold up the envelope), not a wage."), G("b", "2 Timothy 3:1-5 (bold 'having a form')\nBut know this")];
        var on = Plan(groups);
        Assert.True(on[0].Parts[0][0].Text == "Grace is a gift, not a wage." && on[0].Notes == "hold up the envelope");
        Assert.Equal("bold 'having a form'", on[1].Notes);
        var off = Plan(groups, options: new SlideOptions { ParenthesesToNotes = false });
        Assert.True(off[0].Parts[0][0].Text == "Grace is a gift (hold up the envelope), not a wage." && off[0].Notes is null);
        Assert.Equal("bold 'having a form'", off[1].Notes);
        var lyrics = Plan([G("c", "Verse 1\nHoly (x2)\nChorus\nWorthy", "lyrics")])[0];
        Assert.Contains(lyrics.Lyrics!.Sections.SelectMany(s => s.Slides), s => s.Contains("(x2)"));
    }

    [Fact]
    public void UpdateFromNoteMovesTheUnderlinesWithTheWordsAndPlansWithTheOptions()
    {
        var old = G("a", "Grace wins\nLove stays", "points", underlines: ["wins"]);
        var row = Plan([old])[0];
        var slides = NoteSlideBuilder.Build(new SlidePlan([row])).Slides;
        Assert.Equal([(0, 6, 4, true)], Runs(slides[0].Objects[0].StyleRuns));
        var edited = G("a", "Grace always wins\nLove stays", "points", underlines: ["wins"]);
        var reveal = new SlideOptions { RevealPoints = true };
        Assert.False(NoteSlideUpdate.IsBehind(old, slides, Theme, reveal));
        var next = NoteSlideUpdate.Replan(edited, slides, Theme, reveal)!;
        Assert.Equal([0, 1], next.Parts[0][0].RevealLines!);
        var updated = NoteSlideUpdate.Updating(slides, next).Updated[0].Objects[0];
        Assert.Equal("Grace always wins\nLove stays", updated.Text);
        Assert.Equal([(0, 13, 4, true)], Runs(updated.StyleRuns));
    }

    [Fact]
    public void AQuoteFillsTheQuoteBoxNotTheQuotationMark()
    {
        var theme = StarterPack.CleanGeometric.MakeTheme();
        var row = Plan([G("q", "“Faith is taking the first step even when you don’t see the whole staircase.” — Martin Luther King Jr.", "quote")], theme)[0];
        var boxes = row.Parts[0];
        Assert.Equal("Faith is taking the first step even when you don’t see the whole staircase.", boxes.First(b => b.Name == "Quote").Text);
        Assert.Equal("Martin Luther King Jr.", boxes.First(b => b.Name == "Source").Text);
        Assert.Equal("“", boxes.First(b => b.Name == "Quote Mark").Text);
        Assert.Equal("“", NoteSlideBuilder.Build(new SlidePlan([row])).Slides[0].Objects.First(o => o.Name == "Quote Mark").Text);
    }

    [Fact]
    public void ATitlesSecondLineGoesInTheSmallerBox()
    {
        var theme = StarterPack.CleanGeometric.MakeTheme();
        var boxes = Plan([G("t", "Unshakable\nHebrews 12:25–29 · Kingdom Come, week 4", "title", NoteSlideMarks.Shape.Heading)], theme)[0].Parts[0];
        Assert.Equal("Unshakable", boxes.First(b => b.Name == "Section Title").Text);
        Assert.Equal("Hebrews 12:25–29 · Kingdom Come, week 4", boxes.First(b => b.Name == "Kicker").Text);
        Assert.Equal(["Section Title"], Plan([G("u", "Unshakable", "title")], theme)[0].Parts[0].Select(b => b.Name));
    }

    [Fact]
    public void AListIsSeveralPointsAndALabelTakesTheKicker()
    {
        var theme = StarterPack.CleanGeometric.MakeTheme();
        var list = G("p", "Everything that can be shaken will be shaken\nWhat God builds can’t be shaken\nSo we worship with reverence and awe", "points", NoteSlideMarks.Shape.List);
        var row = Plan([list], theme)[0];
        Assert.Equal("Points", row.ThemeSlideName);
        Assert.Single(row.Parts);
        var boxes = Plan([G("n", "Next step: Write down one thing you’re holding onto that can be shaken.", "points")], theme)[0].Parts[0];
        Assert.Equal("Next step", boxes.First(b => b.Name == "Kicker").Text);
        Assert.Equal("Write down one thing you’re holding onto that can be shaken.", boxes.First(b => b.Name == "Point").Text);
    }

    // Part of the Mac's aListIsSeveralPointsAndALabelTakesTheKicker. PR #13's StarterPacks change
    // (the Points box's spacing and shrink floor) is ported by the engine catch-up (wt\up-core).
    [Fact(Skip = "Needs PR #13's StarterPacks change, ported in the engine catch-up (wt\\up-core)")]
    public void ThePointsDesignSpacesItsPoints() =>
        Assert.Equal(28, StarterPack.CleanGeometric.MakeTheme().Slides!.First(s => s.Name == "Points").Objects.First(o => o.Name == "Points").TextStyle?.ParagraphSpacing);

    [Fact]
    public void AnUpdateOntoAnotherDesignTakesItsBoxesAndOrderIsNotBehind()
    {
        var theme = StarterPack.CleanGeometric.MakeTheme();
        var list = G("p", "Everything that can be shaken will be shaken\nWhat God builds can’t be shaken\nSo we worship with reverence and awe", "points", NoteSlideMarks.Shape.List);
        var old = NoteSlideBuilder.Build(new SlidePlan([Plan([list], theme)[0]])).Slides[0];
        old.ThemeSlideName = "Point";
        foreach (var o in old.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text)) o.Name = "Point";
        var updated = NoteSlideUpdate.Updating([old], Plan([list], theme)[0]).Updated[0];
        Assert.Equal(old.Id, updated.Id);
        Assert.Equal("Points", updated.ThemeSlideName);
        Assert.Equal(["Points"], updated.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text).Select(o => o.Name));

        var quote = G("q", "“Faith is taking the first step.” — Martin Luther King Jr.", "quote");
        var slides = NoteSlideBuilder.Build(new SlidePlan([Plan([quote], theme)[0]])).Slides;
        var first = slides[0].Objects[0];
        slides[0].Objects.RemoveAt(0);
        slides[0].Objects.Add(first);
        Assert.False(NoteSlideUpdate.IsBehind(quote, slides, theme));
    }

    [Fact]
    public void AnOwnTypeTakesItsDesignSplitAndRevealAndKeysItsSwitch()
    {
        var withNotice = MxuJson.Clone(Theme);
        withNotice.Slides!.Add(Design("d6", "Notice", Box("Text", 1600, 600)));
        var opts = new SlideOptions { OwnTypes = [new("Notice", SlideOptions.OwnType.Splits.Paragraph), new("Ask", Reveal: true)] };
        var rows = Plan([
            G("a", "Notice: Camp signups close Friday\nBring a friend\n\nSee Pastor Dan", "Notice"),
            G("b", "ask: What are you rooted in?\nWhat holds?"),
            G("c", "Camp signups close Friday", "Notice"),
        ], withNotice, options: opts);
        Assert.True(rows.All(r => r.Kind == SlideKind.Body) && rows.Select(r => r.OwnType).SequenceEqual(["Notice", "Ask", "Notice"]));
        Assert.Equal("Notice", rows[0].ThemeSlideName); // a design named like the type
        Assert.Equal(["Camp signups close Friday\nBring a friend", "See Pastor Dan"], rows[0].Parts.Select(p => p[0].Text)); // one paragraph per slide, label gone
        Same([[new SlideBox("Points", "What are you rooted in?\nWhat holds?", RevealLines: [0, 1])]], rows[1].Parts); // no design named Ask: the theme's first
        Assert.True(rows[2].Parts[0][0].Text == "Camp signups close Friday" && rows[2].TypeKey == "Notice");
        var settings = new SlideDesignMap();
        settings.SetPresetForType(SlidePresetChoice.Preset("lower"), "Notice");
        settings.OtherPreset = SlidePresetChoice.Preset("full");
        var built = NoteSlideBuilder.Build(new SlidePlan(rows));
        NoteSlideBuilder.AttachingSwitches(built, rows, settings, new Dictionary<string, NoteSlideBuilder.SwitchOverride>());
        Assert.Equal(["lower", "lower", "full", "lower"], built.Slides.Select(s => string.Join(",", (s.Actions ?? []).Select(a => a.PresetId ?? "")))); // the own type's switch, All other for the rest

        var overridden = Plan([G("a", "Notice: Hi", "Notice")], withNotice, new() { ["a"] = new Ov(SlideKind.Quote) }, opts)[0];
        Assert.True(overridden.Kind == SlideKind.Quote && overridden.OwnType is null);
    }

    [Fact]
    public void AsideRangesAreWhatTheNotesTake()
    {
        const string text = "Baptism recap (video below) (NIV)\nGrace (John 3:16) (\nThen (pause here)";
        Assert.Equal(["(video below)", "(pause here)"], DeterministicSlidePlanner.AsideRanges(text).Select(r => text.Substring(r.Start, r.Length)));
        Assert.Equal(9, NoteSlides.InlineMarkerLabelLength("Slide 2: Baptism recap"));
    }
}
