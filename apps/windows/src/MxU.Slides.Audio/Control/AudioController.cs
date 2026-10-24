using MxU.Slides.Core.Automation;
using MxU.Slides.Audio.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Audio.Control;

/// <summary>One bus as the Local API reports it (the same fields as <c>ApiAudioBus</c>).</summary>
public sealed record AudioBusStatus(string? PlaylistId, string? AudioItemId, string? TrackName, bool Playing, double? Position, double? Duration);

/// <summary>The buttons the API's /v1/audio/transport and the operator's transport bar press.</summary>
public enum AudioTransportAction
{
    PlayPause,
    Next,
    Previous,
    Seek,
}

/// <summary>A mix's own level: its fader and mute, and whether it reaches a device (false = stream only).</summary>
public readonly record struct AudioMixLevel(float Gain, bool Muted, bool HardwareLive)
{
    public static readonly AudioMixLevel Unity = new(1, false, true);
}

/// <summary>
/// The Audio function: every playing bus (one per mix), the room's fade and crossfade
/// settings and the master level. Fire a playlist, a library item or a timeline clip;
/// stop one or all; press transport buttons; read the buses' status. Port of the Mac's
/// AudioController (AudioController.swift) minus the mixer inventory, which the app
/// supplies through <see cref="RoutingFor"/> and <see cref="MixLevels"/>.
/// <para>
/// Threading: every member is thread-safe (one lock). <see cref="PlayersChanged"/>,
/// <see cref="TransportChanged"/> and <see cref="OutputStatusChanged"/> are raised on the
/// <see cref="SynchronizationContext"/> the controller was made on (the WPF UI thread when
/// made there), or on a thread-pool thread when there was none. Nothing here throws for a
/// missing audio device; see <see cref="OutputStatus"/>.
/// </para>
/// </summary>
public sealed class AudioController : IDisposable
{
    private readonly Lock gate = new();
    private readonly IAudioLibrary library;
    private readonly SynchronizationContext? context;
    private readonly Func<string, AudioEngine> makeEngine;
    private readonly List<AudioPlayer> players = [];
    private readonly ITimer? poll;
    private int pollQueued;
    private float masterVolume = 1;
    private bool muted;
    private double crossfadeSeconds = PlaylistCrossfade.DefaultSeconds;
    private bool disposed;

    /// <param name="engineFactory">Makes each bus's engine (tests make offline ones). Default: a real one.</param>
    /// <param name="startsPolling">False for tests, which call <see cref="PollNow"/>.</param>
    public AudioController(IAudioLibrary library, SynchronizationContext? context = null, Func<string, AudioEngine>? engineFactory = null,
        bool startsPolling = true)
    {
        this.library = library;
        this.context = context ?? SynchronizationContext.Current;
        makeEngine = engineFactory ?? (key => new AudioEngine($"Audio {key}"));
        // Polls run with the engines' notifications (in order with a track ending), never piling up.
        if (startsPolling)
            poll = ShowTimeProvider.Shared.CreateTimer(_ =>
            {
                if (Interlocked.Exchange(ref pollQueued, 1) == 1) return;
                AudioThreads.Events.Async(() =>
                {
                    Volatile.Write(ref pollQueued, 0);
                    PollNow();
                });
            }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    /// <summary>Where a playlist plays (null = a single item or clip). Default: the main mix on the default device.</summary>
    public Func<string?, AudioRouting> RoutingFor { get; set; } = _ => AudioRouting.Main;

    /// <summary>A mix's fader, mute and whether it reaches hardware, by routing key. Default: unity.</summary>
    public Func<string, AudioMixLevel> MixLevels { get; set; } = _ => AudioMixLevel.Unity;

    /// <summary>A playlist's remembered bus fader and mute (the app persists them). Default: unity, unmuted.</summary>
    public Func<string, (float Gain, bool Muted)> PlaylistLevels { get; set; } = _ => (1, false);

    /// <summary>Raised when a bus starts or goes.</summary>
    public event Action? PlayersChanged;

    /// <summary>Raised when what's playing, or where, changes (and every 250 ms while something plays).</summary>
    public event Action? TransportChanged;

    /// <summary>A bus's output device status changed (a device unplugged, say).</summary>
    public event Action<AudioOutputStatus>? OutputStatusChanged;

    /// <summary>Fade transport moves (start, pause, stop) over <see cref="FadeSeconds"/>.</summary>
    public bool FadeEnabled { get; set; } = true;

    public double FadeSeconds { get; set; } = 1.5;

    private double TransportFade => FadeEnabled ? Math.Max(0, FadeSeconds) : 0;

    /// <summary>The room's crossfade for playlists that don't set their own.</summary>
    public double CrossfadeSeconds
    {
        get { lock (gate) return crossfadeSeconds; }
        set
        {
            lock (gate) crossfadeSeconds = value;
            PlaylistDocumentChanged();
        }
    }

    public float MasterVolume
    {
        get { lock (gate) return masterVolume; }
        set
        {
            lock (gate) masterVolume = Math.Clamp(value, 0, 1);
            ReapplyAllMixLevels();
        }
    }

    public bool IsMuted
    {
        get { lock (gate) return muted; }
        set
        {
            lock (gate) muted = value;
            ReapplyAllMixLevels();
        }
    }

    public IReadOnlyList<AudioPlayer> Players
    {
        get { lock (gate) return players.ToList(); }
    }

    /// <summary>Every bus, as the Local API's <c>ApiAudioStatus.buses</c>.</summary>
    public IReadOnlyList<AudioBusStatus> Status()
    {
        lock (gate) return players.Select(p => p.Status).ToList();
    }

    /// <summary>The bus to show in a now-playing line: the first playing, else the first with a track.</summary>
    public (string Name, double Elapsed, double Duration, bool IsPlaying)? NowPlaying
    {
        get
        {
            lock (gate)
            {
                var candidate = players.FirstOrDefault(p => p.IsPlaying) ?? players.FirstOrDefault(p => p.CurrentTrackName is not null);
                if (candidate?.CurrentTrackName is not { } name) return null;
                return (name, candidate.Elapsed, candidate.Duration, candidate.IsPlaying);
            }
        }
    }

    /// <summary>The worst output status among the buses (a missing device shows over a healthy one).</summary>
    public AudioOutputStatus OutputStatus
    {
        get
        {
            lock (gate)
            {
                var statuses = players.Select(p => p.Engine.OutputStatus).ToList();
                return statuses.FirstOrDefault(s => s.State is AudioOutputState.DeviceMissing or AudioOutputState.NoDevice or AudioOutputState.Failed)
                    ?? statuses.FirstOrDefault() ?? AudioOutputStatus.Idle;
            }
        }
    }

    public AudioPlayer? PlayerForPlaylist(string id)
    {
        lock (gate) return players.FirstOrDefault(p => p.PlaylistId == id);
    }

    public AudioPlayer? PlayerForAudioItem(string id)
    {
        lock (gate) return players.FirstOrDefault(p => p.SingleItemId == id);
    }

    // MARK: Firing

    /// <summary>
    /// Plays a playlist on its mix (from <paramref name="startAt"/> when given). Firing the
    /// playlist that's playing again picks up edits; firing a different entry jumps with a crossfade.
    /// Returns what it replaced on that mix (the show state dismisses those cues).
    /// </summary>
    /// <param name="repeat">Override the playlist's own loop setting (true = loop, false = play once).</param>
    public IReadOnlyList<(string? PlaylistId, string? AudioItemId)> Play(Playlist playlist, string? startAt = null, bool? repeat = null)
    {
        IReadOnlyList<(string?, string?)> replaced = [];
        lock (gate)
        {
            if (players.FirstOrDefault(p => p.PlaylistId == playlist.Id) is { } existing)
            {
                existing.Play(playlist, startAt, repeat);
            }
            else
            {
                var routing = RoutingFor(playlist.Id);
                replaced = ClaimTarget(routing.Key);
                var player = MakePlayer(routing);
                var levels = PlaylistLevels(playlist.Id);
                player.BusGain = levels.Gain;
                player.BusMuted = levels.Muted;
                ApplyLevels(player);
                player.Play(playlist, startAt, repeat);
                players.Add(player);
                Raise(PlayersChanged);
            }
        }
        NotifyTransport();
        return replaced;
    }

    /// <summary>Plays one library item on the main mix (no playlist semantics).</summary>
    public IReadOnlyList<(string? PlaylistId, string? AudioItemId)> Play(AudioItem item, bool? repeat = null)
    {
        IReadOnlyList<(string?, string?)> replaced = [];
        lock (gate)
        {
            var routing = RoutingFor(null);
            if (players.FirstOrDefault(p => p.TargetKey == routing.Key) is { SingleItemId: not null } existing)
            {
                existing.Play(item, repeat);
            }
            else
            {
                replaced = ClaimTarget(routing.Key);
                var player = MakePlayer(routing);
                ApplyLevels(player);
                player.Play(item, repeat);
                players.Add(player);
                Raise(PlayersChanged);
            }
        }
        NotifyTransport();
        return replaced;
    }

    /// <summary>Plays a timeline music clip on the main mix.</summary>
    public IReadOnlyList<(string? PlaylistId, string? AudioItemId)> Play(TimelineMusicClip clip)
    {
        IReadOnlyList<(string?, string?)> replaced = [];
        lock (gate)
        {
            var routing = RoutingFor(null);
            if (players.FirstOrDefault(p => p.TargetKey == routing.Key) is { SingleItemId: not null } existing)
            {
                existing.Play(clip);
            }
            else
            {
                replaced = ClaimTarget(routing.Key);
                var player = MakePlayer(routing);
                ApplyLevels(player);
                player.Play(clip);
                players.Add(player);
                Raise(PlayersChanged);
            }
        }
        NotifyTransport();
        return replaced;
    }

    /// <summary>Stops every bus (with the transport fade).</summary>
    public void Stop()
    {
        lock (gate)
        {
            foreach (var player in players.ToList()) Retire(player, null, AudioFadeCurve.Linear);
            players.Clear();
            Raise(PlayersChanged);
        }
        NotifyTransport();
    }

    /// <summary>Stops the bus playing a playlist; returns its cue, or null if it wasn't playing.</summary>
    public (string? PlaylistId, string? AudioItemId)? StopPlaylist(string playlistId) => StopPlayer(p => p.PlaylistId == playlistId, null, AudioFadeCurve.Linear);

    /// <summary>Stops the bus playing an item, optionally with its own fade and curve.</summary>
    public (string? PlaylistId, string? AudioItemId)? StopAudioItem(string audioItemId, double? fade = null, AudioFadeCurve curve = AudioFadeCurve.Linear) =>
        StopPlayer(p => p.SingleItemId == audioItemId, fade, curve);

    private (string? PlaylistId, string? AudioItemId)? StopPlayer(Func<AudioPlayer, bool> match, double? fade, AudioFadeCurve curve)
    {
        (string?, string?)? cue;
        lock (gate)
        {
            if (players.FirstOrDefault(match) is not { } player) return null;
            cue = player.Cue;
            Retire(player, fade, curve);
            players.Remove(player);
            Raise(PlayersChanged);
        }
        NotifyTransport();
        return cue;
    }

    /// <summary>
    /// A transport button for the bus that's playing (else the first): play/pause, next,
    /// previous, or seek to <paramref name="position"/> seconds.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing is on the Audio function, or seek has no position.</exception>
    public void Transport(AudioTransportAction action, double? position = null)
    {
        lock (gate)
        {
            var player = players.FirstOrDefault(p => p.IsPlaying) ?? players.FirstOrDefault()
                ?? throw new InvalidOperationException("Nothing is on the Audio function.");
            switch (action)
            {
                case AudioTransportAction.PlayPause:
                    player.TogglePlayPause();
                    break;
                case AudioTransportAction.Next:
                    player.Next();
                    break;
                case AudioTransportAction.Previous:
                    player.Previous();
                    break;
                case AudioTransportAction.Seek:
                    player.Seek(position ?? throw new InvalidOperationException("'seek' needs 'position' (seconds)."));
                    break;
            }
        }
        NotifyTransport();
    }

    /// <summary>Reads the API's action names ("playPause", "next", "previous", "seek").</summary>
    public static AudioTransportAction? ParseTransportAction(string raw) => raw switch
    {
        "playPause" => AudioTransportAction.PlayPause,
        "next" => AudioTransportAction.Next,
        "previous" => AudioTransportAction.Previous,
        "seek" => AudioTransportAction.Seek,
        _ => null,
    };

    /// <summary>A playlist document changed (or the room crossfade): every playing bus picks it up.</summary>
    public void PlaylistDocumentChanged()
    {
        lock (gate)
            foreach (var player in players) player.RefreshFromDocument();
    }

    /// <summary>Sets a bus's own fader and mute (the app persists them per playlist).</summary>
    public void SetGain(AudioPlayer player, float gain)
    {
        lock (gate)
        {
            player.BusGain = Math.Clamp(gain, 0, 1);
            ApplyLevels(player);
        }
    }

    public void SetMuted(AudioPlayer player, bool isMuted)
    {
        lock (gate)
        {
            player.BusMuted = isMuted;
            ApplyLevels(player);
        }
    }

    /// <summary>Re-reads <see cref="MixLevels"/> for every bus (after a mix fader moves).</summary>
    public void ReapplyAllMixLevels()
    {
        lock (gate)
            foreach (var player in players) ApplyLevels(player);
    }

    /// <summary>Re-reads <see cref="RoutingFor"/> for every bus (after outputs or devices change).</summary>
    public void ReapplyAllRouting()
    {
        lock (gate)
            foreach (var player in players) player.ConfigureRouting(RoutingFor(player.PlaylistId));
    }

    /// <summary>Per-bus transport commands, for the UI's own controls on one bus.</summary>
    public void TogglePlayPause(AudioPlayer player) => OnPlayer(player, p => p.TogglePlayPause());

    public void Next(AudioPlayer player) => OnPlayer(player, p => p.Next());

    public void Previous(AudioPlayer player) => OnPlayer(player, p => p.Previous());

    public void Seek(AudioPlayer player, double seconds) => OnPlayer(player, p => p.Seek(seconds));

    private void OnPlayer(AudioPlayer player, Action<AudioPlayer> action)
    {
        lock (gate) action(player);
        NotifyTransport();
    }

    // MARK: Internals

    /// <summary>Runs on the controller's lock (the players' engine callbacks come through here).</summary>
    internal void Run(Action action)
    {
        lock (gate)
        {
            if (!disposed) action();
        }
    }

    internal T Run<T>(Func<T> action)
    {
        lock (gate) return action();
    }

    internal void NotifyTransport() => Raise(TransportChanged);

    private void Raise(Action? handlers)
    {
        if (handlers is null) return;
        if (context is not null) context.Post(_ => handlers(), null);
        else AudioThreads.Events.Async(handlers);
    }

    private void ApplyLevels(AudioPlayer player)
    {
        var mix = MixLevels(player.TargetKey);
        player.ApplyMix(masterVolume * mix.Gain, muted || mix.Muted, mix.HardwareLive);
    }

    // One bus per mix: firing onto a mix stops what was there.
    private IReadOnlyList<(string? PlaylistId, string? AudioItemId)> ClaimTarget(string key)
    {
        var holders = players.Where(p => p.TargetKey == key).ToList();
        foreach (var holder in holders) Retire(holder, null, AudioFadeCurve.Linear);
        players.RemoveAll(p => p.TargetKey == key);
        return holders.Select(h => h.Cue).ToList();
    }

    private AudioPlayer MakePlayer(AudioRouting routing)
    {
        var engine = makeEngine(routing.Key);
        engine.OutputStatusChanged += status =>
        {
            var handlers = OutputStatusChanged;
            if (handlers is null) return;
            if (context is not null) context.Post(_ => handlers(status), null);
            else handlers(status);
        };
        var player = new AudioPlayer(this, engine, library, () => TransportFade, () => crossfadeSeconds);
        player.ConfigureRouting(routing);
        ApplyLevels(player);
        return player;
    }

    // Stops a bus and lets its engine go once the fade has finished.
    private void Retire(AudioPlayer player, double? fade, AudioFadeCurve curve)
    {
        player.Stop(fade, curve);
        var linger = (fade ?? TransportFade) + 0.2;
        var engine = player.Engine;
        _ = Task.Delay(TimeSpan.FromSeconds(linger)).ContinueWith(_ => engine.Dispose(), TaskScheduler.Default);
    }

    /// <summary>One poll of every bus now (the timer does this every 250 ms).</summary>
    public void PollNow()
    {
        var any = false;
        lock (gate)
        {
            if (disposed) return;
            foreach (var player in players.ToList())
            {
                player.Poll();
                any = true;
            }
        }
        if (any) NotifyTransport();
    }

    public void Dispose()
    {
        poll?.Dispose();
        lock (gate)
        {
            disposed = true;
            foreach (var player in players) player.Engine.Dispose();
            players.Clear();
        }
    }
}
