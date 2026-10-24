using MxU.Slides.Audio.Multitrack;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Audio.Control;

/// <summary>
/// The Audio function as Core's timeline sees it (<see cref="ITimelineMusic"/>): the music
/// lane's clips play, seek and stop on the <see cref="AudioController"/>'s buses. Plug it
/// into <c>ITimelineHost.Music</c>. Thread-safe (the controller is).
/// </summary>
public sealed class TimelineMusic(AudioController audio) : ITimelineMusic
{
    /// <summary>A clip started; with the cues it replaced on its mix (the show state dismisses those, then marks the clip live).</summary>
    public event Action<TimelineMusicClip, IReadOnlyList<(string? PlaylistId, string? AudioItemId)>>? Played;

    /// <summary>An item's bus stopped (the show state dismisses its cue).</summary>
    public event Action<(string? PlaylistId, string? AudioItemId)>? Stopped;

    public MusicPlayerState? Player(string audioItemId) => State(audio.PlayerForAudioItem(audioItemId));

    public MusicPlayerState? ClipPlayer => State(audio.Players.FirstOrDefault(p => p.ClipId is not null));

    /// <summary>Moves the item's player to <paramref name="seconds"/> into the clip.</summary>
    public void Seek(string audioItemId, double seconds)
    {
        if (audio.PlayerForAudioItem(audioItemId) is { } player) audio.Seek(player, seconds);
    }

    public void Play(TimelineMusicClip clip)
    {
        var replaced = audio.Play(clip);
        Played?.Invoke(clip, replaced);
    }

    /// <summary>Stops an item's player: with <paramref name="fadeSeconds"/>, or null for the room's transport fade (as on the Mac).</summary>
    public void Stop(string audioItemId, double? fadeSeconds = null, AudioFadeCurve curve = AudioFadeCurve.Linear)
    {
        if (audio.StopAudioItem(audioItemId, fadeSeconds, curve) is { } cue) Stopped?.Invoke(cue);
    }

    private static MusicPlayerState? State(AudioPlayer? player) =>
        player is null ? null : new MusicPlayerState(player.SingleItemId, player.ClipId, player.IsPlaying, player.Elapsed);
}

/// <summary>
/// Core's <see cref="ITracksPlayer"/> over a <see cref="MultitrackEngine"/> in this process.
/// Commands run in order on a background thread (cueing a song decodes its first moments,
/// which mustn't hold up the UI); the position and song are read straight from the engine.
/// Simplest to run; for tracks that survive an app crash use <see cref="RemoteTracksPlayer"/>.
/// </summary>
public sealed class LocalTracksPlayer(MultitrackEngine engine) : ITracksPlayer, IDisposable
{
    private readonly SerialQueue commands = new("MxU tracks commands");
    private readonly Lock gate = new();
    private IReadOnlyList<MultitrackSong> preloaded = [];

    public MultitrackEngine Engine { get; } = engine;

    public string? LatestSongId => Engine.SongId;

    public bool Reattaching => false;

    public double? Position(string songId) => Engine.IsPlaying && Engine.SongId == songId ? Engine.Position() : null;

    public void Play(MultitrackSong song, double from) => commands.Async(() =>
    {
        Engine.Cue(song, from);
        Engine.Play();
    });

    public void Crossfade(MultitrackSong? to, double from, double seconds) => commands.Async(() =>
    {
        if (to is null) Engine.FadeOut(seconds);
        else Engine.Crossfade(to, from, seconds);
    });

    public void Stop() => commands.Async(Engine.Stop);

    public void Seek(double position) => commands.Async(() => Engine.Seek(position));

    public void Chase(MultitrackSong song, double position, double hostTime) =>
        commands.Async(() => Engine.Chase(song, song.Id, position, hostTime));

    public void Transition(MultitrackTransition? transition) => commands.Async(() => Engine.Transition(transition));

    public void CueAhead(IReadOnlyList<MultitrackSong> songs)
    {
        lock (gate)
        {
            if (songs.SequenceEqual(preloaded)) return;
            preloaded = songs.ToList();
        }
        Engine.Preload(songs);
    }

    /// <summary>Plays on <paramref name="device"/> (null = the default). Never throws; see <see cref="MultitrackEngine.OutputStatus"/>.</summary>
    public void Attach(AudioOutputDevice? device) => commands.Async(() => Engine.Attach(device));

    public void SetRouting(MultitrackRouting routing) => commands.Async(() => Engine.SetRouting(routing));

    /// <summary>Runs <paramref name="action"/> after every command sent so far (tests, and an orderly shutdown).</summary>
    public void AfterPending(Action action) => commands.Sync(action);

    public void Dispose() => commands.Dispose();
}

/// <summary>
/// Core's <see cref="ITracksPlayer"/> over the separate tracks player process (so the
/// tracks keep playing if the app restarts), talking through <see cref="TracksPlayerClient"/>.
/// It starts the player when first needed, remembers the routing, device and preloads to
/// send again whenever it (re)connects, and turns the player's status into positions and
/// meters. Mixes the player streams come back through <see cref="ShareAudio"/>. Port of the
/// transport half of the Mac's TracksController (the mixer half needs the app's mixer).
/// </summary>
public sealed class RemoteTracksPlayer : ITracksPlayer, IDisposable
{
    private readonly TracksPlayerClient client;
    private readonly string socketPath;
    private readonly Lock gate = new();
    private TracksPlayerStatus? latest;
    private string? driving;
    private double? lostAt;
    private bool awaitingStatus;
    private MultitrackRouting? routing;
    private MultitrackRouting? sentRouting;
    private (bool Set, string? Uid) device;
    private (bool Set, string? Uid) sentDevice;
    private IReadOnlyList<MultitrackSong> preloaded = [];
    private MultitrackTransition? armed;
    private TracksShareFeeder? feeder;

    /// <param name="launch">Starts a player process (default: this executable with <see cref="TracksPlayerHost.Argument"/>).</param>
    public RemoteTracksPlayer(string socketPath, Action? launch = null)
    {
        this.socketPath = socketPath;
        client = new TracksPlayerClient(socketPath, launch ?? (() => TracksPlayerHost.Launch(socketPath)));
        client.OnStatus = Heard;
        client.OnConnect = Connected;
        client.OnDisconnect = Disconnected;
    }

    /// <summary>The player for this app (one per app identity, shared by its windows).</summary>
    public static RemoteTracksPlayer ForApp(string appIdentity = "MxU Slides") => new(TracksPlayerWire.SocketPath(appIdentity));

    /// <summary>The player was already playing a song when we connected (the app restarted mid-song). Client thread.</summary>
    public event Action<TracksPlayerStatus>? FoundPlaying;

    /// <summary>A streamed mix's audio, by share slot, 48 kHz stereo. Feeder thread.</summary>
    public event Action<int, PcmBuffer>? ShareAudio;

    /// <summary>The connection dropped (and why). Client thread.</summary>
    public event Action<string>? ConnectionLost;

    public bool IsConnected => client.IsConnected;

    /// <summary>The player's last status report, if connected.</summary>
    public TracksPlayerStatus? Latest
    {
        get { lock (gate) return latest; }
    }

    /// <summary>Connects to a player that's already running (call at launch; it never starts one).</summary>
    public void Start() => client.Reattach();

    /// <summary>Asks the player to stop and exit (the app is quitting for good).</summary>
    public void Quit()
    {
        if (client.IsConnected) client.Send(new TracksPlayerMessage.Quit());
    }

    public string? LatestSongId
    {
        get { lock (gate) return latest?.SongId; }
    }

    public bool Reattaching
    {
        get { lock (gate) return lostAt is { } at && ShowState.HostClock() - at < 1.2; }
    }

    public double? Position(string songId)
    {
        lock (gate) return latest is { Playing: true } status && status.SongId == songId ? status.Position() : null;
    }

    /// <summary>Each track channel's level as the player last reported it (left, right).</summary>
    public Dictionary<string, IReadOnlyList<LevelReading>> Meters
    {
        get { lock (gate) return latest?.MeterReadings() ?? []; }
    }

    /// <summary>The tracks mixer (sent now if connected, and again on every reconnect).</summary>
    public void SetRouting(MultitrackRouting next)
    {
        lock (gate) routing = next.Clone();
        Refresh();
    }

    /// <summary>The device the tracks play on (null = the Windows default).</summary>
    public void Attach(string? deviceUid)
    {
        lock (gate) device = (true, deviceUid);
        Refresh();
    }

    public void Play(MultitrackSong song, double from)
    {
        lock (gate)
        {
            armed = null;
            driving = song.Id;
        }
        Refresh(force: true);
        client.Send(new TracksPlayerMessage.Cue(song, from));
        client.Send(new TracksPlayerMessage.Play(0));
    }

    public void Stop()
    {
        lock (gate)
        {
            armed = null;
            if (driving is null) return;
        }
        client.Send(new TracksPlayerMessage.Stop());
    }

    public void Seek(double position) => client.Send(new TracksPlayerMessage.Seek(position));

    public void Transition(MultitrackTransition? transition)
    {
        lock (gate)
        {
            if (Equals(transition, armed)) return;
            armed = transition;
        }
        client.Send(new TracksPlayerMessage.Transition(transition));
    }

    public void Crossfade(MultitrackSong? to, double from, double seconds)
    {
        lock (gate)
        {
            armed = null;
            if (to is not null) driving = to.Id;
        }
        Refresh(force: to is not null);
        client.Send(new TracksPlayerMessage.Crossfade(to, from, seconds));
    }

    public void Chase(MultitrackSong song, double position, double hostTime)
    {
        bool known;
        lock (gate)
        {
            driving = song.Id;
            known = latest?.SongId == song.Id;
        }
        Refresh(force: true);
        // A song the player already has goes by id, so a timecode chase isn't a big message ten times a second.
        client.Send(new TracksPlayerMessage.Chase(known ? null : song, song.Id, position, hostTime));
    }

    public void CueAhead(IReadOnlyList<MultitrackSong> songs)
    {
        lock (gate)
        {
            if (songs.SequenceEqual(preloaded)) return;
            preloaded = songs.ToList();
        }
        client.Send(new TracksPlayerMessage.Preload(songs));
    }

    // Sends the device and routing if they changed (only once the player is in use).
    private void Refresh(bool force = false)
    {
        var messages = new List<TracksPlayerMessage>();
        // Asked before taking the lock: the client's thread may be waiting for it.
        var connected = force || client.IsConnected;
        lock (gate)
        {
            if (!connected && driving is null) return;
            if (device.Set && sentDevice != device)
            {
                sentDevice = device;
                messages.Add(new TracksPlayerMessage.Attach(device.Uid));
            }
            if (routing is not null && !routing.Equals(sentRouting))
            {
                sentRouting = routing.Clone();
                messages.Add(new TracksPlayerMessage.Routing(routing.Clone()));
            }
        }
        foreach (var message in messages) client.Send(message);
    }

    private void Heard(TracksPlayerStatus status)
    {
        bool resend, found;
        lock (gate)
        {
            latest = status;
            resend = awaitingStatus;
            awaitingStatus = false;
            found = status.Playing && status.SongId is not null && driving is null;
            if (found) driving = status.SongId;
        }
        if (resend)
        {
            // A fresh connection: the player may be new, so tell it everything again.
            IReadOnlyList<MultitrackSong> songs;
            lock (gate)
            {
                sentRouting = null;
                sentDevice = default;
                songs = preloaded;
            }
            Refresh(force: true);
            if (songs.Count > 0) client.Send(new TracksPlayerMessage.Preload(songs));
        }
        if (found) FoundPlaying?.Invoke(status);
    }

    private void Connected()
    {
        lock (gate)
        {
            awaitingStatus = true;
            lostAt = null;
            feeder?.Stop();
            feeder = TracksShare.Open(TracksPlayerWire.ShareName(socketPath)) is { } share
                ? new TracksShareFeeder(share, (slot, buffer) => ShareAudio?.Invoke(slot, buffer))
                : null;
        }
    }

    private void Disconnected(string reason)
    {
        lock (gate)
        {
            feeder?.Stop();
            feeder = null;
            latest = null;
            lostAt = ShowState.HostClock();
        }
        ConnectionLost?.Invoke(reason);
    }

    public void Dispose()
    {
        client.Disconnect();
        lock (gate)
        {
            feeder?.Stop();
            feeder = null;
        }
    }
}
