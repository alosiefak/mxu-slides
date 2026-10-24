using MxU.Slides.Core.Media;

namespace MxU.Slides.VideoIO.Common;

/// <summary>Where a live input is: still opening, looking for its source, receiving, or not.</summary>
public enum VideoInputStatus { Starting, Searching, Receiving, NoSignal, Offline, Failed, Stopped }

/// <summary>
/// A live input's status plus a human detail: the NDI source it found, the error that
/// stopped a camera. Compared by value, so "still receiving from the same source"
/// is not a change.
/// </summary>
public sealed record VideoInputState(VideoInputStatus Status, string Detail = "")
{
    public static readonly VideoInputState Starting = new(VideoInputStatus.Starting);
    public static readonly VideoInputState Searching = new(VideoInputStatus.Searching);
    public static readonly VideoInputState NoSignal = new(VideoInputStatus.NoSignal);
    public static readonly VideoInputState Offline = new(VideoInputStatus.Offline);
    public static readonly VideoInputState Stopped = new(VideoInputStatus.Stopped);

    public static VideoInputState ReceivingFrom(string sourceName) => new(VideoInputStatus.Receiving, sourceName);

    public static VideoInputState OfflineBecause(string detail) => new(VideoInputStatus.Offline, detail);

    public static VideoInputState FailedWith(string message) => new(VideoInputStatus.Failed, message);

    public bool IsReceiving => Status == VideoInputStatus.Receiving;

    public override string ToString() => Detail.Length == 0 ? Status.ToString() : $"{Status}: {Detail}";
}

/// <summary>
/// A live picture the app can put on a Video Input layer: a camera or capture card,
/// an NDI source, or a DeckLink input. Frames are 32-bit BGRA (opaque inputs have
/// alpha 255), rows top to bottom, timestamped on <c>ShowState.HostClock</c>.
/// <para>
/// Two ways to read it. The renderer polls: on each render tick call
/// <see cref="CopyLatestFrame"/> (always safe) or <see cref="LatestFrame"/> and draw it.
/// Or listen to <see cref="FrameArrived"/>, raised on the capture thread for every new
/// frame (return quickly; copy what you keep). Everything here is thread-safe.
/// </para>
/// </summary>
public interface IVideoInputSource : IDisposable
{
    /// <summary>The name the operator knows the input by.</summary>
    string Name { get; }

    VideoInputState State { get; }

    /// <summary>
    /// The newest frame, or null before the first one. Its pixels stay as they are while
    /// it is the newest and for at least two more arrivals (inputs keep a ring of four
    /// buffers), so draw or copy it on the next render tick; don't hold on to it.
    /// </summary>
    VideoFrame? LatestFrame();

    /// <summary>
    /// Copies the newest frame into <paramref name="destination"/> (rows
    /// <paramref name="destinationStride"/> bytes apart) while the capture thread waits,
    /// so the copy can never tear. False when there is no frame yet or it doesn't fit.
    /// </summary>
    bool CopyLatestFrame(Span<byte> destination, int destinationStride, out int width, out int height);

    /// <summary>Frames received since the input started: the renderer can skip a redraw when it hasn't moved.</summary>
    long FramesReceived { get; }

    /// <summary>The input's own frame clock once it has delivered a steady second, else null.</summary>
    LiveCadence? Cadence { get; }

    /// <summary>Seconds since the newest frame arrived, or null if none has.</summary>
    double? FrameAge(double hostTime);

    /// <summary>Raised on the capture thread for each new frame (the same object <see cref="LatestFrame"/> now returns).</summary>
    event EventHandler<VideoFrame>? FrameArrived;

    /// <summary>Raised on whichever thread noticed the change. Marshal to the UI thread before touching controls.</summary>
    event EventHandler<VideoInputState>? StateChanged;

    /// <summary>Stops capturing and lets the device go. The input can't be restarted; make a new one.</summary>
    void Stop();
}

/// <summary>
/// The newest frame of a live input, kept in a ring of <see cref="Depth"/> buffers so the
/// capture thread writes frame n+1 into a different buffer from the one the renderer is
/// reading. One capture thread rents a buffer, fills it, then publishes it; any number of
/// readers read. Port of the "latest CVPixelBuffer" boxes the Mac inputs keep, without
/// CoreVideo's reference counting: reuse is by rotation instead.
/// </summary>
public sealed class LatestFrameStore
{
    public const int Depth = 4;

    private readonly Lock gate = new();
    private readonly byte[]?[] buffers = new byte[Depth][];
    private readonly ArrivalWindow arrivals = new();
    private int latestSlot = -1;
    private int rentedSlot = -1;
    private VideoFrame? latest;
    private long count;
    private double lastArrival = double.NegativeInfinity;

    public VideoFrame? Latest
    {
        get { lock (gate) return latest; }
    }

    public long Count
    {
        get { lock (gate) return count; }
    }

    public LiveCadence? Cadence
    {
        get { lock (gate) return arrivals.Cadence; }
    }

    public double? FrameAge(double hostTime)
    {
        lock (gate) return double.IsNegativeInfinity(lastArrival) ? null : Math.Max(0, hostTime - lastArrival);
    }

    /// <summary>A buffer of at least <paramref name="byteCount"/> bytes to fill with the next frame. Never the newest frame's buffer.</summary>
    public byte[] Rent(int byteCount)
    {
        lock (gate)
        {
            var slot = (latestSlot + 1) % Depth;
            if (buffers[slot] is not { } buffer || buffer.Length < byteCount) buffers[slot] = buffer = new byte[byteCount];
            rentedSlot = slot;
            return buffer;
        }
    }

    /// <summary>Makes the last rented buffer the newest frame.</summary>
    public VideoFrame Publish(int width, int height, int stride, double hostTime)
    {
        lock (gate)
        {
            if (rentedSlot < 0 || buffers[rentedSlot] is not { } buffer) throw new InvalidOperationException("Rent a buffer before publishing it.");
            latest = new VideoFrame(width, height, stride, buffer.AsMemory(0, stride * height), hostTime);
            latestSlot = rentedSlot;
            rentedSlot = -1;
            count++;
            lastArrival = hostTime;
            arrivals.Record(hostTime);
            return latest;
        }
    }

    /// <summary>Rent, copy rows in, optionally force alpha to 255, publish. Rows are packed (stride = width × 4).</summary>
    public VideoFrame PublishCopy(ReadOnlySpan<byte> source, int sourceStride, int width, int height, bool forceOpaque, double hostTime)
    {
        var stride = width * 4;
        var buffer = Rent(stride * height);
        PixelCopy.CopyRows(source, sourceStride, buffer, stride, stride, height);
        if (forceOpaque) PixelCopy.ForceOpaque(buffer, stride, width, height);
        return Publish(width, height, stride, hostTime);
    }

    public bool CopyLatest(Span<byte> destination, int destinationStride, out int width, out int height)
    {
        lock (gate)
        {
            width = latest?.Width ?? 0;
            height = latest?.Height ?? 0;
            if (latest is null || destinationStride < width * 4 || destination.Length < destinationStride * (height - 1) + width * 4) return false;
            PixelCopy.CopyRows(latest.Pixels.Span, latest.Stride, destination, destinationStride, width * 4, height);
            return true;
        }
    }

    /// <summary>Forgets the newest frame (a stopped input shows nothing rather than a frozen picture).</summary>
    public void Clear()
    {
        lock (gate) latest = null;
    }
}

/// <summary>
/// The shared half of every input: the frame ring, the state with its change event,
/// and the frame event. Subclasses capture and call <see cref="Deliver"/> /
/// <see cref="SetState"/>.
/// </summary>
public abstract class VideoInputSourceBase(string name, VideoInputState initial) : IVideoInputSource
{
    private readonly Lock stateGate = new();
    private VideoInputState state = initial;

    protected LatestFrameStore Frames { get; } = new();

    public string Name { get; } = name;

    public VideoInputState State
    {
        get { lock (stateGate) return state; }
    }

    public long FramesReceived => Frames.Count;

    public LiveCadence? Cadence => Frames.Cadence;

    public event EventHandler<VideoFrame>? FrameArrived;

    public event EventHandler<VideoInputState>? StateChanged;

    public VideoFrame? LatestFrame() => Frames.Latest;

    public bool CopyLatestFrame(Span<byte> destination, int destinationStride, out int width, out int height) =>
        Frames.CopyLatest(destination, destinationStride, out width, out height);

    public double? FrameAge(double hostTime) => Frames.FrameAge(hostTime);

    public abstract void Stop();

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    /// <summary>Changes the state; listeners hear about it only if it really changed.</summary>
    protected void SetState(VideoInputState next)
    {
        lock (stateGate)
        {
            if (state == next) return;
            state = next;
        }
        try { StateChanged?.Invoke(this, next); }
        catch (Exception ex) { VideoIOLog.Note("input", $"{Name}: a state listener failed: {ex.Message}"); }
    }

    /// <summary>Changes the state only while it still is <paramref name="expected"/> (so a stop isn't undone by a late frame).</summary>
    protected bool SetStateIf(Func<VideoInputState, bool> expected, VideoInputState next)
    {
        lock (stateGate)
        {
            if (!expected(state) || state == next) return false;
            state = next;
        }
        try { StateChanged?.Invoke(this, next); }
        catch (Exception ex) { VideoIOLog.Note("input", $"{Name}: a state listener failed: {ex.Message}"); }
        return true;
    }

    protected void Deliver(VideoFrame frame)
    {
        try { FrameArrived?.Invoke(this, frame); }
        catch (Exception ex) { VideoIOLog.Note("input", $"{Name}: a frame listener failed: {ex.Message}"); }
    }
}
