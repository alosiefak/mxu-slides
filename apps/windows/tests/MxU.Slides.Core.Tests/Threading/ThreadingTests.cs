using System.Diagnostics;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Core.Tests.Threading;

[CollectionDefinition(nameof(BusyPool), DisableParallelization = true)]
public sealed class BusyPool;

/// <summary>Blocks every thread-pool thread (and the ones the pool adds meanwhile) until disposed.</summary>
public sealed class PoolHog : IDisposable
{
    private readonly ManualResetEventSlim release = new(false);

    public PoolHog()
    {
        ThreadPool.GetMinThreads(out var workers, out _);
        using var running = new CountdownEvent(workers);
        for (var i = 0; i < workers * 4; i++)
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try { running.Signal(); }
                catch (InvalidOperationException) { }
                release.Wait();
            }, null);
        running.Wait(TimeSpan.FromSeconds(5));
    }

    public void Dispose() => release.Set();
}

public class ShowTimeProviderTests
{
    private static readonly TimeSpan Never = Timeout.InfiniteTimeSpan;

    [Fact]
    public void AOneShotFiresOnceAfterItsDueTime()
    {
        using var clock = new ShowTimeProvider("test clock");
        var fired = new List<double>();
        using var done = new ManualResetEventSlim(false);
        var started = Stopwatch.StartNew();
        using var timer = clock.CreateTimer(_ =>
        {
            lock (fired) fired.Add(started.Elapsed.TotalMilliseconds);
            done.Set();
        }, null, TimeSpan.FromMilliseconds(60), Never);
        Assert.True(done.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Thread.Sleep(150);
        lock (fired)
        {
            Assert.Single(fired);
            Assert.True(fired[0] >= 55, $"fired at {fired[0]} ms");
        }
    }

    [Fact]
    public void APeriodicTimerRepeatsUntilDisposed()
    {
        using var clock = new ShowTimeProvider("test clock");
        var count = 0;
        var timer = clock.CreateTimer(_ => Interlocked.Increment(ref count), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
        Thread.Sleep(300);
        timer.Dispose();
        var atDispose = Volatile.Read(ref count);
        Thread.Sleep(150);
        Assert.InRange(atDispose, 5, 20);
        Assert.Equal(atDispose, Volatile.Read(ref count));
    }

    [Fact]
    public void ChangeReschedulesAndInfiniteStops()
    {
        using var clock = new ShowTimeProvider("test clock");
        var count = 0;
        using var timer = clock.CreateTimer(_ => Interlocked.Increment(ref count), null, TimeSpan.FromSeconds(30), Never);
        Assert.True(timer.Change(TimeSpan.FromMilliseconds(10), Never));
        Thread.Sleep(200);
        Assert.Equal(1, Volatile.Read(ref count));
        Assert.True(timer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(10)));
        Thread.Sleep(100);
        Assert.True(timer.Change(Never, Never));
        var stopped = Volatile.Read(ref count);
        Thread.Sleep(100);
        Assert.Equal(stopped, Volatile.Read(ref count));
        Assert.True(stopped > 2);
    }

    [Fact]
    public void ADisposedTimerCanNotBeChanged()
    {
        using var clock = new ShowTimeProvider("test clock");
        var timer = clock.CreateTimer(_ => { }, null, Never, Never);
        timer.Dispose();
        Assert.False(timer.Change(TimeSpan.Zero, Never));
    }

    [Fact]
    public void AThrowingCallbackIsReportedAndKeepsTicking()
    {
        using var clock = new ShowTimeProvider("test clock");
        var failures = 0;
        var calls = 0;
        clock.CallbackFailed += _ => Interlocked.Increment(ref failures);
        using var timer = clock.CreateTimer(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("boom");
        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(15));
        Thread.Sleep(200);
        Assert.True(Volatile.Read(ref calls) >= 3);
        Assert.Equal(Volatile.Read(ref calls), Volatile.Read(ref failures));
    }

    [Fact]
    public void ASlowTickDoesNotCauseABurstAfterwards()
    {
        using var clock = new ShowTimeProvider("test clock");
        var stamps = new List<long>();
        var first = true;
        using var timer = clock.CreateTimer(_ =>
        {
            lock (stamps) stamps.Add(Stopwatch.GetTimestamp());
            if (first)
            {
                first = false;
                Thread.Sleep(200);
            }
        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
        Thread.Sleep(320);
        lock (stamps)
        {
            // About 200 ms lost to the slow tick, then ticks every ~20 ms: not ten at once.
            var gaps = stamps.Zip(stamps.Skip(1), (a, b) => (b - a) * 1000.0 / Stopwatch.Frequency).Skip(1).ToList();
            Assert.True(stamps.Count <= 10, $"{stamps.Count} ticks");
            Assert.DoesNotContain(gaps.Take(3), g => g < 5);
        }
    }

    [Fact]
    public void ReadingsAreTheSystemClock()
    {
        var shared = ShowTimeProvider.Shared;
        Assert.True(Math.Abs((shared.GetUtcNow() - DateTimeOffset.UtcNow).TotalSeconds) < 1);
        var before = Stopwatch.GetTimestamp();
        var reading = shared.GetTimestamp();
        Assert.True(reading >= before && reading - before < Stopwatch.Frequency);
    }
}

public class WorkQueueTests
{
    [Fact]
    public void ItemsRunInOrderOnTheQueuesOwnThread()
    {
        using var queue = new WorkQueue("test queue");
        var seen = new List<int>();
        var threads = new HashSet<int>();
        for (var i = 0; i < 50; i++)
        {
            var n = i;
            queue.Async(() =>
            {
                seen.Add(n);
                threads.Add(Environment.CurrentManagedThreadId);
            });
        }
        queue.Sync(() => { });
        Assert.Equal(Enumerable.Range(0, 50), seen);
        Assert.Single(threads);
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, threads);
    }

    [Fact]
    public void SyncReturnsTheResultAndRunsInlineOnItsOwnThread()
    {
        using var queue = new WorkQueue("test queue");
        Assert.Equal(42, queue.Sync(() => 6 * 7));
        Assert.Equal(7, queue.Sync(() => queue.Sync(() => 7)));
        Assert.True(queue.Sync(() => queue.IsCurrent));
        Assert.False(queue.IsCurrent);
    }

    [Fact]
    public void SyncPassesOnTheItemsException()
    {
        using var queue = new WorkQueue("test queue");
        Assert.Throws<InvalidOperationException>(() => queue.Sync(() => throw new InvalidOperationException("no")));
        Assert.Equal(1, queue.Sync(() => 1));
    }

    [Fact]
    public void SyncGivesUpOnABlockedQueue()
    {
        using var queue = new WorkQueue("test queue");
        using var release = new ManualResetEventSlim(false);
        queue.Async(() => release.Wait());
        Assert.Throws<TimeoutException>(() => queue.Sync(() => 1, TimeSpan.FromMilliseconds(100)));
        release.Set();
    }

    [Fact]
    public void AFailingItemIsReportedAndTheQueueCarriesOn()
    {
        using var queue = new WorkQueue("reporting queue");
        Exception? reported = null;
        void Note(WorkQueue from, Exception error)
        {
            if (from == queue) reported = error;
        }
        WorkQueue.Failed += Note;
        try
        {
            queue.Async(() => throw new FormatException("bad"));
            Assert.Equal(2, queue.Sync(() => 2));
            Assert.IsType<FormatException>(reported);
        }
        finally
        {
            WorkQueue.Failed -= Note;
        }
    }

    [Fact]
    public void AsyncAfterWaitsTheDelay()
    {
        using var queue = new WorkQueue("test queue");
        using var done = new ManualResetEventSlim(false);
        var started = Stopwatch.StartNew();
        double at = 0;
        queue.AsyncAfter(TimeSpan.FromMilliseconds(80), () =>
        {
            at = started.Elapsed.TotalMilliseconds;
            done.Set();
        });
        Assert.True(done.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.True(at >= 75, $"ran at {at} ms");
    }

    [Fact]
    public void AnMtaQueueRunsInTheMta()
    {
        using var queue = new WorkQueue("mta queue", apartment: ApartmentState.MTA);
        Assert.Equal(ApartmentState.MTA, queue.Sync(() => Thread.CurrentThread.GetApartmentState()));
    }

    [Fact]
    public void ADisposedQueueIgnoresNewWorkButFinishesWhatItHas()
    {
        var queue = new WorkQueue("test queue");
        using var gate = new ManualResetEventSlim(false);
        using var ran = new ManualResetEventSlim(false);
        queue.Async(() => gate.Wait());
        queue.Async(ran.Set);
        queue.Dispose();
        queue.Async(() => throw new InvalidOperationException("should never run"));
        gate.Set();
        Assert.True(ran.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }
}

/// <summary>The point of both: they keep working while every thread-pool thread is busy.</summary>
[Collection(nameof(BusyPool))]
public class BusyPoolThreadingTests
{
    [Fact]
    public void TheShowClockKeepsTimeWhileThePoolIsBusy()
    {
        using var hog = new PoolHog();
        using var fired = new ManualResetEventSlim(false);
        var ticks = 0;
        using var timer = ShowTimeProvider.Shared.CreateTimer(_ =>
        {
            if (Interlocked.Increment(ref ticks) == 5) fired.Set();
        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
        Assert.True(fired.Wait(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken), $"{ticks} ticks");
    }

    [Fact]
    public void AWorkQueueRunsWhileThePoolIsBusy()
    {
        using var queue = new WorkQueue("busy-pool queue");
        using var hog = new PoolHog();
        Assert.Equal(3, queue.Sync(() => 3, TimeSpan.FromSeconds(1)));
        using var done = new ManualResetEventSlim(false);
        queue.AsyncAfter(TimeSpan.FromMilliseconds(30), done.Set);
        Assert.True(done.Wait(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }
}
