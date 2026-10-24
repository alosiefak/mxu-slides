using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Audio;

/// <summary>
/// One audio bus: the player behind a playlist or a music clip. Two decks so the next
/// track can crossfade in; a master gain, mute and "hardware on/off" (a stream-only
/// bus); a lip-sync delay; meters and a feed tap for streams and recordings; and the
/// output device (a channel pair, or one channel in mono, on any interface).
/// Port of AudioEngine.swift.
/// <para>
/// How it runs: an <see cref="AudioOutputHost"/> thread asks the engine for audio every
/// ~10 ms (<see cref="Render"/>) and the engine mixes the decks into it. Control calls
/// (play, pause, seek…) take <see cref="Lock"/>; the audio thread never does. Both share a
/// short "render gate" that is only held for flag flips, never while decoding, so a slow
/// file open can't make the sound stutter. Fades and crossfades run on the audio clock
/// (frames rendered), so they are sample-smooth and behave the same when rendered offline.
/// </para>
/// <para>
/// Threading: every public member is thread-safe. <see cref="TrackPlayedToEnd"/> and
/// <see cref="OutputStatusChanged"/> arrive on thread-pool threads; the feed consumer is
/// called on the audio thread and must return quickly.
/// </para>
/// </summary>
public sealed class AudioEngine : IDisposable, IAudioRenderSource
{
    internal const int GraphRate = AudioDeck.GraphRate;

    /// <summary>How far ahead a "start at" time must be for the engine to hit it exactly.</summary>
    internal const double StartLead = 0.06;

    /// <summary>-3 dB: stereo folded to mono loses this per side, and the mono leg trims it again.</summary>
    internal const float MonoFold = 0.70710677f;

    private const int RampSlice = 256;

    private enum RampKind { Crossfade, TransportFade }

    private sealed class Ramp
    {
        public RampKind Kind;
        public AudioDeck Deck = null!;
        public AudioDeck? Outgoing;
        public long Total;
        public long Done;
        public float From;
        public float To;
        public AudioFadeCurve Curve;
        public Action? OnDone;
    }

    private sealed class TapState
    {
        private readonly Lock gate = new();
        private bool meterWanted;
        private float gain = 1;
        private Action<PcmBuffer>? feed;
        private LevelReading post;
        private LevelReading source;
        private LevelWindow postWindow;
        private LevelWindow sourceWindow;

        public void SetMeterWanted(bool wanted) { lock (gate) meterWanted = wanted; }
        public void SetGain(float value) { lock (gate) gain = value; }
        public void SetFeed(Action<PcmBuffer>? consumer) { lock (gate) feed = consumer; }

        public bool WantsTap { get { lock (gate) return meterWanted || feed is not null; } }
        public LevelReading PostLevel { get { lock (gate) return post; } }
        public LevelReading SourceLevel { get { lock (gate) return source; } }

        public (bool MeterWanted, float Gain, Action<PcmBuffer>? Feed) Snapshot()
        {
            lock (gate) return (meterWanted, gain, feed);
        }

        public void Store((LevelReading Left, LevelReading Right) raw, float gain)
        {
            LevelReading Scaled(LevelReading level) => new(MathF.Min(level.Rms * gain, 1), MathF.Min(level.Peak * gain, 1));
            lock (gate)
            {
                source = AudioLevel.Louder([raw.Left, raw.Right]);
                post = Scaled(source);
                sourceWindow.Fold(raw.Left, raw.Right);
                postWindow.Fold(Scaled(raw.Left), Scaled(raw.Right));
            }
        }

        public (LevelWindow Post, LevelWindow Source) TakeLevels()
        {
            lock (gate)
            {
                var taken = (postWindow, sourceWindow);
                postWindow = default;
                sourceWindow = default;
                return taken;
            }
        }

        public void ResetLevels()
        {
            lock (gate)
            {
                post = source = LevelReading.Silence;
                postWindow = sourceWindow = default;
            }
        }
    }

    private readonly AudioDeck[] decks = [new(), new()];
    private readonly Lock renderGate = new();
    private readonly AudioDelayLine delay = new();
    private readonly TapState tap = new();
    private readonly AutoResetEvent fillWake = new(false);
    private readonly Thread filler;
    private readonly string name;
    private int activeIndex;
    private Ramp? ramp;
    private float storedVolume = 1;
    private bool muted;
    private bool hardwareEnabled = true;
    private bool transportPlaying;
    private float appliedHardwareGain = 1;
    private bool wasSilent = true;
    private volatile bool disposed;

    // Output.
    private AudioOutputHost? host;
    private bool manual;
    private string? deviceUid;
    private int channelOffset;
    private bool monoOut;

    // Audio-thread scratch, grown on demand.
    private float[] mixLeft = new float[4096];
    private float[] mixRight = new float[4096];
    private float[] scratchLeft = new float[4096];
    private float[] scratchRight = new float[4096];
    private PcmBuffer mixBuffer;

    public AudioEngine(string name = "Audio")
    {
        this.name = name;
        mixBuffer = Wrap(mixLeft, mixRight);
        filler = new Thread(FillLoop) { IsBackground = true, Name = $"MxU audio decode: {name}", Priority = ThreadPriority.AboveNormal };
        filler.Start();
    }

    /// <summary>The control lock. The audio thread never takes it.</summary>
    internal EngineStateLock Lock { get; } = new();

    /// <summary>The active track played to its end by itself (not stopped). Thread-pool thread.</summary>
    public event Action? TrackPlayedToEnd;

    /// <summary>The output's <see cref="OutputStatus"/> changed. Thread-pool thread.</summary>
    public event Action<AudioOutputStatus>? OutputStatusChanged;

    /// <summary>Times the output was rebuilt for a new device or layout (tests watch this).</summary>
    internal int OutputRebuilds { get; private set; }

    /// <summary>Where the sound is going (a device, or silently on a clock with none).</summary>
    public AudioOutputStatus OutputStatus
    {
        get
        {
            if (manual) return AudioOutputStatus.Manual;
            return host?.Status ?? AudioOutputStatus.Idle;
        }
    }

    // MARK: Transport

    /// <summary>
    /// Plays a file on the bus.
    /// </summary>
    /// <param name="crossfade">Seconds to crossfade from what's playing (0 = cut).</param>
    /// <param name="fadeIn">Seconds to fade in from silence (when not crossfading).</param>
    /// <param name="trimStart">The track's in point (seconds into the file).</param>
    /// <param name="trimEnd">Its out point; null = the end of the file.</param>
    /// <param name="from">Where to start (default: the in point).</param>
    /// <param name="shape">A music clip's fades, applied as the file is read.</param>
    /// <param name="overlap">Start on the other deck at full level and let the current track play out (timeline clips that overlap).</param>
    /// <param name="due">A host-clock time this should be heard at; if already past, starts that far in.</param>
    /// <exception cref="AudioEngineException">The file can't be read or the trim leaves nothing to play.</exception>
    public void Play(string path, double crossfade = 0, double fadeIn = 0, double trimStart = 0, double? trimEnd = null,
        double? from = null, AudioClipShape? shape = null, bool overlap = false, double? due = null)
    {
        Lock.WithLock(() =>
        {
            EnsureRunning();
            AudioDeck outgoing, incoming;
            bool overlapping, crossfading;
            lock (renderGate)
            {
                CancelRamp(resolving: true);
                outgoing = decks[activeIndex];
                overlapping = overlap && outgoing.IsPlayingNow;
                crossfading = !overlapping && crossfade > 0 && outgoing.IsPlayingNow;
                incoming = crossfading || overlapping ? decks[1 - activeIndex] : outgoing;
            }
            (double HostTime, double Skip, double Wait)? start = crossfading || due is null
                ? null : ScheduledStart(due.Value, ShowState.HostClock());
            AudioDeck.Prepared prepared;
            try
            {
                prepared = AudioDeck.Prepare(path, (from ?? trimStart) + (start?.Skip ?? 0), trimStart, trimEnd, shape);
            }
            catch
            {
                // Like the Mac, a failed load leaves the deck it was meant for stopped.
                lock (renderGate) Retire(incoming.Clear());
                throw;
            }
            lock (renderGate)
            {
                Retire(incoming.Install(prepared));
                incoming.Shape = shape;
                transportPlaying = true;
                if (overlapping)
                {
                    activeIndex = 1 - activeIndex;
                    incoming.StartHostTime = start?.HostTime;
                    incoming.Playing = true;
                }
                else if (crossfading)
                {
                    activeIndex = 1 - activeIndex;
                    incoming.Volume = incoming.AppliedVolume = 0;
                    incoming.Playing = true;
                    BeginCrossfade(incoming, outgoing, crossfade);
                }
                else if (fadeIn > 0)
                {
                    incoming.Volume = incoming.AppliedVolume = 0;
                    incoming.StartHostTime = start?.HostTime;
                    incoming.Playing = true;
                    BeginFade(incoming, 1, fadeIn);
                }
                else
                {
                    incoming.StartHostTime = start?.HostTime;
                    incoming.Playing = true;
                }
            }
            fillWake.Set();
        });
    }

    /// <summary>
    /// When a clip due at <paramref name="due"/> can really start: at least
    /// <see cref="StartLead"/> from <paramref name="now"/>, skipping into the clip by however
    /// late that is. All in host-clock seconds.
    /// </summary>
    internal static (double HostTime, double Skip, double Wait) ScheduledStart(double due, double now)
    {
        var earliest = now + StartLead;
        var start = Math.Max(due, earliest);
        return (start, start - due, start - now);
    }

    public void Pause(double fade = 0)
    {
        Lock.WithLock(() =>
        {
            lock (renderGate)
            {
                CancelRamp(resolving: true);
                transportPlaying = false;
                var deck = decks[activeIndex];
                if (!(fade > 0 && deck.IsPlayingNow))
                {
                    deck.Playing = false;
                    return;
                }
                BeginFade(deck, 0, fade, onDone: () => deck.Playing = false);
            }
        });
    }

    public void Resume(double fade = 0)
    {
        Lock.WithLock(() =>
        {
            lock (renderGate)
            {
                CancelRamp(resolving: true);
                var deck = decks[activeIndex];
                if (deck.Path is null || deck.Ended) return;
            }
            EnsureRunning();
            lock (renderGate)
            {
                var deck = decks[activeIndex];
                transportPlaying = true;
                deck.Playing = true;
                if (fade > 0) BeginFade(deck, 1, fade);
                else deck.Volume = 1;
            }
        });
    }

    public void Stop(double fade = 0, AudioFadeCurve curve = AudioFadeCurve.Linear)
    {
        Lock.WithLock(() =>
        {
            lock (renderGate)
            {
                transportPlaying = false;
                var deck = decks[activeIndex];
                if (!(fade > 0 && deck.IsPlayingNow))
                {
                    CancelRamp();
                    foreach (var each in decks) Retire(each.Clear());
                    return;
                }
                CancelRamp(resolving: true);
                Retire(decks[1 - activeIndex].Clear());
                BeginFade(deck, 0, fade, curve, onDone: () => Retire(deck.Clear()));
            }
        });
    }

    public bool IsPlaying
    {
        get { lock (renderGate) return decks[activeIndex].IsPlayingNow; }
    }

    /// <summary>Jumps within the track (file seconds), clamped to its trim; keeps playing or paused as it was.</summary>
    public void Seek(double seconds)
    {
        Lock.WithLock(() =>
        {
            string path;
            double floor, duration;
            double? until;
            AudioClipShape? shape;
            bool resume;
            AudioDeck deck;
            lock (renderGate)
            {
                deck = decks[activeIndex];
                if (deck.Path is null || deck.Duration <= 0) return;
                CancelRamp(resolving: true);
                path = deck.Path;
                floor = deck.TrimStart;
                duration = deck.Duration;
                until = deck.TrimUntil;
                shape = deck.Shape;
                resume = transportPlaying;
            }
            var target = Math.Clamp(seconds, floor, Math.Max(floor, duration - 0.05));
            AudioDeck.Prepared prepared;
            try
            {
                prepared = AudioDeck.Prepare(path, target, floor, until, shape);
            }
            catch (AudioEngineException)
            {
                return;
            }
            lock (renderGate)
            {
                Retire(deck.Install(prepared));
                deck.Shape = shape;
                deck.Playing = resume;
            }
            fillWake.Set();
        });
    }

    /// <summary>Where the active track is (file seconds) and its end; null when nothing is loaded.</summary>
    public (double Elapsed, double Duration)? Position
    {
        get
        {
            lock (renderGate)
            {
                var deck = decks[activeIndex];
                if (deck.Path is null || deck.Duration <= 0) return null;
                return (Math.Min(deck.Elapsed, deck.Duration), deck.Duration);
            }
        }
    }

    // MARK: Console

    /// <summary>The bus fader, 0…1 (a linear gain; the UI maps its taper with <see cref="AudioLevel.FaderGain(float)"/>).</summary>
    public float MasterVolume
    {
        get { lock (renderGate) return storedVolume; }
        set
        {
            lock (renderGate)
            {
                storedVolume = Math.Clamp(value, 0, 1);
                ApplyConsole();
            }
        }
    }

    public bool IsMuted
    {
        get { lock (renderGate) return muted; }
        set
        {
            lock (renderGate)
            {
                muted = value;
                ApplyConsole();
            }
        }
    }

    /// <summary>False for a bus that only feeds streams and recordings: the device hears nothing.</summary>
    public bool HardwareOutputEnabled
    {
        get { lock (renderGate) return hardwareEnabled; }
        set { lock (renderGate) hardwareEnabled = value; }
    }

    private void ApplyConsole() => tap.SetGain(muted ? 0 : storedVolume);

    /// <summary>Holds the device output back by this much (0–2000 ms) to line up with a delayed screen.</summary>
    public double OutputDelayMilliseconds
    {
        get => delay.DelayMilliseconds;
        set => delay.DelayMilliseconds = value;
    }

    // MARK: Meters and feed

    public void SetMetering(bool enabled)
    {
        tap.SetMeterWanted(enabled);
        if (!tap.WantsTap) tap.ResetLevels();
    }

    /// <summary>
    /// Hands every block of the bus (after its fader and mute, before the delay) to
    /// <paramref name="consumer"/> on the audio thread: 48 kHz stereo, a fresh buffer each time.
    /// </summary>
    public void SetFeedConsumer(Action<PcmBuffer>? consumer)
    {
        tap.SetFeed(consumer);
        if (!tap.WantsTap) tap.ResetLevels();
    }

    /// <summary>The latest block's level after the fader.</summary>
    public LevelReading MeterLevel => tap.PostLevel;

    /// <summary>The latest block's level before the fader (a pulled-down fader still shows signal here).</summary>
    public LevelReading SourceMeterLevel => tap.SourceLevel;

    /// <summary>The loudest levels since the last call, after and before the fader.</summary>
    public (LevelWindow Post, LevelWindow Source) TakeMeterLevels() => tap.TakeLevels();

    // MARK: Output device

    /// <summary>
    /// Sends the bus to <paramref name="device"/> (null = the Windows default), on the pair
    /// starting at <paramref name="channelOffset"/>, or that one channel when <paramref name="mono"/>.
    /// Playback carries on through the switch. Never throws: a missing device shows in <see cref="OutputStatus"/>.
    /// </summary>
    public void SetOutputDevice(AudioOutputDevice? device, int channelOffset = 0, bool mono = false)
    {
        Lock.WithLock(() =>
        {
            var target = device?.Uid;
            var running = host?.IsRunning == true;
            var effective = target ?? AudioDeviceList.DefaultOutputDevice()?.Uid;
            if (running && effective is not null && host!.OpenDeviceUid == effective
                && mono == monoOut && channelOffset == this.channelOffset && target == deviceUid)
                return;
            if (running && effective is not null && host!.OpenDeviceUid == effective
                && mono == monoOut && channelOffset == this.channelOffset)
            {
                // The same device, named differently (the default by name, or vice versa):
                // keep the stream, just remember whether to follow the default from now on.
                deviceUid = target;
                host.Retarget(deviceUid);
                return;
            }
            OutputRebuilds++;
            lock (renderGate) CancelRamp(resolving: true);
            deviceUid = target;
            this.channelOffset = channelOffset;
            monoOut = mono;
            if (running) host!.Configure(deviceUid, Layout, GraphRate);
        });
    }

    private AudioOutputHost.Layout Layout(int deviceChannels) =>
        new(monoOut ? 1 : 2, ChannelMap.Make(deviceChannels, channelOffset, monoOut));

    private void EnsureRunning()
    {
        if (manual || disposed) return;
        if (host is null)
        {
            host = new AudioOutputHost(name, this, Layout, GraphRate);
            host.StatusChanged += status => OutputStatusChanged?.Invoke(status);
        }
        if (!host.IsRunning) host.Configure(deviceUid, Layout, GraphRate);
    }

    /// <summary>Windows reported a device change. Never blocks the caller (port of the configuration-change observer).</summary>
    internal void OnDeviceConfigurationChanged() => host?.DevicesChanged();

    // MARK: Rendering

    void IAudioRenderSource.Render(float[][] buffers, int frames, int sampleRate, double hostTime) =>
        RealtimeAudioCallback.Scope(() => RenderBlock(buffers, frames, hostTime));

    private void RenderBlock(float[][] buffers, int frames, double hostTime)
    {
        List<(AudioDeck Deck, int Generation)>? ended = null;
        Action<PcmBuffer>? feed;
        PcmBuffer? fed = null;
        var wantsFill = false;
        lock (renderGate)
        {
            Grow(frames);
            Array.Clear(mixLeft, 0, frames);
            Array.Clear(mixRight, 0, frames);
            var offset = 0;
            while (offset < frames)
            {
                var slice = ramp is null ? frames - offset : Math.Min(RampSlice, frames - offset);
                var finished = AdvanceRamp(slice);
                var sliceHost = hostTime > 0 ? hostTime + offset / (double)GraphRate : 0;
                foreach (var deck in decks)
                {
                    if (deck.RenderSlice(mixLeft, mixRight, offset, slice, sliceHost, scratchLeft, scratchRight, out var fill))
                        (ended ??= []).Add((deck, deck.Generation));
                    wantsFill |= fill;
                }
                finished?.Invoke();
                offset += slice;
            }

            // The tap: meters and the stream feed hear the bus after its fader, before the delay.
            var snapshot = tap.Snapshot();
            mixBuffer.FrameLength = frames;
            if (snapshot.MeterWanted) tap.Store(AudioLevel.MeasureSides(mixBuffer), snapshot.Gain);
            feed = snapshot.Feed;
            if (feed is not null)
            {
                fed = new PcmBuffer(GraphRate, 2, frames) { FrameLength = frames };
                for (var frame = 0; frame < frames; frame++)
                {
                    fed.Data[0][frame] = mixLeft[frame] * snapshot.Gain;
                    fed.Data[1][frame] = mixRight[frame] * snapshot.Gain;
                }
            }

            delay.Process(mixBuffer);

            var mono = buffers.Length == 1;
            var target = hardwareEnabled ? (muted ? 0 : storedVolume) * (mono ? MonoFold : 1) : 0;
            // After a silent block a new gain can jump (nothing was sounding to click);
            // otherwise it glides across the block.
            var from = wasSilent ? target : appliedHardwareGain;
            var step = (target - from) / frames;
            if (mono)
            {
                var output = buffers[0];
                for (var frame = 0; frame < frames; frame++)
                    output[frame] = (mixLeft[frame] + mixRight[frame]) * MonoFold * (from + step * (frame + 1));
            }
            else
            {
                for (var frame = 0; frame < frames; frame++)
                {
                    var gain = from + step * (frame + 1);
                    buffers[0][frame] = mixLeft[frame] * gain;
                    buffers[1][frame] = mixRight[frame] * gain;
                }
            }
            appliedHardwareGain = target;
            wasSilent = Peak(mixLeft, mixRight, frames) == 0;
        }
        if (fed is not null)
        {
            try { feed!(fed); }
            catch { }
        }
        if (wantsFill) fillWake.Set();
        if (ended is not null)
            foreach (var (deck, generation) in ended)
                ThreadPool.QueueUserWorkItem(_ => PlayedToEnd(deck, generation));
    }

    private void Grow(int frames)
    {
        if (mixLeft.Length >= frames) return;
        mixLeft = new float[frames];
        mixRight = new float[frames];
        scratchLeft = new float[frames];
        scratchRight = new float[frames];
        mixBuffer = Wrap(mixLeft, mixRight);
    }

    private static float Peak(float[] left, float[] right, int frames)
    {
        float peak = 0;
        for (var frame = 0; frame < frames; frame++) peak = MathF.Max(peak, MathF.Max(MathF.Abs(left[frame]), MathF.Abs(right[frame])));
        return peak;
    }

    // A PcmBuffer over the engine's own mix arrays (no copy), for the meters and the delay.
    private static PcmBuffer Wrap(float[] left, float[] right) => new(GraphRate, [left, right]);

    private void PlayedToEnd(AudioDeck deck, int generation)
    {
        var current = Lock.WithLock(() =>
        {
            lock (renderGate)
            {
                var isCurrent = deck.Generation == generation && decks[activeIndex] == deck;
                if (isCurrent) transportPlaying = false;
                return isCurrent;
            }
        });
        if (current) TrackPlayedToEnd?.Invoke();
    }

    // MARK: Ramps (render gate held)

    private void BeginCrossfade(AudioDeck incoming, AudioDeck outgoing, double seconds) =>
        ramp = new Ramp
        {
            Kind = RampKind.Crossfade, Deck = incoming, Outgoing = outgoing,
            Total = FramesFor(seconds),
        };

    private void BeginFade(AudioDeck deck, float to, double seconds, AudioFadeCurve curve = AudioFadeCurve.Linear, Action? onDone = null) =>
        ramp = new Ramp
        {
            Kind = RampKind.TransportFade, Deck = deck, From = deck.Volume, To = to, Curve = curve,
            Total = FramesFor(seconds), OnDone = onDone,
        };

    private static long FramesFor(double seconds) => Math.Max(1, (long)Math.Round(Math.Max(seconds, 0.01) * GraphRate));

    // Moves the ramp on by `frames`, setting deck gains for the end of the slice. Returns
    // what to do once the slice is rendered, if the ramp finished.
    private Action? AdvanceRamp(int frames)
    {
        if (ramp is not { } current) return null;
        if (current.Kind == RampKind.Crossfade)
        {
            current.Done += frames;
            var progress = Math.Min(1, current.Done / (double)current.Total);
            var (incoming, outgoing) = CrossfadeCurve.Gains(progress);
            current.Deck.Volume = incoming;
            current.Outgoing!.Volume = outgoing;
            if (progress < 1) return null;
            ramp = null;
            var leaving = current.Outgoing;
            return () => Retire(leaving.Clear());
        }
        // A fade on a deck with a scheduled start waits for the start.
        if (current.Deck.StartHostTime is not null) return null;
        current.Done += frames;
        var p = Math.Min(1, current.Done / (double)current.Total);
        current.Deck.Volume = current.Curve == AudioFadeCurve.Linear || current.To > current.From
            ? current.From + (current.To - current.From) * (float)p
            : current.To + (current.From - current.To) * current.Curve.Falling(p);
        if (p < 1) return null;
        ramp = null;
        return current.OnDone;
    }

    private void CancelRamp(bool resolving = false)
    {
        if (ramp is not { } current) return;
        ramp = null;
        if (resolving && current.Kind == RampKind.Crossfade)
        {
            decks[activeIndex].Volume = 1;
            Retire(decks[1 - activeIndex].Clear());
        }
    }

    // Decoders are closed off the audio thread.
    private static void Retire(AudioDeck.Stream? stream)
    {
        if (stream is null) return;
        ThreadPool.QueueUserWorkItem(_ => stream.Dispose());
    }

    private void FillLoop()
    {
        while (!disposed)
        {
            fillWake.WaitOne(20);
            if (disposed) break;
            FillNow();
        }
    }

    private void FillNow()
    {
        AudioDeck.Stream?[] streams;
        lock (renderGate) streams = [decks[0].Current, decks[1].Current];
        foreach (var stream in streams)
        {
            try { stream?.Fill(); }
            catch { }
        }
    }

    // MARK: Offline rendering (tests)

    /// <summary>Detaches from any device; <see cref="RenderOfflineForTests"/> then pulls audio by hand.</summary>
    internal void EnableManualRenderingForTests(bool mono = false)
    {
        Lock.WithLock(() =>
        {
            host?.Stop();
            manual = true;
            monoOut = mono;
        });
    }

    internal PcmBuffer RenderOfflineForTests(int frames)
    {
        var channels = monoOut ? 1 : 2;
        var output = new PcmBuffer(GraphRate, channels, frames) { FrameLength = frames };
        var chunk = new float[channels][];
        for (var channel = 0; channel < channels; channel++) chunk[channel] = new float[4096];
        var done = 0;
        while (done < frames)
        {
            FillNow();
            var count = Math.Min(4096, frames - done);
            foreach (var channel in chunk) Array.Clear(channel, 0, count);
            RenderBlock(chunk, count, 0);
            for (var channel = 0; channel < channels; channel++) Array.Copy(chunk[channel], 0, output.Data[channel], done, count);
            done += count;
        }
        return output;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        fillWake.Set();
        host?.Dispose();
        lock (renderGate)
        {
            ramp = null;
            foreach (var deck in decks) deck.Clear()?.Dispose();
        }
    }
}
