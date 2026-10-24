using MxU.Slides.Audio.Control;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Audio.Video;

/// <summary>
/// The sound of every video that plays with sound, one <see cref="VideoSoundPlayer"/> per
/// playing video (by the video hub's playback key), all on the video sound's output. The
/// Windows stand-in for the sound side of the Mac's MediaEngine: setAudioOutputDevice,
/// setAudioOutputDelay and setAudioVolume, applied to every player, now and later.
/// <para>
/// Opening a file or a device takes a moment, so it happens on this class's own thread
/// ("MxU video sound"), never the caller's (the UI thread) and never .NET's shared pool.
/// Every member is cheap and thread-safe.
/// </para>
/// </summary>
public sealed class VideoSound : IDisposable
{
    private static readonly WorkQueue Work = new("MxU video sound", ThreadPriority.AboveNormal);

    /// <summary>How long a closing video's sound takes to fade before its player goes.</summary>
    private static readonly TimeSpan CloseFade = TimeSpan.FromMilliseconds(60);

    private readonly Lock gate = new();
    private readonly Dictionary<string, VideoSoundPlayer> players = new(StringComparer.Ordinal);
    private readonly Func<string, VideoSoundOptions, VideoSoundPlayer> make;
    private AudioRouting routing = AudioRouting.Main;
    private bool reachesDevice = true;
    private float volume = 1;
    private bool disposed;

    /// <param name="factory">Makes each player (tests make offline ones). Default: real ones.</param>
    public VideoSound(Func<string, VideoSoundOptions, VideoSoundPlayer>? factory = null)
    {
        make = factory ?? ((path, options) => new VideoSoundPlayer(path, options));
    }

    /// <summary>A player's output changed state (a device came or went). Raised on an audio thread.</summary>
    public event Action<AudioOutputStatus>? OutputStatusChanged;

    /// <summary>The playback keys with a player.</summary>
    public IReadOnlyList<string> Keys
    {
        get { lock (gate) return [.. players.Keys]; }
    }

    /// <summary>Where every video's sound plays (and whether it reaches a device at all).</summary>
    public AudioRouting Routing
    {
        get { lock (gate) return routing; }
    }

    /// <summary>
    /// Starts a video's sound (silent until <see cref="Follow"/> says the picture is moving).
    /// Does nothing if the key already has one.
    /// </summary>
    public void Attach(string key, string path, VideoSoundOptions? options = null)
    {
        VideoSoundPlayer player;
        lock (gate)
        {
            if (disposed || players.ContainsKey(key)) return;
            player = make(path, options ?? new VideoSoundOptions());
            player.Volume = volume;
            player.OutputStatusChanged += status => OutputStatusChanged?.Invoke(status);
            players[key] = player;
        }
        Work.Async(() =>
        {
            player.Open();
            AudioRouting where;
            bool live;
            lock (gate)
            {
                // Gone already (cleared before it opened): the Detach queued after this disposes it.
                if (!players.ContainsValue(player)) return;
                (where, live) = (routing, reachesDevice);
            }
            player.Configure(where, live);
        });
    }

    /// <summary>The video's clock now; its sound follows.</summary>
    public void Follow(string key, VideoClockSample sample) => Player(key)?.Follow(sample);

    /// <summary>The video jumped (a seek, a restart, a loop), to <paramref name="to"/> seconds into the file when known.</summary>
    public void Discontinuity(string key, double? to = null) => Player(key)?.Discontinuity(to);

    /// <summary>Stops a video's sound, with a short fade.</summary>
    public void Detach(string key)
    {
        VideoSoundPlayer? player;
        lock (gate)
            if (!players.Remove(key, out player)) return;
        player.IsMuted = true;
        Work.AsyncAfter(CloseFade, player.Dispose);
    }

    /// <summary>Plays every video's sound (now and later) on <paramref name="where"/>; false for "no device".</summary>
    public void SetRouting(AudioRouting where, bool deviceLive = true)
    {
        List<VideoSoundPlayer> all;
        lock (gate)
        {
            routing = where;
            reachesDevice = deviceLive;
            all = [.. players.Values];
        }
        foreach (var player in all) Work.Async(() => player.Configure(where, deviceLive));
    }

    /// <summary>The video sound's level, 0…1 (linear), for every player.</summary>
    public float Volume
    {
        get { lock (gate) return volume; }
        set
        {
            List<VideoSoundPlayer> all;
            lock (gate)
            {
                volume = Math.Clamp(value, 0, 1);
                all = [.. players.Values];
            }
            foreach (var player in all) player.Volume = value;
        }
    }

    public VideoSoundStatus? Status(string key) => Player(key)?.Status;

    /// <summary>True when the key's file turned out to have no sound we can play (the caller can let the video player have it).</summary>
    public bool HasNoSound(string key) => Player(key)?.HasNoSound == true;

    private VideoSoundPlayer? Player(string key)
    {
        lock (gate) return players.GetValueOrDefault(key);
    }

    public void Dispose()
    {
        List<string> keys;
        lock (gate)
        {
            disposed = true;
            keys = [.. players.Keys];
        }
        foreach (var key in keys) Detach(key);
    }
}
