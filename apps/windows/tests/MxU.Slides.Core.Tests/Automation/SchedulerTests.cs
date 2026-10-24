using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;
using static MxU.Slides.Core.Tests.Automation.ScheduleMathTests;

namespace MxU.Slides.Core.Tests.Automation;

// C#: the logic of Sources/SchedulerController.swift (the Mac has no tests for the
// controller itself), run against a fake clock that the tests step by hand.
public class SchedulerTests
{
    private sealed class Host : ISchedulerHost
    {
        public List<ScheduleTrigger> All { get; } = [];
        public IReadOnlyList<ScheduleTrigger> Triggers => All;
        public SchedulerBoard Board { get; set; } = new() { Id = SchedulerBoardLogic.WellKnownId };
        public Dictionary<string, TimerSnapshot> Timers { get; } = [];
        public List<string> Ran { get; } = [];
        public List<string> Archived { get; } = [];

        public TimerSnapshot? Timer(string id) => Timers.GetValueOrDefault(id);

        public void Run(IReadOnlyList<SlideAction> actions) => Ran.AddRange(actions.Select(a => a.Id));

        public void Archive(string triggerId)
        {
            Archived.Add(triggerId);
            All.First(t => t.Id == triggerId).Archived = true;
        }
    }

    private static ScheduleTrigger Weekly(string id, int day, string time, string action) => new()
    {
        Id = id, Name = id,
        Conditions = [new() { Id = id + ".c", Kind = ScheduleConditionKind.Weekly, Days = [day], TimeOfDay = time }],
        Actions = [Make.Action(action, SlideActionKind.ClearAll)],
    };

    private static (Scheduler Scheduler, Host Host, ManualClock Clock) Build(DateTimeOffset start, params ScheduleTrigger[] triggers)
    {
        var host = new Host();
        host.All.AddRange(triggers);
        var clock = new ManualClock(start, Chicago);
        var scheduler = new Scheduler(host, new SchedulerMemory(true, new Dictionary<string, DateTimeOffset>(), start, []), clock);
        return (scheduler, host, clock);
    }

    [Fact]
    public void AStartedSchedulerFiresOnTimeAndArmsTheNextWake()
    {
        var (scheduler, host, clock) = Build(Date(2026, 7, 12, 8, 59), Weekly("t", 1, "09:00", "go"));
        clock.Advance(30);
        Assert.Empty(host.Ran);
        Assert.Null(scheduler.NextFire);

        scheduler.Start();
        Assert.Equal(Date(2026, 7, 12, 9, 0), scheduler.NextFire?.Date);
        Assert.Empty(host.Ran);
        Assert.Equal(1, clock.ActiveTimers);
    }

    [Fact]
    public void TheWakeTimerFiresTheTriggerAndLogsIt()
    {
        var (scheduler, host, clock) = Build(Date(2026, 7, 12, 8, 59), Weekly("t", 1, "09:00", "go"));
        scheduler.Start();
        clock.Advance(61);
        Assert.Equal(["go"], host.Ran);
        var run = Assert.Single(scheduler.History);
        Assert.Equal((Scheduler.RunOutcome.Fired, Scheduler.RunSource.Schedule, "t"), (run.Outcome, run.Source, run.TriggerId));
        Assert.Equal(Date(2026, 7, 19, 9, 0), scheduler.NextFire?.Date);
        Assert.Equal(Date(2026, 7, 12, 9, 0), scheduler.Memory.LastDue["t"]);
    }

    [Fact]
    public void ALongSleepRecordsMissedFiresInsteadOfRunningThem()
    {
        var (scheduler, host, clock) = Build(Date(2026, 7, 9, 12, 0), Weekly("t", 1, "09:00", "go"));
        var saved = scheduler.Memory;
        // The computer slept through Sunday morning; it wakes on Sunday afternoon.
        clock.Advance((Date(2026, 7, 12, 15, 0) - Date(2026, 7, 9, 12, 0)).TotalSeconds);
        var woken = new Scheduler(host, saved, clock);
        woken.Start();
        Assert.Empty(host.Ran);
        var run = Assert.Single(woken.History);
        Assert.Equal((Scheduler.RunOutcome.Missed, Date(2026, 7, 12, 9, 0)), (run.Outcome, run.At));
    }

    [Fact]
    public void GatesCloseForTheMasterSwitchTheTriggerItsFolderAndASnooze()
    {
        var a = Weekly("a", 1, "09:00", "a!");
        var b = Weekly("b", 1, "09:00", "b!");
        var c = Weekly("c", 1, "09:00", "c!");
        var d = Weekly("d", 1, "09:00", "d!");
        b.Enabled = false;
        d.DisabledUntil = "2026-07-12T12:00";
        var (scheduler, host, clock) = Build(Date(2026, 7, 12, 8, 59), a, b, c, d);
        var folder = host.Board.AddFolder("Off", "f");
        host.Board.Reconcile(["a", "b", "c", "d"]);
        host.Board.MoveTriggerIntoFolder("c", folder);
        host.Board.SetFolderEnabled(folder, false);

        scheduler.Start();
        Assert.Equal(["a"], scheduler.Upcoming.Keys);
        clock.Advance(61);
        Assert.Equal(["a!"], host.Ran);

        scheduler.Enabled = false;
        Assert.Null(scheduler.NextFire);
        Assert.False(scheduler.Memory.Enabled);
        var status = scheduler.Status();
        Assert.False(status.Enabled);
        var forC = status.Triggers.Single(t => t.Id == "c");
        Assert.Equal((false, "f", "Off"), (forC.FolderEnabled, forC.FolderId, forC.FolderName));
        var lastFired = status.Triggers.Single(t => t.Id == "a").LastFired;
        Assert.True(lastFired is { } fired && Math.Abs((fired - Date(2026, 7, 12, 9, 0)).TotalSeconds) < 1);
    }

    [Fact]
    public void ASpentOneOffIsArchivedAfterItFires()
    {
        var oneOff = new ScheduleTrigger
        {
            Id = "once", Name = "once",
            Conditions = [new() { Id = "c", Kind = ScheduleConditionKind.OneTime, Date = "2026-07-12T09:00" }],
            Actions = [Make.Action("go", SlideActionKind.ClearAll)],
        };
        var (scheduler, host, clock) = Build(Date(2026, 7, 12, 8, 59), oneOff);
        scheduler.Start();
        clock.Advance(90);
        Assert.Equal(["go"], host.Ran);
        Assert.Equal(["once"], host.Archived);
        Assert.Null(scheduler.NextFire);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public void RunNowFiresByHandAndSaysSo()
    {
        var (scheduler, host, _) = Build(Date(2026, 7, 8, 12, 0), Weekly("t", 1, "09:00", "go"));
        scheduler.RunNow("t");
        scheduler.RunNow("ghost");
        Assert.Equal(["go"], host.Ran);
        Assert.Equal(Scheduler.RunSource.Manual, scheduler.History.Single().Source);
    }

    [Fact]
    public void ATimerConditionFiresOnTheCrossingOnly()
    {
        var trigger = new ScheduleTrigger
        {
            Id = "t", Name = "walk-in over",
            Conditions = [new() { Id = "c", Kind = ScheduleConditionKind.TimerReaches, TimerId = "walkin", TimerSeconds = 0 }],
            Actions = [Make.Action("go", SlideActionKind.ClearAll)],
        };
        var start = Date(2026, 7, 12, 8, 0);
        var (scheduler, host, clock) = Build(start, trigger);
        var running = new TimerSnapshot("Walk-in", TimerMode.Countdown, id: "walkin", durationSeconds: 3)
            with { IsRunning = true, RunningSince = clock.GetUtcNow() };
        host.Timers["walkin"] = running;
        scheduler.Start();
        Assert.Equal(1, clock.ActiveTimers);

        clock.Advance(2);
        Assert.Empty(host.Ran);
        clock.Advance(2);
        Assert.Equal(["go"], host.Ran);
        clock.Advance(5);
        Assert.Equal(["go"], host.Ran);
        Assert.Equal(Scheduler.RunSource.Timer, scheduler.History.Single().Source);

        // A restart is a new run: crossing again fires again.
        host.Timers["walkin"] = running with { RunningSince = clock.GetUtcNow() };
        clock.Advance(5);
        Assert.Equal(["go", "go"], host.Ran);
    }

    [Fact]
    public void TheRunLogKeepsFiveHundredNewestFirst()
    {
        var (scheduler, _, _) = Build(Date(2026, 7, 8, 12, 0), Weekly("t", 1, "09:00", "go"));
        for (var i = 0; i < Scheduler.RunLogLimit + 3; i++) scheduler.RunNow("t");
        Assert.Equal(Scheduler.RunLogLimit, scheduler.History.Count);
    }
}
