using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Stage;

// TimersController.swift semantics, plus TimerBoard.featured/rollup from TimerBoardTests.swift.
public class TimerControllerTests
{
    private sealed class FakeClock(DateTimeOffset now, TimeZoneInfo? zone = null) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => zone ?? TimeZoneInfo.Utc;
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private static readonly DateTimeOffset Morning = new(2026, 7, 5, 10, 0, 0, TimeSpan.Zero);

    private static (TimerController Controller, FakeClock Clock) Make(DateTimeOffset? now = null)
    {
        var clock = new FakeClock(now ?? Morning);
        return (new TimerController(clock), clock);
    }

    private static TimeZoneInfo Pacific()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
    }

    [Fact]
    public void AddedTimersStartIdle()
    {
        var (timers, _) = Make();
        var id = timers.AddCountdown("Sermon", 1800);
        var sermon = timers.Snapshot(id)!;
        Assert.Equal(TimerMode.Countdown, sermon.Mode);
        Assert.False(sermon.IsRunning);
        Assert.Equal(1800, sermon.Value(Morning.AddHours(1)));
        Assert.Equal(TimerWarning.Defaults, sermon.Warnings);

        Assert.Equal(600, timers.Snapshot(timers.AddCountdown())!.DurationSeconds);
        Assert.Equal(0, timers.Snapshot(timers.AddCountdown("Neg", -5))!.DurationSeconds);
        var up = timers.Snapshot(timers.AddCountUp())!;
        Assert.Equal(("Count Up", TimerMode.CountUp, 0.0), (up.Name, up.Mode, up.DurationSeconds));
        Assert.Equal(4, timers.Timers.Count);
    }

    [Fact]
    public void PauseBanksElapsedAndResumeContinues()
    {
        var (timers, clock) = Make();
        var id = timers.AddCountdown("Sermon", 1800);
        timers.Start(id);
        Assert.Equal(Morning, timers.Snapshot(id)!.RunningSince);
        clock.Advance(40);
        timers.Start(id); // already running: keeps its anchor
        Assert.Equal(Morning, timers.Snapshot(id)!.RunningSince);
        timers.Pause(id);
        var paused = timers.Snapshot(id)!;
        Assert.Equal((40.0, false, (DateTimeOffset?)null), (paused.Banked, paused.IsRunning, paused.RunningSince));

        clock.Advance(100);
        Assert.Equal(1760, timers.Snapshot(id)!.Value(clock.Now)); // paused timers hold still
        timers.Pause(id);
        Assert.Equal(40, timers.Snapshot(id)!.Banked);
        timers.Start(id);
        clock.Advance(10);
        Assert.Equal(1750, timers.Snapshot(id)!.Value(clock.Now));
    }

    [Fact]
    public void ToggleFlipsTransport()
    {
        var (timers, clock) = Make();
        var id = timers.AddCountUp("Walk-in");
        timers.Toggle(id);
        Assert.True(timers.Snapshot(id)!.IsRunning);
        clock.Advance(5);
        timers.Toggle(id);
        Assert.False(timers.Snapshot(id)!.IsRunning);
        Assert.Equal(5, timers.Snapshot(id)!.Banked);
        timers.Toggle("missing");
    }

    [Fact]
    public void ResetStopsAndClears()
    {
        var (timers, clock) = Make();
        var id = timers.AddCountdown("Sermon", 300);
        timers.Start(id);
        clock.Advance(100);
        timers.Reset(id);
        var reset = timers.Snapshot(id)!;
        Assert.False(reset.IsRunning);
        Assert.Null(reset.RunningSince);
        Assert.Equal(0, reset.Banked);
        Assert.Equal(300, reset.Value(clock.Now));
    }

    [Fact]
    public void CountdownToTimeArmsForTheNextWallTime()
    {
        var (timers, clock) = Make();
        var id = timers.AddCountdownToTime(19, 30);
        var doors = timers.Snapshot(id)!;
        Assert.Equal("Until 7:30 PM", doors.Name);
        Assert.Equal(new DateTimeOffset(2026, 7, 5, 19, 30, 0, TimeSpan.Zero), doors.TargetTime);
        Assert.Equal(Morning, doors.ArmedAt);
        Assert.True(doors.IsLive);
        Assert.Equal(9.5 * 3600, doors.Value(Morning));

        clock.Advance(12 * 3600);
        Assert.Equal(-2.5 * 3600, timers.Snapshot(id)!.Value(clock.Now));
        timers.Reset(id);
        var rearmed = timers.Snapshot(id)!;
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 19, 30, 0, TimeSpan.Zero), rearmed.TargetTime); // the next occurrence of its wall time
        Assert.Equal(clock.Now, rearmed.ArmedAt);

        var late = timers.AddCountdownToTime(9, 0, "Tomorrow");
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 9, 0, 0, TimeSpan.Zero), timers.Snapshot(late)!.TargetTime);
        timers.SetTarget(late, 23, 15);
        Assert.Equal(new DateTimeOffset(2026, 7, 5, 23, 15, 0, TimeSpan.Zero), timers.Snapshot(late)!.TargetTime);
    }

    [Fact]
    public void WallTimesFollowTheClockTimeZone()
    {
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");
        var clock = new FakeClock(Morning, plusTwo); // 12:00 local
        var timers = new TimerController(clock);
        var id = timers.AddCountdownToTime(13, 0);
        Assert.Equal(new DateTimeOffset(2026, 7, 5, 11, 0, 0, TimeSpan.Zero), timers.Snapshot(id)!.TargetTime);
        Assert.Equal(13, timers.Export()[0].Hour);
    }

    [Fact]
    public void NextOccurrenceIsStrictlyAfter()
    {
        var at = new DateTimeOffset(2026, 7, 5, 19, 30, 0, TimeSpan.Zero);
        Assert.Equal(at.AddDays(1), TimerController.NextOccurrence(19, 30, at, TimeZoneInfo.Utc));
        Assert.Equal(at, TimerController.NextOccurrence(19, 30, at.AddTicks(-1), TimeZoneInfo.Utc));
        Assert.Equal(at.AddDays(1), TimerController.NextOccurrence(19, 30, at.AddSeconds(15), TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextOccurrenceCrossesDaylightSavingChanges()
    {
        var pacific = Pacific();
        // 2:30 does not exist on 8 March 2026: the first time after the gap is 3:00 PDT.
        var springNight = new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.FromHours(-8));
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 10, 0, 0, TimeSpan.Zero), TimerController.NextOccurrence(2, 30, springNight, pacific));
        // 1:30 happens twice on 1 November 2026: first in PDT, then in PST.
        var fallNight = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-7));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero), TimerController.NextOccurrence(1, 30, fallNight, pacific));
        Assert.Equal(
            new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero),
            TimerController.NextOccurrence(1, 30, new DateTimeOffset(2026, 11, 1, 8, 45, 0, TimeSpan.Zero), pacific));
    }

    [Fact]
    public void ConfigureModeChangeRestartsTheCount()
    {
        var (timers, clock) = Make();
        var id = timers.AddCountdown("Sermon", 300);
        timers.Start(id);
        clock.Advance(50);
        timers.Pause(id);
        timers.Start(id);
        clock.Advance(10);
        timers.Configure(id, mode: TimerMode.CountUp);
        var up = timers.Snapshot(id)!;
        Assert.Equal(TimerMode.CountUp, up.Mode);
        Assert.Equal(0, up.Banked);
        Assert.True(up.IsRunning);
        Assert.Equal(clock.Now, up.RunningSince);
        Assert.Null(up.ArmedAt);
    }

    [Fact]
    public void ConfigureDurationRestartsCountdownsButNotCountUps()
    {
        var (timers, clock) = Make();
        var down = timers.AddCountdown("Down", 300);
        var up = timers.AddCountUp("Up");
        foreach (var id in new[] { down, up })
        {
            timers.Start(id);
            clock.Advance(40);
            timers.Pause(id);
        }
        timers.Configure(down, durationSeconds: 600);
        timers.Configure(up, durationSeconds: 600);
        Assert.Equal((600.0, 0.0), (timers.Snapshot(down)!.DurationSeconds, timers.Snapshot(down)!.Banked));
        Assert.Equal((600.0, 40.0), (timers.Snapshot(up)!.DurationSeconds, timers.Snapshot(up)!.Banked)); // a new limit keeps the count
        Assert.Null(timers.Snapshot(down)!.RunningSince);
    }

    [Fact]
    public void ConfigureCountdownToTimeSetsTargetAndArms()
    {
        var (timers, clock) = Make();
        var id = timers.AddCountdown("Doors", 300);
        timers.Configure(id, mode: TimerMode.CountdownToTime, hour: 19, minute: 30);
        var doors = timers.Snapshot(id)!;
        Assert.Equal(new DateTimeOffset(2026, 7, 5, 19, 30, 0, TimeSpan.Zero), doors.TargetTime);
        Assert.Equal(Morning, doors.ArmedAt);

        clock.Advance(60);
        timers.Configure(id, durationSeconds: 5);
        Assert.Equal(Morning, timers.Snapshot(id)!.ArmedAt); // no new target, no re-arm
        timers.Configure(id, hour: 20);
        Assert.Equal(new DateTimeOffset(2026, 7, 5, 20, 0, 0, TimeSpan.Zero), timers.Snapshot(id)!.TargetTime);
        Assert.Equal(clock.Now, timers.Snapshot(id)!.ArmedAt);

        timers.Configure(id, mode: TimerMode.Countdown, hour: 21);
        Assert.Equal(new DateTimeOffset(2026, 7, 5, 20, 0, 0, TimeSpan.Zero), timers.Snapshot(id)!.TargetTime); // hours only steer countdowns-to-time
    }

    [Fact]
    public void SetDurationRestartsARunningTimerFromNow()
    {
        var (timers, clock) = Make();
        var id = timers.AddCountdown("Sermon", 300);
        timers.Start(id);
        clock.Advance(100);
        timers.SetDuration(id, 900);
        var restarted = timers.Snapshot(id)!;
        Assert.Equal((900.0, 0.0, true), (restarted.DurationSeconds, restarted.Banked, restarted.IsRunning));
        Assert.Equal(clock.Now, restarted.RunningSince);
        Assert.Equal(900, restarted.Value(clock.Now));
    }

    [Fact]
    public void LimitsWarningsAndNames()
    {
        var (timers, _) = Make();
        var id = timers.AddCountUp("Up");
        timers.SetLimit(id, -10);
        Assert.Equal(0, timers.Snapshot(id)!.DurationSeconds);
        timers.SetLimit(id, 120);
        Assert.Equal(120, timers.Snapshot(id)!.DurationSeconds);

        timers.SetWarnings(id, [new TimerWarning(0, "#FF0000FF"), new TimerWarning(60, "#00FF00FF"), new TimerWarning(10, "#0000FFFF")]);
        Assert.Equal([60.0, 10.0, 0.0], timers.Snapshot(id)!.Warnings.Select(w => w.RemainingSeconds));

        timers.Rename(id, " \tSermon  ");
        Assert.Equal("Sermon", timers.Snapshot(id)!.Name);
        timers.Rename(id, "   ");
        Assert.Equal("Sermon", timers.Snapshot(id)!.Name);
    }

    [Fact]
    public void RemoveAndImport()
    {
        var (timers, _) = Make();
        var id = timers.AddCountdown("A", 60);
        timers.ImportTimer("show-1", "Pre-show", TimerMode.Countdown, durationSeconds: 120);
        timers.ImportTimer("show-1", "Pre-show", TimerMode.CountdownToTime, targetTime: Morning.AddHours(1));
        Assert.Equal(2, timers.Timers.Count);
        var imported = timers.Snapshot("show-1")!;
        Assert.Equal(TimerMode.CountdownToTime, imported.Mode);
        Assert.Equal(Morning, imported.ArmedAt);
        timers.Remove(id);
        Assert.Equal(["show-1"], timers.Timers.Select(t => t.Id));
    }

    [Fact]
    public void ServiceTrackingTimersAreReadOnly()
    {
        var (timers, _) = Make();
        var user = timers.AddCountdown("Mine", 60);
        var system = new TimerSnapshot("Item Remaining", TimerMode.Countdown, id: "service_tracking.item.remaining", durationSeconds: 300);
        timers.SetSystemTimers([system]);
        Assert.Equal([user, system.Id], timers.Timers.Select(t => t.Id)); // user timers first

        timers.Start(system.Id);
        timers.Rename(system.Id, "Renamed");
        timers.Remove(system.Id);
        timers.ImportTimer(system.Id, "Imported", TimerMode.CountUp);
        Assert.Equal(system, timers.Snapshot(system.Id));

        var changes = 0;
        timers.Changed += () => changes++;
        timers.SetSystemTimers([system]);
        Assert.Equal(0, changes); // nothing new, nothing published
        timers.SetSystemTimers([]);
        Assert.Equal([user], timers.Timers.Select(t => t.Id));
        Assert.Equal(1, changes);
        Assert.Single(timers.Export());
    }

    [Fact]
    public void ChangedFiresAfterEveryEdit()
    {
        var (timers, _) = Make();
        var changes = 0;
        timers.Changed += () => changes++;
        var id = timers.AddCountdown("A", 60);
        timers.Start(id);
        timers.Pause(id);
        timers.Reset(id);
        timers.Configure(id, durationSeconds: 30);
        timers.Rename(id, "B");
        timers.Remove(id);
        Assert.Equal(7, changes);
        timers.Start("missing");
        timers.Remove("missing");
        Assert.Equal(7, changes);
    }

    [Fact]
    public void ExportAndRestoreKeepSetupNotTransport()
    {
        var (timers, clock) = Make();
        var down = timers.AddCountdown("Sermon", 1800);
        var doors = timers.AddCountdownToTime(19, 30, "Doors");
        var up = timers.AddCountUp("Walk-in", 600);
        timers.SetWarnings(doors, [new TimerWarning(120, "#00FF00FF")]);
        timers.Start(down);
        clock.Advance(100);

        var saved = timers.Export();
        Assert.Equal(["countdown", "countdownToTime", "countUp"], saved.Select(s => s.Mode));
        Assert.Null(saved[0].Warnings); // defaults are left out
        Assert.Equal((19, 30), (saved[1].Hour, saved[1].Minute));
        Assert.Null(saved[0].Hour);

        var json = MxuJson.Serialize(saved);
        Assert.Contains("\"durationSeconds\"", json);
        var reloaded = MxuJson.Deserialize<List<PersistedTimer>>(json);
        reloaded.Add(new PersistedTimer("legacy", "Old", "elapsed", 0));
        reloaded.Add(new PersistedTimer("odd", "Odd", "sideways", 0));

        var nextDay = new FakeClock(new DateTimeOffset(2026, 7, 6, 20, 0, 0, TimeSpan.Zero));
        var restored = new TimerController(nextDay);
        restored.Restore(reloaded);
        Assert.Equal([down, doors, up, "legacy"], restored.Timers.Select(t => t.Id));
        Assert.All(restored.Timers, t => Assert.False(t.IsRunning));
        Assert.Equal(0, restored.Snapshot(down)!.Banked);
        Assert.Equal(1800, restored.Snapshot(down)!.DurationSeconds);
        Assert.Equal(new DateTimeOffset(2026, 7, 7, 19, 30, 0, TimeSpan.Zero), restored.Snapshot(doors)!.TargetTime);
        Assert.Equal(nextDay.Now, restored.Snapshot(doors)!.ArmedAt);
        Assert.Equal([new TimerWarning(120, "#00FF00FF")], restored.Snapshot(doors)!.Warnings);
        Assert.Equal(TimerWarning.Defaults, restored.Snapshot(up)!.Warnings);
        Assert.Equal(TimerMode.CountUp, restored.Snapshot("legacy")!.Mode);
    }

    [Fact]
    public void WallLabelsReadTwelveHour()
    {
        Assert.Equal("12:05 AM", TimerController.WallLabel(0, 5));
        Assert.Equal("12:00 PM", TimerController.WallLabel(12, 0));
        Assert.Equal("7:30 PM", TimerController.WallLabel(19, 30));
        Assert.Equal("9:00 AM", TimerController.WallLabel(9, 0));
    }

    [Fact]
    public void FeaturedPicksTheLiveTimerNearestItsEnd()
    {
        var now = Morning;
        var far = new TimerSnapshot("Far", TimerMode.Countdown, id: "far", isRunning: true, runningSince: now, durationSeconds: 600);
        var near = new TimerSnapshot("Near", TimerMode.Countdown, id: "near", isRunning: true, runningSince: now, durationSeconds: 60);
        var paused = new TimerSnapshot("Paused", TimerMode.Countdown, id: "paused", durationSeconds: 5);
        var unlimited = new TimerSnapshot("Up", TimerMode.CountUp, id: "up", isRunning: true, runningSince: now);
        Assert.Equal("near", TimerController.Featured([far, near, paused, unlimited], now)?.Id); // paused never features
        Assert.Equal("far", TimerController.Featured([far, unlimited], now)?.Id); // an unlimited Count Up ranks behind any countdown
        Assert.Null(TimerController.Featured([paused], now));
    }

    [Fact]
    public void RollupUrgencyTakesTheWorstMember()
    {
        var now = Morning;
        var calm = new TimerSnapshot("Calm", TimerMode.Countdown, id: "calm", isRunning: true, runningSince: now, durationSeconds: 600);
        var warning = new TimerSnapshot("Warn", TimerMode.Countdown, id: "warn", isRunning: true, runningSince: now, durationSeconds: 20);
        var overrun = new TimerSnapshot("Over", TimerMode.Countdown, id: "over", isRunning: true, runningSince: now.AddSeconds(-100), durationSeconds: 10);
        Assert.Equal(TimerUrgency.Normal, TimerController.RollupUrgency([calm], now));
        Assert.Equal(TimerUrgency.Warning, TimerController.RollupUrgency([calm, warning], now));
        Assert.Equal(TimerUrgency.Overrun, TimerController.RollupUrgency([calm, warning, overrun], now));

        var idle = new TimerSnapshot("Idle", TimerMode.Countdown, id: "idle");
        Assert.True(idle.IsIdle);
        Assert.Null(idle.ActiveWarning(now));
        Assert.Equal(TimerUrgency.Normal, TimerController.RollupUrgency([calm, idle], now));
        Assert.Null(TimerController.RollupWarning([calm, idle], now));
        Assert.False(calm.IsIdle);
        Assert.Equal(TimerWarning.RedHex, TimerController.RollupWarning([calm, warning, overrun], now)?.ColorHex);
    }
}
