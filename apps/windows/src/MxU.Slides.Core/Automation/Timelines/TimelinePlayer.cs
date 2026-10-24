using System.Globalization;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// Plays a presentation's timeline: one song at a time, on its own clock (30 ticks a
/// second) or following timecode. Each tick it asks <see cref="PresentationTimelines"/>
/// what the playhead crossed and does it: shows slides, fires and clears layers, plays
/// the music lane, runs actions cues. It also records takes (slides fired while recording
/// become cues), keeps multitrack stems in step through an optional
/// <see cref="ITracksPlayer"/>, and hands over to the next service item at a song's end
/// when the item has a transition. Port of Sources/PresentationTimelineController.swift
/// (plus layOutFromMap from SongMapEditing.swift). Drive it from the UI thread.
/// </summary>
public sealed class TimelinePlayer
{
    /// <summary>A transition armed to hand over at the end of the playing song.</summary>
    public sealed record ArmedGo(string ItemId, SongTransitions.Plan Plan, ServiceItem? Next, MultitrackSong? NextSong, bool InPlayer, bool MusicFaded = false);

    /// <summary>The armed handover as the UI shows it ("Crossfade into Way Maker in 0:12").</summary>
    public sealed record PendingGo(string ItemId, SongTransitions.Kind Kind, string? NextName, DateTimeOffset FiresAt);

    /// <summary>Where the playhead was at a moment, for clocks elsewhere (stage display, Local API) to count on from.</summary>
    public sealed record ClockAnchor(string ItemId, double Position, DateTimeOffset At, bool Running, double Length);

    /// <summary>The next slide cue and when it fires (for the "next" countdown).</summary>
    public sealed record NextCueInfo(string ItemId, string CueId, DateTimeOffset FiresAt);

    private readonly ITimelineHost host;
    private readonly IShowActions show;
    private readonly IActionLibrary library;
    private readonly ITracksPlayer? tracks;
    private readonly TimeProvider clock;
    private readonly SynchronizationContext? ui;

    private ShowTimer? timer;
    private double anchorHost;
    private double anchorPosition;

    /// <summary>The tracks player's position this tick and when it was read (lines music clips up with the stems).</summary>
    private (double Position, double At)? stemsReading;

    /// <summary>Where the last tick left off: the next tick fires what lies between.</summary>
    private double lastPosition;

    private PresentationTimeline? recordingTimeline;
    private PresentationTimelines.Take? take;
    private (string PresentationId, List<PresentationTimeline>? Timelines)? takeBefore;

    /// <summary>The timeline is firing a slide itself (so a recording doesn't record it).</summary>
    private bool firing;

    private bool underTimecode;
    private bool timecodePlaysAudio;
    private double audioSyncHold;

    private string? tracksSong;
    private double tracksAsked;
    private (string Key, MultitrackSong? Song)? chaseSong;
    private ArmedGo? armedGo;
    private string? cancelledItemId;

    /// <summary>The previous song's stems still sounding out after a handover.</summary>
    private bool stemsTail;
    private double? stemsTailEnds;

    /// <summary>Set while handing over, so the next song's start doesn't stop the outgoing music.</summary>
    private bool keepingMusic;

    /// <summary>Timers that stop an overlapped song's music at its end (held so they aren't collected).</summary>
    private readonly HashSet<ShowTimer> musicStoppers = [];

    /// <param name="tracks">The multitrack player, if this computer has one.</param>
    /// <param name="clock">Wall time, host time and the tick timer (tests pass a fake).</param>
    /// <param name="ui">Where ticks run; pass the UI thread's <see cref="SynchronizationContext.Current"/>.</param>
    public TimelinePlayer(
        ITimelineHost host, IShowActions show, IActionLibrary library, ITracksPlayer? tracks = null,
        TimeProvider? clock = null, SynchronizationContext? ui = null)
    {
        this.host = host;
        this.show = show;
        this.library = library;
        this.tracks = tracks;
        this.clock = clock ?? ShowTimeProvider.Shared;
        this.ui = ui;
    }

    /// <summary>The diagnostics trail: (crumb, detail).</summary>
    public event Action<string, string>? Noted;

    /// <summary>Fires when the public state below changes.</summary>
    public event Action? Changed;

    /// <summary>The timecode input (for Record, which waits for timecode on a following computer).</summary>
    public TimecodeFollower? Timecode { get; set; }

    public string? ItemId { get; private set; }
    public string? PresentationId { get; private set; }
    public string? ArrangementId { get; private set; }
    public bool Playing { get; private set; }
    public bool Recording { get; private set; }
    public double PlayheadTime { get; private set; }

    /// <summary>An armed or running take against timecode.</summary>
    public TimecodeRecording? TimecodeRecording { get; private set; }

    public PendingGo? Pending { get; private set; }
    public ClockAnchor? Anchor { get; private set; }
    public NextCueInfo? NextCue { get; private set; }

    private double HostNow => clock.GetTimestamp() / (double)clock.TimestampFrequency;

    private DateTimeOffset Now => clock.GetUtcNow();

    private void Note(string crumb, string detail) => Noted?.Invoke(crumb, detail);

    private static string F(double value, int places = 2) => value.ToString("F" + places, CultureInfo.InvariantCulture);

    public bool IsCurrent(string itemId) => ItemId == itemId;

    /// <summary>Where a new cue goes: at the playhead in the current song, else at zero.</summary>
    public double InsertionTime(string itemId) => ItemId == itemId ? PresentationTimelines.SnappedToFrame(PlayheadTime) : 0;

    // MARK: Transport

    /// <summary>
    /// Plays a song's timeline from the playhead. Starting another song by hand while stems
    /// sound crossfades to it; <paramref name="adoptingTracks"/> takes over stems the tracks
    /// player has already handed to.
    /// </summary>
    public void Play(string itemId, Presentation presentation, string? arrangementId, string? adoptingTracks = null, bool handingOver = false)
    {
        LayOutFromMap(presentation.Id, arrangementId);
        double? crossfade = null;
        if (SongTransitions.ManualJumpCrossfades(ItemId != itemId, Playing, tracksSong is not null || stemsTail, handingOver))
        {
            crossfade = SongTransitions.ManualCrossfade(CurrentTransition());
            tracksSong = null;
            stemsTail = false;
        }
        if (ItemId != itemId) Select(itemId, presentation, arrangementId);
        if (Playing) return;

        if (TimecodeRecording is not null) EndRecording();
        // Music on the timeline: have the sound device open before its first clip is due.
        if (host.Music is { } music && CuesOf(presentation)?.Any(c => c.Kind() == TimelineLane.Audio) == true) music.Prepare();
        Playing = true;
        AnchorAt(PlayheadTime);
        if (PlayheadTime <= 0) lastPosition = -PresentationTimelines.Frame;
        else Land(PlayheadTime);
        PublishClock(PlayheadTime, true);
        if (adoptingTracks is not null)
        {
            tracksSong = adoptingTracks;
            tracksAsked = HostNow;
        }
        else if (crossfade is { } seconds)
        {
            var song = MultitrackSongNow();
            tracksSong = song?.Id;
            tracksAsked = HostNow;
            stemsTail = song is null;
            tracks?.Crossfade(song, PlayheadTime, seconds);
        }
        else
        {
            StartTracks(PlayheadTime);
        }
        StartTimer();
        RefreshTransition();
        Note("timeline.play", F(PlayheadTime));
        Changed?.Invoke();
    }

    private void StartTimer()
    {
        timer?.Dispose();
        timer = ShowTimer.Every(clock, ui, TimeSpan.FromSeconds(1.0 / 30), Tick);
    }

    public void Stop()
    {
        timer?.Dispose();
        timer = null;
        var againstTimecode = TimecodeRecording is not null;
        EndRecording();
        if (!Playing)
        {
            if (againstTimecode) Note("timeline.stop", $"timecode take {F(PlayheadTime)}");
            Changed?.Invoke();
            return;
        }
        DisarmGo();
        if (stemsTail && tracksSong is null) tracks?.Stop();
        stemsTail = false;
        StopTracks();
        Playing = false;
        PublishClock(PlayheadTime, false);
        Note("timeline.stop", F(PlayheadTime));
        Changed?.Invoke();
    }

    private void EndRecording()
    {
        if (recordingTimeline is { } timeline && takeBefore is { } before)
            Save(timeline, before.PresentationId, "Record Timeline", new TimelinesUndo(before.Timelines));
        Recording = false;
        recordingTimeline = null;
        take = null;
        takeBefore = null;
        TimecodeRecording = null;
    }

    /// <summary>Throws the take away: the timelines go back to how they were before recording.</summary>
    public void DiscardTake()
    {
        if (takeBefore is not { } before)
        {
            Stop();
            return;
        }
        take = null;
        takeBefore = null;
        recordingTimeline = null;
        Stop();
        host.SaveTimelines(before.PresentationId, before.Timelines, "Discard Take");
        Note("timeline.record", "take discarded");
    }

    private void BeginTake(PresentationTimeline timeline, double start, Presentation presentation)
    {
        var recorded = timeline.Copy();
        var newTake = new PresentationTimelines.Take(timeline.Cues, start);
        takeBefore = (presentation.Id, presentation.Timelines?.ToList());
        recorded.Cues = newTake.Cues;
        take = newTake;
        recordingTimeline = recorded;
        Save(recorded, presentation.Id, null);
    }

    /// <summary>
    /// Records a take: on a computer following timecode (for a timecode song) it waits for the
    /// timecode to reach the song; otherwise it plays from the playhead, and slides fired land as cues.
    /// <paramref name="newStart"/> is the timecode start a new timeline gets.
    /// </summary>
    public void Record(string itemId, Presentation presentation, string? arrangementId, int newStart, int? start = null)
    {
        Stop();
        if (ItemId != itemId) Select(itemId, presentation, arrangementId);
        if (Timecode is { } follower
            && Automation.TimecodeRecording.ModeFor(TimelineOf(presentation), follower.HasInput, follower.Follows, start) is TimecodeRecording.Mode.Timecode armed)
        {
            TimecodeRecording = new TimecodeRecording(itemId, armed.Start);
            Recording = true;
            Note("timeline.record", "armed for timecode");
            Changed?.Invoke();
            return;
        }
        var timeline = TimelineOf(presentation) ?? new PresentationTimeline
        {
            Id = TimelineClips.NewId(),
            ArrangementId = string.IsNullOrEmpty(arrangementId) ? null : arrangementId,
            TimecodeEnabled = false,
            TimecodeStart = newStart,
            Cues = [],
        };
        BeginTake(timeline, PlayheadTime, presentation);
        Play(itemId, presentation, arrangementId);
        Recording = true;
        NextCue = null;
        Changed?.Invoke();
    }

    public void StopTracks()
    {
        StopStems();
        StopMusic();
    }

    public void StopStems()
    {
        if (tracksSong is null) return;
        tracks?.Stop();
        tracksSong = null;
    }

    /// <summary>Stops this timeline's music lane (not while handing over: the outgoing music fades on its own).</summary>
    public void StopMusic()
    {
        if (keepingMusic || host.Music is not { } music || Deck() is not { } deck) return;
        foreach (var id in MusicIds(deck))
            if (music.Player(id) is not null) music.Stop(id);
    }

    private IEnumerable<string> MusicIds(Presentation deck) =>
        (CuesOf(deck) ?? []).Where(c => c.Kind() == TimelineLane.Audio).Select(c => c.ContentId()).OfType<string>().Distinct();

    /// <summary>Moves the playhead (switching song first if needed); while playing, lands there.</summary>
    public void Seek(double position, string itemId, Presentation presentation, string? arrangementId)
    {
        if (ItemId != itemId)
        {
            Stop();
            Select(itemId, presentation, arrangementId);
        }
        position = Math.Max(position, 0);
        SetPlayhead(position);
        if (Playing)
        {
            Land(position);
            AnchorAt(position);
            if (tracksSong is not null)
            {
                tracks?.Seek(position);
                tracksAsked = HostNow;
            }
            RefreshTransition();
        }
        PublishClock(position, Playing);
        Changed?.Invoke();
    }

    // MARK: Multitrack

    private MultitrackSong? MultitrackSongNow() =>
        ItemId is { } itemId && Deck() is { } deck ? MultitrackSongFor(itemId, deck, ArrangementId) : null;

    /// <summary>The song's stems for the tracks player (only those whose files are on this computer).</summary>
    private MultitrackSong? MultitrackSongFor(string itemId, Presentation deck, string? arrangementId)
    {
        if (tracks is null || PresentationTimelines.Timeline(arrangementId, deck)?.Cues is not { } cues) return null;
        var missing = 0;
        var stems = new List<MultitrackStem>();
        foreach (var cue in TimelineTracks.Stems(cues))
        {
            if (cue.TrackChannelId() is not { } channel || cue.MediaId is not { } id || library.Audio(id) is not { } item
                || host.AudioFilePath(item) is not { } path)
            {
                missing++;
                continue;
            }
            stems.Add(new MultitrackStem(path, channel, cue.At, (item.InPoint ?? 0) + (cue.InPoint ?? 0), cue.Duration, Envelope(cue, cues)));
        }
        if (missing > 0) Note("tracks.missing", $"{missing} stems not on this computer");
        if (stems.Count == 0) return null;
        return new MultitrackSong(string.Join('|', itemId, deck.Id, arrangementId ?? ""), stems);
    }

    /// <summary>A clip's fades and edge fades as the audio engine plays them.</summary>
    public static ClipEnvelope Envelope(TimelineCue cue, IReadOnlyList<TimelineCue> cues)
    {
        static ClipEnvelope.Fade? Fade(ClipFade? fade) =>
            fade is null ? null : new ClipEnvelope.Fade(fade.Seconds, TimelineClips.ShapeNamed(fade.Shape), fade.Midpoint ?? 0.5);
        var edges = TimelineClips.Edges(cue, cues);
        return new ClipEnvelope(Fade(cue.FadeIn), Fade(cue.FadeOut), TimelineClips.EdgeSeconds, edges.Start, edges.End);
    }

    /// <summary>Loads the coming songs' stems ahead of time.</summary>
    public void CueAhead(IEnumerable<(string ItemId, Presentation Deck, string? ArrangementId)> items)
    {
        var songs = items.Select(i => MultitrackSongFor(i.ItemId, i.Deck, i.ArrangementId)).OfType<MultitrackSong>()
            .Where(s => s.Id != tracksSong).ToList();
        tracks?.CueAhead(songs);
    }

    private void ChaseTracks(TimecodeFollow.Command command, double now, double readAt)
    {
        if (tracks is null || ItemId is not { } itemId || Deck() is not { } deck) return;
        var key = string.Join('|', itemId, deck.Id, ArrangementId ?? "");
        var landing = command is TimecodeFollow.Command.Land;
        if (landing || chaseSong?.Key != key)
        {
            chaseSong = (key, MultitrackSongFor(itemId, deck, ArrangementId));
            landing = true;
        }
        if (chaseSong?.Song is not { } song || !(landing || now - tracksAsked >= 0.1)) return;
        tracksSong = song.Id;
        tracksAsked = now;
        tracks.Chase(song, command.Position, readAt);
    }

    private void StartTracks(double position)
    {
        if (MultitrackSongNow() is not { } song) return;
        tracksSong = song.Id;
        tracksAsked = HostNow;
        tracks?.Play(song, position);
    }

    /// <summary>Clear Audio with stems sounding: stops the stems, and the timeline too when it was playing them.</summary>
    public void ClearTracks()
    {
        DisarmGo();
        if (Playing && tracksSong is not null)
        {
            Stop();
        }
        else
        {
            tracks?.Stop();
            tracksSong = null;
            stemsTail = false;
        }
    }

    /// <summary>
    /// After a relaunch, picks up a song the tracks player is still playing (its id is
    /// "item|presentation|arrangement"). False when that presentation isn't loaded yet (try again later).
    /// </summary>
    public bool Resume(string songId, double position)
    {
        if (Playing) return true;
        var parts = songId.Split('|');
        if (parts.Length != 3) return true;
        if (host.Presentation(parts[1]) is not { } deck) return false;
        Select(parts[0], deck, parts[2].Length == 0 ? null : parts[2]);
        Playing = true;
        tracksSong = songId;
        tracksAsked = HostNow;
        Land(position);
        AnchorAt(position);
        SetPlayhead(position);
        PublishClock(position, true);
        StartTimer();
        host.OpenTimelineCard(parts[0]);
        Note("timeline.resume", $"tracks at {F(position)}");
        Changed?.Invoke();
        return true;
    }

    // MARK: Timecode

    /// <summary>
    /// One tick of following timecode: switches to the song if needed, then plays across (or
    /// lands at) the position. Under timecode the music lane plays only when <paramref name="playsAudio"/>.
    /// </summary>
    public void Follow(
        TimecodeFollow.Command command, string itemId, Presentation presentation, string? arrangementId, bool playsAudio = false, double? readAt = null)
    {
        if (ItemId != itemId || PresentationId != presentation.Id || ArrangementId != arrangementId) Select(itemId, presentation, arrangementId);
        else if (Playing) Stop();
        underTimecode = true;
        timecodePlaysAudio = playsAudio;
        try
        {
            switch (command)
            {
                case TimecodeFollow.Command.Play play:
                    lastPosition = TimecodeRecording?.Started == true ? Math.Max(play.From, lastPosition) : play.From;
                    Advance(play.To);
                    break;
                case TimecodeFollow.Command.Land land:
                    Land(land.At);
                    break;
            }
            var now = HostNow;
            if (command is TimecodeFollow.Command.Land) audioSyncHold = now + 0.5;
            if (playsAudio && now >= audioSyncHold && host.Music is { } music && Deck() is { } deck && CuesOf(deck) is { } cues)
            {
                var playing = new Dictionary<string, double>();
                foreach (var id in cues.Where(c => c.Kind() == TimelineLane.Audio).Select(c => c.ContentId()).OfType<string>().Distinct())
                    if (music.Player(id) is { IsPlaying: true } player) playing[id] = player.Elapsed;
                var sync = TimecodeFollow.AudioSync(cues, command.Position, playing);
                if (sync.Any(e => e is TimelineEffect.PlayAudio)) audioSyncHold = now + 0.5;
                Apply(sync, cues, deck);
            }
            ChaseTracks(command, now, readAt ?? now);
            SetPlayhead(command.Position);
            PublishClock(command.Position, true);
        }
        finally
        {
            underTimecode = false;
        }
        Changed?.Invoke();
    }

    /// <summary>Timecode stopped (not a dropout): the clock elsewhere holds.</summary>
    public void TimecodeHeld()
    {
        if (Anchor?.Running != true || Playing) return;
        PublishClock(PlayheadTime, false);
        Changed?.Invoke();
    }

    /// <summary>
    /// Each timecode tick while a take is armed: begins it when the timecode reaches the
    /// song's start, ends it when the timecode stops. Returns the take (null when none).
    /// </summary>
    public TimecodeRecording? TimecodeRecordingTick(TimecodeClock.Reading reading)
    {
        if (TimecodeRecording is not { } recording) return null;
        switch (recording.StepFor(reading))
        {
            case TimecodeRecording.Step.End:
                Stop();
                return null;
            case TimecodeRecording.Step.Begin begin:
            {
                if (Deck() is not { } deck) break;
                var timeline = TimelineOf(deck) ?? new PresentationTimeline
                {
                    Id = TimelineClips.NewId(),
                    ArrangementId = string.IsNullOrEmpty(ArrangementId) ? null : ArrangementId,
                    TimecodeEnabled = true,
                    TimecodeStart = begin.Start,
                    Cues = [],
                };
                BeginTake(timeline, begin.At, deck);
                recording.Start = begin.Start;
                recording.Started = true;
                TimecodeRecording = recording;
                lastPosition = begin.At;
                SetPlayhead(begin.At);
                Note("timeline.record", $"against timecode from {F(begin.At, 3)}, start {begin.Start}");
                Changed?.Invoke();
                break;
            }
        }
        return recording;
    }

    // MARK: Clock

    private void PublishClock(double position, bool running)
    {
        if (!running && NextCue is not null) NextCue = null;
        if (ItemId is not { } itemId)
        {
            Anchor = null;
            return;
        }
        var now = Now;
        var length = Deck() is { } deck && TimelineOf(deck) is { } timeline ? PresentationTimelines.Length(timeline) : 0;
        if (Anchor is { } held && held.ItemId == itemId && held.Running == running && held.Length == length
            && Math.Abs(held.Position + (running ? (now - held.At).TotalSeconds : 0) - position) < 0.1)
            return;
        Anchor = new ClockAnchor(itemId, position, now, running, length);
    }

    private void Select(string itemId, Presentation presentation, string? arrangementId)
    {
        Stop();
        if (cancelledItemId != itemId) cancelledItemId = null;
        ItemId = itemId;
        PresentationId = presentation.Id;
        ArrangementId = arrangementId;
        SetPlayhead(0);
        PublishClock(0, false);
    }

    private void AnchorAt(double position)
    {
        anchorHost = HostNow;
        anchorPosition = position;
        lastPosition = position;
    }

    private void SetPlayhead(double position) => PlayheadTime = position;

    /// <summary>One tick of the own clock (the stems' position when the tracks player is playing them).</summary>
    internal void Tick()
    {
        if (!Playing) return;
        try
        {
            var position = anchorPosition + (HostNow - anchorHost);
            if (armedGo is { } handing && handing.ItemId == ItemId && handing.NextSong is { } nextSong && tracks?.LatestSongId == nextSong.Id)
            {
                Advance(Math.Max(Math.Min(position, handing.Plan.NextAt), lastPosition));
                HandOver(handing);
                return;
            }
            if (tracksSong is { } song)
            {
                if (tracks?.Position(song) is { } heard)
                {
                    position = heard;
                    anchorHost = HostNow;
                    anchorPosition = heard;
                    stemsReading = (heard, anchorHost);
                }
                else if (armedGo is { NextSong: null } ending && position >= ending.Plan.NextAt - 0.25)
                {
                    position = Math.Max(position, ending.Plan.NextAt);
                }
                else if (HostNow - tracksAsked < 2)
                {
                    // The tracks player hasn't answered yet: hold the clock.
                    anchorHost = HostNow;
                    return;
                }
                else if (tracks?.Reattaching == true)
                {
                }
                else
                {
                    Note("tracks.silent", "no answer from the Tracks Player; own clock");
                    tracksSong = null;
                }
            }
            if (armedGo is { } go && go.ItemId == ItemId)
            {
                var reported = go.NextSong is { } next && tracks?.LatestSongId == next.Id;
                switch (SongTransitions.StepFor(go.Plan, position, go.InPlayer && go.NextSong is not null, reported))
                {
                    case SongTransitions.Step.HandOver handOver:
                        Advance(Math.Max(handOver.At, lastPosition));
                        HandOver(go);
                        return;
                    case SongTransitions.Step.Play play:
                        position = play.To;
                        if (go.Plan.FadeFrom is { } fadeFrom && position >= fadeFrom && !go.MusicFaded)
                        {
                            armedGo = go with { MusicFaded = true };
                            FadeMusic(Math.Max(go.Plan.End - position, 0.01), go.Plan.FadeInSeconds > 0 ? AudioFadeCurve.EqualPower : AudioFadeCurve.Cosine);
                        }
                        break;
                }
            }
            if (stemsTailEnds is { } lastFrame && HostNow > lastFrame)
            {
                stemsTail = false;
                stemsTailEnds = null;
            }
            Advance(position);
            SetPlayhead(position);
            if (!Recording && Deck() is { } deck && TimelineOf(deck) is { } timeline && position > PresentationTimelines.Length(timeline) + 1)
                Stop();
        }
        finally
        {
            stemsReading = null;
            Changed?.Invoke();
        }
    }

    private List<string> PlayingMusic()
    {
        if (host.Music is not { } music || Deck() is not { } deck) return [];
        return MusicIds(deck).Where(id => music.Player(id)?.IsPlaying == true).ToList();
    }

    private void FadeMusic(double seconds, AudioFadeCurve curve)
    {
        foreach (var id in PlayingMusic()) host.Music?.Stop(id, seconds, curve);
    }

    private SongTransition? CurrentTransition() => ItemId is { } itemId ? host.CurrentServiceItem(itemId)?.Transition : null;

    // MARK: Transitions

    /// <summary>
    /// Arms (or disarms) the playing song's handover to the next service item, from the item's
    /// transition, the timelines' ends, and the playhead. Called on play, seek, and when the service changes.
    /// </summary>
    public void RefreshTransition()
    {
        if (!Playing || Recording || ItemId is not { } itemId || Deck() is not { } deck || TimelineOf(deck) is not { } timeline
            || host.CurrentServiceItem(itemId) is not { } item)
        {
            DisarmGo();
            return;
        }
        var kind = SongTransitions.KindOf(item.Transition);
        if (!SongTransitions.Arms(kind, ownClock: true, Recording, cancelledItemId == itemId))
        {
            DisarmGo();
            return;
        }
        var next = NextFireable(host.FireableItems(), itemId);
        var nextDeck = next is { ItemKind: ServiceItemKind.Presentation } ? host.Presentation(next.RefId) : null;
        var nextTimeline = nextDeck is null ? null : PresentationTimelines.Timeline(next!.ArrangementId, nextDeck);
        var nextBlocks = nextDeck is null ? [] : BlocksFor(nextDeck, next!.ArrangementId);
        var planned = SongTransitions.PlanFor(item.Transition, timeline, BlocksOf(deck), nextTimeline is null ? null : (nextTimeline, nextBlocks));
        if (planned is not { } plan)
        {
            DisarmGo();
            return;
        }
        plan = SongTransitions.Landing(plan, PlayheadTime) ?? plan;
        var nextSong = next is not null && nextDeck is not null ? MultitrackSongFor(next.Id, nextDeck, next.ArrangementId) : null;
        var go = new ArmedGo(itemId, plan, next, nextSong, tracksSong is not null);
        if (tracksSong is { } song)
        {
            tracks?.Transition(new MultitrackTransition(
                song, nextSong, nextSong is null ? plan.End : plan.NextAt, plan.FadeFrom, plan.FadeSeconds,
                nextSong is null ? 0 : plan.FadeInSeconds, plan.End));
        }
        armedGo = go;
        var firesAt = Now.AddSeconds(Math.Max(plan.NextAt - PlayheadTime, 0));
        var name = next is null ? null : host.Name(next.RefId) ?? next.Name;
        var pending = new PendingGo(itemId, kind, name, firesAt);
        if (Pending is not { } current || current.ItemId != pending.ItemId || current.Kind != pending.Kind || current.NextName != pending.NextName
            || Math.Abs((current.FiresAt - pending.FiresAt).TotalSeconds) > 0.25)
        {
            Pending = pending;
            Note("timeline.transition", $"armed {kind.Raw()} at {F(plan.NextAt)} into {name ?? "nothing"}");
        }
    }

    /// <summary>Cancel Transition: this song stops at its end instead.</summary>
    public void CancelGo()
    {
        if (ItemId is not { } itemId) return;
        cancelledItemId = itemId;
        DisarmGo();
        Note("timeline.transition", "cancelled");
        Changed?.Invoke();
    }

    private void DisarmGo()
    {
        if (armedGo is null && Pending is null) return;
        if (armedGo?.InPlayer == true) tracks?.Transition(null);
        armedGo = null;
        Pending = null;
    }

    private void HandOver(ArmedGo go)
    {
        var musicFaded = armedGo?.MusicFaded ?? go.MusicFaded;
        armedGo = null;
        Pending = null;
        if (go.Plan.FadeFrom is { } fadeFrom && !musicFaded)
            FadeMusic(Math.Max(go.Plan.End - Math.Max(fadeFrom, go.Plan.NextAt), 0.01), go.Plan.FadeInSeconds > 0 ? AudioFadeCurve.EqualPower : AudioFadeCurve.Cosine);
        Note("timeline.transition", $"{go.Plan.Kind.Raw()} into {go.Next?.Name ?? "nothing"} at {F(go.Plan.NextAt)}");
        if (go.Next is not { } next) return;
        var handedInPlayer = go.NextSong is { } nextSong && tracks?.LatestSongId == nextSong.Id;
        var tail = (tracksSong is not null && !handedInPlayer) || stemsTail;
        tracksSong = null;
        stemsTail = false;

        // An overlap lets this song's music play on to its end, then stops it.
        var music = go.Plan.FadeFrom is null ? PlayingMusic() : [];
        if (music.Count > 0)
        {
            ShowTimer? stopper = null;
            stopper = ShowTimer.After(clock, ui, TimeSpan.FromSeconds(Math.Max(go.Plan.End - go.Plan.NextAt, 0)), () =>
            {
                foreach (var id in music)
                    if (host.Music?.Player(id)?.IsPlaying == true) host.Music.Stop(id);
                if (stopper is not null) musicStoppers.Remove(stopper);
            });
            musicStoppers.Add(stopper);
        }
        keepingMusic = true;
        try
        {
            var deck = next.ItemKind == ServiceItemKind.Presentation ? host.Presentation(next.RefId) : null;
            var hasTimeline = deck is not null && PresentationTimelines.Timeline(next.ArrangementId, deck) is not null;
            switch (SongTransitions.NextStartFor(next, hasTimeline))
            {
                case SongTransitions.NextStart.PlayTimeline:
                    if (deck is not null)
                    {
                        host.OpenTimelineCard(next.Id);
                        Play(next.Id, deck, next.ArrangementId, handedInPlayer ? go.NextSong?.Id : null, handingOver: true);
                    }
                    break;
                case SongTransitions.NextStart.Fire:
                    Stop();
                    host.FireServiceItem(next);
                    break;
            }
            if (tail)
            {
                stemsTail = true;
                stemsTailEnds = HostNow + Math.Max(go.Plan.End - go.Plan.NextAt, 0) + 0.5;
            }
        }
        finally
        {
            keepingMusic = false;
        }
    }

    /// <summary>The next item a transition hands to: the next presentation or media in the run order. Port of ServiceRunOrder.nextFireable.</summary>
    public static ServiceItem? NextFireable(IReadOnlyList<ServiceItem> show, string itemId)
    {
        var index = show.ToList().FindIndex(i => i.Id == itemId);
        if (index < 0) return null;
        return show.Skip(index + 1).FirstOrDefault(i => i.ItemKind is ServiceItemKind.Presentation or ServiceItemKind.Media);
    }

    // MARK: Crossing and landing

    /// <summary>Normal play to a position: fires what lies between the last tick and it (and records the take's sweep).</summary>
    public void Advance(double position)
    {
        if (recordingTimeline is { } before && take is not null)
        {
            take.Played(lastPosition, position);
            var swept = before.Copy();
            swept.Cues = take.Cues;
            recordingTimeline = swept;
            if (swept.Cues.Count != before.Cues.Count && PresentationId is { } presentationId) Save(swept, presentationId, null);
        }
        if (Deck() is not { } deck || CuesOf(deck) is not { } cues)
        {
            lastPosition = position;
            return;
        }
        var blocks = BlocksOf(deck);
        var effects = PresentationTimelines.Crossing(cues, blocks, lastPosition, position, host.Live);
        lastPosition = position;
        Apply(effects, cues, deck);
        NoteNextCue(cues, blocks, position);
    }

    /// <summary>A jump: shows what is at the position, without running actions.</summary>
    public void Land(double position)
    {
        lastPosition = position;
        if (Deck() is not { } deck || CuesOf(deck) is not { } cues) return;
        var blocks = BlocksOf(deck);
        Apply(PresentationTimelines.Landing(cues, blocks, position, host.Live), cues, deck);
        NoteNextCue(cues, blocks, position);
    }

    private void NoteNextCue(IReadOnlyList<TimelineCue> cues, IReadOnlyList<TimelinePlayedBlock> blocks, double position)
    {
        var running = Playing || underTimecode;
        NextCueInfo? next = null;
        if (!Recording && running && ItemId is { } itemId && PresentationTimelines.NextSlideCue(position, cues, blocks) is { } cue)
            next = new NextCueInfo(itemId, cue.Id, Now.AddSeconds(cue.At - position));
        var nextAt = next?.FiresAt ?? DateTimeOffset.MinValue;
        var heldAt = NextCue?.FiresAt ?? DateTimeOffset.MinValue;
        if (next?.CueId != NextCue?.CueId || next?.ItemId != NextCue?.ItemId || Math.Abs((nextAt - heldAt).TotalSeconds) > 0.25) NextCue = next;
    }

    private void Apply(List<TimelineEffect> effects, IReadOnlyList<TimelineCue> cues, Presentation deck)
    {
        var applied = underTimecode ? TimecodeFollow.UnderTimecode(effects, timecodePlaysAudio) : effects;
        if (applied.Count == 0) return;
        Fire(applied, cues, deck);
    }

    private void Fire(IReadOnlyList<TimelineEffect> effects, IReadOnlyList<TimelineCue> cues, Presentation deck)
    {
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case TimelineEffect.ShowSlide slide:
                {
                    var slides = SlideSceneBuilder.ArrangedSlides(deck, ArrangementId, host.EmptyRepeatsOnce);
                    if (slide.Occurrence < 0 || slide.Occurrence >= slides.Count) continue;
                    firing = true;
                    try
                    {
                        host.FireSlide(slides[slide.Occurrence], deck, ArrangementId, ItemId, slide.Occurrence, slide.Actions);
                    }
                    finally
                    {
                        firing = false;
                    }
                    break;
                }
                case TimelineEffect.Fire fire:
                    FireLane(fire.Lane, fire.Id);
                    break;
                case TimelineEffect.Clear clear:
                    ClearLane(clear.Lane, clear.Id);
                    break;
                case TimelineEffect.PlayAudio play:
                    PlayMusic(play, cues);
                    break;
                case TimelineEffect.EndAudio end:
                    if (host.Music is { } music && music.Player(end.Id)?.ClipId == end.Clip) music.Stop(end.Id);
                    break;
                case TimelineEffect.RunActions run:
                    if (cues.FirstOrDefault(c => c.Id == run.CueId)?.Actions is { Count: > 0 } actions) host.RunActions(actions);
                    break;
            }
            Note("timeline.fire", $"{effect} at {F(lastPosition, 3)}");
        }
    }

    private void FireLane(TimelineLane lane, string id)
    {
        switch (lane)
        {
            case TimelineLane.Overlays:
                if (library.Overlay(id) is { } overlay) show.FireOverlay(overlay);
                break;
            case TimelineLane.Alerts:
                if (library.AlertPreset(id) is { } preset) show.FireAlert(preset);
                break;
            case TimelineLane.VideoInput:
                show.FireLiveInput(id, LayerKind.VideoInput);
                break;
            case TimelineLane.Videos:
            case TimelineLane.StillGraphics:
            case TimelineLane.LoopingVideos:
                if (library.Media(id) is { } media) show.FireMedia(media);
                break;
        }
    }

    private void ClearLane(TimelineLane lane, string id)
    {
        switch (lane)
        {
            case TimelineLane.Overlays:
                show.DismissOverlay(id);
                break;
            case TimelineLane.Alerts:
                if (host.LiveAlertId == id) show.DismissAlert();
                break;
            case TimelineLane.Audio:
                if (host.Music is { } music && music.Player(id) is not null) music.Stop(id);
                break;
            case TimelineLane.Videos:
            case TimelineLane.StillGraphics:
            case TimelineLane.LoopingVideos:
            case TimelineLane.VideoInput:
                if (host.LayerShowing(id, lane == TimelineLane.VideoInput) is { } layer) show.ClearLayer(layer);
                break;
        }
    }

    /// <summary>Plays (or seeks) a music clip: one run of split pieces plays as one, overlapping the clip before it in a crossfade.</summary>
    private void PlayMusic(TimelineEffect.PlayAudio play, IReadOnlyList<TimelineCue> cues)
    {
        if (host.Music is not { } music) return;
        var runs = TimelineClips.WithMusicRuns(cues);
        var player = music.ClipPlayer;
        if (player is not null && player.ClipId == play.Clip && player.SingleItemId == play.Id)
        {
            music.Seek(play.Id, play.From);
            return;
        }
        if (runs.FirstOrDefault(c => c.Id == play.Clip) is not { } run || library.Audio(play.Id) is not { } item) return;
        var current = player?.IsPlaying == true ? runs.FirstOrDefault(c => c.Id == player.ClipId) : null;
        var overlap = current is not null && current.At < run.At && PresentationTimelines.End(current, runs) > run.At + 0.001;
        var start = (item.InPoint ?? 0) + (run.InPoint ?? 0);
        var end = run.Duration is { } duration ? start + duration : item.OutPoint;
        double? due = stemsReading is { } reading ? reading.At + lastPosition - reading.Position : null;
        music.Play(new TimelineMusicClip(play.Clip, item, start, end, Math.Max(play.From, 0), Envelope(run, runs), overlap, due));
    }

    /// <summary>
    /// The app calls this whenever a slide lands live. While recording this song (and a
    /// timecode take has begun), it becomes a cue at the playhead.
    /// </summary>
    public void NoteSlideLanded(string? contextId, int? occurrence)
    {
        if (!Recording || firing || ItemId is not { } itemId || contextId != itemId || occurrence is not { } landed
            || !(TimecodeRecording?.Started ?? true) || Deck() is not { } deck || recordingTimeline is not { } timeline
            || PresentationTimelines.Anchor(landed, BlocksOf(deck)) is not { } anchor)
            return;
        var cue = PresentationTimelines.SlideCue(TimelineClips.NewId(), PresentationTimelines.SnappedToFrame(lastPosition), anchor);
        take?.Record(cue);
        var recorded = timeline.Copy();
        recorded.Cues = take?.Cues ?? PresentationTimelines.Recorded(cue, timeline.Cues);
        recordingTimeline = recorded;
        lastPosition = Math.Max(lastPosition, cue.At);
        Save(recorded, deck.Id, null);
        Note("timeline.record", string.Create(CultureInfo.InvariantCulture, $"{landed} at {cue.At}"));
        Changed?.Invoke();
    }

    private Presentation? Deck() => PresentationId is { } id ? host.Presentation(id) : null;

    private PresentationTimeline? TimelineOf(Presentation deck) => PresentationTimelines.Timeline(ArrangementId, deck);

    /// <summary>The cues playing: the take's while recording; under timecode a song with no slide cue gets its first slide at zero.</summary>
    private List<TimelineCue>? CuesOf(Presentation deck)
    {
        if ((recordingTimeline?.Cues ?? TimelineOf(deck)?.Cues) is not { } cues) return null;
        return underTimecode && TimecodeRecording is null ? TimecodeFollow.FollowedCues(cues, BlocksOf(deck)) : cues;
    }

    private List<TimelinePlayedBlock> BlocksOf(Presentation deck) => BlocksFor(deck, ArrangementId);

    private List<TimelinePlayedBlock> BlocksFor(Presentation deck, string? arrangementId) =>
        PresentationTimelines.Blocks(deck, arrangementId, host.EmptyRepeatsOnce);

    /// <summary>Saves a timeline into its presentation's list (replacing the one with its id).</summary>
    public void Save(PresentationTimeline timeline, string presentationId, string? undoLabel, TimelinesUndo? undoTo = null)
    {
        var all = PresentationTimelines.Replacing(timeline, host.Presentation(presentationId)?.Timelines);
        host.SaveTimelines(presentationId, all, undoLabel, undoTo);
    }

    /// <summary>
    /// A song with a section map lays its timeline out for the arrangement (each block's audio
    /// under its slides) before playing. True when it changed the timeline. Port of
    /// layOutFromMap in Sources/SongMapEditing.swift.
    /// </summary>
    public bool LayOutFromMap(string presentationId, string? arrangementId)
    {
        if (host.Presentation(presentationId) is not { } deck || deck.SongMap is not { } map) return false;
        var blocks = BlocksFor(deck, arrangementId);
        var timeline = PresentationTimelines.Timeline(arrangementId, deck) ?? new PresentationTimeline
        {
            Id = TimelineClips.NewId(),
            ArrangementId = string.IsNullOrEmpty(arrangementId) ? null : arrangementId,
            TimecodeEnabled = false,
            TimecodeStart = deck.Timelines?.FirstOrDefault()?.TimecodeStart ?? 3600 * PresentationTimelines.FramesPerSecond,
            Cues = [],
        };
        if (SongMaps.LaidOut(map, blocks, timeline) is not { } laid) return false;
        Save(laid, deck.Id, null);
        Note("timeline.songMap.laid", $"{laid.MapLayout?.Count ?? 0} blocks, {laid.Cues.Count} cues");
        return true;
    }
}
