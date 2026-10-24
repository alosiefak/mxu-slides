using MxU.Slides.Core.Automation;
using MxU.Slides.Audio.Library;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Audio.Control;

/// <summary>What a player needs from the library: playlists, audio items and where their files are.</summary>
public interface IAudioLibrary
{
    Playlist? Playlist(string id);

    AudioItem? AudioItem(string id);

    /// <summary>The item's file on disk, or null when it's missing.</summary>
    string? FilePath(AudioItem item);
}

/// <summary>The library on disk as an <see cref="IAudioLibrary"/>.</summary>
public sealed class LibraryAudioSource(LibraryStore store) : IAudioLibrary
{
    public Playlist? Playlist(string id) => store.Get<Playlist>(id);

    public AudioItem? AudioItem(string id) => store.Get<AudioItem>(id);

    public string? FilePath(AudioItem item) => AudioImporter.FilePath(store, item);
}

/// <summary>Where a bus plays: a device (null = the default), a channel pair or mono channel, a delay. <c>Key</c> names the mix it serves.</summary>
public sealed record AudioRouting(AudioOutputDevice? Device, int ChannelOffset, string Key, int DelayMs = 0, bool Mono = false)
{
    public const string MainKey = "main";

    public static readonly AudioRouting Main = new(null, 0, MainKey);
}

/// <summary>
/// One audio bus: a playlist (in order, shuffled or looped, crossfading between tracks),
/// a single library item, or a timeline music clip, played on its own <see cref="AudioEngine"/>.
/// Owned by an <see cref="AudioController"/>, whose lock every member takes, so it is
/// safe from any thread. Port of the Mac's AudioPlayer (AudioController.swift).
/// </summary>
public sealed class AudioPlayer
{
    private readonly AudioController owner;
    private readonly IAudioLibrary library;
    private readonly Func<double> transportFade;
    private readonly Func<double> defaultCrossfade;
    private PlaylistQueue? queue;
    private double currentTrimStart;
    private double crossfadeSeconds;
    private bool? repeatOverride;
    private string? crossfadeArmedEntryId;
    private int consecutiveSkips;

    internal AudioPlayer(AudioController owner, AudioEngine engine, IAudioLibrary library, Func<double> transportFade, Func<double> defaultCrossfade)
    {
        this.owner = owner;
        Engine = engine;
        this.library = library;
        this.transportFade = transportFade;
        this.defaultCrossfade = defaultCrossfade;
        engine.TrackPlayedToEnd += () => owner.Run(TrackPlayedToEnd);
    }

    public AudioEngine Engine { get; }

    public string? PlaylistId { get; private set; }

    /// <summary>The single item (or timeline clip's item) playing, when not a playlist.</summary>
    public string? SingleItemId { get; private set; }

    public string? CurrentEntryId { get; private set; }

    public string? CurrentTrackName { get; private set; }

    public string? ClipId { get; private set; }

    public bool IsPlaying { get; private set; }

    /// <summary>Seconds into the track (from its in point).</summary>
    public double Elapsed { get; private set; }

    /// <summary>The track's length (in point to out point).</summary>
    public double Duration { get; private set; }

    /// <summary>The mix this bus serves (one bus per mix).</summary>
    public string TargetKey { get; private set; } = AudioRouting.MainKey;

    public float BusGain { get; internal set; } = 1;

    public bool BusMuted { get; internal set; }

    /// <summary>"Track 2 of 5" in the current pass.</summary>
    public (int Index, int Count)? PassPosition => owner.Run(() => queue?.PositionInPass);

    /// <summary>The playlist's name (or the track's), for the now-playing line.</summary>
    public string DisplayName => owner.Run(() =>
        PlaylistId is { } id ? library.Playlist(id)?.Name ?? "Playlist" : CurrentTrackName ?? "Music");

    /// <summary>The ApiAudioBus shape.</summary>
    public AudioBusStatus Status => owner.Run(() => new AudioBusStatus(PlaylistId, SingleItemId, CurrentTrackName, IsPlaying, Elapsed, Duration));

    internal (string? PlaylistId, string? AudioItemId) Cue => (PlaylistId, SingleItemId);

    internal void ApplyMix(float master, bool globalMute, bool hardwareLive = true)
    {
        Engine.MasterVolume = master * BusGain;
        Engine.IsMuted = globalMute || BusMuted;
        Engine.HardwareOutputEnabled = hardwareLive;
    }

    internal void ConfigureRouting(AudioRouting routing)
    {
        TargetKey = routing.Key;
        Engine.SetOutputDevice(routing.Device, routing.ChannelOffset, routing.Mono);
        Engine.OutputDelayMilliseconds = routing.DelayMs;
    }

    // MARK: Starting

    internal void Play(Playlist playlist, string? startAt = null, bool? repeat = null)
    {
        if (PlaylistId == playlist.Id && startAt is null && IsPlaying)
        {
            // Fired again while playing: just pick up any edits.
            repeatOverride = repeat;
            RefreshFromDocument();
            return;
        }
        var isJumpWithinLive = PlaylistId == playlist.Id && IsPlaying;
        repeatOverride = repeat;
        var next = new PlaylistQueue(playlist.Entries.Select(e => e.Id).ToList(), EffectiveRepeat(playlist.PlaybackMode), playlist.Shuffle ?? false);
        next.Start(startAt);
        queue = next;
        PlaylistId = playlist.Id;
        SingleItemId = null;
        crossfadeSeconds = PlaylistCrossfade.Resolve(playlist.CrossfadeSeconds, defaultCrossfade());
        crossfadeArmedEntryId = null;
        PlayCurrentEntry(isJumpWithinLive ? crossfadeSeconds : 0, isJumpWithinLive ? 0 : transportFade());
    }

    internal void Play(AudioItem item, bool? repeat = null)
    {
        ClipId = null;
        queue = null;
        PlaylistId = null;
        SingleItemId = item.Id;
        repeatOverride = repeat;
        crossfadeSeconds = 0;
        crossfadeArmedEntryId = null;
        CurrentEntryId = null;
        Start(item, 0, transportFade());
    }

    internal void Play(TimelineMusicClip clip)
    {
        if (library.FilePath(clip.Item) is not { } path) return;
        queue = null;
        PlaylistId = null;
        SingleItemId = clip.Item.Id;
        ClipId = clip.Id;
        repeatOverride = false;
        crossfadeSeconds = 0;
        crossfadeArmedEntryId = null;
        CurrentEntryId = null;
        // A clip with its own fade in, or fired part way, comes in as written.
        var fadeIn = clip.Overlap || clip.Envelope.FadeIn is not null || clip.From > 0.05 ? 0 : transportFade();
        try
        {
            Engine.Play(path, fadeIn: fadeIn, trimStart: clip.Start, trimEnd: clip.End, from: clip.Start + clip.From,
                shape: new AudioClipShape(clip.Envelope, clip.Start, clip.End ?? double.PositiveInfinity), overlap: clip.Overlap, due: clip.Due);
            currentTrimStart = clip.Start;
            CurrentTrackName = clip.Item.Name;
            IsPlaying = true;
            RefreshPosition();
        }
        catch (AudioEngineException)
        {
            IsPlaying = false;
        }
    }

    // MARK: Transport

    internal void Stop(double? fade = null, AudioFadeCurve curve = AudioFadeCurve.Linear)
    {
        Engine.Stop(fade ?? transportFade(), curve);
        queue = null;
        repeatOverride = null;
        ClipId = null;
        CurrentEntryId = null;
        CurrentTrackName = null;
        IsPlaying = false;
        Elapsed = 0;
        Duration = 0;
        crossfadeArmedEntryId = null;
        owner.NotifyTransport();
    }

    internal void TogglePlayPause()
    {
        if (CurrentTrackName is null)
        {
            // A playlist that played to its end starts again from the top.
            if (queue is not null && PlaylistId is not null)
            {
                queue.Start();
                PlayCurrentEntry(0, transportFade());
            }
            return;
        }
        if (IsPlaying)
        {
            Engine.Pause(transportFade());
            IsPlaying = false;
        }
        else
        {
            Engine.Resume(transportFade());
            IsPlaying = true;
        }
    }

    internal void Next()
    {
        if (queue is null) return;
        crossfadeArmedEntryId = null;
        if (queue.Next() is not null) PlayCurrentEntry(crossfadeSeconds);
        else StopAtEndOfPass();
    }

    internal void Previous()
    {
        crossfadeArmedEntryId = null;
        if (queue is null)
        {
            if (SingleItemId is { } id && library.AudioItem(id) is { } item) Start(item, 0);
            return;
        }
        // More than 3 s in, Previous restarts the track (like a CD player).
        if (Elapsed > 3 && CurrentEntryId is not null)
        {
            PlayCurrentEntry(0);
            return;
        }
        queue.Previous();
        PlayCurrentEntry(crossfadeSeconds);
    }

    /// <summary>Jumps to <paramref name="seconds"/> into the track (from its in point).</summary>
    internal void Seek(double seconds)
    {
        if (CurrentTrackName is null || Duration <= 0) return;
        // Seeking back out of the crossfade window lets the crossfade happen again.
        if (CurrentEntryId is { } entry && crossfadeArmedEntryId == entry && queue?.CurrentEntryId == entry) crossfadeArmedEntryId = null;
        Engine.Seek(seconds + currentTrimStart);
        IsPlaying = Engine.IsPlaying;
        RefreshPosition();
    }

    /// <summary>The playlist document changed: take its new crossfade, repeat, shuffle and entries.</summary>
    internal void RefreshFromDocument()
    {
        if (PlaylistId is not { } id || library.Playlist(id) is not { } playlist) return;
        crossfadeSeconds = PlaylistCrossfade.Resolve(playlist.CrossfadeSeconds, defaultCrossfade());
        if (queue is null) return;
        queue.Repeat = EffectiveRepeat(playlist.PlaybackMode);
        if (playlist.Shuffle is { } shuffled && shuffled != queue.IsShuffled) queue.SetShuffled(shuffled);
        queue.UpdateEntries(playlist.Entries.Select(e => e.Id).ToList());
        // The playing track was removed: let it finish, then stop rather than crossfade.
        if (queue.CurrentEntryId is null && CurrentEntryId is not null) crossfadeArmedEntryId = CurrentEntryId;
    }

    private void PlayCurrentEntry(double crossfade, double fadeIn = 0)
    {
        if (queue?.CurrentEntryId is not { } entryId)
        {
            StopAtEndOfPass();
            return;
        }
        CurrentEntryId = entryId;
        if (ItemForEntry(entryId) is not { } item)
        {
            SkipUnplayable();
            return;
        }
        Start(item, crossfade, fadeIn);
    }

    private void Start(AudioItem item, double crossfade, double fadeIn = 0)
    {
        if (library.FilePath(item) is not { } path)
        {
            SkipUnplayable();
            return;
        }
        var trimStart = Math.Max(item.InPoint ?? 0, 0);
        var trimEnd = item.OutPoint;
        if (trimEnd is { } end && end <= trimStart)
        {
            trimStart = 0;
            trimEnd = null;
        }
        try
        {
            Engine.Play(path, crossfade, fadeIn, trimStart, trimEnd);
            currentTrimStart = trimStart;
            CurrentTrackName = item.Name;
            IsPlaying = true;
            RefreshPosition();
        }
        catch (AudioEngineException)
        {
            SkipUnplayable();
        }
    }

    // A missing or unreadable track is skipped; a playlist with nothing playable stops.
    private void SkipUnplayable()
    {
        consecutiveSkips++;
        if (queue is null || consecutiveSkips > QueueLength)
        {
            consecutiveSkips = 0;
            StopAtEndOfPass();
            return;
        }
        if (queue.Next() is not null)
        {
            PlayCurrentEntry(0);
        }
        else
        {
            consecutiveSkips = 0;
            StopAtEndOfPass();
        }
    }

    private int QueueLength => PlaylistId is { } id ? library.Playlist(id)?.Entries.Count ?? 1 : 1;

    private void TrackPlayedToEnd()
    {
        consecutiveSkips = 0;
        if (crossfadeArmedEntryId is not null && crossfadeArmedEntryId == CurrentEntryId)
        {
            crossfadeArmedEntryId = null;
            if (queue?.CurrentEntryId is null) StopAtEndOfPass();
            return;
        }
        if (queue is null)
        {
            if (repeatOverride == true && SingleItemId is { } id && library.AudioItem(id) is { } item)
            {
                Start(item, 0);
                return;
            }
            IsPlaying = false;
            RefreshPosition();
            owner.NotifyTransport();
            return;
        }
        if (queue.AdvanceAfterNaturalEnd() is not null) PlayCurrentEntry(0);
        else StopAtEndOfPass();
        owner.NotifyTransport();
    }

    private void StopAtEndOfPass()
    {
        Engine.Stop();
        IsPlaying = false;
        CurrentEntryId = null;
        CurrentTrackName = null;
        currentTrimStart = 0;
        Elapsed = 0;
        Duration = 0;
    }

    /// <summary>Every 250 ms: the position, and starting the next track's crossfade when this one is nearly done.</summary>
    internal void Poll()
    {
        RefreshPosition();
        if (!IsPlaying || crossfadeSeconds <= 0 || queue is null || Duration <= 0) return;
        if (CurrentEntryId is not { } entryId || crossfadeArmedEntryId == entryId) return;
        if (Duration - Elapsed > crossfadeSeconds) return;
        crossfadeArmedEntryId = entryId;
        if (queue.AdvanceAfterNaturalEnd() is not null) PlayCurrentEntry(Math.Min(crossfadeSeconds, Math.Max(0.1, Duration - Elapsed)));
    }

    private void RefreshPosition()
    {
        if (Engine.Position is not { } position) return;
        Elapsed = Math.Max(0, position.Elapsed - currentTrimStart);
        Duration = Math.Max(0, position.Duration - currentTrimStart);
    }

    private AudioItem? ItemForEntry(string entryId)
    {
        if (PlaylistId is not { } id || library.Playlist(id) is not { } playlist) return null;
        var entry = playlist.Entries.FirstOrDefault(e => e.Id == entryId);
        if (entry is null || entry.RefKind != PlaylistRefKind.Audio) return null;
        return library.AudioItem(entry.RefId);
    }

    public static PlaylistQueue.RepeatBehavior RepeatBehavior(PlaybackMode mode) => mode switch
    {
        PlaybackMode.LoopPlaylist => PlaylistQueue.RepeatBehavior.Playlist,
        PlaybackMode.LoopSingle => PlaylistQueue.RepeatBehavior.Single,
        _ => PlaylistQueue.RepeatBehavior.None,
    };

    // "Loop" on the cue overrides the playlist's own mode either way.
    private PlaylistQueue.RepeatBehavior EffectiveRepeat(PlaybackMode mode) => repeatOverride switch
    {
        true => PlaylistQueue.RepeatBehavior.Playlist,
        false => PlaylistQueue.RepeatBehavior.None,
        null => RepeatBehavior(mode),
    };
}
