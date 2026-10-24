using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Stage;

// The Timers panel's rules (TimersModule.swift, TimerInlineEditor, WarningEditorRow) and the
// board half of TimersController.swift as TimerController carries it.
public class TimerPanelTests
{
    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private static readonly DateTimeOffset Morning = new(2026, 7, 5, 10, 0, 0, TimeSpan.Zero);

    private static (TimerController Controller, FakeClock Clock) Make()
    {
        var clock = new FakeClock(Morning);
        return (new TimerController(clock), clock);
    }

    [Fact]
    public void QuickLengthsAndTheDurationMenu()
    {
        Assert.Equal([5, 10, 15, 20, 30, 45, 60], TimerPanel.QuickLengths);
        Assert.Equal(TimerPanel.QuickLengths, TimerPanel.DurationChoices(30)); // a quick length is already there
        Assert.Equal(TimerPanel.QuickLengths, TimerPanel.DurationChoices(0));
        Assert.Equal([5, 10, 15, 20, 25, 30, 45, 60], TimerPanel.DurationChoices(25)); // its own length joins, in order
        Assert.Equal([5, 10, 15, 20, 30, 45, 60, 90], TimerPanel.DurationChoices(90));
    }

    [Fact]
    public void CountdownToTimeOffersTheNextHalfHour()
    {
        Assert.Equal(new WallClockFields(10, 30, false), TimerPanel.CountdownToTimeDefault(new(2026, 7, 5, 10, 12, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
        Assert.Equal(new WallClockFields(11, 0, false), TimerPanel.CountdownToTimeDefault(new(2026, 7, 5, 10, 45, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
        Assert.Equal(new WallClockFields(12, 0, true), TimerPanel.CountdownToTimeDefault(new(2026, 7, 5, 11, 30, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
        Assert.Equal(new WallClockFields(12, 0, false), TimerPanel.CountdownToTimeDefault(new(2026, 7, 5, 23, 50, 0, TimeSpan.Zero), TimeZoneInfo.Utc)); // midnight wraps
    }

    [Fact]
    public void WallClockFieldsReadTwelveHour()
    {
        Assert.Equal(19, new WallClockFields(7, 30, true).Hour24);
        Assert.Equal(0, new WallClockFields(12, 0, false).Hour24);
        Assert.Equal(12, new WallClockFields(12, 0, true).Hour24);
        Assert.Equal(new WallClockFields(12, 59, false), new WallClockFields(40, 99, false).Clamped); // the boxes clamp hour 1–12, minute 0–59
        Assert.Equal(new WallClockFields(1, 0, false), new WallClockFields(0, -5, false).Clamped);
        Assert.Equal(new WallClockFields(7, 5, true), WallClockFields.From(19, 5));
    }

    [Fact]
    public void LengthsSplitAndJoinAsTheBoxesShowThem()
    {
        Assert.Equal((1, 2, 3), TimerPanel.Split(3723));
        Assert.Equal((0, 10, 0), TimerPanel.Split(600.9));
        Assert.Equal(3723, TimerPanel.Join(1, 2, 3));
        Assert.Equal(99 * 3600 + 59 * 60 + 59, TimerPanel.Join(150, 80, 70)); // hours 0–99, minutes and seconds 0–59
        Assert.Equal((2, 5), TimerPanel.WarningFields(new TimerWarning(125, TimerWarning.AmberHex)));
        Assert.Equal(999 * 60 + 59, TimerPanel.WarningSeconds(1500, 80)); // a warning's minutes go up to 999
    }

    [Fact]
    public void WarningsAddChangeAndRemove()
    {
        IReadOnlyList<TimerWarning> warnings = TimerWarning.Defaults;
        var added = TimerPanel.AddingWarning(warnings);
        Assert.Equal(3, added.Count);
        Assert.Equal(new TimerWarning(60, TimerWarning.AmberHex), added[^1]); // + adds amber at one minute
        var recoloured = TimerPanel.ChangingWarning(warnings, 1, w => w with { ColorHex = "#3B82F6FF" });
        Assert.Equal("#3B82F6FF", recoloured[1].ColorHex);
        Assert.Equal(warnings, TimerPanel.ChangingWarning(warnings, 7, w => w with { RemainingSeconds = 1 })); // out of range does nothing
        Assert.Equal([warnings[1]], TimerPanel.RemovingWarning(warnings, 0));
        Assert.Equal(8, TimerPanel.WarningPalette.Count);
        Assert.Equal("Purple", TimerPanel.ColorName("#a855f7ff"));
        Assert.Equal("Custom", TimerPanel.ColorName("#123456FF"));
    }

    [Fact]
    public void WarningsShowOnlyWhenTheTimerHasAnEnd()
    {
        Assert.True(TimerPanel.ShowsWarnings(new TimerSnapshot("t", TimerMode.Countdown)));
        Assert.True(TimerPanel.ShowsWarnings(new TimerSnapshot("t", TimerMode.CountdownToTime)));
        Assert.False(TimerPanel.ShowsWarnings(new TimerSnapshot("t", TimerMode.CountUp))); // an unlimited count up has nothing to warn against
        Assert.True(TimerPanel.ShowsWarnings(new TimerSnapshot("t", TimerMode.CountUp, durationSeconds: 120)));
    }

    [Fact]
    public void TheEditorSeedsFromTheTimer()
    {
        var countdown = TimerPanel.Seed(new TimerSnapshot("Sermon", TimerMode.Countdown, durationSeconds: 1865), TimeZoneInfo.Utc);
        Assert.Equal(new TimerEdit("Sermon", 0, 31, 5, true, new WallClockFields(11, 0, false)), countdown);
        var unlimited = TimerPanel.Seed(new TimerSnapshot("Up", TimerMode.CountUp), TimeZoneInfo.Utc);
        Assert.False(unlimited.HasLimit);
        Assert.True(TimerPanel.Seed(new TimerSnapshot("Up", TimerMode.CountUp, durationSeconds: 60), TimeZoneInfo.Utc).HasLimit);
        var toTime = TimerPanel.Seed(new TimerSnapshot("Doors", TimerMode.CountdownToTime, targetTime: new(2026, 7, 5, 19, 30, 0, TimeSpan.Zero)), TimeZoneInfo.Utc);
        Assert.Equal(new WallClockFields(7, 30, true), toTime.Until);
    }

    [Fact]
    public void ApplySetsOnlyWhatChanged()
    {
        var (timers, clock) = Make();
        var id = timers.AddCountdown("Sermon", 1800);
        timers.Start(id);
        clock.Advance(100);
        var changes = 0;
        timers.Changed += () => changes++;

        var edit = TimerPanel.Seed(timers.Snapshot(id)!, TimeZoneInfo.Utc);
        TimerPanel.Apply(timers, timers.Snapshot(id)!, edit);
        Assert.Equal(0, changes); // nothing changed: the running count is left alone
        Assert.Equal(1700, timers.Snapshot(id)!.Value(clock.Now));

        TimerPanel.Apply(timers, timers.Snapshot(id)!, edit with { Name = "Message", Minutes = 35 });
        var applied = timers.Snapshot(id)!;
        Assert.Equal(("Message", 2100.0), (applied.Name, applied.DurationSeconds));
        Assert.Equal(2100, applied.Value(clock.Now)); // a new length restarts the count

        var up = timers.AddCountUp("Walk-in");
        TimerPanel.Apply(timers, timers.Snapshot(up)!, new TimerEdit("Walk-in", 0, 2, 0, HasLimit: true, default));
        Assert.Equal(120, timers.Snapshot(up)!.DurationSeconds); // "Count to" 2:00
        TimerPanel.Apply(timers, timers.Snapshot(up)!, new TimerEdit("Walk-in", 0, 2, 0, HasLimit: false, default));
        Assert.Equal(0, timers.Snapshot(up)!.DurationSeconds); // off: runs until paused

        var doors = timers.AddCountdownToTime(19, 30, "Doors");
        TimerPanel.Apply(timers, timers.Snapshot(doors)!, TimerPanel.Seed(timers.Snapshot(doors)!, TimeZoneInfo.Utc) with { Until = new WallClockFields(8, 15, true) });
        Assert.Equal(new DateTimeOffset(2026, 7, 5, 20, 15, 0, TimeSpan.Zero), timers.Snapshot(doors)!.TargetTime);
    }

    [Fact]
    public void RowsReadLikeTheMac()
    {
        var idleTracking = ServiceTrackingTimers.Idle(ServiceTrackingTimers.Subject.Item, ServiceTrackingTimers.Face.Remaining);
        Assert.Equal("--:--", TimerPanel.RowText(idleTracking, Morning));
        Assert.Equal(new TimerTint(null, false), TimerPanel.RowTint(idleTracking, Morning));
        Assert.False(TimerPanel.ShowsPlayPause(idleTracking));
        Assert.False(TimerPanel.ShowsReset(idleTracking));
        Assert.NotNull(TimerPanel.PermanentNote(idleTracking.Id));
        Assert.Null(TimerPanel.PermanentNote("mine"));

        var running = new TimerSnapshot("Sermon", TimerMode.Countdown, isRunning: true, runningSince: Morning, durationSeconds: 100);
        Assert.Equal("1:40", TimerPanel.RowText(running, Morning));
        Assert.Equal(new TimerTint(null, true), TimerPanel.RowTint(running, Morning)); // running: green
        Assert.Equal(TimerWarning.AmberHex, TimerPanel.RowTint(running, Morning.AddSeconds(80)).WarningHex); // inside its last 30 s
        Assert.True(TimerPanel.ShowsPlayPause(running));

        var toTime = new TimerSnapshot("Doors", TimerMode.CountdownToTime, targetTime: Morning.AddHours(1));
        Assert.False(TimerPanel.ShowsPlayPause(toTime)); // it runs by the clock
        Assert.Equal("Re-arm to the next occurrence", TimerPanel.ResetTooltip(toTime));
        Assert.Equal("Reset", TimerPanel.ResetTooltip(running));
    }

    [Fact]
    public void AFolderRowReadsItsFeaturedTimer()
    {
        var near = new TimerSnapshot("Near", TimerMode.Countdown, isRunning: true, runningSince: Morning, durationSeconds: 20);
        var far = new TimerSnapshot("Far", TimerMode.Countdown, isRunning: true, runningSince: Morning, durationSeconds: 600);
        var (text, tint) = TimerPanel.FolderReadout([far, near], Morning);
        Assert.Equal("0:20", text);
        Assert.Equal(new TimerTint(TimerWarning.AmberHex, true), tint);
        var idle = TimerPanel.FolderReadout([new TimerSnapshot("Idle", TimerMode.Countdown, durationSeconds: 60)], Morning);
        Assert.Null(idle.Text);
        Assert.False(idle.Tint.IsLive);
        Assert.False(TimerPanel.IsEditableFolder(ServiceTrackingTimers.FolderId));
        Assert.True(TimerPanel.IsEditableFolder("mine"));
    }

    // MARK: TimerController's board

    [Fact]
    public void NewTimersJoinTheBoardAndDeletedOnesLeaveIt()
    {
        var (timers, _) = Make();
        var a = timers.AddCountdown("A", 60);
        var folder = timers.AddFolder("Band");
        var b = timers.AddCountUp("B");
        Assert.Equal([a, folder, b], timers.Board.Nodes.Select(n => n.Id));
        timers.MoveTimerIntoFolder(a, folder);
        Assert.Equal([a], timers.Board.Folder(folder)?.TimerIds);
        timers.Remove(a);
        Assert.Empty(timers.Board.Folder(folder)!.TimerIds); // the folder stays, emptied
        Assert.Equal([b], timers.Board.AllTimerIds);
    }

    [Fact]
    public void TheTimersFollowTheBoardsOrder()
    {
        var (timers, _) = Make();
        var a = timers.AddCountdown("A", 60);
        var b = timers.AddCountdown("B", 60);
        var c = timers.AddCountdown("C", 60);
        timers.MoveTimer(c, beforeNode: a);
        Assert.Equal([c, a, b], timers.Timers.Select(t => t.Id)); // the stage display's "first timer" follows the panel
        Assert.True(timers.StepTimer(b, -1));
        Assert.Equal([c, b, a], timers.Timers.Select(t => t.Id));
        var folder = timers.AddFolder("F");
        timers.PutInFolder(c, folder);
        Assert.Equal([b, a, c], timers.Timers.Select(t => t.Id));
        Assert.True(timers.StepFolder(folder, -1));
        Assert.True(timers.StepFolder(folder, -1));
        Assert.False(timers.StepFolder(folder, -1)); // already first
        Assert.Equal([c, b, a], timers.Timers.Select(t => t.Id));
        timers.RemoveFolder(folder);
        Assert.Equal([c, b, a], timers.Board.Nodes.Select(n => n.Id)); // its timer takes its place
    }

    [Fact]
    public void BoardEditsSignalAChange()
    {
        var (timers, _) = Make();
        var a = timers.AddCountdown("A", 60);
        var changes = 0;
        timers.Changed += () => changes++;
        var folder = timers.AddFolder("F");
        timers.RenameFolder(folder, "Walk-in");
        timers.SetFolderCollapsed(folder, true);
        timers.PutInFolder(a, folder);
        timers.RemoveFolder(folder);
        Assert.Equal(5, changes); // each is saved
        Assert.Null(timers.Board.Folder(folder));
    }

    [Fact]
    public void ServiceTrackingTimersAreFiledInTheirFolderAndStayThere()
    {
        var (timers, _) = Make();
        var mine = timers.AddCountdown("Mine", 60);
        var item = ServiceTrackingTimers.Idle(ServiceTrackingTimers.Subject.Item, ServiceTrackingTimers.Face.Remaining);
        timers.SetSystemTimers([item]);
        Assert.Equal([item.Id], timers.Board.Folder(ServiceTrackingTimers.FolderId)?.TimerIds);
        Assert.Equal([mine, item.Id], timers.Timers.Select(t => t.Id));

        timers.PutInFolder(mine, ServiceTrackingTimers.FolderId); // nothing goes into it by hand
        timers.MoveTimer(item.Id, beforeNode: mine); // and its timers don't move
        timers.RenameFolder(ServiceTrackingTimers.FolderId, "Mine now");
        timers.RemoveFolder(ServiceTrackingTimers.FolderId);
        var board = timers.Board;
        Assert.Equal([item.Id], board.Folder(ServiceTrackingTimers.FolderId)?.TimerIds);
        Assert.Equal(ServiceTrackingTimers.FolderName, board.Folder(ServiceTrackingTimers.FolderId)?.Name);
        Assert.Null(board.FolderContaining(mine));
    }

    [Fact]
    public void ARestoredBoardKeepsItsFoldersAndMeetsTheTimersThereAre()
    {
        var (timers, _) = Make();
        var a = timers.AddCountdown("A", 60);
        var b = timers.AddCountdown("B", 60);
        var folder = timers.AddFolder("Band");
        timers.MoveTimerIntoFolder(b, folder);
        var saved = timers.Export();
        var board = TimerBoard.FromJson(timers.Board.ToJson());

        var (restored, _) = Make();
        restored.Restore(saved);
        restored.RestoreBoard(board);
        Assert.Equal([b], restored.Board.Folder(folder)?.TimerIds);
        Assert.Equal([a, b], restored.Timers.Select(t => t.Id));

        // A board naming a timer that's gone, and missing one that's new.
        var stale = new TimerBoard([TimerBoardNode.Timer("gone"), TimerBoardNode.Timer(b)]);
        restored.RestoreBoard(stale);
        Assert.Equal([b, a], restored.Board.AllTimerIds);
        Assert.Equal([b, a], restored.Timers.Select(t => t.Id));
    }
}
