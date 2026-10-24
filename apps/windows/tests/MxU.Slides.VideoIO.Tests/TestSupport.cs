using MxU.Slides.Core.Media;
using MxU.Slides.VideoIO.Common;

namespace MxU.Slides.VideoIO.Tests;

/// <summary>NDI tests share one runtime (and its config variable), so they run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NdiRuntimeCollection
{
    public const string Name = "NDI runtime";
}

internal static class TestSupport
{
    /// <summary>Waits up to <paramref name="seconds"/> for <paramref name="condition"/>, checking every 20 ms.</summary>
    public static bool Eventually(Func<bool> condition, double seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    /// <summary>A packed BGRA frame filled with one colour.</summary>
    public static VideoFrame Solid(int width, int height, byte blue, byte green, byte red, byte alpha = 255, double hostTime = 0)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = blue;
            pixels[i + 1] = green;
            pixels[i + 2] = red;
            pixels[i + 3] = alpha;
        }
        return new VideoFrame(width, height, width * 4, pixels, hostTime);
    }

    /// <summary>A temporary folder that deletes itself.</summary>
    public sealed class ScratchFolder : IDisposable
    {
        public ScratchFolder() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mxu-videoio-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}

/// <summary>A live input the tests drive by hand: push frames, set states, see whether it was stopped.</summary>
internal sealed class FakeSource(string name, VideoInputState? initial = null) : VideoInputSourceBase(name, initial ?? VideoInputState.Starting)
{
    public bool Stopped { get; private set; }

    public VideoFrame Push(byte fill, double hostTime, int width = 4, int height = 4)
    {
        var frame = TestSupport.Solid(width, height, fill, fill, fill, 255, hostTime);
        var published = Frames.PublishCopy(frame.Pixels.Span, frame.Stride, width, height, forceOpaque: false, hostTime);
        Deliver(published);
        return published;
    }

    public void Become(VideoInputState state) => SetState(state);

    public override void Stop()
    {
        Stopped = true;
        SetState(VideoInputState.Stopped);
    }
}
