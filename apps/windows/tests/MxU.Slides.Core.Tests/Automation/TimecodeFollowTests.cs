using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Stage;
using Command = MxU.Slides.Core.Automation.TimecodeFollow.Command;
using E = MxU.Slides.Core.Automation.TimelineEffect;
using State = MxU.Slides.Core.Automation.TimecodeClock.State;
using T = MxU.Slides.Core.Automation.PresentationTimelines;

namespace MxU.Slides.Core.Tests.Automation;

// Ported from PresenterCoreTests/TimecodeFollowTests.swift (TimecodeClockTests,
// TimecodeFollowTests, TimecodeRulesTableTests, TimecodeSignalCheckTests). The
// BackupSettings check is not ported: Windows has no settings backup yet.
public class TimecodeClockTests
{
    private const double Fps = 30.0;

    private static double Feed(TimecodeClock clock, IEnumerable<int> numbers, double start, bool reverse = false)
    {
        var time = start;
        foreach (var n in numbers)
        {
            time += 1 / Fps;
            clock.Receive(new LtcFrame(LtcSignal.Label(n, 30), Reverse: reverse), TimecodeRate.Fps30, time);
        }
        return time;
    }

    [Fact]
    public void LocksAfterThreeGoodFrames()
    {
        var clock = new TimecodeClock();
        Assert.Equal(State.NoInput, clock.ReadingAt(0).State);
        var time = Feed(clock, [100, 101], 0);
        Assert.Equal(State.Locking, clock.ReadingAt(time).State);
        Assert.False(clock.ReadingAt(time).Live);
        time = Feed(clock, [102], time);
        var reading = clock.ReadingAt(time);
        Assert.Equal(State.Locked, reading.State);
        Assert.True(reading.Live);
        Assert.True(Math.Abs((reading.Position ?? 0) - 103 / Fps) < 1e-9);
        Assert.True(Math.Abs((clock.ReadingAt(time + 0.02).Position ?? 0) - (103 / Fps + 0.02)) < 1e-9);
    }

    [Fact]
    public void NoiseNeverLocks()
    {
        var clock = new TimecodeClock();
        var time = 0.0;
        foreach (var n in new[] { 5, 900, 31, 4000, 77 }) time = Feed(clock, [n], time);
        Assert.Equal(State.Locking, clock.ReadingAt(time).State);
    }

    [Fact]
    public void DropoutFreewheelsThenStopsAndHolds()
    {
        var clock = new TimecodeClock();
        var time = Feed(clock, Enumerable.Range(0, 30), 0);
        var freewheel = clock.ReadingAt(time + 0.3);
        Assert.Equal(State.Freewheel, freewheel.State);
        Assert.False(freewheel.Live);
        Assert.True(Math.Abs((freewheel.Position ?? 0) - 1.3) < 1e-9);
        var stopped = clock.ReadingAt(time + 2);
        Assert.Equal(State.Stopped, stopped.State);
        Assert.Equal(1, stopped.Position);
    }

    [Fact]
    public void SignalBackWithinTheFreewheelIsStillLocked()
    {
        var clock = new TimecodeClock();
        var time = Feed(clock, Enumerable.Range(0, 30), 0);
        time = Feed(clock, Enumerable.Range(39, 2), time + 0.3);
        Assert.Equal(State.Locked, clock.ReadingAt(time).State);
    }

    [Fact]
    public void AGapFreeJumpNeedsTwoFrames()
    {
        var clock = new TimecodeClock();
        var time = Feed(clock, Enumerable.Range(0, 30), 0);
        time = Feed(clock, [900], time);
        Assert.True(Math.Abs((clock.ReadingAt(time).Position ?? 0) - 31 / Fps) < 1e-9);
        time = Feed(clock, [600, 601], time);
        var reading = clock.ReadingAt(time);
        Assert.Equal(State.Locked, reading.State);
        Assert.True(Math.Abs((reading.Position ?? 0) - 602 / Fps) < 1e-9);
    }

    [Fact]
    public void AfterStoppingItLocksAfresh()
    {
        var clock = new TimecodeClock();
        var time = Feed(clock, Enumerable.Range(0, 30), 0);
        time = Feed(clock, [300], time + 5);
        Assert.Equal(State.Locking, clock.ReadingAt(time).State);
        time = Feed(clock, [301, 302], time);
        Assert.Equal(State.Locked, clock.ReadingAt(time).State);
    }

    [Fact]
    public void ReverseIsReadAndNeverLive()
    {
        var clock = new TimecodeClock();
        var time = Feed(clock, Enumerable.Range(200, 30).Reverse(), 0, reverse: true);
        var reading = clock.ReadingAt(time);
        Assert.Equal(State.Locked, reading.State);
        Assert.True(reading.Reverse);
        Assert.False(reading.Live);
        Assert.True((clock.ReadingAt(time + 0.1).Position ?? 0) < (reading.Position ?? 0));
    }
}

public class TimecodeFollowTests
{
    /// <summary>The Swift tests' ≈: commands equal after rounding to millionths.</summary>
    internal static void Near(Command expected, Command? actual)
    {
        static Command Rounded(Command c)
        {
            static double R(double x) => Math.Round(x * 1e6, MidpointRounding.AwayFromZero) / 1e6;
            return c switch
            {
                Command.Play p => new Command.Play(p.ItemId, R(p.From), R(p.To)),
                Command.Land l => new Command.Land(l.ItemId, R(l.At)),
                _ => c,
            };
        }
        Assert.NotNull(actual);
        Assert.Equal(Rounded(expected), Rounded(actual));
    }

    private static readonly List<TimecodeFollow.Song> Songs =
    [
        new("a", 108_000, 120),
        new("b", 216_000, 120),
        new("c", 324_000, 120),
    ];

    private static TimecodeClock.Reading Live(double seconds, TimecodeRate rate = TimecodeRate.Fps30) => new(State.Locked, seconds, Rate: rate);

    [Fact]
    public void ResolverPicksTheWindowHoldingTheTimecode()
    {
        Assert.Equal("a", TimecodeFollow.SongContaining(3600 + 10, TimecodeRate.Fps30, Songs)?.Song.ItemId);
        Assert.Equal("b", TimecodeFollow.SongContaining(7200 + 119.9, TimecodeRate.Fps30, Songs)?.Song.ItemId);
        Assert.Null(TimecodeFollow.SongContaining(3600 + 120, TimecodeRate.Fps30, Songs));
        Assert.Null(TimecodeFollow.SongContaining(0, TimecodeRate.Fps30, Songs));

        List<TimecodeFollow.Song> overlapping = [Songs[1], new("x", 216_000 + 300, 60)];
        Assert.Equal("b", TimecodeFollow.SongContaining(7200 + 20, TimecodeRate.Fps30, overlapping)?.Song.ItemId);

        var start = TimecodeRate.Fps2997.Seconds(new TimecodeLabel(2, 0, 0, 0));
        var position = TimecodeFollow.SongContaining(start + 60, TimecodeRate.Fps2997, Songs)?.Position;
        Assert.True(Math.Abs((position ?? 0) - 60) < 1e-9);
    }

    [Fact]
    public void AnOpenEndedTimelineKeepsATailOtherSongsBeat()
    {
        var timeline = Make.Timeline("t", true, 108_000, cues: [Make.Cue("s", "slide", 30, slideId: "x")]);
        var song = TimecodeFollow.Song.For("a", timeline);
        Assert.Equal(30, song.Length);
        Assert.Equal(1, song.Tail);
        Assert.Equal("a", TimecodeFollow.SongContaining(3600 + 30.02, TimecodeRate.Fps30, [song])?.Song.ItemId);
        var next = new TimecodeFollow.Song("b", 108_000 + 900, 60);
        Assert.Equal("b", TimecodeFollow.SongContaining(3600 + 30.02, TimecodeRate.Fps30, [song, next])?.Song.ItemId);
        timeline.Length = 30;
        Assert.Equal(0, TimecodeFollow.Song.For("a", timeline).Tail);
    }

    [Fact]
    public void ClashesFlagSongsSharingAStart()
    {
        List<TimecodeFollow.Song> defaultStart = [new("a", 0, 200), new("b", 0, 180), new("c", 108_000, 240)];
        Assert.Equal(new HashSet<string> { "a", "b" }, TimecodeFollow.Clashes(defaultStart));
        Assert.Empty(TimecodeFollow.Clashes(defaultStart.TakeLast(2).ToList()));
    }

    [Fact]
    public void CueRowsReadAsTimecode()
    {
        List<TimelinePlayedBlock> blocks = [new("v", ["v1", "v2"]), new("c", ["c1"])];
        var timeline = Make.Timeline("t", true, 108_000, cues:
        [
            Make.Cue("2", "slide", 12.5, sectionId: "c", sectionOccurrence: 0, slideId: "c1"),
            Make.Cue("1", "slide", 4, sectionId: "v", sectionOccurrence: 0, slideId: "v2"),
            Make.Cue("x", "actions", 6),
            Make.Cue("o", "slide", 8, sectionId: "gone", slideId: "z"),
        ]);
        var rows = TimecodeFollow.CueRows(timeline, blocks, id => id == "v" ? "Verse" : "Chorus");
        Assert.Equal(["01:00:04:00", "01:00:12:15"], rows.Select(r => r.Timecode));
        Assert.Equal(["Verse · 2", "Chorus · 1"], rows.Select(r => r.Label));
    }

    [Fact]
    public void StationSettingsUseTheMacsKeys() =>
        Assert.Equal(["timecode.deviceUID", "timecode.channel", "timecode.offsetFrames", "timecode.follow", "timecode.playsAudio", "timecode.source", "timecode.midiSourceUID"],
            TimecodeStation.Keys);

    [Fact]
    public void SongStartPlaysFromTheTopWithActions() =>
        Near(new Command.Play("a", -T.Frame, 0.1), new TimecodeFollow().Step(Live(3600.1), Songs));

    [Fact]
    public void NormalPlayCrossesStepByStep()
    {
        var follow = new TimecodeFollow();
        follow.Step(Live(3600.1), Songs);
        var play = Assert.IsType<Command.Play>(follow.Step(Live(3600.1 + 1.0 / 30), Songs));
        Assert.Equal("a", play.ItemId);
        Assert.True(Math.Abs(play.From - 0.1) < 1e-9);
        Assert.True(Math.Abs(play.To - (0.1 + 1.0 / 30)) < 1e-9);
    }

    [Fact]
    public void MidSongStartLandsWithoutActions() => Near(new Command.Land("a", 45), new TimecodeFollow().Step(Live(3600 + 45), Songs));

    [Fact]
    public void JumpsBothWaysLand()
    {
        var follow = new TimecodeFollow();
        follow.Step(Live(3600 + 30), Songs);
        Near(new Command.Land("a", 50), follow.Step(Live(3600 + 50), Songs));
        Near(new Command.Land("a", 19), follow.Step(Live(3600 + 19), Songs));
        Assert.Equal("a", follow.Step(Live(3600 + 0.05), Songs)?.ItemId);
        Assert.IsNotType<Command.Land>(follow.Step(Live(3600 + 0.05), Songs));
    }

    [Fact]
    public void PlaybacksResumeStepBackIsIgnored()
    {
        var follow = new TimecodeFollow();
        follow.Step(Live(3600 + 30), Songs);
        Assert.Null(follow.Step(Live(3600 + 30 - 2.0 / 30), Songs));
        var play = Assert.IsType<Command.Play>(follow.Step(Live(3600 + 30 + 1.0 / 30), Songs));
        Assert.Equal(30, play.From);
        Near(new Command.Land("a", 30 - 3.0 / 30), follow.Step(Live(3600 + 30 - 3.0 / 30), Songs));
    }

    [Fact]
    public void SilenceFreewheelAndRewindHold()
    {
        var follow = new TimecodeFollow();
        follow.Step(Live(3600 + 30), Songs);
        foreach (var state in new[] { State.Freewheel, State.Stopped, State.Locking, State.NoInput })
            Assert.Null(follow.Step(new TimecodeClock.Reading(state, 3600 + 31, Rate: TimecodeRate.Fps30), Songs));
        Assert.Null(follow.Step(new TimecodeClock.Reading(State.Locked, 3600 + 20, Rate: TimecodeRate.Fps30, Reverse: true), Songs));
        Near(new Command.Land("a", 20), follow.Step(Live(3600 + 20), Songs));
    }

    [Fact]
    public void ADropoutCatchesUpOnReturn()
    {
        var follow = new TimecodeFollow();
        follow.Step(Live(3600 + 30), Songs);
        Near(new Command.Play("a", 30, 30.4), follow.Step(Live(3600 + 30.4), Songs));
    }

    [Fact]
    public void OutsideEveryWindowHoldsThenTheNextSongEnters()
    {
        var follow = new TimecodeFollow();
        follow.Step(Live(3600 + 100), Songs);
        Assert.Null(follow.Step(Live(3600 + 130), Songs));
        Near(new Command.Play("b", -T.Frame, 0.2), follow.Step(Live(7200 + 0.2), Songs));
        Near(new Command.Land("c", 61), follow.Step(Live(10800 + 61), Songs));
    }

    [Fact]
    public void NoSongsNoCommands() => Assert.Null(new TimecodeFollow().Step(Live(3600 + 10), []));
}

public class TimecodeRulesTableTests
{
    private static readonly List<TimelinePlayedBlock> Blocks = [new("v", ["v1", "v2"]), new("c", ["c1"])];

    private static List<Model.TimelineCue> Cues() =>
    [
        Make.Cue("s0", "slide", 0, sectionId: "v", sectionOccurrence: 0, slideId: "v1"),
        Make.Cue("s1", "slide", 10, sectionId: "v", sectionOccurrence: 0, slideId: "v2"),
        Make.Cue("s2", "slide", 20, sectionId: "c", sectionOccurrence: 0, slideId: "c1"),
        Make.Cue("bg", "loopingVideos", 0, mediaId: "loop"),
        Make.Cue("ov", "overlays", 5, 10, overlayId: "lower"),
        Make.Cue("act", "actions", 12),
        Make.Cue("track", "audio", 0, 30, "stems"),
    ];

    private static List<TimelineEffect> Effects(Command command, IReadOnlySet<string>? live = null)
    {
        var up = live ?? new HashSet<string>();
        var effects = command switch
        {
            Command.Play p => T.Crossing(Cues(), Blocks, p.From, p.To, up),
            Command.Land l => T.Landing(Cues(), Blocks, l.At, up),
            _ => [],
        };
        return TimecodeFollow.UnderTimecode(effects);
    }

    [Fact]
    public void PlayingThroughFiresEverythingInTime()
    {
        List<TimecodeFollow.Song> songs = [new("a", 108_000, 30)];
        var follow = new TimecodeFollow();
        var fired = new List<TimelineEffect>();
        var live = new HashSet<string>();
        for (var frame = 3; frame <= 16 * 30; frame++)
        {
            if (follow.Step(new TimecodeClock.Reading(State.Locked, 3600 + frame / 30.0, Rate: TimecodeRate.Fps30), songs) is not { } command) continue;
            var step = Effects(command, live);
            foreach (var fire in step.OfType<E.Fire>()) live.Add(fire.Id);
            fired.AddRange(step);
        }
        Assert.Equal(
        [
            new E.Fire(TimelineLane.LoopingVideos, "loop"),
            new E.ShowSlide(0, true),
            new E.Fire(TimelineLane.Overlays, "lower"),
            new E.ShowSlide(1, true),
            new E.RunActions("act"),
            new E.Clear(TimelineLane.Overlays, "lower"),
        ], fired);
    }

    [Fact]
    public void AJumpShowsTheSlideWithoutActionsAndRunsNoActions() =>
        Assert.Equal([new E.ShowSlide(1, false), new E.Fire(TimelineLane.Overlays, "lower")], Effects(new Command.Land("a", 13), new HashSet<string> { "loop" }));

    [Fact]
    public void TheBackingTrackNeverChasesTimecode()
    {
        var raw = T.Landing(Cues(), Blocks, 13, new HashSet<string>());
        Assert.Contains(raw, e => e is E.PlayAudio);
        var followed = TimecodeFollow.UnderTimecode([.. raw, new E.Clear(TimelineLane.Audio, "stems")]);
        Assert.Equal(raw.Where(e => e is not E.PlayAudio), followed);
        Assert.Contains(new E.ShowSlide(1, false), followed);
    }

    [Fact]
    public void TimecodeCanPlayTheAudioLane()
    {
        var raw = T.Landing(Cues(), Blocks, 13, new HashSet<string>());
        Assert.Equal(raw, TimecodeFollow.UnderTimecode(raw, playsAudio: true));
        var track = Make.Cue("t", TimelineLane.Audio.Raw(), 2, 100, "track");
        Assert.Equal([new E.PlayAudio("track", 10, "t")], TimecodeFollow.AudioSync([track], 12, new Dictionary<string, double>()));
        Assert.Empty(TimecodeFollow.AudioSync([track], 12, new Dictionary<string, double> { ["track"] = 9.7 }));
        Assert.Equal([new E.PlayAudio("track", 10, "t")], TimecodeFollow.AudioSync([track], 12, new Dictionary<string, double> { ["track"] = 8 }));
        Assert.Equal([new E.Clear(TimelineLane.Audio, "track")], TimecodeFollow.AudioSync([track], 150, new Dictionary<string, double> { ["track"] = 140 }));
    }
}

public class TimecodeSignalCheckTests
{
    private static List<bool> Run(IEnumerable<(float Level, bool Decoding, double At)> ticks)
    {
        var check = new TimecodeSignalCheck();
        return ticks.Select(t => check.Update(t.Level, t.Decoding, t.At)).ToList();
    }

    [Fact]
    public void SoundWithNoTimecodeIsAProblemOnceItLasts() =>
        Assert.Equal([false, false, true, false, false, true],
            Run([(0.2f, false, 10), (0.2f, false, 11.4), (0.2f, false, 11.5), (0.2f, true, 12), (0.2f, false, 13), (0.2f, false, 14.6)]));

    [Fact]
    public void SilenceIsNeverAProblem()
    {
        Assert.All(Run(Enumerable.Range(0, 21).Select(i => (0.001f, false, i * 0.5))), Assert.False);
        Assert.True(TimecodeSignalCheck.Decoding(State.Locked) && TimecodeSignalCheck.Decoding(State.Freewheel));
        Assert.False(TimecodeSignalCheck.Decoding(State.Stopped) || TimecodeSignalCheck.Decoding(State.NoInput));
    }
}
