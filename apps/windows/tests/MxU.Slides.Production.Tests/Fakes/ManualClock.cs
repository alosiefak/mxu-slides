namespace MxU.Slides.Production.Tests.Fakes;

/// <summary>
/// A clock the tests step by hand: its timers fire, in order, as <see cref="Advance"/> passes
/// them, on the test's own thread. (The same idea as Core.Tests' ManualClock.)
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly List<ManualTimer> timers = [];

    public double Elapsed { get; private set; }

    public int ActiveTimers => timers.Count(t => t.Due is not null);

    public override long TimestampFrequency => 1_000_000_000;

    public override long GetTimestamp() => (long)Math.Round(Elapsed * TimestampFrequency);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(double seconds)
    {
        var end = Elapsed + seconds;
        for (var guard = 0; guard < 100_000; guard++)
        {
            var next = timers.Where(t => t.Due is { } due && due <= end + 1e-12).MinBy(t => t.Due);
            if (next is null) break;
            Elapsed = Math.Max(Elapsed, next.Due!.Value);
            next.Fire();
        }
        Elapsed = end;
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public double? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Elapsed + dueTime.TotalSeconds;
            return true;
        }

        public void Fire()
        {
            Due = null;
            callback(state);
        }

        public void Dispose()
        {
            Due = null;
            clock.timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
