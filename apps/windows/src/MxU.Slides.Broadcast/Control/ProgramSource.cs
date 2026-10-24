using MxU.Slides.Core.Media;

namespace MxU.Slides.Broadcast.Control;

/// <summary>
/// Where program frames and audio come from: something sinks can be attached to. The WPF
/// app's ProgramFeed has exactly these two methods (Add and Remove), so it can implement this
/// directly or be wrapped with <see cref="From"/>.
/// </summary>
public interface IProgramSource
{
    void Add(IProgramSink sink);

    void Remove(IProgramSink sink);
}

public static class ProgramSource
{
    /// <summary>Wraps any add/remove pair, e.g. <c>ProgramSource.From(feed.Add, feed.Remove)</c>.</summary>
    public static IProgramSource From(Action<IProgramSink> add, Action<IProgramSink> remove) => new Delegated(add, remove);

    /// <summary>Attaches <paramref name="sink"/> and returns a handle that detaches it when disposed.</summary>
    public static IDisposable Attach(this IProgramSource source, IProgramSink sink)
    {
        source.Add(sink);
        return new Attachment(source, sink);
    }

    private sealed class Delegated(Action<IProgramSink> add, Action<IProgramSink> remove) : IProgramSource
    {
        public void Add(IProgramSink sink) => add(sink);

        public void Remove(IProgramSink sink) => remove(sink);
    }

    private sealed class Attachment(IProgramSource source, IProgramSink sink) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) source.Remove(sink);
        }
    }
}

/// <summary>
/// Passes frames on at most <paramref name="frameRate"/> per second (audio always), so a
/// 30 fps stream can listen to a 60 fps program feed. A frame is let through once at least
/// 95% of an interval has passed; after a pause the cadence restarts from the next frame. The
/// Mac renders each destination at its own rate (OutputMirror); Windows has one program feed,
/// so each destination thins it out instead. Called on the feed's threads; keeps no locks.
/// </summary>
public sealed class FramePacer(IProgramSink inner, int frameRate) : IProgramSink
{
    private readonly double interval = 1.0 / Math.Clamp(frameRate, 1, 240);
    private double last = double.NegativeInfinity;

    public IProgramSink Inner { get; } = inner;

    public void WriteVideo(VideoFrame frame)
    {
        var now = frame.HostTime;
        if (now - last < interval * 0.95) return;
        last = now - last > interval * 2 ? now : last + interval;
        Inner.WriteVideo(frame);
    }

    public void WriteAudio(AudioBuffer buffer) => Inner.WriteAudio(buffer);
}
