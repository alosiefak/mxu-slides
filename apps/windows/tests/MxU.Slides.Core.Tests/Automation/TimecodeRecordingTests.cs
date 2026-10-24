using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;
using State = MxU.Slides.Core.Automation.TimecodeClock.State;
using Step = MxU.Slides.Core.Automation.TimecodeRecording.Step;
using T = MxU.Slides.Core.Automation.PresentationTimelines;

namespace MxU.Slides.Core.Tests.Automation;

// Ported from PresenterCoreTests/TimecodeRecordingTests.swift.
public class TimecodeRecordingTests
{
    private static PresentationTimeline Timeline(bool enabled, int start = 108_000) => Make.Timeline("t", enabled, start);

    private static TimecodeClock.Reading Reading(double seconds, State state = State.Locked, TimecodeRate rate = TimecodeRate.Fps30, bool reverse = false) =>
        new(state, seconds, rate.LabelForSeconds(seconds), rate, reverse);

    [Fact]
    public void RecordArmsForTimecodeOnlyOnAFollowingComputer()
    {
        Assert.Equal(new TimecodeRecording.Mode.Timecode(108_000), TimecodeRecording.ModeFor(Timeline(true), true, true));
        Assert.Equal(new TimecodeRecording.Mode.Timecode(null), TimecodeRecording.ModeFor(null, true, true));
        Assert.IsType<TimecodeRecording.Mode.OwnClock>(TimecodeRecording.ModeFor(Timeline(false), true, true));
        Assert.IsType<TimecodeRecording.Mode.OwnClock>(TimecodeRecording.ModeFor(null, false, true));
        Assert.IsType<TimecodeRecording.Mode.OwnClock>(TimecodeRecording.ModeFor(Timeline(true), true, false));
    }

    [Fact]
    public void ANewSongStartsAtTheTimecodesHourAndMinute()
    {
        var take = new TimecodeRecording("a", null);
        var begin = Assert.IsType<Step.Begin>(take.StepFor(Reading(3600 + 600 + 42 + 17.0 / 30)));
        Assert.Equal(126_000, begin.Start);
        Assert.True(Math.Abs(begin.At - (42 + 17.0 / 30)) < 1e-9);
        Assert.Equal(216_000, TimecodeRecording.StartFrom(new TimecodeLabel(2, 0, 59, 29)));
    }

    [Fact]
    public void ATakeWaitsForTheSongsStart()
    {
        var take = new TimecodeRecording("a", 108_000);
        Assert.IsType<Step.None>(take.StepFor(Reading(3599)));
        Assert.IsType<Step.None>(take.StepFor(Reading(3599, State.Stopped)));
        Assert.IsType<Step.None>(take.StepFor(new TimecodeClock.Reading(State.NoInput)));
        Assert.Equal(new Step.Begin(108_000, 2.5), take.StepFor(Reading(3602.5)));
        Assert.Equal(new Step.Begin(108_000, 2.5), take.StepFor(Reading(3602.5, rate: TimecodeRate.Fps25)));
    }

    [Fact]
    public void ARunningTakeEndsWhenTimecodeStopsOnly()
    {
        var take = new TimecodeRecording("a", 108_000) { Started = true };
        Assert.IsType<Step.None>(take.StepFor(Reading(3610)));
        Assert.IsType<Step.None>(take.StepFor(Reading(3610, State.Freewheel)));
        Assert.IsType<Step.None>(take.StepFor(Reading(3610, reverse: true)));
        Assert.IsType<Step.End>(take.StepFor(Reading(3610, State.Stopped)));
        Assert.IsType<Step.End>(take.StepFor(new TimecodeClock.Reading(State.NoInput)));
    }

    [Fact]
    public void WhileRecordingTimecodeFollowsOnlyThatSongWithNoEnd()
    {
        var take = new TimecodeRecording("b", 126_000);
        List<TimecodeFollow.Song> songs = [new("a", 108_000, 30), new("b", 126_000, 1, 1)];
        var followed = take.Songs(songs);
        Assert.Equal(["b"], followed.Select(s => s.ItemId));
        var found = TimecodeFollow.SongContaining(4200 + 600, TimecodeRate.Fps30, followed)!.Value;
        Assert.Equal("b", found.Song.ItemId);
        Assert.True(Math.Abs(found.Position - 600) < 1e-9);
        Assert.Null(TimecodeFollow.SongContaining(3605, TimecodeRate.Fps30, followed));
    }
}

// Ported from PresenterCoreTests/TimecodeStartTimesTests.swift (the Automerge round trip becomes JSON).
public class TimecodeStartTimesTests
{
    private const int Hour = 3600 * T.FramesPerSecond;

    private static PresentationTimeline Timeline(string id, int start = 0, double length = 300) => Make.Timeline(id, true, start, length);

    private static readonly List<(string Id, PresentationTimeline? Timeline)> Service =
    [
        ("a", Timeline("ta")), ("header", null), ("b", Timeline("tb")), ("bare", null), ("c", Timeline("tc")),
    ];

    private static List<TimecodeFollow.Song> Songs(bool byOrder, List<(string Id, PresentationTimeline? Timeline)>? items = null)
    {
        items ??= Service;
        var starts = T.ServiceStarts(items, byOrder);
        return items.Where(i => i.Timeline is not null)
            .Select(i => TimecodeFollow.Song.For(i.Id, i.Timeline!, starts.TryGetValue(i.Id, out var s) ? s : null)).ToList();
    }

    [Fact]
    public void V143ServiceDecodesSavedWithEachSong() =>
        Assert.Null(MxuJson.Deserialize<Service>("""{"id":"s","name":"Sunday","serviceDate":"2026-10-18","items":[]}""").TimecodeByOrder);

    [Fact]
    public void StartTimesRoundTripThroughTheDocument()
    {
        var service = MxuJson.Deserialize<Service>("""{"id":"s","name":"Sunday","serviceDate":"2026-10-18","items":[]}""");
        service.TimecodeByOrder = true;
        Assert.True(MxuJson.Deserialize<Service>(MxuJson.Serialize(service)).TimecodeByOrder);
    }

    [Fact]
    public void SavedWithEachSongUsesEachTimelinesOwnStart()
    {
        List<(string, PresentationTimeline?)> items = [("a", Timeline("ta", 5 * Hour)), ("bare", null), ("b", Timeline("tb", 9 * Hour))];
        Assert.Equal(new Dictionary<string, int> { ["a"] = 5 * Hour, ["b"] = 9 * Hour }, T.ServiceStarts(items, false));
    }

    [Fact]
    public void InServiceOrderGivesEachTimelineSongAnHourAndSkipsTheRest() =>
        Assert.Equal(new Dictionary<string, int> { ["a"] = Hour, ["b"] = 2 * Hour, ["c"] = 3 * Hour }, T.ServiceStarts(Service, true));

    [Fact]
    public void InServiceOrderFollowsReordersAndAddedOrRemovedSongs()
    {
        List<(string, PresentationTimeline?)> moved = [Service[4], .. Service.Take(4)];
        Assert.Equal(new Dictionary<string, int> { ["c"] = Hour, ["a"] = 2 * Hour, ["b"] = 3 * Hour }, T.ServiceStarts(moved, true));
        var removed = Service.Where(i => i.Id != "b").ToList();
        Assert.Equal(new Dictionary<string, int> { ["a"] = Hour, ["c"] = 2 * Hour }, T.ServiceStarts(removed, true));
        var added = Service.Select(i => i.Id == "bare" ? ("bare", Timeline("tbare")) : i).ToList();
        Assert.Equal(new Dictionary<string, int> { ["a"] = Hour, ["b"] = 2 * Hour, ["bare"] = 3 * Hour, ["c"] = 4 * Hour }, T.ServiceStarts(added, true));
    }

    [Fact]
    public void TheSameSongTwiceInAServiceIsTwoSongs()
    {
        List<(string, PresentationTimeline?)> twice = [("a1", Timeline("ta")), ("a2", Timeline("ta"))];
        Assert.Equal(new Dictionary<string, int> { ["a1"] = Hour, ["a2"] = 2 * Hour }, T.ServiceStarts(twice, true));
    }

    [Fact]
    public void SavedStartsAllAtZeroCantTellTheSongsApart()
    {
        var songs = Songs(false);
        Assert.Equal("a", TimecodeFollow.SongContaining(10, TimecodeRate.Fps30, songs)?.Song.ItemId);
        Assert.Null(TimecodeFollow.SongContaining(7200 + 10, TimecodeRate.Fps30, songs));
        Assert.Equal(new HashSet<string> { "a", "b", "c" }, TimecodeFollow.Clashes(songs));
    }

    [Fact]
    public void InServiceOrderTheHourSaysWhichSong()
    {
        var songs = Songs(true);
        Assert.Equal("a", TimecodeFollow.SongContaining(3600 + 10, TimecodeRate.Fps30, songs)?.Song.ItemId);
        var b = TimecodeFollow.SongContaining(7200 + 42, TimecodeRate.Fps30, songs);
        Assert.Equal("b", b?.Song.ItemId);
        Assert.True(Math.Abs((b?.Position ?? 0) - 42) < 1e-9);
        Assert.Equal("c", TimecodeFollow.SongContaining(3 * 3600 + 1, TimecodeRate.Fps30, songs)?.Song.ItemId);
        Assert.Null(TimecodeFollow.SongContaining(10, TimecodeRate.Fps30, songs));
        Assert.Empty(TimecodeFollow.Clashes(songs));
        Assert.Equal("b", new TimecodeFollow().Step(new TimecodeClock.Reading(State.Locked, 7200 + 0.1, Rate: TimecodeRate.Fps30), songs)?.ItemId);
    }

    [Fact]
    public void InServiceOrderOnlyASongOverAnHourOverlaps()
    {
        List<(string, PresentationTimeline?)> longSong = [("a", Timeline("ta", length: 3700)), ("b", Timeline("tb"))];
        Assert.Equal(new HashSet<string> { "a", "b" }, TimecodeFollow.Clashes(Songs(true, longSong)));
        var windows = T.ServiceStarts(longSong, true).Select(p => new T.Window(p.Key, p.Value, 300 * 30)).ToList();
        Assert.Equal(["b"], T.Overlaps(new T.Window("a", Hour, T.Frames(3700.0)), windows).Select(w => w.Id));
    }

    [Fact]
    public void CueRowsAndRecordingUseTheStartInThisService()
    {
        List<TimelinePlayedBlock> blocks = [new("v", ["v1"])];
        var song = Timeline("t");
        song.Cues = [T.SlideCue("s", 1, T.Anchor(0, blocks)!)];
        Assert.Equal(["02:00:01:00"], TimecodeFollow.CueRows(song, blocks, _ => "Verse", 2 * Hour).Select(r => r.Timecode));
        Assert.Equal(["00:00:01:00"], TimecodeFollow.CueRows(song, blocks, _ => "Verse").Select(r => r.Timecode));
        Assert.Equal(new TimecodeRecording.Mode.Timecode(2 * Hour), TimecodeRecording.ModeFor(song, true, true, 2 * Hour));
        Assert.Equal(new TimecodeRecording.Mode.Timecode(0), TimecodeRecording.ModeFor(song, true, true));
    }

    [Fact]
    public void InServiceOrderOnlySongsThatPlayFromTimecodeTakeAnHour()
    {
        var off = Timeline("tb", 7 * Hour);
        off.TimecodeEnabled = false;
        var items = Service.Select(i => i.Id == "b" ? ("b", off) : i).ToList();
        Assert.Equal(new Dictionary<string, int> { ["a"] = Hour, ["b"] = 7 * Hour, ["c"] = 2 * Hour }, T.ServiceStarts(items, true));
        Assert.Equal(3 * Hour, T.ServiceStarts(Service, true)["c"]);
    }

    [Fact]
    public void ASongWithNoSlideCueShowsItsFirstSlideFromTimecode()
    {
        List<TimelinePlayedBlock> blocks = [new("v", ["v1", "v2"])];
        var overlay = Make.Cue("o", "overlays", 2, overlayId: "lt");
        var cues = TimecodeFollow.FollowedCues([overlay], blocks);
        Assert.Equal(["v1", null], cues.Select(c => c.SlideId));
        Assert.Equal([new TimelineEffect.ShowSlide(0, true)], T.Crossing(cues, blocks, -T.Frame, 0.1, new HashSet<string>()));
        Assert.Equal(new TimelineEffect.ShowSlide(0, false), T.Landing(cues, blocks, 30, new HashSet<string>())[0]);

        List<TimelineCue> cued = [T.SlideCue("s", 3, T.Anchor(1, blocks)!)];
        Assert.True(Make.Same(cued, TimecodeFollow.FollowedCues(cued, blocks)));
        Assert.Empty(TimecodeFollow.FollowedCues([], []));
    }
}
