using System.Globalization;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Production.Actions;

/// <summary>
/// Runs production steps when their owner runs, each after its own delay. The
/// <see cref="ActionRouter"/> calls <see cref="OwnerRan"/> each time a slide or media item
/// fires, a combo runs, or a schedule trigger or timeline actions cue goes off (it is the
/// router's <see cref="IExtraSteps"/>).
/// <para>
/// Steps with no delay are handed over at once; the rest wait on the show clock (its own
/// thread, not the UI's or the thread pool's, so a busy screen doesn't make a camera cut
/// late). Steps with the same delay go in their list order. "Handing over" only queues the
/// step with its device (or changes the lighting levels), which takes microseconds: the
/// device does the slow part on its own thread. Clear All drops the steps still waiting, as
/// it does the shared ones.
/// </para>
/// </summary>
public sealed class ProductionRunner : IExtraSteps
{
    private readonly ProductionSteps steps;
    private readonly Action<ProductionAction> perform;
    private readonly TimeProvider clock;
    private readonly Lock gate = new();
    private readonly HashSet<ITimer> waiting = [];

    /// <param name="perform">Hands one step to its device or the lights (the hub's <c>Perform</c>).</param>
    /// <param name="clock">Times the delays (tests pass one they step by hand).</param>
    public ProductionRunner(ProductionSteps steps, Action<ProductionAction> perform, TimeProvider? clock = null)
    {
        this.steps = steps;
        this.perform = perform;
        this.clock = clock ?? ShowTimeProvider.Shared;
    }

    /// <summary>The diagnostics trail: (crumb, detail).</summary>
    public event Action<string, string>? Noted;

    /// <summary>Delayed groups of steps still waiting.</summary>
    public int Pending
    {
        get
        {
            lock (gate) return waiting.Count;
        }
    }

    public void OwnerRan(string ownerId)
    {
        var list = steps.For(ownerId);
        if (list.Count == 0) return;
        Note("production.owner", $"{ownerId} {list.Count} step(s)");
        Run(list);
    }

    /// <summary>Runs a list of steps, honouring their delays.</summary>
    public void Run(IReadOnlyList<ProductionAction> list)
    {
        foreach (var group in list.GroupBy(s => Math.Clamp(s.DelaySeconds ?? 0, 0, 3600)).OrderBy(g => g.Key))
        {
            var batch = group.Select(s => s.Copy()).ToList();
            if (group.Key <= 0)
            {
                foreach (var step in batch) Perform(step);
                continue;
            }
            Note("production.delayed", string.Create(CultureInfo.InvariantCulture, $"{batch.Count} step(s) in {group.Key}s"));
            ITimer? timer = null;
            timer = clock.CreateTimer(_ =>
            {
                lock (gate)
                {
                    // Cancelled (Clear All) between being due and running: do nothing.
                    if (timer is null || !waiting.Remove(timer)) return;
                }
                timer.Dispose();
                foreach (var step in batch) Perform(step);
            }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            lock (gate) waiting.Add(timer);
            timer.Change(TimeSpan.FromSeconds(group.Key), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Runs one step now.</summary>
    public void Perform(ProductionAction step)
    {
        try
        {
            Note("production.step", step.KindName);
            perform(step);
        }
        catch (Exception ex)
        {
            // One bad step (a device gone mid-change) must never stop the others or the show.
            Note("production.failed", $"{step.KindName}: {ex.Message}");
        }
    }

    public void CancelPending()
    {
        List<ITimer> cancelled;
        lock (gate)
        {
            cancelled = [.. waiting];
            waiting.Clear();
        }
        if (cancelled.Count == 0) return;
        foreach (var timer in cancelled) timer.Dispose();
        Note("production.cancelled", $"{cancelled.Count} group(s)");
    }

    private void Note(string crumb, string detail)
    {
        try { Noted?.Invoke(crumb, detail); }
        catch { }
    }
}
