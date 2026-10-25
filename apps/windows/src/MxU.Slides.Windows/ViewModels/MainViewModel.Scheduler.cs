using Microsoft.Win32;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;
using MxU.Slides.LocalApi;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The scheduler: schedule triggers fire their actions at a time of day, on chosen days,
/// once, or when a timer reaches a value (e.g. "start the countdown loop at 10:25 every
/// Sunday"). The <see cref="Scheduler"/> (ported from the Mac) decides when; its memory —
/// whether it's on and what has already fired — is kept in <c>scheduler.json</c> so a
/// restart never fires anything twice.
/// </summary>
public sealed partial class MainViewModel : ISchedulerHost
{
    private Scheduler? scheduler;

    private static string SchedulerMemoryPath => Path.Combine(AppPaths.DataRoot, "scheduler.json");

    /// <summary>Starts the scheduler once the library is loaded (on the UI thread, so its timers come back to it).</summary>
    public Scheduler Schedule => scheduler ??= CreateScheduler();

    private Scheduler CreateScheduler()
    {
        SchedulerMemory? memory = null;
        try
        {
            if (File.Exists(SchedulerMemoryPath)) memory = MxuJson.Deserialize<SchedulerMemory>(File.ReadAllBytes(SchedulerMemoryPath));
        }
        catch (Exception ex)
        {
            Log.Warn($"Scheduler memory was unreadable; starting fresh: {ex.Message}");
        }
        var created = new Scheduler(this, memory, ui: SynchronizationContext.Current);
        created.Noted += (crumb, detail) => Log.Info($"Scheduler: {crumb} {detail}");
        created.MemoryChanged += () =>
        {
            try { Core.Library.LibraryStore.AtomicWrite(SchedulerMemoryPath, MxuJson.SerializeToUtf8(created.Memory)); }
            catch (Exception ex) { Log.Error("Couldn't save the scheduler's memory", ex); }
        };
        // Anything that can change what's due next makes it look again.
        Library.Changed += (type, _) =>
        {
            if (type == typeof(ScheduleTrigger) || type == typeof(SchedulerBoard)) SweepSoon();
        };
        Timers.Changed += SweepSoon;
        SystemEvents.TimeChanged += (_, _) => SweepSoon();
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Resume) SweepSoon();
        };
        return created;
    }

    private void SweepSoon() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => scheduler?.Sweep());

    private void StartScheduler()
    {
        try { Schedule.Start(); }
        catch (Exception ex) { Log.Error("The scheduler couldn't start", ex); }
    }

    IReadOnlyList<ScheduleTrigger> ISchedulerHost.Triggers => Library.All<ScheduleTrigger>();

    SchedulerBoard ISchedulerHost.Board =>
        Library.Get<SchedulerBoard>(SchedulerBoardLogic.WellKnownId) ?? new SchedulerBoard { Id = SchedulerBoardLogic.WellKnownId };

    TimerSnapshot? ISchedulerHost.Timer(string id) => Timers.Snapshot(id);

    void ISchedulerHost.Run(IReadOnlyList<SlideAction> actions) => Actions.Execute(actions);

    // Naming the trigger lets its production steps (Windows only) run beside its actions.
    void ISchedulerHost.Run(string triggerId, IReadOnlyList<SlideAction> actions) => Actions.Execute(triggerId, actions);

    void ISchedulerHost.Archive(string triggerId)
    {
        if (Library.Get<ScheduleTrigger>(triggerId) is not { } trigger) return;
        trigger.Archived = true;
        Library.Save(trigger);
    }

    // MARK: Local API

    ApiSchedulerStatus IApiBridge.SchedulerStatus()
    {
        var status = Schedule.Status();
        return new ApiSchedulerStatus(status.Enabled,
            status.NextFire is { } next ? new ApiSchedulerUpcoming(next.TriggerId, next.Name, next.Date) : null,
            [.. status.Triggers.Select(t => new ApiSchedulerTriggerStatus(t.Id, t.Name, t.Enabled, t.FolderEnabled, t.FolderId, t.FolderName,
                t.NextFire, t.LastFired, t.Archived))]);
    }

    void IApiBridge.SetSchedulerEnabled(bool enabled) => Schedule.Enabled = enabled;

    void IApiBridge.SetScheduleTriggerEnabled(string id, bool enabled)
    {
        var trigger = Library.Get<ScheduleTrigger>(ApiIds.Validated(id)) ?? throw ApiError.NotFound($"No schedule trigger '{id}'.");
        ScheduleTriggers.SetEnabled(trigger, enabled);
        Library.Save(trigger);
    }

    void IApiBridge.FireScheduleTrigger(string id)
    {
        if (Library.Get<ScheduleTrigger>(ApiIds.Validated(id)) is null) throw ApiError.NotFound($"No schedule trigger '{id}'.");
        Schedule.RunNow(id);
    }
}
