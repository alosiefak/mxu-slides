using MxU.Slides.Core.Motion;

namespace MxU.Slides.Core.Tests.Motion;

/// <summary>Frame timing, the loop noise, and the thread crew the renderer runs on.</summary>
public sealed class LoopTests
{
    // MARK: Frame timing

    [Fact]
    public void ALoopHasSecondsTimesFrameRateFrames()
    {
        Assert.Equal(300, LoopTiming.FrameCount(10, 30));
        Assert.Equal(1800, LoopTiming.FrameCount(30, 60));
        Assert.Equal(600, new MotionRecipe { Seconds = 20, FrameRate = 30 }.FrameCount);
    }

    [Fact]
    public void FrameIIsAtIOverTheFrameCountAndWraps()
    {
        Assert.Equal(0, LoopTiming.Phase(0, 300));
        Assert.Equal(0.5, LoopTiming.Phase(150, 300));
        Assert.Equal(0, LoopTiming.Phase(300, 300));
        Assert.Equal(LoopTiming.Phase(1, 300), LoopTiming.Phase(301, 300));
        Assert.Equal(LoopTiming.Phase(299, 300), LoopTiming.Phase(-1, 300));
    }

    [Fact]
    public void TimeStampsAreRoundedToTheNearestTickAndNeverDrift()
    {
        Assert.Equal([0L, 333_333L, 666_667L, 1_000_000L], Enumerable.Range(0, 4).Select(i => LoopTiming.TimeHns(i, 30)));
        Assert.Equal([0L, 166_667L, 333_333L, 500_000L], Enumerable.Range(0, 4).Select(i => LoopTiming.TimeHns(i, 60)));
        foreach (var (seconds, rate) in new[] { (10, 30), (30, 60), (17, 60) })
        {
            var frames = LoopTiming.FrameCount(seconds, rate);
            // Every frame lasts a frame (to within a tick), and together they last exactly the loop.
            Assert.All(Enumerable.Range(0, frames), i => Assert.InRange(LoopTiming.DurationHns(i, rate) - 10_000_000.0 / rate, -1, 1));
            Assert.Equal(seconds * LoopTiming.TicksPerSecond, Enumerable.Range(0, frames).Sum(i => LoopTiming.DurationHns(i, rate)));
            Assert.Equal(seconds * LoopTiming.TicksPerSecond, LoopTiming.TimeHns(frames, rate));
        }
    }

    [Fact]
    public void TheBitrateIsGenerousButCapped()
    {
        Assert.InRange(LoopTiming.Bitrate(1920, 1080, 30), 12_000_000, 13_000_000);
        Assert.Equal(60_000_000, LoopTiming.Bitrate(3840, 2160, 60));
        Assert.Equal(2_000_000, LoopTiming.Bitrate(160, 90, 30));
    }

    // MARK: Noise

    [Fact]
    public void SimplexStaysInRange()
    {
        var values = Samples((x, y, z, w) => LoopNoise.Simplex(x, y, z, w));
        Assert.InRange(values.Max(v => Math.Abs(v)), 0.5, 1.05);
        Assert.InRange(values.Average(), -0.02, 0.02);
    }

    [Fact]
    public void NoiseIsSpreadAsDocumented()
    {
        var clock = new LoopClock(0.3);
        foreach (var values in new[]
                 {
                     Samples((x, y, _, _) => LoopNoise.Loop(x, y, clock, 0.4f)),
                     Samples((x, y, _, _) => LoopNoise.Fbm(x, y, clock, 0.4f, 2)),
                     Samples((x, y, _, _) => LoopNoise.Fbm(x, y, clock, 0.4f, 5)),
                 })
        {
            var spread = Math.Sqrt(values.Average(v => v * v));
            Assert.True(spread is > 0.2 and < 0.45, $"spread {spread:0.000}");
            Assert.InRange(values.Average(), -0.03, 0.03);
            Assert.True(values.Count(v => Math.Abs(v) < 0.8) > values.Count * 0.98);
        }
    }

    [Fact]
    public void LoopNoiseIsBackWhereItStartedAfterOneLoop()
    {
        for (var i = 0; i < 50; i++)
        {
            float x = i * 0.37f, y = i * 0.11f;
            Assert.Equal(LoopNoise.Loop(x, y, new LoopClock(0), 0.6f), LoopNoise.Loop(x, y, new LoopClock(1), 0.6f));
            Assert.Equal(LoopNoise.Fbm(x, y, new LoopClock(0.25), 0.6f, 4), LoopNoise.Fbm(x, y, new LoopClock(1.25), 0.6f, 4));
        }
        Assert.NotEqual(LoopNoise.Loop(1.3f, 2.1f, new LoopClock(0), 0.6f), LoopNoise.Loop(1.3f, 2.1f, new LoopClock(0.5), 0.6f));
    }

    [Fact]
    public void NoiseChangesSmoothly()
    {
        var clock = new LoopClock(0.1);
        for (var i = 0; i < 200; i++)
        {
            var x = i * 0.013f;
            Assert.True(Math.Abs(LoopNoise.Fbm(x, 0.4f, clock, 0.3f, 4) - LoopNoise.Fbm(x + 0.001f, 0.4f, clock, 0.3f, 4)) < 0.03);
        }
    }

    [Fact]
    public void TheHashIsTheSameOnEveryRun()
    {
        // Fixed numbers: a change here would change every motion background ever made.
        Assert.Equal(1_753_845_952u, LoopNoise.Hash(1u));
        Assert.Equal(LoopNoise.Hash(7u, 3u), LoopNoise.Hash(7u, 3u));
        Assert.NotEqual(LoopNoise.Hash(7u, 3u), LoopNoise.Hash(3u, 7u));
        Assert.InRange(LoopNoise.Unit(5, 1, 2), 0, 0.999_999f);
    }

    [Fact]
    public void DriftCopiesTakeTurnsAndComeBack()
    {
        foreach (var rounds in new[] { 1, 2, 3 })
        {
            var start = LoopNoise.Drift(new LoopClock(0), rounds);
            var end = LoopNoise.Drift(new LoopClock(1), rounds);
            Assert.Equal(start, end);
            for (var p = 0.0; p < 1; p += 0.01)
            {
                var (a, wa, b, wb) = LoopNoise.Drift(new LoopClock(p), rounds);
                Assert.Equal(1, wa + wb, 4);
                // A copy only jumps back (offset ½ to −½) while it's invisible.
                if (Math.Abs(a) > 0.49) Assert.True(wa < 0.03);
                if (Math.Abs(b) > 0.49) Assert.True(wb < 0.03);
            }
        }
    }

    private static List<float> Samples(Func<float, float, float, float, float> noise)
    {
        var values = new List<float>(40_000);
        for (var i = 0; i < 40_000; i++)
        {
            var h = LoopNoise.Hash((uint)i);
            values.Add(noise(LoopNoise.ToUnit(h) * 90, LoopNoise.ToUnit(LoopNoise.Hash(h)) * 90, LoopNoise.ToUnit(LoopNoise.Hash(h, 1)) * 90, LoopNoise.ToUnit(LoopNoise.Hash(h, 2)) * 90));
        }
        return values;
    }

    // MARK: The crew

    [Fact]
    public void TheCrewDoesEveryItemOnceOnItsOwnThreads()
    {
        using var crew = new RenderCrew("test", 3);
        var done = new int[500];
        var pooled = 0;
        var threads = new System.Collections.Concurrent.ConcurrentDictionary<int, ThreadPriority>();
        crew.For(done.Length, (item, worker) =>
        {
            Interlocked.Increment(ref done[item]);
            // Item work by the caller (worker 0) runs on whatever thread called For; the helpers are the crew's own.
            if (worker > 0 && Thread.CurrentThread.IsThreadPoolThread) Interlocked.Increment(ref pooled);
            if (worker > 0) threads[Environment.CurrentManagedThreadId] = Thread.CurrentThread.Priority;
            Assert.InRange(worker, 0, crew.Size - 1);
            Thread.SpinWait(2000);
        });
        Assert.All(done, count => Assert.Equal(1, count));
        Assert.Equal(0, pooled);
        // The helpers stand back for the show's threads.
        Assert.All(threads.Values, priority => Assert.Equal(ThreadPriority.BelowNormal, priority));
    }

    [Fact]
    public void TheCrewPassesOnAFailureAndKeepsWorking()
    {
        using var crew = new RenderCrew("test", 2);
        var error = Assert.Throws<AggregateException>(() => crew.For(100, (item, _) =>
        {
            if (item == 37) throw new InvalidOperationException("item 37");
        }));
        Assert.Equal("item 37", error.InnerException?.Message);
        var count = 0;
        crew.For(10, (_, _) => Interlocked.Increment(ref count));
        Assert.Equal(10, count);
    }

    [Fact]
    public void ACrewOfOneIsJustTheCaller()
    {
        using var crew = new RenderCrew("alone", 0);
        var callers = new HashSet<int>();
        crew.For(20, (_, worker) =>
        {
            lock (callers) callers.Add(Environment.CurrentManagedThreadId);
            Assert.Equal(0, worker);
        });
        Assert.Equal([Environment.CurrentManagedThreadId], callers);
    }
}
