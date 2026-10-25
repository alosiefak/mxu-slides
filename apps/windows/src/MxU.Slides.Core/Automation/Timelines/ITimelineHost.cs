using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// What the <see cref="TimelinePlayer"/> needs from the app beyond <see cref="IShowActions"/>:
/// the presentations and current service, saving a timeline, firing a slide or a service
/// item, and what is up on the outputs right now. On the Mac this is ServiceControls and AppModel.
/// </summary>
public interface ITimelineHost
{
    Presentation? Presentation(string id);

    /// <summary>The Present setting "Show a repeated chord-only section once" (it changes the played blocks).</summary>
    bool EmptyRepeatsOnce => SlideSceneBuilder.EmptyRepeatsOnce;

    /// <summary>
    /// Stores a presentation's timelines. <paramref name="undoLabel"/> null means no undo step
    /// (a take in progress); <paramref name="undoTo"/>, when given, is what undo puts back.
    /// </summary>
    void SaveTimelines(string presentationId, List<PresentationTimeline>? timelines, string? undoLabel, TimelinesUndo? undoTo = null);

    /// <summary>A service item of the current service, by id.</summary>
    ServiceItem? CurrentServiceItem(string itemId);

    /// <summary>The current service's fireable items in run order (the Mac's fireableItems).</summary>
    IReadOnlyList<ServiceItem> FireableItems();

    /// <summary>A library item's name (for "next: Way Maker").</summary>
    string? Name(string refId);

    /// <summary>Fires a slide as the timeline's own (contextId is the service item; actions false on a jump).</summary>
    void FireSlide(Slide slide, Presentation presentation, string? arrangementId, string? contextId, int occurrence, bool runSlideActions);

    /// <summary>Fires the next service item in the run order (what a transition hands over to when it has no timeline).</summary>
    void FireServiceItem(ServiceItem item);

    /// <summary>Runs an actions cue's steps (the WPF app passes them to <see cref="ActionRouter.Execute(IReadOnlyList{SlideAction})"/>).</summary>
    void RunActions(IReadOnlyList<SlideAction> actions);

    /// <summary>
    /// Runs an actions cue, knowing which cue (on Windows its production steps run beside its
    /// actions, so a cue may have none of its own).
    /// </summary>
    void RunActions(string cueId, IReadOnlyList<SlideAction> actions)
    {
        if (actions.Count > 0) RunActions(actions);
    }

    /// <summary>The ids up now: media and live inputs on any layer, overlays, music items playing, and the alert.</summary>
    IReadOnlySet<string> Live { get; }

    /// <summary>The live alert's id, if one is up.</summary>
    string? LiveAlertId { get; }

    /// <summary>The layer showing a media item (or, <paramref name="liveInput"/>, a live input), if it is up.</summary>
    LayerKind? LayerShowing(string contentId, bool liveInput);

    /// <summary>A music file's path on this computer, or null when it isn't here (stems need it).</summary>
    string? AudioFilePath(AudioItem item);

    /// <summary>The music players (the Audio function); null when this computer has none yet.</summary>
    ITimelineMusic? Music { get; }

    /// <summary>Asks the UI to open this item's timeline card (a handover or resume moved to it).</summary>
    void OpenTimelineCard(string itemId) { }
}

/// <summary>How a fade-out falls.</summary>
public enum AudioFadeCurve { Linear, EqualPower, Cosine }

/// <summary>A music player as the timeline sees it.</summary>
public sealed record MusicPlayerState(string? SingleItemId, string? ClipId, bool IsPlaying, double Elapsed);

/// <summary>
/// A music clip for the Audio function to play: the item from <see cref="Start"/> to <see cref="End"/>
/// seconds of its file, starting <see cref="From"/> seconds into the clip, shaped by its envelope.
/// <see cref="Overlap"/> plays it alongside the clip still sounding (a crossfade);
/// <see cref="Due"/> lines it up with the stems when they are playing.
/// </summary>
public sealed record TimelineMusicClip(
    string Id, AudioItem Item, double Start, double? End, double From, ClipEnvelope Envelope, bool Overlap, double? Due);

/// <summary>The Audio function's players, for the timeline's music lane.</summary>
public interface ITimelineMusic
{
    /// <summary>The player playing this music item, if any.</summary>
    MusicPlayerState? Player(string audioItemId);

    /// <summary>The first player playing a timeline clip, if any.</summary>
    MusicPlayerState? ClipPlayer { get; }

    /// <summary>Moves the clip player to a point.</summary>
    void Seek(string audioItemId, double seconds);

    void Play(TimelineMusicClip clip);

    /// <summary>Gets ready to play soon (opens the sound device ahead of time); a timeline with music asks before it plays.</summary>
    void Prepare() { }

    /// <summary>Stops an item's player, at once or with a fade.</summary>
    void Stop(string audioItemId, double? fadeSeconds = null, AudioFadeCurve curve = AudioFadeCurve.Linear);
}

/// <summary>A clip's gain envelope for the audio engine (port of AudioEngine's ClipEnvelope).</summary>
public sealed record ClipEnvelope(ClipEnvelope.Fade? FadeIn, ClipEnvelope.Fade? FadeOut, double Edge, bool EdgeIn, bool EdgeOut)
{
    public sealed record Fade(double Seconds, TimelineClips.Shape Shape, double Midpoint);
}

/// <summary>One stem of a multitrack song: a file on a track channel, placed and trimmed.</summary>
public sealed record MultitrackStem(string Path, string ChannelId, double At, double? InPoint, double? Duration, ClipEnvelope? Envelope);

/// <summary>A song's stems for the tracks player; the id is "serviceItem|presentation|arrangement".</summary>
public sealed record MultitrackSong(string Id, IReadOnlyList<MultitrackStem> Stems)
{
    public bool Equals(MultitrackSong? other) => other is not null && Id == other.Id && Stems.SequenceEqual(other.Stems);

    public override int GetHashCode() => HashCode.Combine(Id, Stems.Count);
}

/// <summary>A song transition for the tracks player to run itself (sample-accurate), in the outgoing song's seconds.</summary>
public sealed record MultitrackTransition(
    string FromSongId, MultitrackSong? Next, double NextAt, double? FadeFrom, double FadeSeconds, double FadeInSeconds, double End);

/// <summary>
/// The multitrack tracks player (stems through the audio interface). Optional: without
/// one the timeline runs on its own clock and stems don't sound. Port of the calls the
/// Mac's PresentationTimelineController makes on its TracksPlayer.
/// </summary>
public interface ITracksPlayer
{
    /// <summary>The song the player last reported playing.</summary>
    string? LatestSongId { get; }

    /// <summary>The player is reconnecting (keep waiting rather than falling back to the own clock).</summary>
    bool Reattaching { get; }

    /// <summary>Where a song is, in its seconds, as the player last reported (null when it isn't playing that song).</summary>
    double? Position(string songId);

    void Play(MultitrackSong song, double from);

    /// <summary>Crossfades from whatever plays to a song (null: fade out to silence).</summary>
    void Crossfade(MultitrackSong? to, double from, double seconds);

    void Stop();

    void Seek(double position);

    /// <summary>Follows timecode: be at <paramref name="position"/> at host time <paramref name="hostTime"/>.</summary>
    void Chase(MultitrackSong song, double position, double hostTime);

    /// <summary>Arms (or, null, disarms) a handover for the player to run.</summary>
    void Transition(MultitrackTransition? transition);

    /// <summary>Loads the next songs' files ahead of time.</summary>
    void CueAhead(IReadOnlyList<MultitrackSong> songs);
}

/// <summary>What undo should put back for a timelines save (null timelines: none).</summary>
public sealed record TimelinesUndo(List<PresentationTimeline>? Timelines);
