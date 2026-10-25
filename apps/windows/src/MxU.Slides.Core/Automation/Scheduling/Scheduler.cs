using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Core.Automation;

/// <summary>What the scheduler needs from the app. On the Mac: the AppModel, the timers and the action router.</summary>
public interface ISchedulerHost
{
    /// <summary>Every schedule trigger in the library (archived ones are skipped by the scheduler).</summary>
    IReadOnlyList<ScheduleTrigger> Triggers { get; }

    /// <summary>The scheduler board (folders gate their triggers).</summary>
    SchedulerBoard Board { get; }

    /// <summary>A timer's state, for "when a timer reaches" conditions.</summary>
    TimerSnapshot? Timer(string id);

    /// <summary>Runs a trigger's actions (the WPF app passes them to <see cref="ActionRouter.Execute(IReadOnlyList{SlideAction})"/>).</summary>
    void Run(IReadOnlyList<SlideAction> actions);

    /// <summary>Runs a trigger's actions, knowing which trigger (on Windows its production steps run beside them).</summary>
    void Run(string triggerId, IReadOnlyList<SlideAction> actions) => Run(actions);

    /// <summary>Marks a spent trigger archived in the library (it has fired for the last time).</summary>
    void Archive(string triggerId);
}

/// <summary>
/// What the scheduler keeps between launches: whether it is on, the last due time each
/// trigger used (so nothing fires twice), when it last swept (so a sleep is noticed), and
/// its run log. The app saves it when <see cref="Scheduler.MemoryChanged"/> fires.
/// </summary>
public sealed record SchedulerMemory(
    bool Enabled, IReadOnlyDictionary<string, DateTimeOffset> LastDue, DateTimeOffset? LastSweep, IReadOnlyList<Scheduler.RunRecord> History);

/// <summary>The scheduler as the Local API reports it (the same shape as ApiSchedulerStatus).</summary>
public sealed record SchedulerStatus(bool Enabled, Scheduler.UpcomingFire? NextFire, IReadOnlyList<SchedulerTriggerStatus> Triggers);

public sealed record SchedulerTriggerStatus(
    string Id, string Name, bool Enabled, bool FolderEnabled, string? FolderId, string? FolderName,
    DateTimeOffset? NextFire, DateTimeOffset? LastFired, bool? Archived = null);

/// <summary>
/// Runs schedule triggers at their times. Each sweep fires what came due since the last
/// sweep (late if a little late, recorded as missed if more than a minute late, as after
/// sleep), then sets a timer for the next due time. While any open trigger waits on a
/// timer, it checks the timers every second for a crossing. Port of the logic in
/// Sources/SchedulerController.swift; drive it from the UI thread. The app calls
/// <see cref="Sweep"/> again whenever the triggers, the board or the timers change, and
/// when the computer wakes or its clock or time zone changes.
/// </summary>
public sealed class Scheduler
{
    /// <summary>A line in the run log.</summary>
    public sealed record RunRecord(Guid Id, string TriggerId, string Name, DateTimeOffset At, RunOutcome Outcome, RunSource Source);

    public enum RunOutcome { Fired, Late, Missed }

    /// <summary>What ran it: its schedule, a timer crossing, or Run Now.</summary>
    public enum RunSource { Schedule, Timer, Manual }

    public sealed record UpcomingFire(string TriggerId, string Name, DateTimeOffset Date);

    /// <summary>The run log keeps this many lines.</summary>
    public const int RunLogLimit = 500;

    private readonly ISchedulerHost host;
    private readonly TimeProvider clock;
    private readonly TimeZoneInfo? zone;
    private readonly SynchronizationContext? ui;

    private bool enabled;
    private Dictionary<string, DateTimeOffset> lastDue;
    private DateTimeOffset lastSweep;
    private readonly List<RunRecord> history;
    private ShowTimer? wakeTimer;
    private ShowTimer? timerWatch;
    private List<(ScheduleTrigger Trigger, ScheduleCondition Condition)> watchedTimerConditions = [];
    private SchedulerBoard watchedBoard = new() { Id = SchedulerBoardLogic.WellKnownId };
    private readonly Dictionary<string, (DateTimeOffset? Stamp, double Value)> crossingState = [];
    private bool started;

    /// <param name="saved">What was kept from the last launch (null: on, nothing run yet).</param>
    /// <param name="clock">The clock (tests pass a fake).</param>
    /// <param name="zone">The time zone wall times are in (null: the clock's local zone, read at each sweep).</param>
    /// <param name="ui">Where timer callbacks run; pass the UI thread's <see cref="SynchronizationContext.Current"/>.</param>
    public Scheduler(ISchedulerHost host, SchedulerMemory? saved = null, TimeProvider? clock = null, TimeZoneInfo? zone = null, SynchronizationContext? ui = null)
    {
        this.host = host;
        this.clock = clock ?? ShowTimeProvider.Shared;
        this.zone = zone;
        this.ui = ui;
        enabled = saved?.Enabled ?? true;
        lastDue = saved?.LastDue.ToDictionary() ?? [];
        lastSweep = saved?.LastSweep ?? this.clock.GetUtcNow();
        history = saved?.History.ToList() ?? [];
    }

    /// <summary>The diagnostics trail: (crumb, detail).</summary>
    public event Action<string, string>? Noted;

    /// <summary>Fires when <see cref="Memory"/> should be saved.</summary>
    public event Action? MemoryChanged;

    /// <summary>Fires after a sweep updates <see cref="Upcoming"/> and <see cref="NextFire"/>.</summary>
    public event Action? Changed;

    private TimeZoneInfo Zone => zone ?? clock.LocalTimeZone;

    private DateTimeOffset Now => clock.GetUtcNow();

    /// <summary>The master switch; switching it sweeps at once.</summary>
    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value) return;
            enabled = value;
            MemoryChanged?.Invoke();
            Noted?.Invoke("scheduler.enabled", value ? "on" : "off");
            Sweep();
        }
    }

    /// <summary>The soonest upcoming fire among open triggers.</summary>
    public UpcomingFire? NextFire { get; private set; }

    /// <summary>The run log, newest first.</summary>
    public IReadOnlyList<RunRecord> History => history;

    /// <summary>Each open trigger's next due time.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> Upcoming { get; private set; } = new Dictionary<string, DateTimeOffset>();

    public SchedulerMemory Memory => new(enabled, new Dictionary<string, DateTimeOffset>(lastDue), lastSweep, history.ToList());

    /// <summary>Starts scheduling (once). Call it when the library has loaded.</summary>
    public void Start()
    {
        if (started) return;
        started = true;
        Sweep();
    }

    /// <summary>Stops the timers (for shutdown).</summary>
    public void Stop()
    {
        started = false;
        wakeTimer?.Dispose();
        wakeTimer = null;
        timerWatch?.Dispose();
        timerWatch = null;
    }

    private List<ScheduleTrigger> OpenTriggers() => host.Triggers.Where(t => t.Archived != true).ToList();

    /// <summary>A trigger can fire now: the scheduler is on, the trigger is on, not snoozed, not ended, and its folder is on.</summary>
    private bool GateOpen(ScheduleTrigger trigger, SchedulerBoard board)
    {
        var now = Now;
        return enabled && (trigger.Enabled ?? true)
            && !ScheduleMath.Snoozed(trigger, now, Zone)
            && !ScheduleMath.Ended(trigger, now, Zone)
            && board.FolderEnabled(trigger.Id);
    }

    /// <summary>Fires what came due since the last sweep and arms the next wake.</summary>
    public void Sweep()
    {
        if (!started) return;
        var now = Now;
        var triggers = OpenTriggers();
        var board = host.Board;
        var result = ScheduleMath.Sweep(
            triggers, t => GateOpen(t, board), lastDue, lastSweep, now, zone: Zone,
            timerSatisfied: c => TimerConditionSatisfied(c, now));

        foreach (var gated in result.Gated)
            Noted?.Invoke("scheduler.gated", $"{NameOf(gated.TriggerId, triggers)} — all-conditions gate closed");
        foreach (var miss in result.Missed)
        {
            Noted?.Invoke("scheduler.missed", $"{NameOf(miss.TriggerId, triggers)} due {miss.DueAt:O}");
            Record(new RunRecord(Guid.NewGuid(), miss.TriggerId, NameOf(miss.TriggerId, triggers), miss.DueAt, RunOutcome.Missed, RunSource.Schedule));
        }
        foreach (var firing in result.Firings)
            if (triggers.FirstOrDefault(t => t.Id == firing.TriggerId) is { } trigger) Fire(trigger, RunSource.Schedule, firing.Late);

        lastDue = result.LastDue;
        lastSweep = now;
        MemoryChanged?.Invoke();

        var rows = new Dictionary<string, DateTimeOffset>();
        foreach (var trigger in triggers.Where(t => GateOpen(t, board)))
            if (ScheduleMath.NextDue(trigger, now, Zone) is { } due) rows[trigger.Id] = due;
        Upcoming = rows;
        NextFire = null;
        if (rows.Count > 0)
        {
            var soonest = rows.MinBy(r => r.Value);
            NextFire = new UpcomingFire(soonest.Key, NameOf(soonest.Key, triggers), soonest.Value);
        }

        var snoozeWake = triggers.Select(t => ScheduleMath.SnoozeExpiry(t, now, Zone)).OfType<DateTimeOffset>().Cast<DateTimeOffset?>().Min();
        var wake = new[] { result.NextWake, snoozeWake }.OfType<DateTimeOffset>().Cast<DateTimeOffset?>().Min();
        ScheduleWake(wake, now);
        UpdateTimerWatch(triggers, board);
        Changed?.Invoke();
    }

    private void ScheduleWake(DateTimeOffset? date, DateTimeOffset now)
    {
        wakeTimer?.Dispose();
        wakeTimer = null;
        if (date is not { } at) return;
        var interval = Math.Max(0, (at - now).TotalSeconds) + 0.05;
        wakeTimer = ShowTimer.After(clock, ui, TimeSpan.FromSeconds(interval), Sweep);
    }

    private void UpdateTimerWatch(IReadOnlyList<ScheduleTrigger> triggers, SchedulerBoard board)
    {
        var watched = triggers.Where(t => GateOpen(t, board)).SelectMany(trigger => trigger.Conditions
            .Where(c => c.Kind == ScheduleConditionKind.TimerReaches && !string.IsNullOrEmpty(c.TimerId))
            .Select(c => (trigger, c))).ToList();
        watchedTimerConditions = watched;
        watchedBoard = board;
        var keys = watched.Select(w => WatchKey(w.Item1, w.Item2)).ToHashSet();
        foreach (var key in crossingState.Keys.Where(k => !keys.Contains(k)).ToList()) crossingState.Remove(key);
        if (watched.Count == 0)
        {
            timerWatch?.Dispose();
            timerWatch = null;
            return;
        }
        timerWatch ??= ShowTimer.Every(clock, ui, TimeSpan.FromSeconds(1), TickTimerWatch);
    }

    private static string WatchKey(ScheduleTrigger trigger, ScheduleCondition condition) => $"{trigger.Id}/{condition.Id}";

    /// <summary>
    /// Once a second: fires a trigger whose timer just crossed its value. A crossing counts
    /// only between two readings of the same run of the timer (a restart isn't a crossing).
    /// </summary>
    internal void TickTimerWatch()
    {
        var now = Now;
        foreach (var (trigger, condition) in watchedTimerConditions)
        {
            if (!GateOpen(trigger, watchedBoard)) continue;
            var key = WatchKey(trigger, condition);
            if (condition.TimerId is not { } timerId || host.Timer(timerId) is not { IsLive: true } snapshot)
            {
                crossingState.Remove(key);
                continue;
            }
            var threshold = condition.TimerSeconds ?? 0;
            var countsUp = snapshot.Mode == TimerMode.CountUp;
            var stamp = countsUp ? snapshot.RunningSince : snapshot.RunningSince ?? snapshot.TargetTime;
            var value = countsUp ? snapshot.Elapsed(now) : snapshot.Value(now);
            var hadPrevious = crossingState.TryGetValue(key, out var previous);
            crossingState[key] = (stamp, value);
            if (!hadPrevious || previous.Stamp != stamp) continue;
            var crossed = countsUp
                ? ScheduleMath.CountUpCrossed(previous.Value, value, threshold)
                : ScheduleMath.CountdownCrossed(previous.Value, value, threshold);
            if (!crossed) continue;
            if (ScheduleMath.LogicGateOpen(trigger, condition, now, Zone, c => TimerConditionSatisfied(c, now)))
                Fire(trigger, RunSource.Timer, false);
            else
                Noted?.Invoke("scheduler.gated", $"{trigger.Name} — all-conditions gate closed");
        }
    }

    /// <summary>A timer condition read as a state: a countdown at or under its value, a count-up at or over it.</summary>
    private bool TimerConditionSatisfied(ScheduleCondition condition, DateTimeOffset now)
    {
        if (condition.TimerId is not { } timerId || host.Timer(timerId) is not { } snapshot) return false;
        var threshold = condition.TimerSeconds ?? 0;
        return snapshot.Mode == TimerMode.CountUp ? snapshot.Elapsed(now) >= threshold : snapshot.Value(now) <= threshold;
    }

    /// <summary>Run Now: fires a trigger by hand, whatever its schedule.</summary>
    public void RunNow(string triggerId)
    {
        if (host.Triggers.FirstOrDefault(t => t.Id == triggerId) is not { } trigger) return;
        Fire(trigger, RunSource.Manual, false);
    }

    private void Fire(ScheduleTrigger trigger, RunSource source, bool late)
    {
        var crumb = source switch
        {
            RunSource.Schedule => late ? "scheduler.late" : "scheduler.fired",
            RunSource.Timer => "scheduler.timerFired",
            _ => "scheduler.runNow",
        };
        Noted?.Invoke(crumb, trigger.Name);
        Record(new RunRecord(Guid.NewGuid(), trigger.Id, trigger.Name, Now, late ? RunOutcome.Late : RunOutcome.Fired, source));
        host.Run(trigger.Id, trigger.Actions);
        if (ScheduleMath.SpentOneOffDue(trigger, Now, Zone) is not null || ScheduleMath.SeriesCompleted(trigger, Now, Zone))
        {
            Noted?.Invoke("scheduler.archived", trigger.Name);
            host.Archive(trigger.Id);
        }
    }

    private void Record(RunRecord entry)
    {
        history.Insert(0, entry);
        if (history.Count > RunLogLimit) history.RemoveRange(RunLogLimit, history.Count - RunLogLimit);
        MemoryChanged?.Invoke();
    }

    private static string NameOf(string triggerId, IReadOnlyList<ScheduleTrigger> triggers) =>
        triggers.FirstOrDefault(t => t.Id == triggerId)?.Name ?? triggerId;

    /// <summary>The scheduler for the Local API (port of LocalAPIBridgeAdapter.schedulerStatus).</summary>
    public SchedulerStatus Status()
    {
        var board = host.Board;
        var triggers = host.Triggers.Select(trigger =>
        {
            var folder = board.FolderContaining(trigger.Id);
            var lastFired = history.FirstOrDefault(r => r.TriggerId == trigger.Id && r.Outcome != RunOutcome.Missed)?.At;
            return new SchedulerTriggerStatus(
                trigger.Id, trigger.Name, trigger.Enabled ?? true, board.FolderEnabled(trigger.Id), folder?.Id, folder?.Name,
                Upcoming.TryGetValue(trigger.Id, out var next) ? next : null, lastFired, trigger.Archived == true ? true : null);
        }).ToList();
        return new SchedulerStatus(enabled, NextFire, triggers);
    }
}
