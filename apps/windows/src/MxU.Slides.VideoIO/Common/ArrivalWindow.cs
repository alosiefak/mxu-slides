namespace MxU.Slides.VideoIO.Common;

/// <summary>
/// How regularly a live input is delivering: when its newest frame arrived and the
/// average time between frames, both on <c>ShowState.HostClock</c> seconds. The
/// renderer can use it to pace itself to a capture card instead of guessing.
/// Port of RenderEngine/LiveCadence.swift.
/// </summary>
public readonly record struct LiveCadence(double LastArrival, double Period);

/// <summary>
/// Remembers when the last couple of seconds of frames arrived and turns that into a
/// <see cref="LiveCadence"/>. Delivery jitter averages out because the period is taken
/// across the whole window, not between the last two frames. A gap of more than half
/// a second (the signal dropped) starts the window again. Not thread-safe: the input
/// that owns it locks around it. Port of MediaEngine/ArrivalWindow.swift.
/// </summary>
public sealed class ArrivalWindow
{
    /// <summary>The most arrivals remembered (four seconds at 30 fps).</summary>
    internal const int Capacity = 120;

    /// <summary>No cadence is reported until this many frames (about a second) have arrived.</summary>
    internal const int MinimumCount = 30;

    /// <summary>A pause longer than this many seconds restarts the window.</summary>
    internal const double Gap = 0.5;

    private readonly Queue<double> times = new(Capacity + 1);
    private double last = double.NaN;

    /// <summary>Notes a frame arriving at <paramref name="now"/> (host seconds).</summary>
    public void Record(double now)
    {
        if (!double.IsNaN(last) && now - last > Gap) times.Clear();
        times.Enqueue(now);
        last = now;
        while (times.Count > Capacity) times.Dequeue();
    }

    /// <summary>The cadence, or null until a second of steady frames has arrived.</summary>
    public LiveCadence? Cadence
    {
        get
        {
            if (times.Count < MinimumCount) return null;
            var first = times.Peek();
            return new LiveCadence(last, (last - first) / (times.Count - 1));
        }
    }
}
