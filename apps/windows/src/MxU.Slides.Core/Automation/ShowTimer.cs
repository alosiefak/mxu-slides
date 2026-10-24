namespace MxU.Slides.Core.Automation;

/// <summary>
/// A one-shot or repeating timer for the automation classes. Time comes from a
/// <see cref="TimeProvider"/> (tests pass a fake one they can step), and when a
/// <see cref="SynchronizationContext"/> is given the callback is posted to it, so in
/// the WPF app it runs on the UI thread like everything else in the show.
/// </summary>
internal sealed class ShowTimer : IDisposable
{
    private readonly SynchronizationContext? ui;
    private readonly Action callback;
    private readonly bool repeats;
    private readonly ITimer timer;
    private volatile bool stopped;

    private ShowTimer(TimeProvider clock, SynchronizationContext? ui, TimeSpan due, TimeSpan period, Action callback)
    {
        this.ui = ui;
        this.callback = callback;
        repeats = period != Timeout.InfiniteTimeSpan;
        timer = clock.CreateTimer(_ => Elapsed(), null, due < TimeSpan.Zero ? TimeSpan.Zero : due, period);
    }

    /// <summary>Runs <paramref name="callback"/> once after <paramref name="delay"/>.</summary>
    public static ShowTimer After(TimeProvider clock, SynchronizationContext? ui, TimeSpan delay, Action callback) =>
        new(clock, ui, delay, Timeout.InfiniteTimeSpan, callback);

    /// <summary>Runs <paramref name="callback"/> every <paramref name="period"/> until disposed.</summary>
    public static ShowTimer Every(TimeProvider clock, SynchronizationContext? ui, TimeSpan period, Action callback) =>
        new(clock, ui, period, period, callback);

    private void Elapsed()
    {
        if (stopped) return;
        if (!repeats) stopped = true;
        if (ui is null) callback();
        else ui.Post(_ => Run(), null);
    }

    private void Run()
    {
        if (repeats && stopped) return;
        callback();
    }

    public void Dispose()
    {
        stopped = true;
        timer.Dispose();
    }
}
