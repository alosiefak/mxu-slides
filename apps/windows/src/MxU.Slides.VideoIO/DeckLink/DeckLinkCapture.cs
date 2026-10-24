using MxU.Slides.Core.Media;
using MxU.Slides.Core.Scene;
using MxU.Slides.VideoIO.Common;

namespace MxU.Slides.VideoIO.DeckLink;

/// <summary>Opens card capture; the real one is <see cref="DeckLinkInput.TryOpen"/>, tests pass a fake.</summary>
internal delegate IDisposable? DeckLinkInputOpener(long persistentId, int audioChannels, DeckLinkFrameHandler onFrame,
    Action<DeckLinkInputFormat> onFormat, out string error);

/// <summary>
/// A DeckLink input as a live input, kept alive: frames are converted from the card's
/// UYVY to BGRA (deinterlacing interlaced signals) into the latest-frame ring, embedded
/// audio goes to any audio sinks, and a failed or silent card is reopened. A failed open
/// retries every two seconds (noting each distinct error once); an open card that sends
/// nothing for three seconds is reopened (call <see cref="CheckLiveness"/> about once a
/// second). States: Starting, Receiving, NoSignal (card open, nothing plugged in), Failed
/// (detail: why; it is retrying). Shared per card through <see cref="DeckLinkCaptureHub"/>,
/// because the picture and the audio of one card are used by different parts of the app.
/// Port of the Mac's DeckLinkDirectCapture (Sources/DeckLinkDirectInputs.swift) plus the
/// helper's DeckLinkInputRelay, in process.
/// </summary>
public sealed class DeckLinkCapture : VideoInputSourceBase
{
    internal const double RetrySeconds = 2;

    private readonly Lock gate = new();
    private readonly DeckLinkInputOpener opener;
    private readonly DirectInputLiveness liveness = new();
    private readonly DeckLinkFrameConverter converter = new();
    private readonly Dictionary<Guid, Action<AudioBuffer>> audioSinks = [];
    private readonly int audioChannels;
    private readonly TimeSpan retryDelay;
    private IDisposable? input;
    private int generation;
    private bool? lastSignal;
    private float[] audioScratch = [];

    /// <param name="audioChannels">
    /// Embedded channels to capture. 0 (the default) asks the card when it opens, through
    /// <see cref="DeckLinkInput.AudioChannels"/>: 16, 8 or 2, the most it offers.
    /// </param>
    public DeckLinkCapture(long persistentId, string name, int audioChannels = 0)
        : this(persistentId, name, audioChannels, (long id, int channels, DeckLinkFrameHandler frames, Action<DeckLinkInputFormat> formats, out string error) =>
            DeckLinkInput.TryOpen(id, channels, frames, formats, out error))
    {
    }

    internal DeckLinkCapture(long persistentId, string name, int audioChannels, DeckLinkInputOpener opener, TimeSpan? retryDelay = null)
        : base(name, VideoInputState.Starting)
    {
        PersistentId = persistentId;
        this.audioChannels = audioChannels;
        this.opener = opener;
        this.retryDelay = retryDelay ?? TimeSpan.FromSeconds(RetrySeconds);
    }

    public long PersistentId { get; }

    /// <summary>True while a frame arrived in the last two seconds.</summary>
    public bool IsReceiving(double hostTime) => FrameAge(hostTime) is < 2;

    /// <summary>Starts capturing; the card is opened on a worker thread, so this returns at once.</summary>
    public void Start()
    {
        lock (gate) liveness.Running = true;
        Task.Run(Open);
    }

    public override void Stop()
    {
        IDisposable? closing;
        lock (gate)
        {
            liveness.Stopped();
            generation++;
            closing = input;
            input = null;
        }
        Frames.Clear();
        SetState(VideoInputState.Stopped);
        // Closing waits for the driver's last callback; do it off the caller's thread.
        if (closing is not null) ThreadPool.QueueUserWorkItem(_ => closing.Dispose());
    }

    /// <summary>Reopens the card (on a worker thread) if it has been open but silent for three seconds.</summary>
    public void CheckLiveness(double hostTime)
    {
        lock (gate)
        {
            if (!liveness.ShouldReopen(hostTime)) return;
            liveness.Opening(); // so the next check doesn't reopen it again while this one runs
        }
        VideoIOLog.Note("input.decklink", $"{Name}: nothing from the card for {DirectInputLiveness.SilentSeconds:0} s — reopening");
        Task.Run(Open);
    }

    /// <summary>Starts sending this card's embedded audio (interleaved float, 48 kHz) to <paramref name="sink"/>, on the driver's thread.</summary>
    public Guid AddAudioSink(Action<AudioBuffer> sink)
    {
        var token = Guid.NewGuid();
        lock (gate) audioSinks[token] = sink;
        return token;
    }

    public void RemoveAudioSink(Guid token)
    {
        lock (gate) audioSinks.Remove(token);
    }

    private void Open()
    {
        int attempt;
        IDisposable? previous;
        lock (gate)
        {
            if (!liveness.Running) return;
            previous = input;
            input = null;
            generation++;
            attempt = generation;
            liveness.Opening();
            lastSignal = null;
        }
        previous?.Dispose();
        IDisposable? opened;
        string error;
        try
        {
            var channels = audioChannels > 0 ? audioChannels : DeckLinkInput.AudioChannels(
                DeckLinkRuntime.Devices().FirstOrDefault(device => device.PersistentId == PersistentId)?.MaxAudioChannels ?? 2);
            opened = opener(PersistentId, channels, (in DeckLinkInputArrival arrival) => Arrived(arrival, attempt), format => FormatChanged(format, attempt), out error);
        }
        catch (Exception ex)
        {
            opened = null;
            error = $"DeckLink input failed: {ex.Message}";
        }
        if (opened is null)
        {
            Failed(error, attempt);
            return;
        }
        bool current;
        lock (gate)
        {
            current = attempt == generation && liveness.Running;
            if (current)
            {
                input = opened;
                liveness.Opened(ShowState.HostClock());
            }
        }
        if (!current)
        {
            opened.Dispose();
            return;
        }
        VideoIOLog.Note("input.decklink", $"{Name}: open");
    }

    private void Failed(string message, int attempt)
    {
        LivenessFailureAction action;
        lock (gate) action = liveness.Failed(message, attempt == generation);
        if (!action.Retry) return;
        if (action.Note) VideoIOLog.Note("input.decklink", $"{Name}: {message} — retrying");
        SetStateIf(state => state.Status != VideoInputStatus.Stopped, VideoInputState.FailedWith(message));
        Task.Delay(retryDelay).ContinueWith(_ =>
        {
            lock (gate) liveness.Retrying();
            Open();
        }, TaskScheduler.Default);
    }

    private void FormatChanged(DeckLinkInputFormat format, int attempt)
    {
        lock (gate)
            if (attempt != generation) return;
        VideoIOLog.Note("input.decklink", $"{Name}: {format.Name}" + (format.Interlaced ? " (interlaced — deinterlacing)" : ""));
    }

    private void Arrived(in DeckLinkInputArrival arrival, int attempt)
    {
        Action<AudioBuffer>[] sinks;
        bool signalChanged;
        lock (gate)
        {
            if (attempt != generation) return;
            liveness.Arrived(ShowState.HostClock());
            signalChanged = lastSignal != arrival.HasSignal;
            lastSignal = arrival.HasSignal;
            sinks = audioSinks.Count == 0 ? [] : [.. audioSinks.Values];
        }
        if (signalChanged)
        {
            VideoIOLog.Note("input.decklink", $"{Name}: " + (arrival.HasSignal ? "signal" : "no signal"));
            if (!arrival.HasSignal) SetStateIf(state => state.Status != VideoInputStatus.Stopped, VideoInputState.NoSignal);
        }
        if (sinks.Length > 0 && arrival.AudioFrames > 0 && arrival.AudioChannels > 0)
        {
            var count = arrival.AudioFrames * arrival.AudioChannels;
            if (audioScratch.Length < count) audioScratch = new float[count];
            for (var i = 0; i < count; i++) audioScratch[i] = arrival.Audio[i] / 2147483648f;
            var buffer = new AudioBuffer(48_000, arrival.AudioChannels, audioScratch.AsMemory(0, count), arrival.CaptureHostTime);
            foreach (var sink in sinks)
            {
                try { sink(buffer); }
                catch (Exception ex) { VideoIOLog.Note("input.decklink", $"{Name}: an audio sink failed: {ex.Message}"); }
            }
        }
        if (!arrival.HasSignal || arrival.Video.IsEmpty) return;
        var stride = arrival.Width * 4;
        var pixels = Frames.Rent(stride * arrival.Height);
        if (!converter.ConvertToBgra(arrival.Video, arrival.RowBytes, arrival.Width, arrival.Height, arrival.Interlaced, pixels, stride)) return;
        var frame = Frames.Publish(arrival.Width, arrival.Height, stride, arrival.CaptureHostTime);
        SetStateIf(state => state.Status != VideoInputStatus.Stopped, VideoInputState.ReceivingFrom(Name));
        Deliver(frame);
    }
}

/// <summary>
/// One <see cref="DeckLinkCapture"/> per card, shared by everything that wants it (the
/// picture on a Video Input layer, the card's audio in the mixer): the first
/// <see cref="Acquire"/> opens it, the last lease's Dispose closes it. Port of the Mac's
/// DeckLinkDirectInputs.
/// </summary>
public sealed class DeckLinkCaptureHub
{
    private readonly Lock gate = new();
    private readonly Dictionary<long, (DeckLinkCapture Capture, int Users)> captures = [];
    private readonly Func<long, string, DeckLinkCapture> factory;

    public DeckLinkCaptureHub()
        : this((id, name) => new DeckLinkCapture(id, name))
    {
    }

    internal DeckLinkCaptureHub(Func<long, string, DeckLinkCapture> factory) => this.factory = factory;

    public static DeckLinkCaptureHub Shared { get; } = new();

    /// <summary>A lease on the card's capture, opening it if this is the first. Dispose the lease to let go.</summary>
    public DeckLinkCaptureLease Acquire(long persistentId, string name)
    {
        DeckLinkCapture? started = null;
        DeckLinkCapture capture;
        lock (gate)
        {
            if (captures.TryGetValue(persistentId, out var existing))
            {
                captures[persistentId] = (existing.Capture, existing.Users + 1);
                capture = existing.Capture;
            }
            else
            {
                capture = started = factory(persistentId, name);
                captures[persistentId] = (capture, 1);
            }
        }
        started?.Start();
        return new DeckLinkCaptureLease(this, capture);
    }

    public DeckLinkCapture? Capture(long persistentId)
    {
        lock (gate) return captures.TryGetValue(persistentId, out var entry) ? entry.Capture : null;
    }

    /// <summary>Reopens any card that has gone silent. Call about once a second.</summary>
    public void CheckLiveness(double hostTime)
    {
        DeckLinkCapture[] running;
        lock (gate) running = [.. captures.Values.Select(entry => entry.Capture)];
        foreach (var capture in running) capture.CheckLiveness(hostTime);
    }

    internal void Release(DeckLinkCapture capture)
    {
        DeckLinkCapture? stopped = null;
        lock (gate)
        {
            if (!captures.TryGetValue(capture.PersistentId, out var entry) || !ReferenceEquals(entry.Capture, capture)) return;
            if (entry.Users <= 1)
            {
                captures.Remove(capture.PersistentId);
                stopped = capture;
            }
            else
            {
                captures[capture.PersistentId] = (capture, entry.Users - 1);
            }
        }
        stopped?.Stop();
    }
}

/// <summary>One user's hold on a shared <see cref="DeckLinkCapture"/>: reads through to it; Dispose releases it once.</summary>
public sealed class DeckLinkCaptureLease : IVideoInputSource
{
    private readonly DeckLinkCaptureHub hub;
    private int released;

    internal DeckLinkCaptureLease(DeckLinkCaptureHub hub, DeckLinkCapture capture)
    {
        this.hub = hub;
        Capture = capture;
    }

    public DeckLinkCapture Capture { get; }

    public string Name => Capture.Name;
    public VideoInputState State => Capture.State;
    public long FramesReceived => Capture.FramesReceived;
    public LiveCadence? Cadence => Capture.Cadence;

    public event EventHandler<VideoFrame>? FrameArrived
    {
        add => Capture.FrameArrived += value;
        remove => Capture.FrameArrived -= value;
    }

    public event EventHandler<VideoInputState>? StateChanged
    {
        add => Capture.StateChanged += value;
        remove => Capture.StateChanged -= value;
    }

    public VideoFrame? LatestFrame() => Capture.LatestFrame();

    public bool CopyLatestFrame(Span<byte> destination, int destinationStride, out int width, out int height) =>
        Capture.CopyLatestFrame(destination, destinationStride, out width, out height);

    public double? FrameAge(double hostTime) => Capture.FrameAge(hostTime);

    public void Stop()
    {
        if (Interlocked.Exchange(ref released, 1) == 0) hub.Release(Capture);
    }

    public void Dispose() => Stop();
}
