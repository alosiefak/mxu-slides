using System.Globalization;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// The timeline editor's rules, as pure functions over a song's cues and the editor's
/// selection: which lanes show, which cues move together, what a drag, nudge, delete,
/// split, copy or paste does, the fade and crossfade menu commands, snapping a drag onto
/// other cues, and making new cues. Every edit returns a new list of cues and never changes
/// the one it was given, so the editor can keep the old one for undo.
/// The WPF editor (Views/TimelineEditorWindow and ViewModels/TimelineEditorViewModel) only
/// turns the mouse and keys into these calls and saves what they return. Port of the
/// editing half of Sources/PresentationTimelineView.swift (PresentationTimelineLanes).
/// </summary>
public static class TimelineEditing
{
    // MARK: Lanes

    /// <summary>Lanes every song shows (slides and background media); the others appear once they hold a cue or are added.</summary>
    public static readonly IReadOnlySet<TimelineLane> StandingLanes = new HashSet<TimelineLane> { TimelineLane.Slide, TimelineLane.LoopingVideos };

    /// <summary>The lanes shown for these cues, in the Mac's order (alerts at the top, actions at the bottom).</summary>
    public static List<TimelineLane> VisibleLanes(IReadOnlyList<TimelineCue> cues, IReadOnlySet<TimelineLane> added) =>
        TimelineLanes.All.Where(lane => StandingLanes.Contains(lane) || added.Contains(lane) || cues.Any(c => c.Kind() == lane)).ToList();

    /// <summary>A lane's name as the editor shows it.</summary>
    public static string Title(this TimelineLane lane) => lane switch
    {
        TimelineLane.Alerts => "Alerts",
        TimelineLane.Overlays => "Overlays",
        TimelineLane.Slide => "Slides",
        TimelineLane.Videos => "Foreground Videos",
        TimelineLane.StillGraphics => "Still Graphics",
        TimelineLane.LoopingVideos => "Background Media",
        TimelineLane.VideoInput => "Video Input",
        TimelineLane.Audio => "Music",
        _ => "Actions",
    };

    // MARK: Ruler and length

    /// <summary>The ruler's tick spacing in seconds: the first of 1, 2, 5, 10, 15, 30 or 60 that leaves 44 points between ticks.</summary>
    public static double Interval(double pointsPerSecond)
    {
        foreach (var seconds in new double[] { 1, 2, 5, 10, 15, 30, 60 })
            if (seconds * pointsPerSecond >= 44) return seconds;
        return 120;
    }

    /// <summary>A ruler label: minutes and seconds ("2:05").</summary>
    public static string Clock(double seconds)
    {
        var total = (int)SwiftMath.Rounded(seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 60}:{total % 60:00}");
    }

    /// <summary>
    /// How much of the song the editor draws: the timeline's set length, else its content
    /// plus a little room after (8%, at least two seconds) to drag things past the end.
    /// An empty timeline shows half a minute.
    /// </summary>
    public static double DisplayLength(PresentationTimeline? timeline)
    {
        if (timeline is null || timeline.Cues.Count == 0) return 30;
        var length = Math.Max(PresentationTimelines.Length(timeline), 1);
        return timeline.Length ?? length + Math.Max(2, length * 0.08);
    }

    /// <summary>The editor's map from time to x: one stretch of the song, at a zoom.</summary>
    public static TimelineMap Map(double displayLength, double pointsPerSecond) =>
        new([new TimelineSegment(0, displayLength, displayLength * pointsPerSecond)], pointsPerSecond);

    /// <summary>The first zoom: the song (up to 45 seconds of it) fitted to the view, else 12 points a second.</summary>
    public static double StartingZoom(double displayLength, double viewportWidth) =>
        TimelineZoom.Fit(Math.Min(displayLength, 45), viewportWidth, reserve: 0, TimelineZoom.SongRange) ?? 12;

    // MARK: Selection

    /// <summary>A press that turned into a drag: a card outside the selection becomes the only one selected.</summary>
    public static HashSet<string> Engaged(IReadOnlySet<string> selection, string id) =>
        selection.Contains(id) ? [.. selection] : [id];

    /// <summary>
    /// The selection after a press on a card is let go: a plain click picks just that card,
    /// a Ctrl (or Shift) click adds it or takes it out, and a drag keeps what it moved.
    /// </summary>
    public static HashSet<string> Released(IReadOnlySet<string> selection, string id, TimelineBlockEdit.Mode? dragged, bool toggle)
    {
        if (dragged is null && toggle)
        {
            var toggled = new HashSet<string>(selection);
            if (!toggled.Remove(id)) toggled.Add(id);
            return toggled;
        }
        if (dragged is null || !selection.Contains(id)) return [id];
        return [.. selection];
    }

    /// <summary>The one bar standing for all the stems while the track rows are folded.</summary>
    public static bool IsFolded(TimelineCue cue) => cue.Id.StartsWith(TimelineTracks.FoldedPrefix, StringComparison.Ordinal);

    /// <summary>The folded stems bar, when it is selected.</summary>
    public static TimelineCue? SelectedFolded(IReadOnlyList<TimelineCue> cues, IReadOnlySet<string> selection) =>
        selection.Any(id => id.StartsWith(TimelineTracks.FoldedPrefix, StringComparison.Ordinal))
            ? TimelineTracks.Folded(TimelineTracks.Stems(cues), cues)
            : null;

    /// <summary>The cues a command on this card reaches: every stem for the folded bar, else just the cue.</summary>
    public static HashSet<string> Reach(TimelineCue cue, IReadOnlyList<TimelineCue> cues) =>
        IsFolded(cue) ? TimelineTracks.Reach(cue, cues) : [cue.Id];

    /// <summary>Every cue the selection stands for (the folded bar stands for every stem).</summary>
    public static HashSet<string> PickedIds(IReadOnlyList<TimelineCue> cues, IReadOnlySet<string> selection)
    {
        var picked = cues.Where(c => selection.Contains(c.Id)).ToList();
        if (SelectedFolded(cues, selection) is { } folded) picked.Add(folded);
        var ids = new HashSet<string>();
        foreach (var cue in picked) ids.UnionWith(Reach(cue, cues));
        return ids;
    }

    /// <summary>The cue the keyboard acts on: the first selected one, else the folded bar if that is selected.</summary>
    public static TimelineCue? Picked(IReadOnlyList<TimelineCue> cues, IReadOnlySet<string> selection) =>
        cues.FirstOrDefault(c => selection.Contains(c.Id)) ?? SelectedFolded(cues, selection);

    /// <summary>
    /// The cues that move with this one: the whole selection when it is part of it, and for
    /// each stem the others that start with it (a song's stems move as one).
    /// </summary>
    public static HashSet<string> Together(TimelineCue cue, IReadOnlyList<TimelineCue> cues, IReadOnlySet<string> selection)
    {
        var picked = selection.Contains(cue.Id) ? cues.Where(c => selection.Contains(c.Id)).ToList() : [];
        var ids = new HashSet<string> { cue.Id };
        foreach (var each in picked.Count == 0 ? [cue] : picked) ids.UnionWith(TimelineTracks.Reach(each, cues));
        return ids;
    }

    // MARK: Moving, nudging and deleting

    /// <summary>
    /// A drag or a nudge: moves the cue (and what moves with it), or stretches or trims a
    /// block, on the frame grid. Slide cues that land on another slide cue step a frame at a
    /// time until they are clear of it. <paramref name="openEnd"/> stands in for the end of a
    /// block that runs until the next one. The folded stems bar only moves.
    /// </summary>
    public static List<TimelineCue> Moved(
        IReadOnlyList<TimelineCue> cues, TimelineCue cue, TimelineBlockEdit.Mode mode, double seconds,
        IReadOnlySet<string> selection, double? openEnd = null)
    {
        var folded = IsFolded(cue);
        var id = folded ? cue.Id[TimelineTracks.FoldedPrefix.Length..] : cue.Id;
        if (folded) mode = TimelineBlockEdit.Mode.Move;
        if (!cues.Any(c => c.Id == id)) return [.. cues];
        var together = mode == TimelineBlockEdit.Mode.Move ? Together(cue, cues, selection) : [id];
        var moved = cues.ToList();
        for (var index = 0; index < cues.Count; index++)
        {
            var each = cues[index];
            if (together.Contains(each.Id))
                moved[index] = PresentationTimelines.Edited(each, cues, mode, seconds, each.Id == id ? openEnd : null);
        }
        return PresentationTimelines.SteppingSlideCuesApart(moved, together, forward: seconds >= 0);
    }

    /// <summary>The undo label for a delete.</summary>
    public static string DeleteLabel(TimelineCue cue, IReadOnlySet<string> selection) =>
        selection.Count > 1 && selection.Contains(cue.Id) ? "Remove Cues" : "Remove Cue";

    /// <summary>Removes the cue, or the whole selection when it is part of it (the folded bar takes every stem with it).</summary>
    public static List<TimelineCue> Deleted(IReadOnlyList<TimelineCue> cues, TimelineCue cue, IReadOnlySet<string> selection)
    {
        var picked = selection.Contains(cue.Id) ? cues.Where(c => selection.Contains(c.Id)).Append(cue).ToList() : [cue];
        var gone = new HashSet<string>();
        foreach (var each in picked) gone.UnionWith(Reach(each, cues));
        return cues.Where(c => !gone.Contains(c.Id)).ToList();
    }

    /// <summary>"Remove These Stems": every stem that starts with this one.</summary>
    public static List<TimelineCue> RemovingStems(IReadOnlyList<TimelineCue> cues, TimelineCue stem)
    {
        var group = TimelineTracks.Reach(stem, cues);
        return cues.Where(c => !group.Contains(c.Id)).ToList();
    }

    // MARK: Split, copy, paste

    /// <summary>What Split at Playhead cuts: the selection, or every clip when nothing is selected.</summary>
    public static HashSet<string> SplitTargets(IReadOnlyList<TimelineCue> cues, IReadOnlySet<string> selection)
    {
        var picked = PickedIds(cues, selection);
        return picked.Count > 0 ? picked : cues.Where(TimelineClips.IsClip).Select(c => c.Id).ToHashSet();
    }

    /// <summary>
    /// What Copy puts on the clipboard: everything in the range when there is one (and no
    /// cue was named), else the named cues or the selection. Timed from the first one.
    /// </summary>
    public static List<TimelineCue> Copied(
        IReadOnlyList<TimelineCue> cues, IReadOnlySet<string> selection, ClosedRange? range, IReadOnlySet<string>? ids = null)
    {
        if (ids is null && range is { } window) return TimelineClips.Copied(cues, cues, window);
        var picked = ids ?? PickedIds(cues, selection);
        return TimelineClips.Copied(cues.Where(c => picked.Contains(c.Id)).ToList(), cues);
    }

    // MARK: Fades and crossfades

    /// <summary>A copy of the cue list with one cue changed (on its own copy).</summary>
    public static List<TimelineCue> Changing(IReadOnlyList<TimelineCue> cues, string id, Func<TimelineCue, TimelineCue> change) =>
        cues.Select(c => c.Id == id ? change(c.Copy()) : c).ToList();

    /// <summary>A copy of the cue list with these cues put in place of the ones with their ids.</summary>
    public static List<TimelineCue> Writing(IReadOnlyList<TimelineCue> cues, params TimelineCue[] clips)
    {
        var byId = new Dictionary<string, TimelineCue>();
        foreach (var clip in clips) byId[clip.Id] = clip;
        return cues.Select(c => byId.GetValueOrDefault(c.Id) ?? c).ToList();
    }

    /// <summary>
    /// Adds, reshapes or (with no shape) removes a clip's fade in or out. A new fade is a
    /// second long (a third of a short clip); a reshaped one keeps its length and midpoint.
    /// </summary>
    public static List<TimelineCue> WithFade(IReadOnlyList<TimelineCue> cues, string id, bool @out, TimelineClips.Shape? shape) =>
        Changing(cues, id, clip =>
        {
            var current = @out ? clip.FadeOut : clip.FadeIn;
            var fade = shape is { } chosen
                ? new ClipFade
                {
                    Seconds = current?.Seconds ?? TimelineClips.DefaultFadeSeconds(clip),
                    Shape = chosen.Raw(),
                    Midpoint = current?.Midpoint,
                }
                : null;
            if (@out) clip.FadeOut = fade;
            else clip.FadeIn = fade;
            return TimelineClips.Fitted(clip);
        });

    /// <summary>A fade handle let go: the fade as it was dragged, fitted to the clip.</summary>
    public static List<TimelineCue> WithFadeSet(IReadOnlyList<TimelineCue> cues, string id, bool @out, ClipFade fade) =>
        Changing(cues, id, clip =>
        {
            if (@out) clip.FadeOut = fade.Copy();
            else clip.FadeIn = fade.Copy();
            return TimelineClips.Fitted(clip);
        });

    /// <summary>The 5 ms anti-click fades on a clip's edges, on (the default, stored as nothing) or off.</summary>
    public static List<TimelineCue> WithEdgeFades(IReadOnlyList<TimelineCue> cues, string id, bool on) =>
        Changing(cues, id, clip =>
        {
            clip.EdgeFades = on ? null : false;
            return clip;
        });

    /// <summary>Crossfades two overlapping clips across their overlap; null when they don't overlap.</summary>
    public static List<TimelineCue>? Crossfading(IReadOnlyList<TimelineCue> cues, TimelineCue a, TimelineCue b, TimelineClips.Shape shape) =>
        TimelineClips.Crossfaded(a, b, cues, shape) is { } made ? Writing(cues, made.Left, made.Right) : null;

    /// <summary>Takes a crossfade made at a cut back to the plain cut; null when there isn't one.</summary>
    public static List<TimelineCue>? Uncrossfading(IReadOnlyList<TimelineCue> cues, TimelineCue a, TimelineCue b) =>
        TimelineClips.Uncrossfaded(a, b, cues) is { } undone ? Writing(cues, undone.Left, undone.Right) : null;

    /// <summary>
    /// The spare audio at a cut: after the left clip's end and before the right clip's start.
    /// <paramref name="window"/> gives a music item's usable length (out point less in point).
    /// </summary>
    public static (double After, double Before) Handles(
        TimelineCue left, TimelineCue right, IReadOnlyList<TimelineCue> cues, Func<string, double?> window)
    {
        double? Window(TimelineCue cue) => cue.MediaId is { } id ? window(id) : null;
        return (TimelineClips.Handles(left, Window(left), cues).After, TimelineClips.Handles(right, Window(right), cues).Before);
    }

    /// <summary>
    /// The crossfade a butt cut would get (or, with <paramref name="placement"/>, a crossfade
    /// already made moved to before, on or after the cut); null when there's no audio to
    /// spare. Used both to label the menu and to make it.
    /// </summary>
    public static (TimelineCue Left, TimelineCue Right, TimelineClips.Placement Placement)? Placed(
        TimelineCue left, TimelineCue right, TimelineClips.Placement? placement, IReadOnlyList<TimelineCue> cues, Func<string, double?> window)
    {
        var all = cues;
        if (TimelineClips.Uncrossfaded(left, right, all) is { } undone)
        {
            (left, right) = (undone.Left, undone.Right);
            all = Writing(all, left, right);
        }
        var room = Handles(left, right, all, window);
        var seconds = right.FadeIn?.Seconds ?? 1;
        return TimelineClips.CrossfadedAtCut(
            left, right, all, room.After, room.Before, seconds, TimelineClips.ShapeNamed(right.FadeIn?.Shape ?? "equalPower"), placement);
    }

    /// <summary>"Crossfade at the Cut": each clip plays on into its spare audio, crossfading over a second.</summary>
    public static List<TimelineCue>? CrossfadingAtCut(
        IReadOnlyList<TimelineCue> cues, TimelineCue left, TimelineCue right, TimelineClips.Shape shape, Func<string, double?> window)
    {
        var room = Handles(left, right, cues, window);
        return TimelineClips.CrossfadedAtCut(left, right, cues, room.After, room.Before, shape: shape) is { } made
            ? Writing(cues, made.Left, made.Right)
            : null;
    }

    /// <summary>Moves a crossfade made at a cut to before, on or after it.</summary>
    public static List<TimelineCue>? Replacing(
        IReadOnlyList<TimelineCue> cues, TimelineCue a, TimelineCue b, TimelineClips.Placement placement, Func<string, double?> window)
    {
        var (left, right) = a.At <= b.At ? (a, b) : (b, a);
        return Placed(left, right, placement, cues, window) is { } made ? Writing(cues, made.Left, made.Right) : null;
    }

    // MARK: Stems

    /// <summary>"Move Stems To": every stem on one track row goes to another.</summary>
    public static List<TimelineCue> MovingStems(IReadOnlyList<TimelineCue> cues, string from, string to)
    {
        var lane = TimelineTracks.Lane(to);
        return cues.Select(c =>
        {
            if (c.TrackChannelId() != from) return c;
            var moved = c.Copy();
            moved.Lane = lane;
            return moved;
        }).ToList();
    }

    // MARK: Snapping

    /// <summary>
    /// A card drag, pulled onto the nearest other cue's start or end (or the playhead) when an
    /// edge comes within six points of one. A moving block snaps by either edge; a stretch by
    /// its end; a trim by its start; a point cue by where it is, and not onto its own kind.
    /// <paramref name="left"/> and <paramref name="width"/> are the card before the drag.
    /// </summary>
    public static double SnappedDelta(
        double dx, TimelineCue cue, IReadOnlyList<TimelineCue> cues, TimelineBlockEdit.Mode mode, IReadOnlySet<string> selection,
        TimelineMap map, double left, double width, double? playhead)
    {
        var isBlock = cue.IsBlock();
        var end = PresentationTimelines.End(cue, cues);
        var moving = mode == TimelineBlockEdit.Mode.Move ? Together(cue, cues, selection) : [cue.Id];
        double[] edges = mode switch
        {
            TimelineBlockEdit.Mode.Move => isBlock && double.IsFinite(end) ? [left, left + width] : [left],
            TimelineBlockEdit.Mode.Stretch => [left + width],
            _ => [left],
        };
        var times = new List<double>();
        foreach (var other in cues)
        {
            if (moving.Contains(other.Id) || !(isBlock || other.Kind() != cue.Kind())) continue;
            times.Add(other.At);
            var otherEnd = PresentationTimelines.End(other, cues);
            if (double.IsFinite(otherEnd) && otherEnd > other.At) times.Add(otherEnd);
        }
        if (playhead is { } at) times.Add(at);
        return TimelineBlockEdit.SnappedDelta(dx, edges, times.Select(map.XOfTime).ToList());
    }

    /// <summary>A range dragged across the lanes, on the frame grid; null when it is empty.</summary>
    public static ClosedRange? Range(double from, double to)
    {
        var lower = PresentationTimelines.SnappedToFrame(Math.Min(from, to));
        var upper = PresentationTimelines.SnappedToFrame(Math.Max(from, to));
        return upper > lower ? new ClosedRange(lower, upper) : null;
    }

    // MARK: Cards

    /// <summary>How wide a point cue's pin is around its time; its label runs on to the right.</summary>
    public const double PinSize = 16;

    /// <summary>
    /// Where a cue's card sits on its lane, in points: a block spans its length (one that runs
    /// until replaced reaches the end of the view); a point cue (slide, actions) is a pin
    /// whose label runs up to the next cue on its lane, 16 to 140 points wide.
    /// </summary>
    public static (double X, double Width) Card(TimelineCue cue, IReadOnlyList<TimelineCue> cues, TimelineMap map)
    {
        var left = map.XOfTime(cue.At);
        if (cue.IsBlock())
        {
            var end = PresentationTimelines.End(cue, cues);
            return (left, double.IsFinite(end) ? map.XOfTime(end) - left : Math.Max(map.Width - left, TimelineBlockEdit.MinimumWidth));
        }
        var index = 0;
        for (var i = 0; i < cues.Count; i++)
            if (cues[i].Id == cue.Id)
            {
                index = i;
                break;
            }
        double? next = null;
        var kind = cue.Kind();
        for (var i = 0; i < cues.Count; i++)
        {
            var other = cues[i];
            if (other.Kind() != kind || !(other.At > cue.At || (other.At == cue.At && i > index))) continue;
            if (next is null || other.At < next) next = other.At;
        }
        var gap = (next is { } at ? map.XOfTime(at) : left + 140) - left;
        return (left, Math.Min(Math.Max(gap - 4, 16), 140));
    }

    // MARK: Making cues

    /// <summary>A new id for a cue (an upper-case GUID, like the Mac's UUID strings).</summary>
    public static string NewId() => TimelineClips.NewId();

    /// <summary>A block on a lane at a time, ten seconds long (overlays, alerts, video inputs).</summary>
    public static TimelineCue Block(TimelineLane lane, double at, Func<string>? newId = null) => new()
    {
        Id = (newId ?? NewId)(),
        Lane = lane.Raw(),
        At = PresentationTimelines.SnappedToFrame(Math.Max(0, at)),
        Duration = 10,
    };

    public static TimelineCue OverlayCue(string overlayId, double at, Func<string>? newId = null)
    {
        var cue = Block(TimelineLane.Overlays, at, newId);
        cue.OverlayId = overlayId;
        return cue;
    }

    public static TimelineCue AlertCue(string alertId, double at, Func<string>? newId = null)
    {
        var cue = Block(TimelineLane.Alerts, at, newId);
        cue.AlertId = alertId;
        return cue;
    }

    public static TimelineCue LiveInputCue(string liveInputId, double at, Func<string>? newId = null)
    {
        var cue = Block(TimelineLane.VideoInput, at, newId);
        cue.LiveInputId = liveInputId;
        return cue;
    }

    /// <summary>A music clip: the item's file from its in point to its out point.</summary>
    public static TimelineCue MusicCue(AudioItem item, double at, Func<string>? newId = null)
    {
        var length = (item.OutPoint ?? item.DurationSeconds) - (item.InPoint ?? 0);
        return new TimelineCue
        {
            Id = (newId ?? NewId)(),
            Lane = TimelineLane.Audio.Raw(),
            At = PresentationTimelines.SnappedToFrame(Math.Max(0, at)),
            Duration = length > 0 ? length : null,
            MediaId = item.Id,
        };
    }

    /// <summary>The lane a media item goes on: the layer it fires to (a background, a still or a foreground video).</summary>
    public static TimelineLane MediaLane(MediaItem media) =>
        TimelineLanes.Parse(SlideSceneBuilder.LayerKindOf(MediaCueRouting.Dropped(media).Layer).Raw()) ?? TimelineLane.Videos;

    /// <summary>A media cue: a background plays until replaced; a still or video for its length (ten seconds when unknown).</summary>
    public static TimelineCue MediaCue(MediaItem media, double at, Func<string>? newId = null)
    {
        var lane = MediaLane(media);
        return new TimelineCue
        {
            Id = (newId ?? NewId)(),
            Lane = lane.Raw(),
            At = PresentationTimelines.SnappedToFrame(Math.Max(0, at)),
            Duration = lane == TimelineLane.LoopingVideos ? null : media.DurationSeconds ?? 10,
            MediaId = media.Id,
        };
    }

    /// <summary>An actions cue that runs these actions.</summary>
    public static TimelineCue ActionsCue(IEnumerable<SlideAction> actions, double at, Func<string>? newId = null) => new()
    {
        Id = (newId ?? NewId)(),
        Lane = TimelineLane.Actions.Raw(),
        At = PresentationTimelines.SnappedToFrame(Math.Max(0, at)),
        Actions = [.. actions],
    };

    /// <summary>An actions cue that runs an action combo (what dropping a combo on the timeline makes).</summary>
    public static TimelineCue ComboCue(string comboId, double at, Func<string>? newId = null) =>
        ActionsCue([new SlideAction { Id = (newId ?? NewId)(), Kind = SlideActionKind.FireCombo, ComboId = comboId }], at, newId);

    /// <summary>
    /// A slide cue for the slide at a place in the song's played order (so "the second time
    /// the chorus plays" stays itself when the arrangement changes); null when there is none.
    /// </summary>
    public static TimelineCue? SlideCue(int occurrence, IReadOnlyList<TimelinePlayedBlock> blocks, double at, Func<string>? newId = null) =>
        PresentationTimelines.Anchor(occurrence, blocks) is { } anchor
            ? PresentationTimelines.SlideCue((newId ?? NewId)(), PresentationTimelines.SnappedToFrame(Math.Max(0, at)), anchor)
            : null;

    /// <summary>Adds a cue; a slide cue that lands on another steps a frame later until it is clear.</summary>
    public static List<TimelineCue> Adding(IReadOnlyList<TimelineCue> cues, TimelineCue cue) =>
        PresentationTimelines.SteppingSlideCuesApart([.. cues, cue], new HashSet<string> { cue.Id }, forward: true);

    // MARK: Song transition handle

    /// <summary>A dragged transition length: to the half second, at least half a second, at most 30 (or the song).</summary>
    public static double ClampedTransition(double seconds, double end) =>
        Math.Min(Math.Max(SwiftMath.Rounded(seconds * 2) / 2, 0.5), Math.Max(Math.Min(30, end), 0.5));

    /// <summary>"5 s", or "2.5 s" when it isn't whole.</summary>
    public static string Seconds(double value) =>
        value == SwiftMath.Rounded(value)
            ? string.Create(CultureInfo.InvariantCulture, $"{(long)value} s")
            : value.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    /// <summary>A song transition's name in menus.</summary>
    public static string Title(this SongTransitions.Kind kind) => kind switch
    {
        SongTransitions.Kind.Fade => "Fade",
        SongTransitions.Kind.Overlap => "Overlap",
        SongTransitions.Kind.Crossfade => "Crossfade",
        _ => "Stop",
    };

    /// <summary>The menu's lengths for a transition.</summary>
    public static readonly IReadOnlyList<double> TransitionLengths = [1, 2, 3, 5, 8, 10, 15, 20];

    /// <summary>
    /// Picking a kind in the transition menu: Stop with no typed length is stored as nothing;
    /// otherwise the kind keeps the length already typed. Port of SongTransitionMenuItems.
    /// </summary>
    public static SongTransition? Choosing(SongTransitions.Kind kind, SongTransition? current) =>
        kind == SongTransitions.Kind.Stop && current?.Seconds is null ? null : new SongTransition { Kind = kind.Raw(), Seconds = current?.Seconds };

    /// <summary>"Crossfade 5 s", "Overlap · auto", "Stop".</summary>
    public static string Summary(SongTransition? transition)
    {
        var kind = SongTransitions.KindOf(transition);
        if (kind == SongTransitions.Kind.Stop) return kind.Title();
        if (transition?.Seconds is { } seconds) return $"{kind.Title()} {Seconds(seconds)}";
        return kind == SongTransitions.Kind.Overlap ? $"{kind.Title()} · auto" : $"{kind.Title()} 5 s";
    }

    // MARK: Keys

    /// <summary>What a key does in the lanes (Mac ⌘ is Ctrl here).</summary>
    public abstract record KeyVerb
    {
        private KeyVerb() { }

        public sealed record Split : KeyVerb;

        public sealed record Copy : KeyVerb;

        public sealed record Paste : KeyVerb;

        public sealed record Delete : KeyVerb;

        public sealed record RippleDelete : KeyVerb;

        public sealed record Nudge(double Seconds) : KeyVerb;

        /// <summary>Esc while recording throws the take away.</summary>
        public sealed record Discard : KeyVerb;
    }

    /// <summary>
    /// The lanes' keys: Ctrl+B split, Ctrl+C copy, Ctrl+V paste, Delete or Backspace remove,
    /// Shift+Delete ripple delete, ← → a frame, Shift+← → a second, Esc discard the take.
    /// Port of TimelineClipKeyMonitor in TimelineClipEditing.swift. <paramref name="key"/> is
    /// "B", "C", "V", "Delete", "Backspace", "Left", "Right" or "Escape".
    /// </summary>
    public static KeyVerb? VerbFor(string key, bool control, bool shift, bool alt = false)
    {
        if (alt) return null;
        var delete = key is "Delete" or "Backspace";
        double? arrow = key == "Left" ? -1 : key == "Right" ? 1 : null;
        return (control, shift) switch
        {
            (true, false) when key == "B" => new KeyVerb.Split(),
            (true, false) when key == "C" => new KeyVerb.Copy(),
            (true, false) when key == "V" => new KeyVerb.Paste(),
            (false, false) when delete => new KeyVerb.Delete(),
            (false, true) when delete => new KeyVerb.RippleDelete(),
            (false, false) when arrow is { } frames => new KeyVerb.Nudge(frames * PresentationTimelines.Frame),
            (false, true) when arrow is { } seconds => new KeyVerb.Nudge(seconds),
            (false, false) when key == "Escape" => new KeyVerb.Discard(),
            _ => null,
        };
    }
}
