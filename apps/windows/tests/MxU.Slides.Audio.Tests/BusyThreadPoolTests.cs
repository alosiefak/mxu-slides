using MxU.Slides.Core.Automation;
using MxU.Slides.Audio.Multitrack;

namespace MxU.Slides.Audio.Tests;

[CollectionDefinition(nameof(BusyThreadPool), DisableParallelization = true)]
public sealed class BusyThreadPool;

/// <summary>
/// The engines must not wait for .NET's shared thread pool: a playlist moving on to its next
/// song, or the next song's stems loading, can't depend on spare pool threads (an import, a
/// slow disk or a busy CI machine can tie them all up). Each test ties up every pool thread
/// first, then checks the engine still does its job on time. Run on their own, as they
/// starve the whole test process while they run.
/// </summary>
[Collection(nameof(BusyThreadPool))]
public sealed class BusyThreadPoolTests
{
    private static readonly AudioOutputDevice Unplugged = new("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}", "Unplugged Interface", 2);

    /// <summary>Blocks every pool thread (and the ones it adds) until disposed.</summary>
    private sealed class PoolHog : IDisposable
    {
        private readonly ManualResetEventSlim release = new(false);
        private readonly ManualResetEventSlim busy = new(false);
        private int toStart;

        public PoolHog()
        {
            ThreadPool.GetMinThreads(out var workers, out _);
            var count = workers * 4;
            toStart = workers;
            for (var i = 0; i < count; i++)
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    // Exactly one job sees the count reach zero. (Checking IsSet and then signalling a
                    // CountdownEvent let two jobs signal its last count, and the second threw on a pool
                    // thread, which takes the whole test process down: it did on a CI machine.)
                    if (Interlocked.Decrement(ref toStart) == 0) busy.Set();
                    release.Wait();
                }, null);
            busy.Wait(TimeSpan.FromSeconds(5));
        }

        public void Dispose() => release.Set();
    }

    [Fact]
    public void ATrackStillEndsOnTimeWhenThePoolIsBusy()
    {
        using var engine = new AudioEngine { MasterVolume = 0 };
        engine.SetOutputDevice(Unplugged, channelOffset: 0);
        var ended = new ManualResetEventSlim(false);
        engine.TrackPlayedToEnd += () => ended.Set();
        using (new PoolHog())
        {
            engine.Play(TestAudio.Tone("busy-pool", 0.3, 440));
            Assert.True(ended.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken), "the end of the track is noticed without a free pool thread");
        }
    }

    [Fact]
    public void StemsStillPreloadWhenThePoolIsBusy()
    {
        using var engine = new MultitrackEngine(48_000, 1);
        var song = new MultitrackSong("busy", [MultitrackStems.Make(TestAudio.Tone("busy-stem", 0.5, 330), "a")]);
        using (new PoolHog())
        {
            engine.Preload([song]);
            Assert.True(TestAudio.WaitFor(() => engine.PreloadedSongIds.Contains("busy"), 3), "the song preloads without a free pool thread");
        }
    }
}
