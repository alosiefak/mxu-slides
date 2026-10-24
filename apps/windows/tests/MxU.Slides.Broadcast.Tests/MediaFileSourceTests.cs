using MxU.Slides.Broadcast.Media;
using MxU.Slides.Core.Media;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>Port of MediaFileSourceTests.swift, plus the Windows promises about sound and in/out points.</summary>
public sealed class MediaFileSourceTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "MediaFileSource-" + Guid.NewGuid().ToString("N"));

    public MediaFileSourceTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Ending
    {
        private readonly TaskCompletionSource<string?> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public void Report(string? reason)
        {
            Interlocked.Increment(ref Calls);
            ended.TrySetResult(reason);
        }

        public Task<string?> Task => ended.Task;
    }

    [Fact]
    public async Task PumpsEveryFrameThenEndsNaturally()
    {
        var path = await TestMovies.WriteAsync(Path.Combine(scratch, "eight.mp4"), frames: 8);
        var ending = new Ending();
        var sizes = new List<(int, int)>();
        var source = new MediaFileSource(path, null, null,
            frame => { lock (sizes) sizes.Add((frame.Width, frame.Height)); }, _ => { }, ending.Report);
        source.Start();

        var reason = await ending.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Null(reason);
        Assert.Equal(8, source.FramesDelivered);
        Assert.Equal(0, source.FramesDropped);
        Assert.All(sizes, size => Assert.Equal((160, 96), size));
        Assert.Equal(1, ending.Calls);
    }

    [Fact]
    public async Task UnreadableFileEndsWithReason()
    {
        var path = Path.Combine(scratch, "junk.mp4");
        await File.WriteAllTextAsync(path, "not a movie", Ct);
        var ending = new Ending();
        var source = new MediaFileSource(path, null, null, _ => { }, _ => { }, ending.Report);
        source.Start();
        var reason = await ending.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public async Task StopNeverReportsAnEnd()
    {
        var path = await TestMovies.WriteAsync(Path.Combine(scratch, "long.mp4"), frames: 90);
        var ending = new Ending();
        var source = new MediaFileSource(path, null, null, _ => { }, _ => { }, ending.Report);
        source.Start();
        await Task.Delay(300, Ct);
        source.Stop();
        await Task.Delay(500, Ct);
        Assert.Equal(0, ending.Calls);
        // (Opening the decoders can take a while on a busy PC, so maybe nothing was shown yet; nothing more comes after Stop.)
        var stopped = source.FramesDelivered;
        await Task.Delay(300, Ct);
        Assert.Equal(stopped, source.FramesDelivered);
        Assert.InRange(stopped, 0, 30);
    }

    [Fact]
    public async Task SoundComesOutAsStereoAlongsideThePictures()
    {
        var path = await TestMovies.WriteAsync(Path.Combine(scratch, "tone.mp4"), frames: 30, withAudio: true);
        var ending = new Ending();
        var buffers = new List<(int Rate, int Channels, float Peak, double Host)>();
        var firstFrame = double.NaN;
        var source = new MediaFileSource(path, null, null,
            frame => { if (double.IsNaN(firstFrame)) firstFrame = frame.HostTime; },
            buffer =>
            {
                var peak = 0f;
                foreach (var value in buffer.Samples.Span) peak = Math.Max(peak, Math.Abs(value));
                lock (buffers) buffers.Add((buffer.SampleRate, buffer.Channels, peak, buffer.HostTime));
            },
            ending.Report);
        source.Start();
        Assert.Null(await ending.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await Task.Delay(100, Ct);
        lock (buffers)
        {
            Assert.NotEmpty(buffers);
            Assert.All(buffers, b => Assert.Equal(2, b.Channels));
            Assert.Equal(48_000, buffers[0].Rate);
            Assert.True(buffers.Max(b => b.Peak) > 0.3f, "the tone is heard");
            // The sound is stamped on the same clock as the pictures, starting with them.
            Assert.InRange(buffers[0].Host - firstFrame, -0.2, 0.2);
        }
    }

    [Fact]
    public async Task InAndOutPointsTrimWhatPlays()
    {
        var path = await TestMovies.WriteAsync(Path.Combine(scratch, "two-seconds.mp4"), frames: 60);
        var ending = new Ending();
        var source = new MediaFileSource(path, 0.5, 1.0, _ => { }, _ => { }, ending.Report);
        var started = DateTime.UtcNow;
        source.Start();
        Assert.Null(await ending.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        var took = DateTime.UtcNow - started;
        Assert.InRange(source.FramesDelivered, 13, 17);
        Assert.True(took < TimeSpan.FromSeconds(1.5), $"half a second of video took {took.TotalSeconds:0.00}s");
    }
}
