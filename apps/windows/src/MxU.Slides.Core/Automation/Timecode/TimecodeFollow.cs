using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// Following timecode through a service: each song with a timecode-enabled timeline owns
/// a window of the timecode line from its start. Each tick, <see cref="Step"/> finds the
/// song the clock is in and says whether to play on (crossing cues in order, with their
/// actions) or land (a jump: show what is there without running actions). Port of
/// TimecodeFollow in PresenterCore/Timeline/TimecodeFollow.swift.
/// </summary>
public sealed class TimecodeFollow
{
    /// <summary>A song's window: its start (30 fps frames) and its length, plus a short tail an open-ended timeline keeps.</summary>
    public sealed record Song(string ItemId, int StartFrames, double Length, double Tail = 0)
    {
        /// <summary>The window for a service item's timeline (<paramref name="start"/> overrides its saved start).</summary>
        public static Song For(string itemId, PresentationTimeline timeline, int? start = null) =>
            new(itemId, start ?? timeline.TimecodeStart, PresentationTimelines.Length(timeline), timeline.Length is null ? 1 : 0);
    }

    /// <summary>What the timeline player should do this tick.</summary>
    public abstract record Command(string ItemId)
    {
        /// <summary>Normal play from one position to the next: cues between fire in order, with actions.</summary>
        public sealed record Play(string ItemId, double From, double To) : Command(ItemId);

        /// <summary>A jump: show what is at the position, without running actions.</summary>
        public sealed record Land(string ItemId, double At) : Command(ItemId);

        /// <summary>Where the playhead goes.</summary>
        public double Position => this switch
        {
            Play play => play.To,
            Land land => land.At,
            _ => 0,
        };
    }

    /// <summary>A move of more than this many seconds is a jump.</summary>
    public const double Jump = 1.0;

    /// <summary>Playback machines step back a few frames when they resume; that's not a jump.</summary>
    public const int StepBackFrames = 3;

    public string? ItemId { get; private set; }
    public double? Position { get; private set; }

    /// <summary>
    /// The song whose window holds the timecode, and how far into it. A song's own length
    /// wins over another's tail, so an open-ended song never steals the next one's start.
    /// </summary>
    public static (Song Song, double Position)? SongContaining(double seconds, TimecodeRate rate, IReadOnlyList<Song> songs)
    {
        var placed = songs.Select(s => (Song: s, At: seconds - rate.Seconds(rate.LabelForStartFrames(s.StartFrames)))).ToList();
        foreach (var p in placed) if (p.At >= 0 && p.At < p.Song.Length) return (p.Song, p.At);
        foreach (var p in placed) if (p.At >= 0 && p.At < p.Song.Length + p.Song.Tail) return (p.Song, p.At);
        return null;
    }

    /// <summary>One tick: null holds (not live, outside every song, or a playback's small step back).</summary>
    public Command? Step(TimecodeClock.Reading reading, IReadOnlyList<Song> songs)
    {
        if (!reading.Live || reading.Position is not { } seconds || reading.Rate is not { } rate
            || SongContaining(seconds, rate, songs) is not { } found)
            return null;
        var (song, position) = found;
        var id = song.ItemId;
        var previousItem = ItemId;
        ItemId = id;
        if (id != previousItem || Position is not { } last)
        {
            Position = position;
            return Enter(id, position);
        }
        var delta = position - last;
        if (delta <= 0 && -delta <= StepBackFrames / rate.Actual() + 0.001) return null;
        Position = position;
        if (delta > 0 && delta <= Jump) return new Command.Play(id, last, position);
        return Enter(id, position);
    }

    /// <summary>Songs whose windows overlap another's (the timecode can't tell them apart).</summary>
    public static HashSet<string> Clashes(IReadOnlyList<Song> songs)
    {
        var windows = songs.Select(s => new PresentationTimelines.Window(
            s.ItemId, s.StartFrames, PresentationTimelines.Frames(s.Length + s.Tail))).ToList();
        return windows.Where(w => PresentationTimelines.Overlaps(w, windows).Count > 0).Select(w => w.Id).ToHashSet();
    }

    /// <summary>A slide cue as the cue list shows it: its timecode and "Section · slide number".</summary>
    public sealed record CueRow(string Id, string Timecode, string Label, double At);

    public static List<CueRow> CueRows(
        PresentationTimeline timeline, IReadOnlyList<TimelinePlayedBlock> blocks, Func<string?, string> sectionName, int? start = null)
    {
        var starts = PresentationTimelines.BlockStarts(blocks);
        var rows = new List<CueRow>();
        foreach (var cue in timeline.Cues.Where(c => c.Kind() == TimelineLane.Slide).OrderBy(c => c.At))
        {
            if (PresentationTimelines.Occurrence(cue, blocks) is not { } occurrence) continue;
            var block = starts.FindLastIndex(s => s <= occurrence);
            if (block < 0) continue;
            var frames = (start ?? timeline.TimecodeStart) + (int)SwiftMath.Rounded(cue.At * PresentationTimelines.FramesPerSecond);
            rows.Add(new CueRow(
                cue.Id, PresentationTimelines.Timecode(frames),
                $"{sectionName(blocks[block].SectionId)} · {occurrence - starts[block] + 1}", cue.At));
        }
        return rows;
    }

    /// <summary>Under timecode the audio lane stays quiet unless "Play Audio" is on (the playback rig plays the tracks).</summary>
    public static List<TimelineEffect> UnderTimecode(IEnumerable<TimelineEffect> effects, bool playsAudio = false)
    {
        if (playsAudio) return effects.ToList();
        return effects.Where(effect => effect switch
        {
            TimelineEffect.PlayAudio or TimelineEffect.EndAudio => false,
            TimelineEffect.Fire fire => fire.Lane != TimelineLane.Audio,
            TimelineEffect.Clear clear => clear.Lane != TimelineLane.Audio,
            _ => true,
        }).ToList();
    }

    /// <summary>A song with no slide cue still shows its first slide from timecode (a cue at zero is added for following).</summary>
    public static List<TimelineCue> FollowedCues(IReadOnlyList<TimelineCue> cues, IReadOnlyList<TimelinePlayedBlock> blocks)
    {
        if (cues.Any(c => c.Kind() == TimelineLane.Slide) || PresentationTimelines.Anchor(0, blocks) is not { } anchor) return cues.ToList();
        return [PresentationTimelines.SlideCue("timecode.firstSlide", 0, anchor), .. cues];
    }

    /// <summary>Music already playing this close to where timecode says is left alone.</summary>
    public const double AudioDrift = 0.5;

    /// <summary>
    /// Keeps the music lane in step with timecode: the clip under the playhead plays (or is
    /// re-seeked if it drifted), any other music from this timeline stops.
    /// <paramref name="playing"/> is each audio item's elapsed seconds.
    /// </summary>
    public static List<TimelineEffect> AudioSync(IReadOnlyList<TimelineCue> cues, double position, IReadOnlyDictionary<string, double> playing)
    {
        var all = TimelineClips.WithMusicRuns(cues);
        var tracks = all.Where(c => c.Kind() == TimelineLane.Audio && c.ContentId() is not null).ToList();
        var held = tracks.Where(c => c.At <= position && position < PresentationTimelines.End(c, all)).MaxBy(c => c.At);
        var effects = new List<TimelineEffect>();
        foreach (var id in tracks.Select(c => c.ContentId()!).Distinct().Order(StringComparer.Ordinal))
            if (id != held?.ContentId() && playing.ContainsKey(id)) effects.Add(new TimelineEffect.Clear(TimelineLane.Audio, id));
        if (held?.ContentId() is { } heldId)
        {
            var due = position - held.At;
            if (playing.TryGetValue(heldId, out var at) && Math.Abs(at - due) <= AudioDrift) return effects;
            effects.Add(new TimelineEffect.PlayAudio(heldId, due, held.Id));
        }
        return effects;
    }

    /// <summary>Entering a song near its top plays from just before zero (so the first cues fire); further in, it lands.</summary>
    private static Command Enter(string id, double position) =>
        position <= Jump ? new Command.Play(id, -PresentationTimelines.Frame, position) : new Command.Land(id, position);
}

/// <summary>
/// Recording a timeline against timecode: armed, it waits for the song's start to come
/// round, then the take runs until the timecode stops. Port of
/// PresenterCore/Timeline/TimecodeRecording.swift.
/// </summary>
public sealed record TimecodeRecording(string ItemId, int? Start)
{
    /// <summary>The take's start, once known (a new song takes it from the timecode heard).</summary>
    public int? Start { get; set; } = Start;

    public bool Started { get; set; }

    /// <summary>How Record behaves: on the timeline's own clock, or waiting for timecode.</summary>
    public abstract record Mode
    {
        private Mode() { }

        public sealed record OwnClock : Mode;

        public sealed record Timecode(int? Start) : Mode;
    }

    /// <summary>Record waits for timecode only on a computer that has an input and follows it, for a timecode-enabled song.</summary>
    public static Mode ModeFor(PresentationTimeline? timeline, bool hasInput, bool follows, int? start = null)
    {
        if (!hasInput || !follows) return new Mode.OwnClock();
        if (timeline is null) return new Mode.Timecode(null);
        return timeline.TimecodeEnabled ? new Mode.Timecode(start ?? timeline.TimecodeStart) : new Mode.OwnClock();
    }

    /// <summary>A new song starts at the hour and minute the timecode is in.</summary>
    public static int StartFrom(TimecodeLabel label) => (label.Hours * 3600 + label.Minutes * 60) * PresentationTimelines.FramesPerSecond;

    /// <summary>What a tick does to the take.</summary>
    public abstract record Step
    {
        private Step() { }

        public sealed record None : Step;

        public sealed record Begin(int Start, double At) : Step;

        public sealed record End : Step;
    }

    public Step StepFor(TimecodeClock.Reading reading)
    {
        if (!reading.Live)
            return Started && reading.State is TimecodeClock.State.Stopped or TimecodeClock.State.NoInput ? new Step.End() : new Step.None();
        if (Started || reading.Position is not { } seconds || reading.Rate is not { } rate) return new Step.None();
        if ((Start ?? (reading.Label is { } label ? StartFrom(label) : null)) is not { } start) return new Step.None();
        var position = seconds - rate.Seconds(rate.LabelForStartFrames(start));
        return position >= 0 ? new Step.Begin(start, position) : new Step.None();
    }

    /// <summary>While recording, timecode follows only this song, and never off its end.</summary>
    public List<TimecodeFollow.Song> Songs(IEnumerable<TimecodeFollow.Song> songs) =>
        songs.Where(s => s.ItemId == ItemId).Select(s => s with { Length = double.MaxValue }).ToList();
}
