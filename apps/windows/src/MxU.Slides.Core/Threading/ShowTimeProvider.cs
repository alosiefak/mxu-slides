using System.Diagnostics;

namespace MxU.Slides.Core.Threading;

/// <summary>
/// A <see cref="TimeProvider"/> whose timers fire on one dedicated, above-normal-priority
/// thread instead of .NET's shared thread pool. Show timing (timeline ticks, delayed slide
/// actions, the scheduler, timecode, the 10 ms audio feeds) then keeps time even when the
/// pool is tied up by an import, a slow disk or a busy machine.
/// <para>
/// Callbacks run one at a time on the clock's thread, so they must be short: post to the UI
/// thread or hand slow work to a <see cref="WorkQueue"/>. A periodic timer that falls behind
/// carries on from now rather than firing a burst to catch up. The clock readings
/// (<see cref="TimeProvider.GetUtcNow"/>, <see cref="TimeProvider.GetTimestamp"/>) are the system's.
/// </para>
/// </summary>
public sealed class ShowTimeProvider : TimeProvider, IDisposable
{
    /// <summary>The app's show clock.</summary>
    public static ShowTimeProvider Shared { get; } = new("MxU show clock");

    private readonly Lock gate = new();
    private readonly PriorityQueue<(ClockTimer Timer, int Generation), long> due = new();
    private readonly AutoResetEvent wake = new(false);
    private bool disposed;

    /// <param name="name">The clock thread's name. Make a separate clock for timers whose callbacks do real work.</param>
    public ShowTimeProvider(string name)
    {
        Name = name;
        new Thread(Run) { IsBackground = true, Name = name, Priority = ThreadPriority.AboveNormal }.Start();
    }

    public string Name { get; }

    /// <summary>A timer callback threw (the timer keeps going). Raised on the clock's thread.</summary>
    public event Action<Exception>? CallbackFailed;

    /// <summary>Timers waiting to fire, including superseded entries not yet dropped (tests).</summary>
    internal int QueuedCount
    {
        get { lock (gate) return due.Count; }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ClockTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private static long Ticks(TimeSpan span) => (long)(Math.Max(0, span.TotalSeconds) * Stopwatch.Frequency);

    private void Enqueue(ClockTimer timer, int generation, long at)
    {
        due.Enqueue((timer, generation), at);
        wake.Set();
    }

    private void Run()
    {
        while (true)
        {
            ClockTimer? fire = null;
            var generation = 0;
            var wait = Timeout.Infinite;
            lock (gate)
            {
                if (disposed) return;
                while (due.TryPeek(out var entry, out var at))
                {
                    if (!entry.Timer.IsCurrent(entry.Generation))
                    {
                        due.Dequeue();
                        continue;
                    }
                    var now = Stopwatch.GetTimestamp();
                    if (at <= now)
                    {
                        due.Dequeue();
                        (fire, generation) = entry;
                        break;
                    }
                    wait = (int)Math.Clamp((long)Math.Ceiling((at - now) * 1000.0 / Stopwatch.Frequency), 1, int.MaxValue);
                    break;
                }
            }
            if (fire is null)
            {
                wake.WaitOne(wait);
                continue;
            }
            if (fire.Fire(generation) is { } error)
            {
                try { CallbackFailed?.Invoke(error); }
                catch { }
            }
        }
    }

    /// <summary>Stops the clock's thread; its timers never fire again.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            due.Clear();
        }
        wake.Set();
    }

    private sealed class ClockTimer(ShowTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        // Bumped by every Change and by Dispose, so queued entries for an older setting are skipped.
        private int generation;
        private long period;
        private long nextAt;
        private bool disposed;

        /// <summary>Called with the owner's lock held.</summary>
        public bool IsCurrent(int queued) => !disposed && queued == generation;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                if (disposed || owner.disposed) return false;
                generation++;
                this.period = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : Math.Max(1, Ticks(period));
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                nextAt = Stopwatch.GetTimestamp() + Ticks(dueTime);
                owner.Enqueue(this, generation, nextAt);
            }
            return true;
        }

        /// <summary>Runs the callback if this entry is still current (on the clock's thread); returns what it threw.</summary>
        public Exception? Fire(int queued)
        {
            lock (owner.gate)
            {
                if (!IsCurrent(queued)) return null;
                if (period > 0)
                {
                    // Queue the next tick first, so a Change or Dispose inside the callback wins.
                    var now = Stopwatch.GetTimestamp();
                    nextAt += period;
                    if (nextAt <= now) nextAt = now + period;
                    owner.Enqueue(this, generation, nextAt);
                }
            }
            try
            {
                callback(state);
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        public void Dispose()
        {
            lock (owner.gate)
            {
                disposed = true;
                generation++;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
