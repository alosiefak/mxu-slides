using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Stage;

// Port of SlideSceneTests/LinkedTextTests.swift (plus the resolution-level cases of ChordSceneTests.swift).
public class LinkedTextTests
{
    private static readonly DateTimeOffset FixedDate = DateTimeOffset.FromUnixTimeSeconds(1_784_800_000);

    private static ConfidenceInfo FixtureInfo(CueAlert? alert = null, bool alertVisible = true, IReadOnlyList<TimerSnapshot>? timers = null) => new()
    {
        Current = new SlideText("current line"),
        Next = new SlideText("next line"),
        Last = new SlideText("last line"),
        Alert = alert,
        AlertVisible = alertVisible,
        Timers = timers ?? [],
        VideoCountdown = new VideoCountdown("Bumper", 120, 30, FixedDate, IsPlaying: false),
        SlidePosition = new SlidePosition(3, 12),
        CurrentItemName = "Amazing Grace",
        NextItemName = "Announcements",
        CurrentPresentationName = "Amazing Grace (Live in G)",
        NextServiceTitle = "Sunday Gathering",
    };

    private static TextLink Link(TextSourceKind source) => new() { Source = source };

    private static SlideObject Text(string id, string name, string text, TextLink? link = null) =>
        new() { Id = id, ObjectKind = SlideObjectKind.Text, Name = name, Text = text, TextLink = link };

    private static ChordPlacement Chord(int line, int column, string symbol) => new() { Line = line, Column = column, Symbol = symbol };

    [Fact]
    public void SlideSourcesResolveCurrentNextLast()
    {
        var info = FixtureInfo();
        Assert.Equal("current line", LinkedText.Resolve(Link(TextSourceKind.CurrentSlide), info, FixedDate));
        Assert.Equal("next line", LinkedText.Resolve(Link(TextSourceKind.NextSlide), info, FixedDate));
        Assert.Equal("last line", LinkedText.Resolve(Link(TextSourceKind.LastSlide), info, FixedDate));
    }

    [Fact]
    public void NamedPullReadsOneObjectOfTheSourceSlide()
    {
        var info = FixtureInfo() with
        {
            Current = new SlideText("verse words\nJohn 3:16")
            {
                ObjectTexts = [new ObjectText("Verse", "verse words"), new ObjectText("Reference", "John 3:16")],
            },
            Next = new SlideText("next words") { ObjectTexts = [new ObjectText("Verse", "next words")] },
        };
        var named = Link(TextSourceKind.CurrentSlide);
        named.SourceObjectName = "reference";
        Assert.Equal("John 3:16", LinkedText.Resolve(named, info, FixedDate));
        named.SourceObjectName = "Chorus";
        Assert.Null(LinkedText.Resolve(named, info, FixedDate));
        var nextNamed = Link(TextSourceKind.NextSlide);
        nextNamed.SourceObjectName = "Verse";
        Assert.Equal("next words", LinkedText.Resolve(nextNamed, info, FixedDate));
        Assert.Equal("verse words\nJohn 3:16", LinkedText.Resolve(Link(TextSourceKind.CurrentSlide), info, FixedDate));

        info = info with { NextStep = new SlideText("the coming step") };
        nextNamed.IncludeSteps = true;
        Assert.Equal("next words", LinkedText.Resolve(nextNamed, info, FixedDate));
    }

    [Fact]
    public void NamedPullCarriesNoChords()
    {
        var info = FixtureInfo() with
        {
            Current = new SlideText("verse words")
            {
                Chords = [Chord(0, 0, "G")],
                ObjectTexts = [new ObjectText("Verse", "verse words")],
            },
        };
        var named = Link(TextSourceKind.CurrentSlide);
        named.SourceObjectName = "Verse";
        var box = Text("b", "Pull", "", named);
        var resolved = LinkedText.ResolvedObjects([box], info, FixedDate)[0];
        Assert.Equal("verse words", resolved.Text);
        Assert.Null(resolved.Chords);
        box.TextLink = Link(TextSourceKind.CurrentSlide);
        var whole = LinkedText.ResolvedObjects([box], info, FixedDate)[0];
        Assert.Single(whole.Chords!); // whole-slide reads keep their chords
    }

    [Fact]
    public void SlideTextBuilderCarriesObjectTexts()
    {
        var verse = Text("v", "Verse", "verse words");
        verse.X = 0;
        var slide = new Slide
        {
            Id = "s", Name = "V1",
            Objects = [verse, Text("r", "Reference", "John 3:16"), Text("u", "", "orphan"), Text("e", "Empty", "")],
        };
        var text = ConfidenceSceneBuilder.SlideText(slide, null);
        Assert.Equal(["Verse", "Reference"], text.ObjectTexts!.Select(o => o.Name)); // named, text-carrying objects only, document order
    }

    [Fact]
    public void EmptySlideTextResolvesToNothing()
    {
        var info = FixtureInfo() with { Last = new SlideText("") };
        Assert.Null(LinkedText.Resolve(Link(TextSourceKind.LastSlide), info, FixedDate));
    }

    [Fact]
    public void ServiceDataSourcesResolve()
    {
        var info = FixtureInfo();
        Assert.Equal("3 of 12", LinkedText.Resolve(Link(TextSourceKind.SlidePosition), info, FixedDate));
        Assert.Equal("Amazing Grace", LinkedText.Resolve(Link(TextSourceKind.CurrentServiceItem), info, FixedDate));
        Assert.Equal("Announcements", LinkedText.Resolve(Link(TextSourceKind.NextServiceItem), info, FixedDate));
        Assert.Equal("Sunday Gathering", LinkedText.Resolve(Link(TextSourceKind.NextServiceTitle), info, FixedDate));
        Assert.Null(LinkedText.Resolve(Link(TextSourceKind.NextServiceTitle), new ConfidenceInfo(), FixedDate));
        Assert.Equal("Amazing Grace (Live in G)", LinkedText.Resolve(Link(TextSourceKind.CurrentPresentation), info, FixedDate));
    }

    [Fact]
    public void VideoCountdownResolvesFrozenRemaining()
    {
        var info = FixtureInfo();
        Assert.Equal("Bumper  1:30", LinkedText.Resolve(Link(TextSourceKind.VideoCountdown), info, FixedDate));
        Assert.Equal("Bumper  1:30", LinkedText.Resolve(Link(TextSourceKind.VideoCountdown), info, FixedDate.AddSeconds(3600)));
    }

    [Fact]
    public void VideoCountdownNamePrefixFollowsShowsVideoName()
    {
        var info = FixtureInfo();
        var bare = Link(TextSourceKind.VideoCountdown);
        bare.ShowsVideoName = false;
        Assert.Equal("1:30", LinkedText.Resolve(bare, info, FixedDate));
        var named = Link(TextSourceKind.VideoCountdown);
        named.ShowsVideoName = true;
        Assert.Equal("Bumper  1:30", LinkedText.Resolve(named, info, FixedDate));
    }

    [Fact]
    public void VideoCountdownReadsItsDeclaredLayer()
    {
        var info = FixtureInfo() with
        {
            VideoCountdowns = new Dictionary<string, VideoCountdown>
            {
                ["loopingVideos"] = new("Loop", 240, 60, FixedDate, IsPlaying: false),
            },
        };
        var background = Link(TextSourceKind.VideoCountdown);
        background.VideoLayer = "loopingVideos";
        background.ShowsVideoName = false;
        Assert.Equal("3:00", LinkedText.Resolve(background, info, FixedDate));
        Assert.Equal("Bumper  1:30", LinkedText.Resolve(Link(TextSourceKind.VideoCountdown), info, FixedDate)); // absent videoLayer stays the foreground default
        var idle = Link(TextSourceKind.VideoCountdown);
        idle.VideoLayer = "slide";
        Assert.Null(LinkedText.Resolve(idle, info, FixedDate));
    }

    [Fact]
    public void ClockUsesBoothDefaultWithoutAFormat() =>
        Assert.Equal(ConfidenceSceneBuilder.ClockString(FixedDate), LinkedText.Resolve(Link(TextSourceKind.Clock), FixtureInfo(), FixedDate));

    [Fact]
    public void ClockHonorsACustomFormat()
    {
        var custom = new TextLink { Source = TextSourceKind.Clock, ClockFormat = "mm" };
        var expected = TimeZoneInfo.ConvertTime(FixedDate, TimeZoneInfo.Local).Minute.ToString("00");
        Assert.Equal(expected, LinkedText.Resolve(custom, FixtureInfo(), FixedDate));
    }

    [Fact]
    public void TimerPicksTheNamedTimer()
    {
        var sermon = new TimerSnapshot("Sermon", TimerMode.Countdown, id: "sermon", banked: 60, durationSeconds: 300);
        var walkIn = new TimerSnapshot("Walk-in", TimerMode.Countdown, id: "walk-in", banked: 30, durationSeconds: 600);
        var info = FixtureInfo(timers: [walkIn, sermon]);
        var named = new TextLink { Source = TextSourceKind.Timer, TimerId = "sermon" };
        Assert.Equal("4:00", LinkedText.Resolve(named, info, FixedDate));
    }

    [Fact]
    public void TimerHonorsTheLinkFormat()
    {
        var evt = new TimerSnapshot("Event", TimerMode.Countdown, id: "event", banked: 0, durationSeconds: 3 * 86400 + 30 * 60 + 12);
        var info = FixtureInfo(timers: [evt]);
        var abbreviated = new TextLink { Source = TextSourceKind.Timer, TimerId = "event", TimerFormat = TimerTextFormat.Abbreviated };
        Assert.Equal("3d 0h 30m 12s", LinkedText.Resolve(abbreviated, info, FixedDate));
        var words = new TextLink { Source = TextSourceKind.Timer, TimerId = "event", TimerFormat = TimerTextFormat.Words };
        Assert.Equal("3 days 0 hours 30 minutes 12 seconds", LinkedText.Resolve(words, info, FixedDate));
        Assert.Equal("72:30:12", LinkedText.Resolve(new TextLink { Source = TextSourceKind.Timer, TimerId = "event" }, info, FixedDate)); // absent = digits
        var video = new TextLink { Source = TextSourceKind.VideoCountdown, ShowsVideoName = false, TimerFormat = TimerTextFormat.Abbreviated };
        Assert.Equal("1m 30s", LinkedText.Resolve(video, info, FixedDate));
    }

    [Fact]
    public void TimerPatternOutranksTheFormat()
    {
        var evt = new TimerSnapshot("Event", TimerMode.Countdown, id: "event", banked: 0, durationSeconds: 1046.42);
        var info = FixtureInfo(timers: [evt]);
        var pattern = new TextLink { Source = TextSourceKind.Timer, TimerId = "event", TimerFormat = TimerTextFormat.Words, TimerPattern = "m:ss.f" };
        Assert.Equal("17:26.4", LinkedText.Resolve(pattern, info, FixedDate));
        var empty = new TextLink { Source = TextSourceKind.Timer, TimerId = "event", TimerFormat = TimerTextFormat.Abbreviated, TimerPattern = "" };
        Assert.Equal("17m 26s", LinkedText.Resolve(empty, info, FixedDate)); // empty = the format read
        var video = new TextLink { Source = TextSourceKind.VideoCountdown, ShowsVideoName = false, TimerPattern = "S" };
        Assert.Equal("90", LinkedText.Resolve(video, info, FixedDate));
    }

    [Fact]
    public void SubSecondTickFollowsTheFractionPattern()
    {
        Assert.True(LinkedText.TicksSubSecond(new TextLink { Source = TextSourceKind.Timer, TimerPattern = "s.fff" }));
        Assert.True(LinkedText.TicksSubSecond(new TextLink { Source = TextSourceKind.VideoCountdown, TimerPattern = "ss.f" }));
        Assert.False(LinkedText.TicksSubSecond(new TextLink { Source = TextSourceKind.Timer, TimerPattern = "m:ss" }));
        Assert.False(LinkedText.TicksSubSecond(new TextLink { Source = TextSourceKind.Timer, TimerFormat = TimerTextFormat.Words }));
        Assert.False(LinkedText.TicksSubSecond(new TextLink { Source = TextSourceKind.Clock, TimerPattern = "s.f" })); // a clock never reads a timer pattern
    }

    [Fact]
    public void TimerFallsBackToThePrimary()
    {
        var idle = new TimerSnapshot("Idle", TimerMode.Countdown, id: "idle", banked: 0, durationSeconds: 600);
        var running = new TimerSnapshot("Live", TimerMode.Countdown, id: "live", isRunning: true, runningSince: FixedDate, banked: 100, durationSeconds: 300);
        var info = FixtureInfo(timers: [idle, running]);
        Assert.Equal("3:20", LinkedText.Resolve(Link(TextSourceKind.Timer), info, FixedDate));
    }

    [Fact]
    public void DeletedTimerRendersNothing()
    {
        var info = FixtureInfo(timers: [new TimerSnapshot("A", TimerMode.CountUp, id: "a")]);
        var dangling = new TextLink { Source = TextSourceKind.Timer, TimerId = "gone" };
        Assert.Null(LinkedText.Resolve(dangling, info, FixedDate)); // a deleted timer is a no-op, never an error
    }

    [Fact]
    public void StageMessageReadsTheConfidenceAlert()
    {
        var info = FixtureInfo(alert: new CueAlert("Mics hot", AlertBehavior.Persist, AlertTarget.Confidence));
        Assert.Equal("Mics hot", LinkedText.Resolve(Link(TextSourceKind.StageMessage), info, FixedDate));
    }

    [Fact]
    public void StageMessageRespectsTheFlashOffBeat()
    {
        var info = FixtureInfo(alert: new CueAlert("Mics hot", AlertBehavior.Flash, AlertTarget.Confidence), alertVisible: false);
        Assert.Null(LinkedText.Resolve(Link(TextSourceKind.StageMessage), info, FixedDate)); // the linked box blinks in step with the banner
    }

    [Fact]
    public void StageMessageIgnoresAudienceOnlyAlerts()
    {
        var info = FixtureInfo(alert: new CueAlert("Nursery", AlertBehavior.Persist, AlertTarget.Audience));
        Assert.Null(LinkedText.Resolve(Link(TextSourceKind.StageMessage), info, FixedDate));
    }

    [Fact]
    public void ResolvedObjectsLeavesUnlinkedObjectsUntouched()
    {
        var plain = Text("a", "Static", "hello");
        var before = MxuJson.Serialize(plain);
        var output = LinkedText.ResolvedObjects([plain], FixtureInfo(), FixedDate);
        Assert.Same(plain, Assert.Single(output));
        Assert.Equal(before, MxuJson.Serialize(output[0]));
    }

    [Fact]
    public void ResolvedObjectsSubstitutesLinkedText()
    {
        var linked = Text("b", "Next", "stored text is ignored", Link(TextSourceKind.NextSlide));
        var output = LinkedText.ResolvedObjects([linked], FixtureInfo(), FixedDate);
        Assert.Equal("next line", output[0].Text);
        Assert.Equal("b", output[0].Id);
        Assert.Equal("stored text is ignored", linked.Text); // the stored object is never written to
    }

    [Fact]
    public void ResolvedObjectsPreservesEverythingButTextAndInk()
    {
        var linked = Text("c", "Clock", "", Link(TextSourceKind.Clock));
        linked.MaskObjectId = "mask-target";
        linked.GroupId = "group-1";
        var output = LinkedText.ResolvedObjects([linked], FixtureInfo(), FixedDate);
        Assert.Equal("mask-target", output[0].MaskObjectId);
        Assert.Equal("group-1", output[0].GroupId);
    }

    [Fact]
    public void EmptySourceResolvesToEmptyText()
    {
        var linked = Text("d", "Next", "stale", Link(TextSourceKind.NextSlide));
        var info = FixtureInfo() with { Next = null };
        var output = LinkedText.ResolvedObjects([linked], info, FixedDate);
        Assert.Empty(output[0].Text); // no next slide = the box goes dark, not stale
    }

    [Fact]
    public void TimerWarningInkOverridesTheStyledColor()
    {
        var timer = new TimerSnapshot("T", TimerMode.Countdown, id: "t", banked: 280, durationSeconds: 300);
        var linked = Text("e", "Timer", "", Link(TextSourceKind.Timer));
        linked.TextStyle = new TextStyle { ColorHex = "#FFFFFFFF" };
        var output = LinkedText.ResolvedObjects([linked], FixtureInfo(timers: [timer]), FixedDate);
        Assert.Equal(TimerWarning.AmberHex, output[0].TextStyle?.ColorHex); // an active warning outruns styling
        Assert.Equal("#FFFFFFFF", linked.TextStyle.ColorHex);
    }

    [Fact]
    public void UsesWarningColorFalseKeepsTheStyledColor()
    {
        var timer = new TimerSnapshot("T", TimerMode.Countdown, id: "t", banked: 280, durationSeconds: 300);
        var linked = Text("f", "Timer", "", new TextLink { Source = TextSourceKind.Timer, UsesWarningColor = false });
        linked.TextStyle = new TextStyle { ColorHex = "#FFFFFFFF" };
        var output = LinkedText.ResolvedObjects([linked], FixtureInfo(timers: [timer]), FixedDate);
        Assert.Equal("#FFFFFFFF", output[0].TextStyle?.ColorHex);
    }

    [Fact]
    public void HealthyTimerKeepsTheStyledColor()
    {
        var timer = new TimerSnapshot("T", TimerMode.Countdown, id: "t", banked: 0, durationSeconds: 300);
        var linked = Text("g", "Timer", "", Link(TextSourceKind.Timer));
        linked.TextStyle = new TextStyle { ColorHex = "#AABBCCFF" };
        var output = LinkedText.ResolvedObjects([linked], FixtureInfo(timers: [timer]), FixedDate);
        Assert.Equal("#AABBCCFF", output[0].TextStyle?.ColorHex);
    }

    [Fact]
    public void IsTimeVaryingTruthTable()
    {
        Assert.True(LinkedText.IsTimeVarying(Link(TextSourceKind.Clock)));
        Assert.True(LinkedText.IsTimeVarying(Link(TextSourceKind.Timer)));
        Assert.True(LinkedText.IsTimeVarying(Link(TextSourceKind.VideoCountdown)));
        Assert.False(LinkedText.IsTimeVarying(Link(TextSourceKind.CurrentSlide)));
        Assert.False(LinkedText.IsTimeVarying(Link(TextSourceKind.NextSlide)));
        Assert.False(LinkedText.IsTimeVarying(Link(TextSourceKind.LastSlide)));
        Assert.False(LinkedText.IsTimeVarying(Link(TextSourceKind.SlidePosition)));
        Assert.False(LinkedText.IsTimeVarying(Link(TextSourceKind.CurrentServiceItem)));
        Assert.False(LinkedText.IsTimeVarying(Link(TextSourceKind.NextServiceItem)));
        Assert.False(LinkedText.IsTimeVarying(Link(TextSourceKind.NextServiceTitle)));
        Assert.False(LinkedText.IsTimeVarying(Link(TextSourceKind.StageMessage)));
    }

    [Fact]
    public void NamedTimerPreviewsWithTheSampleTimer()
    {
        var named = new TextLink { Source = TextSourceKind.Timer, TimerId = "item-remaining", TimerPattern = "m:ss.f" };
        Assert.Equal("17:26.4", LinkedText.SampleText(named));
        var obj = Text("t", "Timer", "", named);
        var previewed = LinkedText.PreviewObjects([obj]);
        Assert.Equal("17:26.4", previewed[0].Text);
        Assert.Equal("item-remaining", previewed[0].TextLink?.TimerId);
        Assert.Null(LinkedText.Resolve(named, LinkedText.PreviewInfo, LinkedText.PreviewDate)); // the live read still renders nothing for a timer the room lacks
    }

    [Fact]
    public void EverySourceHasAVisiblePreview()
    {
        foreach (var source in Enum.GetValues<TextSourceKind>())
            Assert.False(string.IsNullOrEmpty(LinkedText.SampleText(Link(source))), $"previewInfo leaves {source} dark");
    }

    [Fact]
    public void AuthoringHidesNextServiceTitleUnlessAlreadyLinked()
    {
        Assert.DoesNotContain(TextSourceKind.NextServiceTitle, LinkedText.AuthoringSources(null));
        Assert.DoesNotContain(TextSourceKind.NextServiceTitle, LinkedText.AuthoringSources(TextSourceKind.Clock));
        Assert.Contains(TextSourceKind.NextServiceTitle, LinkedText.AuthoringSources(TextSourceKind.NextServiceTitle));
        Assert.Equal(Enum.GetValues<TextSourceKind>().Length - 1, LinkedText.AuthoringSources(null).Count);
    }

    [Fact]
    public void TimecodeSourceCountsHoldsAndHidesWithNoInput()
    {
        var info = new ConfidenceInfo();
        Assert.Null(LinkedText.Resolve(Link(TextSourceKind.Timecode), info, FixedDate)); // no input, nothing shown

        info = info with { Timecode = new TimecodeDisplay(3600, TimecodeRate.Fps30, FixedDate, IsRunning: true) };
        Assert.Equal("01:00:00:00", LinkedText.Resolve(Link(TextSourceKind.Timecode), info, FixedDate));
        Assert.Equal("01:00:00:15", LinkedText.Resolve(Link(TextSourceKind.Timecode), info, FixedDate.AddSeconds(0.5)));

        info = info with { Timecode = info.Timecode with { IsRunning = false } };
        Assert.Equal("01:00:00:00", LinkedText.Resolve(Link(TextSourceKind.Timecode), info, FixedDate.AddSeconds(10)));

        var rate = TimecodeRate.Fps2997DropFrame;
        info = info with
        {
            Timecode = new TimecodeDisplay(rate.Seconds(new TimecodeLabel(0, 1, 0, 2)) + 0.001, rate, FixedDate, IsRunning: false),
        };
        Assert.Equal("00:01:00;02", LinkedText.Resolve(Link(TextSourceKind.Timecode), info, FixedDate));
        Assert.True(LinkedText.IsTimeVarying(Link(TextSourceKind.Timecode)));
        Assert.True(LinkedText.TicksSubSecond(Link(TextSourceKind.Timecode))); // frames tick faster than a second
        Assert.Equal("01:01:02:15", LinkedText.Resolve(Link(TextSourceKind.Timecode), LinkedText.PreviewInfo, LinkedText.PreviewDate));
    }

    // MARK: ChordSceneTests.swift, resolution level

    private static Slide ChordedSlide()
    {
        var lyrics = Text("lyrics", "Lyrics", "Amazing grace how sweet the sound\nThat saved a wretch like me");
        lyrics.Chords = [Chord(0, 0, "G"), Chord(1, 5, "C")];
        return new Slide { Id = "s1", Name = "", Objects = [lyrics] };
    }

    private static ConfidenceInfo ChordedInfo() => new()
    {
        Current = ConfidenceSceneBuilder.SlideText(ChordedSlide(), new Presentation
        {
            Id = "p", Name = "Song", PresentationKind = PresentationKind.Deck, ThemeId = "", Slides = [],
            MusicKey = "G", DisplayKey = "A",
        }),
        Next = new SlideText("next one\nnext two\nnext three") { Chords = [Chord(0, 0, "G"), Chord(2, 0, "D")] },
    };

    [Fact]
    public void LyricChordsShiftLinesAcrossObjects()
    {
        var first = Text("a", "A", "one\ntwo");
        first.Chords = [Chord(1, 0, "G")];
        var second = Text("b", "B", "three");
        second.Chords = [Chord(0, 2, "C")];
        var media = new SlideObject { Id = "m", ObjectKind = SlideObjectKind.Media, Name = "M", Text = "" };
        var slide = new Slide { Id = "s", Name = "", Objects = [first, media, Text("e", "E", ""), second] };

        var chords = ConfidenceSceneBuilder.LyricChords(slide);
        Assert.Equal("one\ntwo\nthree", ConfidenceSceneBuilder.LyricText(slide));
        Assert.Equal([1, 2], chords.Select(c => c.Line));
        Assert.Equal(["G", "C"], chords.Select(c => c.Symbol));
    }

    [Fact]
    public void ChordsOnlyBlankSlideReachesTheConfidenceBody()
    {
        var blank = Text("e", "E", "");
        blank.Chords = [Chord(0, 0, "G"), Chord(0, 0, "D")];
        var slide = new Slide { Id = "s", Name = "", Objects = [blank, Text("v", "V", "words")] };
        Assert.Equal("\nwords", ConfidenceSceneBuilder.LyricText(slide));
        Assert.Equal([0, 0], ConfidenceSceneBuilder.LyricChords(slide).Select(c => c.Line));

        var chordsOnly = new Slide { Id = "c", Name = "", Objects = [blank] };
        var box = Text("box", "Current", "", Link(TextSourceKind.CurrentSlide));
        box.TextStyle = new TextStyle { ShowChords = true };
        var info = new ConfidenceInfo { Current = ConfidenceSceneBuilder.SlideText(chordsOnly, null) };
        var resolved = LinkedText.ResolvedObjects([box], info, FixedDate)[0];
        Assert.Equal("", resolved.Text);
        Assert.Equal(["G", "D"], resolved.Chords!.Select(c => c.Symbol));

        var empty = new Slide { Id = "blank", Name = "", Objects = [] };
        Assert.Equal("c", ConfidenceSceneBuilder.NextSlide([empty, chordsOnly], skippingBlanks: true)?.Id);
    }

    [Fact]
    public void MaxLinesTruncatesBodyAndChords()
    {
        var box = Text("box", "Next", "", new TextLink { Source = TextSourceKind.NextSlide, MaxLines = 1 });
        var resolved = LinkedText.ResolvedObjects([box], ChordedInfo(), FixedDate)[0];
        Assert.Equal("next one", resolved.Text);
        Assert.Equal(["G"], resolved.Chords!.Select(c => c.Symbol));
    }

    [Fact]
    public void MaxLinesLeavesShortSlidesAlone()
    {
        var box = Text("box", "Next", "", new TextLink { Source = TextSourceKind.NextSlide, MaxLines = 5 });
        var resolved = LinkedText.ResolvedObjects([box], ChordedInfo(), FixedDate)[0];
        Assert.Equal("next one\nnext two\nnext three", resolved.Text);
    }

    [Fact]
    public void NonSlideSourcesNeverCarryChords()
    {
        var box = Text("box", "Clock", "", Link(TextSourceKind.Clock));
        var resolved = LinkedText.ResolvedObjects([box], ChordedInfo(), FixedDate)[0];
        Assert.Null(resolved.Chords);
    }

    [Fact]
    public void ResolvedChordsAreCopiesOfTheInfo()
    {
        var info = ChordedInfo();
        var box = Text("box", "Current", "", Link(TextSourceKind.CurrentSlide));
        var resolved = LinkedText.ResolvedObjects([box], info, FixedDate)[0];
        resolved.Chords![0].Symbol = "X";
        Assert.Equal("G", info.Current!.Chords[0].Symbol);
    }
}
