using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using T = MxU.Slides.Core.Automation.PresentationTimelines;
using E = MxU.Slides.Core.Automation.TimelineEffect;

namespace MxU.Slides.Core.Tests.Automation;

// Ported from PresenterCoreTests/PresentationTimelineTests.swift (the Automerge round trip
// becomes a JSON round trip; DeckField has no Windows twin).
public class PresentationTimelineTests
{
    internal static readonly List<TimelinePlayedBlock> Blocks =
    [
        new("v", ["v1", "v2"]),
        new("c", ["c1"]),
        new("v", ["v1", "v2"]),
        new("c", ["c1"]),
    ];

    internal static TimelineCue SlideAt(string id, double at, int occurrence) => T.SlideCue(id, at, T.Anchor(occurrence, Blocks)!);

    internal static TimelineCue Block(string id, TimelineLane lane, double at, string reference, double? duration = null) => new()
    {
        Id = id, Lane = lane.Raw(), At = at, Duration = duration,
        MediaId = lane is TimelineLane.Videos or TimelineLane.StillGraphics or TimelineLane.LoopingVideos or TimelineLane.Audio ? reference : null,
        OverlayId = lane == TimelineLane.Overlays ? reference : null,
        AlertId = lane == TimelineLane.Alerts ? reference : null,
        LiveInputId = lane == TimelineLane.VideoInput ? reference : null,
    };

    private static readonly HashSet<string> None = [];

    [Fact]
    public void V140PresentationDecodesWithNoTimelines()
    {
        var decoded = MxuJson.Deserialize<Presentation>("""{"id":"p","name":"Song","presentationKind":"deck","themeId":"","slides":[]}""");
        Assert.Null(decoded.Timelines);
    }

    [Fact]
    public void TimelinesRoundTripThroughTheDocument()
    {
        var presentation = Make.Deck("p");
        presentation.Timelines =
        [
            Make.Timeline("t", true, 108_000, 240, [
                SlideAt("s", 1.5, 4),
                Block("o", TimelineLane.Overlays, 2, "lower-third", 8),
                Make.Cue("x", "actions", 3, actions: [Make.Action("a1", SlideActionKind.TimerStart, a => a.TimerId = "t1")]),
            ], arrangementId: "a"),
        ];
        var decoded = MxuJson.Deserialize<Presentation>(MxuJson.Serialize(presentation));
        Assert.True(Make.Same(presentation.Timelines, decoded.Timelines));
    }

    [Fact]
    public void EachArrangementHasItsOwnTimeline()
    {
        var presentation = Make.Deck("p");
        var @base = Make.Timeline("base");
        var live = Make.Timeline("live", arrangementId: "a");
        presentation.Timelines = T.Replacing(live, T.Replacing(@base, null));
        Assert.Equal("base", T.Timeline(null, presentation)?.Id);
        Assert.Equal("base", T.Timeline("", presentation)?.Id);
        Assert.Equal("live", T.Timeline("a", presentation)?.Id);
        Assert.Null(T.Timeline("other", presentation));
    }

    [Fact]
    public void ARepeatedChorusIsADifferentCueEachTime()
    {
        var first = SlideAt("1", 0, 2);
        var second = SlideAt("2", 0, 5);
        Assert.True(first.SlideId == "c1" && second.SlideId == "c1");
        Assert.True(first.SectionOccurrence == 0 && second.SectionOccurrence == 1);
        Assert.Equal(2, T.Occurrence(first, Blocks));
        Assert.Equal(5, T.Occurrence(second, Blocks));
    }

    [Fact]
    public void AnArrangementEditOrphansItsCuesInsteadOfMisfiring()
    {
        Assert.Null(T.Occurrence(SlideAt("2", 0, 5), Blocks.Take(3).ToList()));
        List<TimelinePlayedBlock> moved = [new("c", []), .. Blocks.Skip(2)];
        Assert.Null(T.Occurrence(SlideAt("1", 0, 2), moved));
    }

    [Fact]
    public void SectionsTakeTheTimeFromTheirFirstCueToTheNext()
    {
        List<TimelineCue> cues = [SlideAt("a", 0, 0), SlideAt("b", 5, 1), SlideAt("c", 10, 2), SlideAt("d", 25, 3)];
        var spans = T.SectionSpans(cues, Blocks, 40);
        Assert.Equal([0, 1, 2], spans.Select(s => s.Block));
        Assert.Equal(["v", "c", "v"], spans.Select(s => s.SectionId));
        Assert.Equal([0.0, 10, 25], spans.Select(s => s.Start));
        Assert.Equal([10.0, 25, 40], spans.Select(s => s.End));
    }

    [Fact]
    public void NormalPlayFiresEachCueOnceWithItsActions()
    {
        List<TimelineCue> cues =
        [
            SlideAt("s1", 0, 0),
            SlideAt("s2", 4, 1),
            Block("bg", TimelineLane.LoopingVideos, 0, "motion"),
            Block("ov", TimelineLane.Overlays, 2, "lower-third", 3),
            Make.Cue("act", "actions", 4.5),
            Block("fg", TimelineLane.Videos, 6, "bumper", 0.5),
        ];
        var live = new HashSet<string>();
        var fired = new List<TimelineEffect>();
        var last = -T.Frame;
        for (var frame = 0; frame <= 7 * 30; frame++)
        {
            var tick = frame / 30.0;
            var effects = T.Crossing(cues, Blocks, last, tick, live);
            foreach (var effect in effects)
                if (effect is E.Fire fire) live.Add(fire.Id);
            fired.AddRange(effects);
            last = tick;
        }
        Assert.Equal(
        [
            new E.Fire(TimelineLane.LoopingVideos, "motion"),
            new E.ShowSlide(0, true),
            new E.Fire(TimelineLane.Overlays, "lower-third"),
            new E.ShowSlide(1, true),
            new E.RunActions("act"),
            new E.Clear(TimelineLane.Overlays, "lower-third"),
            new E.Fire(TimelineLane.Videos, "bumper"),
            new E.Clear(TimelineLane.Videos, "bumper"),
        ], fired);
    }

    [Fact]
    public void APlayingBackgroundNeverRetriggers()
    {
        List<TimelineCue> cues = [Block("bg", TimelineLane.LoopingVideos, 1, "motion")];
        Assert.Empty(T.Crossing(cues, Blocks, 0, 2, new HashSet<string> { "motion" }));
        Assert.Equal([new E.Fire(TimelineLane.LoopingVideos, "motion")], T.Crossing(cues, Blocks, 0, 2, None));
    }

    [Fact]
    public void ABackgroundHasNoOutPoint()
    {
        List<TimelineCue> cues = [Block("bg", TimelineLane.LoopingVideos, 0, "motion", 2)];
        Assert.Empty(T.Crossing(cues, Blocks, 1, 3, new HashSet<string> { "motion" }));
    }

    [Fact]
    public void SlidesCrossedInOneStepShowOnlyTheLast()
    {
        List<TimelineCue> cues = [SlideAt("a", 1, 0), SlideAt("b", 1.5, 1)];
        Assert.Equal([new E.ShowSlide(1, true)], T.Crossing(cues, Blocks, 0, 2, None));
    }

    [Fact]
    public void OrphanedSlideCuesNeverFire()
    {
        List<TimelineCue> orphan = [Make.Cue("o", "slide", 1, sectionId: "gone", slideId: "x")];
        Assert.Empty(T.Crossing(orphan, Blocks, 0, 2, None));
        Assert.Empty(T.Landing(orphan, Blocks, 2, None));
    }

    [Fact]
    public void AJumpShowsTheSlideWithoutItsActionsAndNeverRunsActions()
    {
        List<TimelineCue> cues = [SlideAt("s1", 0, 0), SlideAt("s2", 10, 1), Make.Cue("act", "actions", 9)];
        Assert.Equal([new E.ShowSlide(1, false)], T.Landing(cues, Blocks, 12, None));
        Assert.Equal([new E.ShowSlide(0, false)], T.Landing(cues, Blocks, 5, None));
    }

    [Fact]
    public void AJumpFiresTheOverlaysItLandsInAndClearsTheRest()
    {
        List<TimelineCue> cues = [Block("a", TimelineLane.Overlays, 0, "title", 5), Block("b", TimelineLane.Overlays, 10, "lyrics-bug", 5)];
        Assert.Equal(
            [new E.Clear(TimelineLane.Overlays, "title"), new E.Fire(TimelineLane.Overlays, "lyrics-bug")],
            T.Landing(cues, Blocks, 12, new HashSet<string> { "title" }));
        Assert.Empty(T.Landing(cues, Blocks, 12, new HashSet<string> { "lyrics-bug", "hand-fired" }));
    }

    [Fact]
    public void AJumpFiresABackgroundOnlyWhenItIsADifferentOne()
    {
        List<TimelineCue> cues = [Block("one", TimelineLane.LoopingVideos, 0, "blue"), Block("two", TimelineLane.LoopingVideos, 30, "red")];
        Assert.Empty(T.Landing(cues, Blocks, 20, new HashSet<string> { "blue" }));
        Assert.Equal([new E.Fire(TimelineLane.LoopingVideos, "red")], T.Landing(cues, Blocks, 40, new HashSet<string> { "blue" }));
    }

    [Fact]
    public void AJumpFiresAForegroundVideoOnlyInsideItsBlock()
    {
        List<TimelineCue> cues = [Block("fg", TimelineLane.Videos, 10, "bumper", 5)];
        Assert.Equal([new E.Fire(TimelineLane.Videos, "bumper")], T.Landing(cues, Blocks, 12, None));
        Assert.Empty(T.Landing(cues, Blocks, 16, None));
        Assert.Empty(T.Landing(cues, Blocks, 12, new HashSet<string> { "bumper" }));
        Assert.Equal([new E.Clear(TimelineLane.Videos, "bumper")], T.Landing(cues, Blocks, 16, new HashSet<string> { "bumper" }));
    }

    [Fact]
    public void ATakeReplacesOnlyTheSlideCuesItPlayedOver()
    {
        List<TimelineCue> cues =
        [
            SlideAt("early", 2, 0), SlideAt("start", 5, 1), SlideAt("mid", 7, 2), Block("ov", TimelineLane.Overlays, 8, "title", 2),
            SlideAt("late", 12, 3), SlideAt("far", 30, 4),
        ];
        var take = new T.Take(cues, 5);
        Assert.Equal(["early", "mid", "ov", "late", "far"], take.Cues.Select(c => c.Id));
        take.Played(5, 9);
        take.Record(SlideAt("new", 6, 2));
        Assert.Equal(["early", "new", "ov", "late", "far"], take.Cues.Select(c => c.Id));

        take.Record(SlideAt("again", 6.01, 3));
        Assert.Equal(["early", "again", "ov", "late", "far"], take.Cues.Select(c => c.Id));

        take.Played(25, 31);
        Assert.Equal(["early", "again", "ov", "late"], take.Cues.Select(c => c.Id));
        Assert.True(Make.Same(cues, take.Original.ToList()));
    }

    [Fact]
    public void SlidesFiredByIdTakeTheirNextPlaceInTheOrder()
    {
        (double, string)[] fires = [(1, "v1"), (3, "v2"), (5, "c1"), (9, "v1"), (11.01, "v2"), (13, "c1"), (15, "c1"), (20, "gone")];
        var (cues, missing) = T.SlideCues(fires, Blocks, "pro-");
        Assert.Equal(1, missing);
        Assert.Equal([0, 1, 2, 3, 4, 5, 5], cues.Select(c => T.Occurrence(c, Blocks)));
        Assert.True(cues[5].SectionId == "c" && cues[5].SectionOccurrence == 1, "the second chorus");
        Assert.Equal(T.SnappedToFrame(11.01), cues[4].At);
        Assert.Equal("pro-0", cues[0].Id);
    }

    [Fact]
    public void SlideMarkersShowTheWordsThatFit()
    {
        const string text = "Amazing grace\nhow sweet the sound";
        Assert.Equal("Amazing grace how sweet the sound", T.LeadingWords(text, 1000));
        Assert.Equal("Amazing grace", T.LeadingWords(text, 90));
        Assert.Equal("", T.LeadingWords(text, 20));
    }

    [Fact]
    public void TheNextSlideCueSkipsOrphans()
    {
        var orphan = Make.Cue("o", "slide", 4, sectionId: "x", sectionOccurrence: 0, slideId: "nope");
        List<TimelineCue> cues = [SlideAt("a", 2, 0), orphan, SlideAt("b", 6, 1)];
        Assert.Equal("b", T.NextSlideCue(2, cues, Blocks)?.Id);
        Assert.Null(T.NextSlideCue(6, cues, Blocks));
    }

    [Fact]
    public void ASlideCueMovedOntoAnotherStepsPastIt()
    {
        List<TimelineCue> cues = [SlideAt("a", 1, 0), SlideAt("b", 2, 1), SlideAt("c", 2 + T.Frame, 2)];
        var moved = cues.Select(c => c.Copy()).ToList();
        moved[0].At = 2;
        Assert.Equal(T.SnappedToFrame(2 + 2 * T.Frame), T.SteppingSlideCuesApart(moved, new HashSet<string> { "a" }, true)[0].At);
        Assert.Equal(T.SnappedToFrame(2 - T.Frame), T.SteppingSlideCuesApart(moved, new HashSet<string> { "a" }, false)[0].At);
        Assert.Equal(2, moved[0].At);

        List<TimelineCue> atZero = [SlideAt("a", 0, 0), SlideAt("z", 0, 1)];
        Assert.Equal(T.Frame, T.SteppingSlideCuesApart(atZero, new HashSet<string> { "a" }, false)[0].At);
    }

    [Fact]
    public void NudgesSnapToFramesAndStopAtZero()
    {
        var cue = SlideAt("s", 1, 0);
        Assert.Equal(T.SnappedToFrame(1 + 1.0 / 30), T.Moved(cue, T.Frame).At);
        Assert.Equal(0, T.Moved(cue, -5).At);
        Assert.Equal(1, cue.At);
    }

    [Fact]
    public void CardDragsMoveStretchAndTrimOnFrames()
    {
        var overlay = Block("o", TimelineLane.Overlays, 2, "x", 4);
        var point = SlideAt("s", 3, 0);
        List<TimelineCue> cues = [overlay, point];
        Assert.Equal(3, T.Edited(overlay, cues, TimelineBlockEdit.Mode.Move, 1).At);
        Assert.Equal(5, T.Edited(overlay, cues, TimelineBlockEdit.Mode.Stretch, 1).Duration);
        var trimmed = T.Edited(overlay, cues, TimelineBlockEdit.Mode.Trim, 1);
        Assert.True(trimmed.At == 3 && trimmed.Duration == 3);
        Assert.Equal(T.SnappedToFrame(6 - T.Frame), T.Edited(overlay, cues, TimelineBlockEdit.Mode.Trim, 10).At);

        var open = Block("v", TimelineLane.Videos, 2, "clip");
        Assert.Equal(18, T.Edited(open, [open], TimelineBlockEdit.Mode.Stretch, -10, openEnd: 30).Duration);
        Assert.Equal(4, T.Edited(point, cues, TimelineBlockEdit.Mode.Stretch, 1).At);
    }

    [Fact]
    public void LengthIsTheLastEndUnlessSet()
    {
        List<TimelineCue> cues = [SlideAt("s", 3, 0), Block("o", TimelineLane.Overlays, 4, "x", 6), Block("b", TimelineLane.LoopingVideos, 12, "y")];
        var timeline = Make.Timeline("t", cues: cues);
        Assert.Equal(12, T.Length(timeline));
        timeline.Length = 90;
        Assert.Equal(90, T.Length(timeline));
    }

    [Fact]
    public void TimecodeStartsReadAndWriteAsFramesAt30()
    {
        Assert.Equal("01:00:00:00", T.Timecode(108_000));
        Assert.Equal("01:01:01:29", T.Timecode(3600 * 30 + 61 * 30 + 29));
        Assert.Equal(3600 * 30 + 61 * 30 + 29, T.Frames("01:01:01:29"));
        Assert.Equal((2 * 3600 + 600) * 30, T.Frames("02:10:00"));
        Assert.Equal(15, T.Frames("00:00:00;15"));
        Assert.Null(T.Frames("00:61:00:00"));
        Assert.Null(T.Frames("00:00:00:30"));
        Assert.Null(T.Frames("nonsense"));
    }

    [Fact]
    public void OverlappingStartsAreFoundAndAFreeHourSuggested()
    {
        const int hour = 3600 * 30;
        var songA = new T.Window("a", hour, 300 * 30);
        var songB = new T.Window("b", 2 * hour, 300 * 30);
        var mine = new T.Window("me", hour + 60 * 30, 240 * 30);
        Assert.Equal(["a"], T.Overlaps(mine, [songA, songB]).Select(w => w.Id));
        Assert.Empty(T.Overlaps(new T.Window("me", hour + 300 * 30, 10), [songA]));
        Assert.Equal(3 * hour, T.SuggestedStart(mine.Length, [songA, songB], "me"));
        Assert.Equal(hour, T.SuggestedStart(mine.Length, [new T.Window("me", hour, 10)], "me"));
    }

    [Fact]
    public void LaneIdsReadV141SpellingsAndKeepUnknownOnesQuiet()
    {
        Assert.Equal(TimelineLane.Slide, TimelineLanes.Parse("slides"));
        Assert.Equal(TimelineLane.Videos, TimelineLanes.Parse("foregroundVideos"));
        Assert.Equal(TimelineLane.LoopingVideos, TimelineLanes.Parse("background"));
        Assert.Equal(TimelineLane.VideoInput, TimelineLanes.Parse("videoInput"));
        var unknown = Make.Cue("x", "customLayer7", 1, mediaId: "m");
        Assert.Null(unknown.Kind());
        Assert.Empty(T.Crossing([unknown], Blocks, 0, 2, None));
        Assert.Empty(T.Landing([unknown], Blocks, 1.5, None));
    }

    [Fact]
    public void EveryLayerLaneFiresAndComesDownAtItsOutPoint()
    {
        List<TimelineCue> cues =
        [
            Block("a", TimelineLane.Alerts, 1, "welcome", 2),
            Block("s", TimelineLane.StillGraphics, 1, "logo", 2),
            Block("i", TimelineLane.VideoInput, 1, "camera-1", 2),
        ];
        Assert.Equal(
        [
            new E.Fire(TimelineLane.Alerts, "welcome"), new E.Fire(TimelineLane.StillGraphics, "logo"), new E.Fire(TimelineLane.VideoInput, "camera-1"),
            new E.Clear(TimelineLane.Alerts, "welcome"), new E.Clear(TimelineLane.StillGraphics, "logo"), new E.Clear(TimelineLane.VideoInput, "camera-1"),
        ], T.Crossing(cues, Blocks, 0, 4, None));
        Assert.Equal(
            [new E.Fire(TimelineLane.Alerts, "welcome"), new E.Fire(TimelineLane.StillGraphics, "logo")],
            T.Landing(cues, Blocks, 2, new HashSet<string> { "camera-1" }));
        Assert.Equal(
            [new E.Clear(TimelineLane.Alerts, "welcome"), new E.Clear(TimelineLane.VideoInput, "camera-1")],
            T.Landing(cues, Blocks, 5, new HashSet<string> { "welcome", "camera-1" }));
    }

    [Fact]
    public void ABackingTrackFollowsThePlayhead()
    {
        List<TimelineCue> cues = [Block("t", TimelineLane.Audio, 2, "track", 180)];
        var started = T.Crossing(cues, Blocks, 1.99, 2.02, None);
        var play = Assert.IsType<E.PlayAudio>(Assert.Single(started));
        Assert.True(play.Id == "track" && play.Clip == "t");
        Assert.True(Math.Abs(play.From - 0.02) < 0.001);
        Assert.Equal([new E.EndAudio("track", "t")], T.Crossing(cues, Blocks, 181, 183, new HashSet<string> { "track" }));
        Assert.Equal([new E.PlayAudio("track", 60, "t")], T.Landing(cues, Blocks, 62, new HashSet<string> { "track" }));
        Assert.Equal([new E.Clear(TimelineLane.Audio, "track")], T.Landing(cues, Blocks, 190, new HashSet<string> { "track" }));
    }

    // C#: the cue copy is hand-written for speed; this catches a schema field it misses.
    [Fact]
    public void ACueCopyKeepsEveryField()
    {
        var cue = Make.Cue("x", "track:keys", 1.5, 3, "m", 0.5, "o", "a", "l", "s", 2, "sl", [Make.Action("a1", SlideActionKind.ClearAll)]);
        cue.FadeIn = Make.Fade(1, "sCurve", "centered", 0.4);
        cue.FadeOut = Make.Fade(2);
        cue.EdgeFades = false;
        var json = MxuJson.Serialize(cue);
        foreach (var property in typeof(TimelineCue).GetProperties())
            Assert.True(property.GetValue(cue) is not null, $"set {property.Name} in this test");
        Assert.Equal(json, MxuJson.Serialize(cue.Copy()));
        Assert.NotSame(cue.FadeIn, cue.Copy().FadeIn);

        var timeline = Make.Timeline("t", true, 5, 9, [cue], "arr");
        timeline.MapLayout = [new SongMapBlock { Occurrence = 0, Start = 0, Length = 1, AudioSectionIds = ["a"] }];
        foreach (var property in typeof(PresentationTimeline).GetProperties())
            Assert.True(property.GetValue(timeline) is not null, $"set {property.Name} in this test");
        Assert.Equal(MxuJson.Serialize(timeline), MxuJson.Serialize(timeline.Copy()));
    }

    // C#: SlideSceneBuilder.timelineBlocks, which the player uses.
    [Fact]
    public void BlocksFollowTheArrangementOrTheSlidesOwnSections()
    {
        var deck = Make.Deck("p", ("v1", "v"), ("v2", "v"), ("c1", "c"), ("x", null));
        Assert.Equal([new("v", ["v1", "v2"]), new("c", ["c1"]), new TimelinePlayedBlock(null, ["x"])], T.Blocks(deck));
        deck.Arrangements = [new Arrangement { Id = "a", Name = "a", SectionIds = ["c", "v", "c"] }];
        Assert.Equal([new("c", ["c1"]), new("v", ["v1", "v2"]), new TimelinePlayedBlock("c", ["c1"])], T.Blocks(deck, "a"));
        Assert.Equal(3, T.Blocks(deck, "missing").Count);
    }
}
