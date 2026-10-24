using MxU.Slides.Core.Media;
using MxU.Slides.Core.Scene;
using MxU.Slides.VideoIO.Common;

namespace MxU.Slides.VideoIO.DeckLink;

/// <summary>What to send where: a screen (target) out of one card, at the program's size and rate.</summary>
/// <param name="ModeId">A Format the operator chose (a Bmd display mode); 0 for Auto.</param>
public sealed record DeckLinkOutputRequest(
    string TargetId, string TargetName, long DevicePersistentId, string DeviceName, DeckLinkKeying Keying,
    int Width, int Height, int FrameRate, long ModeId = 0);

/// <summary>Where a DeckLink output is. Interrupted: it was sending (or trying to reconnect) and is retrying by itself.</summary>
public enum DeckLinkOutputState { Idle, Starting, Sending, Failed, Interrupted }

/// <summary>A DeckLink output's state; <see cref="Detail"/> is the device name, or the error when Failed.</summary>
public sealed record DeckLinkOutputStatus(DeckLinkOutputState State, string Detail = "")
{
    public static readonly DeckLinkOutputStatus Idle = new(DeckLinkOutputState.Idle);

    public bool IsActive => State is DeckLinkOutputState.Starting or DeckLinkOutputState.Sending or DeckLinkOutputState.Interrupted;
}

/// <summary>An open card output as the controller sees it, so tests can stand in for the card.</summary>
internal interface IDeckLinkPlayout : IDisposable
{
    bool Display(VideoFrame frame);

    DeckLinkOutputTiming Timing { get; }
}

/// <summary>Opens card outputs: the real one checks the driver, picks the mode and opens a <see cref="DeckLinkOutput"/>.</summary>
internal interface IDeckLinkOutputBackend
{
    (IDeckLinkPlayout? Output, string? Error, string WireDetail) Open(DeckLinkOutputRequest request);
}

/// <summary>
/// Keeps a program going out of a DeckLink card through unplugs, other apps grabbing the
/// card and driver hiccups. Add it to the program feed and <see cref="Start"/> it.
/// <list type="bullet">
/// <item>Opening runs on a worker thread; status goes Starting → Sending, or Failed with
/// the reason. "Device not found" and "claimed by another app" are treated as passing:
/// it retries every two seconds (Interrupted) until the card is back.</item>
/// <item>A second of refused frames (the card stopped completing them) starts a
/// reconnect, as does a watchdog that sees no delivered frame for three seconds.</item>
/// <item>A reconnect only counts as recovered once a frame is actually delivered.</item>
/// </list>
/// <see cref="WriteVideo"/> runs on the program feed's thread and only copies into the
/// card's slot, never waiting on the card. Audio isn't embedded (the Mac doesn't either).
/// Port of the non-UI logic of Sources/DeckLinkOutputController.swift, with the XPC helper
/// replaced by an in-process <see cref="DeckLinkOutput"/>.
/// </summary>
public sealed class DeckLinkOutputController : IProgramSink, IDisposable
{
    private readonly Lock gate = new();
    private readonly IDeckLinkOutputBackend backend;
    private readonly TimeSpan reconnectDelay;
    private readonly TimeSpan stallTimeout;
    private readonly TimeSpan watchdogInterval;
    private readonly TimeSpan openTimeout;
    private readonly CardClock cardClock = new();

    private DeckLinkOutputStatus status = DeckLinkOutputStatus.Idle;
    private DeckLinkOutputRequest? request;
    private IDeckLinkPlayout? playout;
    private int generation;
    private bool inReconnectEpisode;
    private string? lastReconnectError;
    private bool awaitingDeliveryConfirm;
    private double lastDeliveredAt;
    private Timer? reconnectTimer;
    private Timer? watchdog;
    private Timer? openTimer;
    private int refusalStreak;
    private int framesWritten;
    private bool firstOutcomeLogged;
    private CardTiming totals;
    private long delivered;
    private long refused;

    public DeckLinkOutputController()
        : this(new DriverBackend(), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5))
    {
    }

    internal DeckLinkOutputController(IDeckLinkOutputBackend backend, TimeSpan reconnectDelay, TimeSpan stallTimeout,
        TimeSpan watchdogInterval, TimeSpan openTimeout)
    {
        this.backend = backend;
        this.reconnectDelay = reconnectDelay;
        this.stallTimeout = stallTimeout;
        this.watchdogInterval = watchdogInterval;
        this.openTimeout = openTimeout;
    }

    public DeckLinkOutputStatus Status
    {
        get { lock (gate) return status; }
    }

    /// <summary>Raised on a worker thread or the caller's. Marshal to the UI thread before touching controls.</summary>
    public event Action<DeckLinkOutputStatus>? StatusChanged;

    /// <summary>The mode line for the settings, e.g. "1080p29.97 (Auto)".</summary>
    public string WireDetail { get; private set; } = "";

    /// <summary>The request being served (also while reconnecting), or null when stopped.</summary>
    public DeckLinkOutputRequest? Request
    {
        get { lock (gate) return request; }
    }

    /// <summary>Card timing counters added up since <see cref="Start"/> (repeats, skips, late, dropped, jumps).</summary>
    public CardTiming CardTiming
    {
        get { lock (gate) return totals; }
    }

    /// <summary>The card's own frame clock, for pacing the program feed to it. Null until a second of completions.</summary>
    public LiveCadence? Cadence
    {
        get { lock (cardClock) return cardClock.Cadence; }
    }

    public long FramesDelivered => Interlocked.Read(ref delivered);
    public long FramesRefused => Interlocked.Read(ref refused);

    public bool IsActive => Status.IsActive;

    /// <summary>Opens the output (on a worker thread) and keeps it open until <see cref="Stop"/>.</summary>
    public void Start(DeckLinkOutputRequest output)
    {
        StopOutput(keepEpisode: true);
        int attempt;
        DeckLinkOutputStatus next;
        lock (gate)
        {
            request = output;
            generation++;
            attempt = generation;
            awaitingDeliveryConfirm = false;
            refusalStreak = 0;
            framesWritten = 0;
            firstOutcomeLogged = inReconnectEpisode;
            next = inReconnectEpisode ? new(DeckLinkOutputState.Interrupted, output.DeviceName) : new(DeckLinkOutputState.Starting, output.DeviceName);
            openTimer = new Timer(_ => OpenTimedOut(attempt), null, openTimeout, Timeout.InfiniteTimeSpan);
        }
        SetStatus(next);
        Task.Run(() => Open(output, attempt));
    }

    /// <summary>Closes the output and forgets the request.</summary>
    public void Stop()
    {
        StopOutput(keepEpisode: false);
        SetStatus(DeckLinkOutputStatus.Idle);
    }

    public void Dispose() => Stop();

    public void WriteVideo(VideoFrame frame)
    {
        IDeckLinkPlayout? target;
        string deviceName;
        int threshold;
        lock (gate)
        {
            target = playout;
            deviceName = request?.DeviceName ?? "?";
            threshold = Math.Max(1, request?.FrameRate ?? 30);
            framesWritten++;
        }
        if (target is null) return;
        ProbeAlpha(frame, deviceName);
        bool shown;
        DeckLinkOutputTiming timing;
        try
        {
            shown = target.Display(frame);
            timing = target.Timing;
        }
        catch (Exception ex)
        {
            VideoIOLog.Note("decklink.frames", $"{deviceName}: display failed: {ex.Message}");
            return;
        }
        CardTiming delta;
        lock (cardClock)
            delta = cardClock.Record((int)timing.Completions, timing.LastCompletionHostTime, new CardTiming(
                (int)timing.Repeats, (int)timing.Skips, (int)timing.Late, (int)timing.Dropped, (int)timing.Jumps));

        bool firstOutcome, streakHit = false, confirms = false;
        lock (gate)
        {
            if (!ReferenceEquals(target, playout)) return;
            totals += delta;
            firstOutcome = !firstOutcomeLogged;
            firstOutcomeLogged = true;
            if (shown)
            {
                refusalStreak = 0;
                lastDeliveredAt = ShowState.HostClock();
                confirms = awaitingDeliveryConfirm;
                awaitingDeliveryConfirm = false;
            }
            else
            {
                refusalStreak++;
                streakHit = refusalStreak == threshold;
            }
        }
        Interlocked.Increment(ref shown ? ref delivered : ref refused);
        if (firstOutcome) VideoIOLog.Note("decklink.frames", $"first frame {(shown ? "delivered" : "REFUSED")}");
        if (confirms) ReconnectConfirmed();
        if (streakHit) ThreadPool.QueueUserWorkItem(_ => DeviceStoppedTakingFrames());
    }

    /// <summary>Audio isn't embedded in the SDI signal yet (nor on the Mac); the program's audio goes out of the PC's audio device.</summary>
    public void WriteAudio(AudioBuffer buffer)
    {
    }

    /// <summary>Transient: worth retrying until the card is back, rather than giving up.</summary>
    internal static bool IsTransientOpenFailure(string error) =>
        error.StartsWith("DeckLink device not found", StringComparison.Ordinal) || error.Contains("claimed by another app", StringComparison.Ordinal);

    private void Open(DeckLinkOutputRequest output, int attempt)
    {
        (IDeckLinkPlayout? opened, string? error, string wireDetail) result;
        try { result = backend.Open(output); }
        catch (Exception ex) { result = (null, $"DeckLink output failed: {ex.Message}", ""); }

        bool current, episode, noteIt;
        lock (gate)
        {
            current = attempt == generation && request is not null;
            episode = inReconnectEpisode;
            noteIt = !episode || result.error != lastReconnectError;
            if (current)
            {
                openTimer?.Dispose();
                openTimer = null;
            }
        }
        if (!current)
        {
            result.opened?.Dispose();
            return;
        }
        WireDetail = result.wireDetail;
        if (noteIt)
            VideoIOLog.Note("decklink.start", result.error is { } failure ? $"{output.DeviceName}: {failure}" : $"{output.DeviceName}: sending {result.wireDetail}");

        if (result.error is { } message || result.opened is null)
        {
            var error = result.error ?? "DeckLink output failed";
            lock (gate) lastReconnectError = error;
            if (episode || IsTransientOpenFailure(error)) BeginReconnect();
            else SetStatus(new DeckLinkOutputStatus(DeckLinkOutputState.Failed, error));
            return;
        }

        lock (gate)
        {
            playout = result.opened;
            lastDeliveredAt = ShowState.HostClock();
            if (episode) awaitingDeliveryConfirm = true;
            watchdog?.Dispose();
            watchdog = new Timer(_ => Watch(attempt), null, watchdogInterval, watchdogInterval);
        }
        if (!episode) SetStatus(new DeckLinkOutputStatus(DeckLinkOutputState.Sending, output.DeviceName));
    }

    /// <summary>The open never came back: treat the driver as wedged and retry.</summary>
    private void OpenTimedOut(int attempt)
    {
        string? device;
        lock (gate)
        {
            if (attempt != generation || request is null || playout is not null) return;
            device = request.DeviceName;
        }
        VideoIOLog.Note("decklink.reconnect", $"{device}: open never answered — retrying");
        BeginReconnect();
    }

    private void ReconnectConfirmed()
    {
        string? device;
        lock (gate)
        {
            if (!inReconnectEpisode || request is null) return;
            inReconnectEpisode = false;
            lastReconnectError = null;
            device = request.DeviceName;
        }
        VideoIOLog.Note("decklink.start", $"{device}: sending (reconnected)");
        SetStatus(new DeckLinkOutputStatus(DeckLinkOutputState.Sending, device));
    }

    private void DeviceStoppedTakingFrames()
    {
        string? device;
        lock (gate)
        {
            if (status.State != DeckLinkOutputState.Sending) return;
            device = request?.DeviceName;
        }
        VideoIOLog.Note("decklink.reconnect", $"{device ?? "?"}: frames refused — reconnecting");
        BeginReconnect();
    }

    private void BeginReconnect()
    {
        DeckLinkOutputStatus next;
        lock (gate)
        {
            if (request is null) return;
            inReconnectEpisode = true;
            next = new DeckLinkOutputStatus(DeckLinkOutputState.Interrupted, request.DeviceName);
            if (reconnectTimer is null)
            {
                var attempt = generation;
                reconnectTimer = new Timer(_ => Reconnect(attempt), null, reconnectDelay, Timeout.InfiniteTimeSpan);
            }
        }
        SetStatus(next);
    }

    private void Reconnect(int attempt)
    {
        DeckLinkOutputRequest? again;
        lock (gate)
        {
            reconnectTimer?.Dispose();
            reconnectTimer = null;
            again = attempt == generation && status.IsActive ? request : null;
        }
        if (again is not null) Start(again);
    }

    /// <summary>Every couple of seconds: has a frame actually reached the card lately?</summary>
    private void Watch(int attempt)
    {
        DeckLinkOutputRequest? again;
        bool noteIt;
        lock (gate)
        {
            if (attempt != generation || request is null) return;
            var sending = status.State == DeckLinkOutputState.Sending;
            if (!sending && !awaitingDeliveryConfirm) return;
            if (ShowState.HostClock() - lastDeliveredAt <= stallTimeout.TotalSeconds) return;
            noteIt = !inReconnectEpisode;
            inReconnectEpisode = true;
            again = request;
        }
        if (noteIt)
            VideoIOLog.Note("decklink.reconnect", $"{again.DeviceName}: no delivered frames for {stallTimeout.TotalSeconds:0}s — restarting");
        Start(again);
    }

    private void StopOutput(bool keepEpisode)
    {
        IDeckLinkPlayout? closing;
        lock (gate)
        {
            reconnectTimer?.Dispose();
            reconnectTimer = null;
            watchdog?.Dispose();
            watchdog = null;
            openTimer?.Dispose();
            openTimer = null;
            closing = playout;
            playout = null;
            generation++;
            if (!keepEpisode)
            {
                request = null;
                inReconnectEpisode = false;
                lastReconnectError = null;
                totals = default;
            }
        }
        // Closing a card output waits for its last completion; do it off the caller's thread.
        if (closing is not null) ThreadPool.QueueUserWorkItem(_ => closing.Dispose());
    }

    /// <summary>Notes the alpha of the first frame and of frames 150 and 300 (5 s and 10 s in), to diagnose keying.</summary>
    private void ProbeAlpha(VideoFrame frame, string deviceName)
    {
        int index;
        lock (gate) index = framesWritten;
        int? sample = index switch { 1 => 0, 150 => 1, 300 => 2, _ => null };
        if (sample is null || frame.Width == 0 || frame.Height == 0) return;
        var pixels = frame.Pixels.Span;
        var center = frame.Height / 2 * frame.Stride + frame.Width / 2 * 4;
        if (pixels.Length < center + 4) return;
        VideoIOLog.Note("decklink.alpha",
            $"{deviceName} t{sample * 5}s corner α={pixels[3]} center B={pixels[center]} G={pixels[center + 1]} R={pixels[center + 2]} α={pixels[center + 3]}");
    }

    private void SetStatus(DeckLinkOutputStatus next)
    {
        lock (gate)
        {
            if (status == next) return;
            status = next;
        }
        try { StatusChanged?.Invoke(next); }
        catch (Exception ex) { VideoIOLog.Note("decklink.start", $"a status listener failed: {ex.Message}"); }
    }

    /// <summary>The real card: driver checks, mode choice, then <see cref="DeckLinkOutput.TryOpen"/>.</summary>
    private sealed class DriverBackend : IDeckLinkOutputBackend
    {
        public (IDeckLinkPlayout?, string?, string) Open(DeckLinkOutputRequest request)
        {
            if (!DeckLinkRuntime.IsAvailable) return (null, DeckLinkRuntime.NotInstalledMessage, "");
            var version = DeckLinkRuntime.ApiVersion;
            if (DeckLinkRuntime.IsTooOld(version))
                return (null, $"Desktop Video {DeckLinkRuntime.FormatVersion(version) ?? "?"} is too old — install {DeckLinkRuntime.RequiredApiMajor}.0 or later", "");
            if (!DeckLinkRuntime.Devices().Any(device => device.PersistentId == request.DevicePersistentId))
                return (null, "DeckLink device not found (unplugged, or Desktop Video not installed)", "");
            var (choice, error) = DeckLinkModeSelection.Choose(
                DeckLinkRuntime.DisplayModes(request.DevicePersistentId), request.Keying,
                DeckLinkRuntime.ConfiguredOutputMode(request.DevicePersistentId),
                request.Width, request.Height, request.FrameRate * 1000, 1000, request.ModeId,
                () => DeckLinkRuntime.ReferenceLocked(request.DevicePersistentId));
            if (choice is null) return (null, error, "");
            var output = DeckLinkOutput.TryOpen(request.DevicePersistentId, choice.ModeId, request.Keying, out var openError);
            return output is null ? (null, openError, "") : (output, null, choice.WireDetail);
        }
    }
}
