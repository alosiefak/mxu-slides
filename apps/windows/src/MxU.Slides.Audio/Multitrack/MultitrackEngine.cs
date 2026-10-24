using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Audio.Multitrack;

/// <summary>
/// Plays a song's stems ("tracks": click, guide, keys, pads…) sample-locked on one device,
/// through the tracks mixer, at the device's own rate. Songs can be cued at any point,
/// preloaded so the next one starts without decoding, crossfaded or handed over at an
/// exact frame (a transition), and chased to incoming timecode with varispeed. Port of
/// MultitrackEngine.swift.
/// <para>
/// Threading: all members are thread-safe (one lock stands in for the Mac's serial
/// queue). <see cref="OnHandover"/> is called on the engine's tick thread. The audio
/// itself is rendered by <see cref="MultitrackRenderer"/> on the output thread.
/// </para>
/// </summary>
public sealed class MultitrackEngine : IDisposable
{
    internal const double RingSeconds = 3.0;
    internal const double PrimeSeconds = 0.75;
    internal const int PreloadLimit = 2;

    private sealed class RenderBridge(MultitrackEngine engine) : IAudioRenderSource
    {
        public void Render(float[][] buffers, int frames, int sampleRate, double hostTime) =>
            RealtimeAudioCallback.Scope(() =>
            {
                var renderer = Volatile.Read(ref engine.renderer);
                if (buffers.Length < renderer.DeviceChannels) return;
                var done = 0;
                while (done < frames)
                {
                    var count = Math.Min(renderer.MaxFrames, frames - done);
                    if (done == 0)
                    {
                        renderer.Render(count, hostTime, buffers);
                    }
                    else
                    {
                        var slice = engine.Slice(buffers.Length, count);
                        renderer.Render(count, hostTime + done / (double)sampleRate, slice);
                        for (var channel = 0; channel < buffers.Length; channel++) Array.Copy(slice[channel], 0, buffers[channel], done, count);
                    }
                    done += count;
                }
            });
    }

    private readonly Lock queue = new();
    private readonly Lock preloadQueue = new();
    private MultitrackRenderer renderer;
    private ITimer? timer;

    // The 20 ms tick decodes stems into the rings: real work, so it gets a clock of its own.
    private ShowTimeProvider? tickClock;
    private AudioOutputHost? output;
    private MultitrackSong? song;
    private MultitrackRouting routing = new();
    private MultitrackSession? session;
    private readonly Dictionary<MultitrackSession, StemReader[]> readers = new(ReferenceEqualityComparer.Instance);
    private MultitrackProgram? program;
    private MultitrackDelays? delays;
    private readonly List<(MultitrackSong Song, double Rate, StemReader[] Readers)> preloaded = [];
    private List<MultitrackSong> preloadWanted = [];
    private string? chaseSongId;
    private double chaseStarted;
    private MultitrackChase chase;
    private MultitrackTransition? armed;
    private MultitrackSession? armedSession;
    private long seenHandovers;
    private readonly List<(MultitrackProgram Program, double At)> retired = [];
    private float[][] sliceScratch = [];

    public MultitrackEngine(double sampleRate = 48_000, int deviceChannels = 2, TracksShare? share = null)
    {
        SampleRate = sampleRate;
        DeviceChannels = deviceChannels;
        Share = share;
        if (share is not null) share.SampleRate = sampleRate;
        renderer = new MultitrackRenderer(deviceChannels, sampleRate, share: share);
    }

    public double SampleRate { get; private set; }

    public int DeviceChannels { get; private set; }

    /// <summary>The device the tracks play on (null when detached).</summary>
    public string? DeviceUid { get; private set; }

    public TracksShare? Share { get; private set; }

    /// <summary>Song cues that had to decode (a preloaded start doesn't count).</summary>
    internal int CuesDecoded { get; private set; }

    /// <summary>Times a chase had to jump instead of steering.</summary>
    public int ChaseLandings { get; private set; }

    internal MultitrackRenderer Renderer => Volatile.Read(ref renderer);

    /// <summary>A transition fired (the next song took over, or the song ended). Tick thread.</summary>
    public Action? OnHandover { get; set; }

    /// <summary>Where the tracks are going (a device, or silently on a clock with none).</summary>
    public AudioOutputStatus OutputStatus => output?.Status ?? AudioOutputStatus.Idle;

    public void SetShare(TracksShare share)
    {
        lock (queue)
        {
            Share = share;
            share.SampleRate = SampleRate;
            Volatile.Write(ref renderer, new MultitrackRenderer(DeviceChannels, SampleRate, share: share));
            if (session is not null) Publish(session);
        }
    }

    /// <summary>
    /// Plays on <paramref name="device"/> (null = the Windows default) at its rate and channel
    /// count, keeping the song's place if those change. With no device at all the tracks
    /// still run, silently, on a clock (and say so in <see cref="OutputStatus"/>). Never throws.
    /// </summary>
    public void Attach(AudioOutputDevice? device)
    {
        lock (queue)
        {
            var target = device ?? AudioDeviceList.DefaultOutputDevice();
            var rate = (double)(target?.SampleRate ?? (int)SampleRate);
            var channels = target?.ChannelCount ?? DeviceChannels;
            if (output is not null && target?.Uid == DeviceUid && rate == SampleRate && channels == DeviceChannels) return;
            output?.Dispose();
            output = null;
            if (rate != SampleRate || channels != DeviceChannels)
            {
                var resume = PositionLocked(ShowState.HostClock());
                var wasPlaying = Renderer.Published.Playing;
                SampleRate = rate;
                DeviceChannels = channels;
                if (Share is not null) Share.SampleRate = rate;
                Volatile.Write(ref renderer, new MultitrackRenderer(DeviceChannels, SampleRate, share: Share));
                delays = null;
                foreach (var entry in preloaded) Dispose(entry.Readers);
                preloaded.Clear();
                Disarm();
                if (song is not null) CueLocked(song, resume);
                if (wasPlaying) Renderer.Play();
            }
            DeviceUid = target?.Uid;
            output = new AudioOutputHost("Tracks", new RenderBridge(this),
                deviceChannels => new AudioOutputHost.Layout(DeviceChannels, ChannelMap.Identity(Math.Max(deviceChannels, DeviceChannels))),
                (int)SampleRate);
            output.Configure(target?.Uid, deviceChannels => new AudioOutputHost.Layout(DeviceChannels, ChannelMap.Identity(Math.Max(deviceChannels, DeviceChannels))), (int)SampleRate);
            StartTimer();
        }
    }

    public void Detach()
    {
        lock (queue)
        {
            output?.Dispose();
            output = null;
            DeviceUid = null;
        }
    }

    /// <summary>Loads a song at <paramref name="seconds"/> (playing on if it was playing, with a 10 ms crossfade).</summary>
    public void Cue(MultitrackSong next, double seconds = 0)
    {
        lock (queue)
        {
            chaseSongId = null;
            if (!next.Equals(song)) Disarm();
            CueLocked(next, seconds);
        }
    }

    /// <summary>Jumps to another song (or another place) crossfading over <paramref name="crossfade"/> seconds, and plays.</summary>
    public void Crossfade(MultitrackSong to, double seconds, double crossfade)
    {
        lock (queue)
        {
            chaseSongId = null;
            Disarm();
            CueLocked(to, seconds, entrySeconds: crossfade);
            Renderer.Play();
        }
    }

    /// <summary>Fades the playing song out over <paramref name="seconds"/>, then stops.</summary>
    public void FadeOut(double seconds)
    {
        lock (queue)
        {
            if (song is null || session is null || !Renderer.Published.Playing) return;
            chaseSongId = null;
            var from = PositionLocked(ShowState.HostClock()) + 0.02;
            armed = new MultitrackTransition(song.Id, null, from + seconds, from, seconds, 0, from + seconds);
            armedSession = null;
            Publish(session);
        }
    }

    /// <summary>Arms (or with null, disarms) what happens at the end of the current song.</summary>
    public void Transition(MultitrackTransition? transition)
    {
        lock (queue)
        {
            if (Equals(transition, armed)) return;
            if (program?.Handover?.Next is { } next && Renderer.IsCurrent(next)) return;
            var nextSong = transition?.Next;
            if (nextSong is null || !nextSong.Equals(armed?.Next)) DropArmedSession();
            armed = transition;
            if (nextSong is not null && armedSession is null)
            {
                var stemReaders = TakePreloaded(nextSong, 0) ?? OpenReaders(nextSong, SampleRate, 0);
                var made = new MultitrackSession(stemReaders.Select(r => r.Ring).ToArray(), nextSong.Stems.Select(s => s.ChannelId).ToList(), 0);
                readers[made] = stemReaders;
                armedSession = made;
            }
            if (session is not null) Publish(session);
        }
    }

    public MultitrackTransition? ArmedTransition
    {
        get { lock (queue) return armed; }
    }

    private void Disarm()
    {
        armed = null;
        DropArmedSession();
    }

    private void DropArmedSession() => armedSession = null;

    /// <summary>
    /// Follows timecode: <paramref name="position"/> (song seconds) is where the song should be
    /// at <paramref name="hostTime"/> (host-clock seconds). Starts the song there if needed,
    /// steers the speed to stay locked, and jumps when it's far out.
    /// </summary>
    public void Chase(MultitrackSong? target, string songId, double position, double hostTime)
    {
        lock (queue)
        {
            var chased = target ?? (song?.Id == songId ? song : null);
            if (chased is null) return;
            var now = ShowState.HostClock();
            var playing = Renderer.Published.Playing;
            var isCurrent = program is not null && Renderer.IsCurrent(program);
            // Give a fresh start a second to get going before judging it.
            if (chaseSongId == chased.Id && !(isCurrent && playing) && now - chaseStarted < 1) return;
            if (chaseSongId != chased.Id || !chased.Equals(song) || !playing || !isCurrent)
            {
                CueLocked(chased, position, anchor: hostTime);
                Renderer.Play();
                chaseSongId = chased.Id;
                chaseStarted = now;
                chase.Reset();
                return;
            }
            var error = Renderer.Position(hostTime) - position;
            var decision = chase.Step(error, hostTime);
            if (decision.Land)
            {
                CueLocked(chased, position, anchor: hostTime);
                chaseStarted = now;
                ChaseLandings++;
            }
            else
            {
                Renderer.Speed = decision.Speed;
            }
        }
    }

    /// <summary>Opens and primes the next songs (the last two named) so they start without decoding.</summary>
    public void Preload(IReadOnlyList<MultitrackSong> songs)
    {
        double rate;
        lock (queue)
        {
            preloadWanted = songs.TakeLast(PreloadLimit).ToList();
            foreach (var stale in preloaded.Where(entry => !preloadWanted.Contains(entry.Song)).ToList())
            {
                Dispose(stale.Readers);
                preloaded.Remove(stale);
            }
            rate = SampleRate;
        }
        var wanted = songs.TakeLast(PreloadLimit).ToList();
        AudioThreads.Preload.Async(() =>
        {
            lock (preloadQueue)
            {
                foreach (var next in wanted)
                {
                    bool needed;
                    lock (queue) needed = NeedsPreload(next, rate);
                    if (!needed) continue;
                    var opened = OpenReaders(next, rate, 0);
                    lock (queue)
                    {
                        if (NeedsPreload(next, rate)) preloaded.Add((next, rate, opened));
                        else Dispose(opened);
                    }
                }
            }
        });
    }

    private bool NeedsPreload(MultitrackSong next, double rate) =>
        rate == SampleRate && preloadWanted.Contains(next) && !preloaded.Any(entry => entry.Song.Equals(next) && entry.Rate == rate);

    public IReadOnlyList<string> PreloadedSongIds
    {
        get { lock (queue) return preloaded.Select(entry => entry.Song.Id).ToList(); }
    }

    public void SetRouting(MultitrackRouting next)
    {
        lock (queue)
        {
            if (next.Equals(routing)) return;
            routing = next.Clone();
            if (session is not null) Publish(session);
        }
    }

    /// <summary>Plays from where the song is cued, at <paramref name="hostTime"/> (host-clock seconds; 0 = now).</summary>
    public void Play(double hostTime = 0)
    {
        lock (queue) Renderer.Play(hostTime);
    }

    /// <summary>Fades out over 10 ms and holds the place.</summary>
    public void Stop()
    {
        lock (queue)
        {
            chaseSongId = null;
            if (armed is not null)
            {
                Disarm();
                if (session is not null) Publish(session);
            }
            Renderer.Stop();
        }
    }

    public void Seek(double seconds)
    {
        lock (queue)
        {
            if (song is null) return;
            chaseSongId = null;
            CueLocked(song, seconds);
        }
    }

    /// <summary>The song position (seconds) at a host-clock time (default now).</summary>
    public double Position(double? hostTime = null)
    {
        lock (queue) return PositionLocked(hostTime ?? ShowState.HostClock());
    }

    private double PositionLocked(double hostTime)
    {
        // A cue the audio thread hasn't picked up yet is at its start frame.
        if (program is { } p && !Renderer.IsCurrent(p) && !(p.Handover?.Next is { } next && Renderer.IsCurrent(next)))
            return p.Session.StartFrame / SampleRate;
        return Renderer.Position(hostTime);
    }

    public IReadOnlyList<string> SongChannelIds
    {
        get { lock (queue) return song?.Stems.Select(s => s.ChannelId).ToList() ?? []; }
    }

    public bool IsPlaying => Renderer.Published.Playing;

    /// <summary>Where the song's last stem ends (seconds), once cued.</summary>
    public double? SongLength
    {
        get
        {
            lock (queue)
            {
                if (session is null || !readers.TryGetValue(session, out var group) || group.Length == 0) return null;
                return group.Max(r => r.AudioEnd) / SampleRate;
            }
        }
    }

    public string? SongId
    {
        get { lock (queue) return song?.Id; }
    }

    /// <summary>Each track channel's left and right level since the last take (fading-out songs included).</summary>
    public Dictionary<string, (LevelReading Left, LevelReading Right)> TakeMeters()
    {
        lock (queue)
        {
            var levels = new Dictionary<string, (LevelReading Left, LevelReading Right)>(StringComparer.Ordinal);
            if (session is null) return levels;
            var sessions = new List<MultitrackSession> { session };
            foreach (var old in retired.SelectMany(r => new[] { r.Program }.Concat(r.Program.Companions)))
                if (Renderer.IsHolding(old) && !sessions.Contains(old.Session)) sessions.Add(old.Session);
            foreach (var each in sessions)
            {
                var stems = MultitrackRenderer.TakeMeters(each);
                for (var stem = 0; stem < stems.Length; stem++)
                {
                    var id = each.StemChannels[stem];
                    var window = new LevelWindow();
                    if (levels.TryGetValue(id, out var held)) window.Fold(held.Left, held.Right);
                    window.Fold(stems[stem].Left, stems[stem].Right);
                    levels[id] = (window.Left, window.Right);
                }
            }
            return levels;
        }
    }

    /// <summary>Frames the audio thread had to play as silence because decoding fell behind.</summary>
    public long StarvedFrames
    {
        get { lock (queue) return session?.Rings.Sum(r => r.StarvedFrames) ?? 0; }
    }

    private void CueLocked(MultitrackSong next, double seconds, double? anchor = null, double? entrySeconds = null)
    {
        song = next;
        var start = (long)Math.Round(Math.Max(0, seconds) * SampleRate, MidpointRounding.AwayFromZero);
        StemReader[] stemReaders;
        var ready = preloaded.FindIndex(entry => entry.Song.Equals(next) && entry.Rate == SampleRate);
        // A chase landing just after the top can use the preloaded start and skip ahead.
        var nearTop = anchor is not null && seconds < PrimeSeconds - 0.25;
        if (ready >= 0 && (start == 0 || nearTop))
        {
            stemReaders = preloaded[ready].Readers;
            preloaded.RemoveAt(ready);
            start = 0;
        }
        else
        {
            stemReaders = OpenReaders(next, SampleRate, start);
            CuesDecoded++;
        }
        var made = new MultitrackSession(
            stemReaders.Select(r => r.Ring).ToArray(), next.Stems.Select(s => s.ChannelId).ToList(), start,
            anchor is { } host ? ((long)Math.Round(Math.Max(0, seconds) * SampleRate, MidpointRounding.AwayFromZero), host) : null,
            anchor is not null ? new MultitrackVarispeed(stemReaders.Length, Renderer.MaxFrames) : null,
            entrySeconds is { } entry ? (long)(Math.Max(entry, 0.01) * SampleRate) : null);
        readers[made] = stemReaders;
        session = made;
        Publish(made);
    }

    private static StemReader[] OpenReaders(MultitrackSong next, double sampleRate, long startFrame)
    {
        var capacity = (int)(RingSeconds * sampleRate);
        var opened = next.Stems.Select(stem => new StemReader(stem, sampleRate, startFrame, capacity)).ToArray();
        var prime = (int)(PrimeSeconds * sampleRate);
        foreach (var reader in opened) reader.Fill(prime);
        return opened;
    }

    private void Publish(MultitrackSession published)
    {
        var matrix = new MultitrackMatrix(routing, published.StemChannels, DeviceChannels);
        var frames = routing.DelayFrames(DeviceChannels, SampleRate);
        if (delays is null ? frames.Any(f => f > 0) : !delays.Frames.SequenceEqual(frames))
            delays = frames.Any(f => f > 0) ? new MultitrackDelays(frames) : null;
        var companions = new List<MultitrackProgram>();
        foreach (var old in Enumerable.Reverse(retired).SelectMany(r => new[] { r.Program }.Concat(r.Program.Companions)))
        {
            if (old.Session == published || !Renderer.IsHolding(old) || companions.Any(c => c.Session == old.Session)) continue;
            companions.Add(new MultitrackProgram(old.Session, new MultitrackMatrix(routing, old.Session.StemChannels, DeviceChannels), delays));
        }
        var next = new MultitrackProgram(published, matrix, delays, Handover(), companions);
        if (program is not null) retired.Add((program, ShowState.HostClock()));
        program = next;
        Renderer.Publish(next);
    }

    private void StartTimer()
    {
        tickClock ??= new ShowTimeProvider("MxU multitrack");
        timer ??= tickClock.CreateTimer(_ =>
        {
            if (!queue.TryEnter()) return;
            try { Tick(); }
            finally { queue.Exit(); }
        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
    }

    // Every 20 ms: notice handovers, forget programs the audio thread is done with, top up the rings.
    private void Tick()
    {
        var now = ShowState.HostClock();
        PromoteHandover();
        retired.RemoveAll(entry => !Holds(entry.Program) && now - entry.At > 1);
        var held = (program is null ? [] : new[] { program }).Concat(retired.Select(r => r.Program))
            .SelectMany(p => new[] { p }.Concat(p.Companions));
        var live = new HashSet<MultitrackSession>(held.Select(p => p.Session), ReferenceEqualityComparer.Instance);
        if (armedSession is not null) live.Add(armedSession);
        foreach (var gone in readers.Keys.Where(s => !live.Contains(s)).ToList())
        {
            Dispose(readers[gone]);
            readers.Remove(gone);
        }
        foreach (var group in readers.Values)
            foreach (var reader in group) reader.Fill();
    }

    private bool Holds(MultitrackProgram held) =>
        Renderer.IsHolding(held) || held.Companions.Any(Renderer.IsHolding)
        || (held.Handover?.Next is { } next && Renderer.IsHolding(next));

    private MultitrackHandover? Handover()
    {
        if (armed is not { } transition || transition.FromSongId != song?.Id) return null;
        var rate = SampleRate;
        long Frames(double seconds) => (long)Math.Round(Math.Max(seconds, 0) * rate, MidpointRounding.AwayFromZero);
        var outgoing = MultitrackShape.Flat;
        if (transition.FadeFrom is { } fadeFrom)
        {
            outgoing.FadeFrom = Frames(fadeFrom);
            outgoing.FadeFrames = Math.Max(Frames(transition.FadeSeconds), 1);
            // Into a crossfade: equal power, so the two sum steadily. Into silence: a smooth cosine.
            outgoing.FadeCurve = transition.FadeInSeconds > 0 ? MultitrackShape.Curve.EqualPower : MultitrackShape.Curve.Cosine;
        }
        outgoing.End = Frames(transition.End);
        MultitrackProgram? next = null;
        if (transition.Next is not null && armedSession is { } nextSession)
            next = new MultitrackProgram(nextSession, new MultitrackMatrix(routing, nextSession.StemChannels, DeviceChannels), delays);
        if (transition.Next is not null && next is null) return null;
        var incoming = MultitrackShape.Flat;
        if (transition.FadeInSeconds > 0)
        {
            incoming.InFrames = Math.Max(Frames(transition.FadeInSeconds), 1);
            incoming.InCurve = MultitrackShape.Curve.EqualPower;
        }
        return new MultitrackHandover(next, Frames(transition.NextAt), outgoing, incoming);
    }

    private void PromoteHandover()
    {
        var made = Renderer.Handovers;
        if (made == seenHandovers) return;
        seenHandovers = made;
        var next = armed?.Next;
        var nextSession = armedSession;
        Disarm();
        if (next is not null && nextSession is not null)
        {
            song = next;
            session = nextSession;
            Publish(nextSession);
        }
        else if (session is not null)
        {
            Publish(session);
        }
        var handover = OnHandover;
        if (handover is not null) AudioThreads.Events.Async(handover);
    }

    private StemReader[]? TakePreloaded(MultitrackSong next, double seconds)
    {
        if (seconds != 0) return null;
        var ready = preloaded.FindIndex(entry => entry.Song.Equals(next) && entry.Rate == SampleRate);
        if (ready < 0) return null;
        var found = preloaded[ready].Readers;
        preloaded.RemoveAt(ready);
        return found;
    }

    /// <summary>One tick, now (tests drive the engine with this instead of the timer).</summary>
    internal void TickNow()
    {
        lock (queue) Tick();
    }

    private float[][] Slice(int channels, int frames)
    {
        if (sliceScratch.Length != channels || sliceScratch[0].Length < frames)
        {
            sliceScratch = new float[channels][];
            for (var channel = 0; channel < channels; channel++) sliceScratch[channel] = new float[Math.Max(frames, 4096)];
        }
        return sliceScratch;
    }

    private static void Dispose(StemReader[] group)
    {
        foreach (var reader in group) reader.Dispose();
    }

    public void Dispose()
    {
        lock (queue)
        {
            timer?.Dispose();
            timer = null;
            tickClock?.Dispose();
            tickClock = null;
            output?.Dispose();
            output = null;
            foreach (var group in readers.Values) Dispose(group);
            readers.Clear();
            foreach (var entry in preloaded) Dispose(entry.Readers);
            preloaded.Clear();
        }
    }
}
