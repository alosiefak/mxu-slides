using System.Buffers;
using System.Threading.Channels;
using MxU.Slides.Core.Media;
using MxU.Slides.VideoIO.Common;

namespace MxU.Slides.VideoIO.Ndi;

/// <summary>Where an NDI output is: off, opening, on the network, or refused (the message says why).</summary>
public enum NdiOutputState { Idle, Starting, Sending, Failed }

/// <summary>An NDI output's state plus its detail: the source name while sending, the reason when it failed.</summary>
public sealed record NdiOutputStatus(NdiOutputState State, string Detail = "")
{
    public static readonly NdiOutputStatus Idle = new(NdiOutputState.Idle);

    public bool IsActive => State is NdiOutputState.Starting or NdiOutputState.Sending;
}

/// <summary>
/// The program as an NDI source: add it to the program feed and every frame and audio
/// slice goes out on the network. <see cref="WriteVideo"/> and <see cref="WriteAudio"/>
/// only copy the data and queue it for a worker thread that does the sending, so the
/// program feed never waits for NDI. At most two frames are ever queued; past that a
/// frame is dropped and counted (the Mac's "two in flight" rule).
/// When the NDI runtime isn't installed, <see cref="Start"/> leaves the status Failed
/// with "NDI runtime not installed — install NDI Tools from ndi.video" and writes are
/// ignored. Port of the Mac's NDIOutputController plus the NDIHelper's sending half.
/// </summary>
public sealed class NdiOutput : IProgramSink, IDisposable
{
    private const int MaxFramesInFlight = 2;

    private readonly Lock gate = new();
    private readonly Func<(NdiLibrary? Library, NdiError? Error)> loader;
    private readonly Stack<byte[]> spareFrames = new();
    private Channel<Work>? queue;
    private Thread? worker;
    private NdiSender? sender;
    private NdiOutputStatus status = NdiOutputStatus.Idle;
    private int framesInFlight;
    private long framesDropped;
    private long audioDropped;

    /// <param name="sourceName">The NDI name, e.g. "MxU Program". Receivers see "PC-NAME (MxU Program)".</param>
    /// <param name="frameRateNumerator">With the denominator, the frame rate announced to receivers: 30000/1000 = 30 fps.</param>
    public NdiOutput(string sourceName, int frameRateNumerator = 30_000, int frameRateDenominator = 1_000)
        : this(sourceName, frameRateNumerator, frameRateDenominator, () =>
            NdiLibrary.TryLoad(out var library, out var error) ? (library, null) : (null, error))
    {
    }

    internal NdiOutput(string sourceName, int frameRateNumerator, int frameRateDenominator, Func<(NdiLibrary?, NdiError?)> loader)
    {
        SourceName = sourceName;
        FrameRateNumerator = frameRateNumerator;
        FrameRateDenominator = frameRateDenominator;
        this.loader = loader;
    }

    public string SourceName { get; }
    public int FrameRateNumerator { get; }
    public int FrameRateDenominator { get; }

    public NdiOutputStatus Status
    {
        get { lock (gate) return status; }
    }

    /// <summary>Raised on the thread that changed it (the caller of Start/Stop).</summary>
    public event Action<NdiOutputStatus>? StatusChanged;

    public long FramesSent => sender?.Sent ?? 0;
    public long FramesDropped => Interlocked.Read(ref framesDropped);
    public long AudioDropped => Interlocked.Read(ref audioDropped);

    /// <summary>Creates the NDI source. Safe to call again (restarts it).</summary>
    public void Start()
    {
        Stop();
        SetStatus(new NdiOutputStatus(NdiOutputState.Starting));
        var (library, error) = loader();
        if (library is null)
        {
            var message = error?.Message ?? NdiError.NotInstalledMessage;
            VideoIOLog.Note("ndi.output", $"{SourceName}: {message}");
            SetStatus(new NdiOutputStatus(NdiOutputState.Failed, message));
            return;
        }
        var created = NdiSender.Create(library, SourceName);
        if (created is null)
        {
            SetStatus(new NdiOutputStatus(NdiOutputState.Failed, "NDI sender could not be created"));
            return;
        }
        var work = Channel.CreateBounded<Work>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        var thread = new Thread(() => Send(work.Reader, created)) { IsBackground = true, Name = $"NDI output {SourceName}", Priority = ThreadPriority.AboveNormal };
        lock (gate)
        {
            sender = created;
            queue = work;
            worker = thread;
            framesInFlight = 0;
        }
        thread.Start();
        VideoIOLog.Note("ndi.output", $"{SourceName}: sending");
        SetStatus(new NdiOutputStatus(NdiOutputState.Sending, SourceName));
    }

    /// <summary>Takes the source off the network. Frames still queued are sent first (at most two).</summary>
    public void Stop()
    {
        Channel<Work>? stopping;
        Thread? thread;
        NdiSender? closing;
        lock (gate)
        {
            stopping = queue;
            thread = worker;
            closing = sender;
            queue = null;
            worker = null;
            sender = null;
        }
        stopping?.Writer.TryComplete();
        thread?.Join(TimeSpan.FromSeconds(2));
        closing?.Dispose();
        if (Status.State != NdiOutputState.Idle) SetStatus(NdiOutputStatus.Idle);
    }

    public void WriteVideo(VideoFrame frame)
    {
        Channel<Work>? work;
        lock (gate)
        {
            work = queue;
            if (work is null) return;
            if (framesInFlight >= MaxFramesInFlight)
            {
                framesDropped++;
                return;
            }
            framesInFlight++;
        }
        var bytes = frame.Stride * frame.Height;
        byte[] copy;
        lock (spareFrames) copy = spareFrames.Count > 0 && spareFrames.Peek().Length >= bytes ? spareFrames.Pop() : new byte[bytes];
        frame.Pixels.Span[..Math.Min(bytes, frame.Pixels.Length)].CopyTo(copy);
        if (!work.Writer.TryWrite(new Work(copy, frame.Width, frame.Height, frame.Stride, null, 0, 0, 0)))
        {
            lock (gate) framesInFlight--;
            Interlocked.Increment(ref framesDropped);
        }
    }

    public void WriteAudio(AudioBuffer buffer)
    {
        Channel<Work>? work;
        lock (gate) work = queue;
        if (work is null || buffer.FrameCount == 0) return;
        var count = buffer.FrameCount * buffer.Channels;
        var samples = ArrayPool<float>.Shared.Rent(count);
        buffer.Samples.Span[..count].CopyTo(samples);
        if (!work.Writer.TryWrite(new Work(null, 0, 0, 0, samples, count, buffer.Channels, buffer.SampleRate)))
        {
            ArrayPool<float>.Shared.Return(samples);
            Interlocked.Increment(ref audioDropped);
        }
    }

    public void Dispose() => Stop();

    private void Send(ChannelReader<Work> reader, NdiSender target)
    {
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var item))
            {
                try
                {
                    if (item.Pixels is { } pixels)
                    {
                        target.Send(pixels, item.Width, item.Height, item.Stride, FrameRateNumerator, FrameRateDenominator);
                        lock (spareFrames) if (spareFrames.Count < MaxFramesInFlight + 1) spareFrames.Push(pixels);
                        lock (gate) framesInFlight = Math.Max(0, framesInFlight - 1);
                    }
                    else if (item.Samples is { } samples)
                    {
                        target.SendAudio(new AudioBuffer(item.SampleRate, item.Channels, samples.AsMemory(0, item.SampleCount), 0));
                        ArrayPool<float>.Shared.Return(samples);
                    }
                }
                catch (Exception ex)
                {
                    VideoIOLog.Note("ndi.output", $"{SourceName}: send failed: {ex.Message}");
                }
            }
        }
    }

    private void SetStatus(NdiOutputStatus next)
    {
        lock (gate)
        {
            if (status == next) return;
            status = next;
        }
        StatusChanged?.Invoke(next);
    }

    private readonly record struct Work(byte[]? Pixels, int Width, int Height, int Stride, float[]? Samples, int SampleCount, int Channels, int SampleRate);
}
