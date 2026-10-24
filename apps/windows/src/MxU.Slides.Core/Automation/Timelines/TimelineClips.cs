using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// Audio clips on a timeline (music cues and track stems): splitting, trimming,
/// fades, crossfades, copy and paste, and ripple delete, all keeping each clip's
/// audio exactly where it was. A clip reads its file from <see cref="TimelineCue.InPoint"/>
/// seconds in. Port of PresenterCore/Timeline/TimelineClips.swift.
/// </summary>
public static class TimelineClips
{
    /// <summary>A fade's curve; the names match the audio engine's.</summary>
    public enum Shape { Linear, SCurve, EqualPower, Fast, Slow }

    public static readonly IReadOnlyList<Shape> Shapes = Enum.GetValues<Shape>();

    public static string Raw(this Shape shape) => shape switch
    {
        Shape.SCurve => "sCurve",
        Shape.EqualPower => "equalPower",
        Shape.Fast => "fast",
        Shape.Slow => "slow",
        _ => "linear",
    };

    /// <summary>A stored shape name; unknown or missing reads as linear.</summary>
    public static Shape ShapeNamed(string? stored) => Shapes.FirstOrDefault(s => s.Raw() == stored, Shape.Linear);

    public static string Title(this Shape shape) => shape switch
    {
        Shape.SCurve => "S-Curve",
        Shape.EqualPower => "Equal Power",
        Shape.Fast => "Fast",
        Shape.Slow => "Slow",
        _ => "Linear",
    };

    /// <summary>The tiny fade the engine puts on a clip's edges so a cut doesn't click.</summary>
    public const double EdgeSeconds = 0.005;

    /// <summary>Times this close (half a sample at 96 kHz) are the same time.</summary>
    internal const double SameTime = 0.5 / 96_000;

    /// <summary>A music cue or a track stem with its audio file.</summary>
    public static bool IsClip(TimelineCue cue) =>
        (cue.Kind() == TimelineLane.Audio || cue.TrackChannelId() is not null) && cue.MediaId is not null;

    /// <summary>
    /// Cuts a block in two at <paramref name="time"/> (on the frame grid). The right half gets
    /// <paramref name="newId"/>; a clip's right half reads on from where the left stopped.
    /// </summary>
    public static (TimelineCue Left, TimelineCue Right)? Split(
        TimelineCue cue, double time, IReadOnlyList<TimelineCue> cues, double? openEnd = null, Func<string>? newId = null)
    {
        if (!cue.IsBlock()) return null;
        time = PresentationTimelines.SnappedToFrame(time);
        var end = PresentationTimelines.End(cue, cues);
        if (!double.IsFinite(end) && openEnd is { } open) end = open;
        if (time <= cue.At + SameTime || time >= end - SameTime) return null;
        var left = cue.Copy();
        var right = cue.Copy();
        left.Duration = time - cue.At;
        left.FadeOut = null;
        right.Id = (newId ?? NewId)();
        right.At = time;
        right.Duration = double.IsFinite(end) ? end - time : null;
        right.FadeIn = null;
        if (IsClip(cue)) right.InPoint = (cue.InPoint ?? 0) + (time - cue.At);
        return (Fitted(left), Fitted(right));
    }

    /// <summary>Splits each chosen block the time crosses; the others are left as they are.</summary>
    public static List<TimelineCue> Splitting(IReadOnlySet<string> ids, double time, IReadOnlyList<TimelineCue> cues, Func<string>? newId = null)
    {
        var result = new List<TimelineCue>();
        foreach (var cue in cues)
        {
            if (ids.Contains(cue.Id) && Split(cue, time, cues, newId: newId) is { } pieces)
            {
                result.Add(pieces.Left);
                result.Add(pieces.Right);
            }
            else
            {
                result.Add(cue);
            }
        }
        return result;
    }

    /// <summary>Moves a clip's start, reading more or less of its file so the audio stays in place.</summary>
    public static TimelineCue TrimmedStart(TimelineCue cue, double seconds, double end)
    {
        var trimmed = cue.Copy();
        var earliest = Math.Max(0, cue.At - (cue.InPoint ?? 0));
        trimmed.At = Math.Min(Math.Max(earliest, PresentationTimelines.SnappedToFrame(cue.At + seconds)), end - PresentationTimelines.Frame);
        trimmed.InPoint = Math.Max(0, (cue.InPoint ?? 0) + (trimmed.At - cue.At));
        if ((trimmed.InPoint ?? 0) < SameTime) trimmed.InPoint = null;
        trimmed.Duration = end - trimmed.At;
        return Fitted(trimmed);
    }

    /// <summary>A copy of the cue with its fades shortened to fit its length (a fade cut to nothing goes).</summary>
    public static TimelineCue Fitted(TimelineCue cue)
    {
        var fitted = cue.Copy();
        if (cue.Duration is not { } duration) return fitted;
        if (fitted.FadeIn is { } fadeIn)
        {
            fadeIn.Seconds = Math.Min(fadeIn.Seconds, duration);
            fitted.FadeIn = fadeIn.Seconds > 0 ? fadeIn : null;
        }
        if (fitted.FadeOut is { } fadeOut)
        {
            fadeOut.Seconds = Math.Min(fadeOut.Seconds, duration - (fitted.FadeIn?.Seconds ?? 0));
            fitted.FadeOut = fadeOut.Seconds > 0 ? fadeOut : null;
        }
        return fitted;
    }

    /// <summary>A new fade's length: a second, or a third of a short clip.</summary>
    public static double DefaultFadeSeconds(TimelineCue cue) => Math.Min(1, (cue.Duration ?? 3) / 3);

    /// <summary>Dragging a fade's handle (its length) or its curve's midpoint.</summary>
    public static ClipFade Dragged(ClipFade fade, bool midpoint, bool @out, double seconds, TimelineCue clip, double end)
    {
        var dragged = fade.Copy();
        if (midpoint)
        {
            dragged.Midpoint = Math.Min(Math.Max((fade.Midpoint ?? 0.5) + seconds / Math.Max(fade.Seconds, 0.01), 0.05), 0.95);
        }
        else
        {
            var other = (@out ? clip.FadeIn : clip.FadeOut)?.Seconds ?? 0;
            dragged.Seconds = Math.Min(Math.Max(fade.Seconds + (@out ? -seconds : seconds), 0.01), Math.Max(end - clip.At - other, 0.01));
        }
        return dragged;
    }

    /// <summary>Where two clips on the same lane play at once, if they do.</summary>
    public static ClosedRange? Overlap(TimelineCue a, TimelineCue b, IReadOnlyList<TimelineCue> cues)
    {
        if (a.Id == b.Id || a.Lane != b.Lane || !IsClip(a) || !IsClip(b)) return null;
        var (first, second) = a.At <= b.At ? (a, b) : (b, a);
        var end = Math.Min(PresentationTimelines.End(first, cues), PresentationTimelines.End(second, cues));
        if (!double.IsFinite(end) || end - second.At <= SameTime) return null;
        return new ClosedRange(second.At, end);
    }

    public static List<TimelineCue> Overlapping(TimelineCue cue, IReadOnlyList<TimelineCue> cues) =>
        cues.Where(other => Overlap(cue, other, cues) is not null).OrderBy(c => c.At).ToList();

    /// <summary>Fades the earlier clip out and the later in across their overlap.</summary>
    public static (TimelineCue Left, TimelineCue Right)? Crossfaded(
        TimelineCue a, TimelineCue b, IReadOnlyList<TimelineCue> cues, Shape shape = Shape.EqualPower)
    {
        if (Overlap(a, b, cues) is not { } overlap) return null;
        var (left, right) = a.At <= b.At ? (a.Copy(), b.Copy()) : (b.Copy(), a.Copy());
        var fade = new ClipFade { Seconds = overlap.Upper - overlap.Lower, Shape = shape.Raw() };
        var leftEnd = PresentationTimelines.End(left, cues);
        left.FadeOut = new ClipFade { Seconds = leftEnd - overlap.Lower, Shape = shape.Raw() };
        right.FadeIn = fade;
        return (Fitted(left), Fitted(right));
    }

    /// <summary>Where a crossfade made at a butt cut sits: before it, centred on it, or after it.</summary>
    public enum Placement { Pre, Centered, Post }

    public static string Raw(this Placement placement) => placement switch
    {
        Placement.Pre => "pre",
        Placement.Post => "post",
        _ => "centered",
    };

    public static Placement? PlacementNamed(string? raw) => raw switch
    {
        "pre" => Placement.Pre,
        "centered" => Placement.Centered,
        "post" => Placement.Post,
        _ => null,
    };

    public static string Title(this Placement placement) => placement switch
    {
        Placement.Pre => "Before the Cut",
        Placement.Post => "After the Cut",
        _ => "Centered on the Cut",
    };

    /// <summary>The time two clips on a lane butt together (one ends where the other starts).</summary>
    public static double? Cut(TimelineCue a, TimelineCue b, IReadOnlyList<TimelineCue> cues)
    {
        if (a.Id == b.Id || a.Lane != b.Lane || !IsClip(a) || !IsClip(b)) return null;
        var (left, right) = a.At <= b.At ? (a, b) : (b, a);
        var end = PresentationTimelines.End(left, cues);
        return Math.Abs(end - right.At) < SameTime ? right.At : null;
    }

    public static List<TimelineCue> Butted(TimelineCue cue, IReadOnlyList<TimelineCue> cues) =>
        cues.Where(other => Cut(cue, other, cues) is not null).OrderBy(c => c.At).ToList();

    /// <summary>
    /// Where a crossfade of <paramref name="seconds"/> fits at a cut, given the spare audio
    /// after the left clip's end and before the right clip's start. Centred if it fits, else
    /// after, else before; shortened to the room there is, and none under a frame.
    /// </summary>
    public static (Placement Placement, double Seconds)? PlacementFor(double seconds, double after, double before, Placement? preferring = null)
    {
        var frame = PresentationTimelines.Frame;
        double Fit(Placement placement) => placement switch
        {
            Placement.Centered => Math.Min(seconds, 2 * Math.Min(after, before)),
            Placement.Post => Math.Min(seconds, after),
            _ => Math.Min(seconds, before),
        };
        if (preferring is { } wanted) return Fit(wanted) >= frame ? (wanted, Fit(wanted)) : null;
        foreach (var placement in new[] { Placement.Centered, Placement.Post, Placement.Pre })
            if (Fit(placement) >= seconds - SameTime) return (placement, seconds);
        var best = after >= before ? Placement.Post : Placement.Pre;
        return Fit(best) >= frame ? (best, Fit(best)) : null;
    }

    /// <summary>Turns a butt cut into a crossfade by letting each clip play on into its spare audio.</summary>
    public static (TimelineCue Left, TimelineCue Right, Placement Placement)? CrossfadedAtCut(
        TimelineCue a, TimelineCue b, IReadOnlyList<TimelineCue> cues, double after, double before, double seconds = 1,
        Shape shape = Shape.EqualPower, Placement? placement = null)
    {
        if (Cut(a, b, cues) is not { } cut || PlacementFor(seconds, after, before, placement) is not { } fit) return null;
        var (left, right) = a.At <= b.At ? (a.Copy(), b.Copy()) : (b.Copy(), a.Copy());
        var length = fit.Seconds;
        var early = fit.Placement switch
        {
            Placement.Pre => length,
            Placement.Centered => length / 2,
            _ => 0,
        };
        left.Duration = (cut - left.At) + (length - early);
        right.At = cut - early;
        right.InPoint = (right.InPoint ?? 0) - early;
        if ((right.InPoint ?? 0) < SameTime) right.InPoint = null;
        right.Duration = right.Duration + early;
        left.FadeOut = new ClipFade { Seconds = length, Shape = shape.Raw() };
        right.FadeIn = new ClipFade { Seconds = length, Shape = shape.Raw(), Placement = fit.Placement.Raw() };
        return (left, right, fit.Placement);
    }

    /// <summary>Undoes <see cref="CrossfadedAtCut"/>: back to a butt cut where the crossfade was placed.</summary>
    public static (TimelineCue Left, TimelineCue Right)? Uncrossfaded(TimelineCue a, TimelineCue b, IReadOnlyList<TimelineCue> cues)
    {
        if (Overlap(a, b, cues) is not { } overlap) return null;
        var (left, right) = a.At <= b.At ? (a.Copy(), b.Copy()) : (b.Copy(), a.Copy());
        if (PlacementNamed(right.FadeIn?.Placement) is not { } placement) return null;
        var length = overlap.Upper - overlap.Lower;
        var cut = placement switch
        {
            Placement.Pre => overlap.Upper,
            Placement.Centered => overlap.Lower + length / 2,
            _ => overlap.Lower,
        };
        right.InPoint = (right.InPoint ?? 0) + (cut - right.At);
        if ((right.InPoint ?? 0) < SameTime) right.InPoint = null;
        right.Duration = right.Duration - (cut - right.At);
        right.At = cut;
        left.Duration = cut - left.At;
        left.FadeOut = null;
        right.FadeIn = null;
        return (left, right);
    }

    /// <summary>The spare audio either side of a clip: before its in point, and after its end up to the file's length.</summary>
    public static (double Before, double After) Handles(TimelineCue cue, double? windowLength, IReadOnlyList<TimelineCue> cues)
    {
        var end = PresentationTimelines.End(cue, cues);
        var read = (cue.InPoint ?? 0) + (end - cue.At);
        return (cue.InPoint ?? 0, Math.Max(0, (windowLength ?? read) - read));
    }

    /// <summary>Whether each edge gets the anti-click fade: not where the same audio carries on in the next piece.</summary>
    public static (bool Start, bool End) Edges(TimelineCue cue, IReadOnlyList<TimelineCue> cues)
    {
        if (!(cue.EdgeFades ?? true)) return (false, false);
        var end = PresentationTimelines.End(cue, cues);
        var read = (cue.InPoint ?? 0) + (end - cue.At);
        var joinsNext = cues.Any(other =>
            other.Id != cue.Id && other.Lane == cue.Lane && other.MediaId == cue.MediaId && Math.Abs(other.At - end) < SameTime
            && Math.Abs((other.InPoint ?? 0) - read) < SameTime);
        var joinsPrevious = cues.Any(other =>
        {
            if (other.Id == cue.Id || other.Lane != cue.Lane || other.MediaId != cue.MediaId) return false;
            var otherEnd = PresentationTimelines.End(other, cues);
            return Math.Abs(otherEnd - cue.At) < SameTime
                && Math.Abs((other.InPoint ?? 0) + (otherEnd - other.At) - (cue.InPoint ?? 0)) < SameTime;
        });
        return (!joinsPrevious, !joinsNext);
    }

    /// <summary>
    /// Music clips that were split but still read on unbroken play as one clip (a "run"),
    /// so the music doesn't restart at the cut. Pieces that moved or gained a fade play apart.
    /// </summary>
    public static List<TimelineCue> WithMusicRuns(IReadOnlyList<TimelineCue> cues)
    {
        var music = cues.Where(c => c.Kind() == TimelineLane.Audio && IsClip(c)).OrderBy(c => c.At).ToList();
        if (music.Count <= 1) return [.. cues];
        var runs = new List<TimelineCue>();
        var merged = new HashSet<string>();
        foreach (var clip in music)
        {
            var index = runs.FindLastIndex(run => run.MediaId == clip.MediaId);
            if (index >= 0)
            {
                var run = runs[index];
                var end = PresentationTimelines.End(run, cues);
                if (run.FadeOut is null && clip.FadeIn is null && Math.Abs(clip.At - end) < SameTime
                    && Math.Abs((clip.InPoint ?? 0) - ((run.InPoint ?? 0) + end - run.At)) < SameTime)
                {
                    run.Duration = clip.Duration is { } duration ? clip.At + duration - run.At : null;
                    run.FadeOut = clip.FadeOut?.Copy();
                    merged.Add(clip.Id);
                    continue;
                }
            }
            runs.Add(clip.Copy());
        }
        if (merged.Count == 0) return [.. cues];
        var byId = new Dictionary<string, TimelineCue>();
        foreach (var run in runs) byId.TryAdd(run.Id, run);
        return cues.Where(c => !merged.Contains(c.Id)).Select(c => byId.GetValueOrDefault(c.Id) ?? c).ToList();
    }

    /// <summary>
    /// Cues for the clipboard, timed from the first one. With a range, only what the range
    /// covers, blocks cut to it.
    /// </summary>
    public static List<TimelineCue> Copied(IReadOnlyList<TimelineCue> cues, IReadOnlyList<TimelineCue> all, ClosedRange? range = null)
    {
        if (range is not { } window)
        {
            var start = cues.Count == 0 ? 0 : cues.Min(c => c.At);
            return cues.Select(c =>
            {
                var copy = c.Copy();
                copy.At -= start;
                return copy;
            }).ToList();
        }
        var kept = new List<TimelineCue>();
        foreach (var original in cues)
        {
            var cue = original.Copy();
            var end = PresentationTimelines.End(original, all);
            if (!cue.IsBlock())
            {
                if (window.Contains(cue.At))
                {
                    cue.At -= window.Lower;
                    kept.Add(cue);
                }
                continue;
            }
            if (cue.At >= window.Upper || end <= window.Lower) continue;
            if (cue.At < window.Lower) cue = TrimmedStartExactly(cue, window.Lower, end);
            var cueEnd = cue.At + (cue.Duration ?? (end - cue.At));
            if (cueEnd > window.Upper || !double.IsFinite(cueEnd))
            {
                cue.Duration = window.Upper - cue.At;
                cue.FadeOut = null;
            }
            cue.At -= window.Lower;
            kept.Add(Fitted(cue));
        }
        return kept;
    }

    /// <summary>Clipboard cues placed at a time, each with a new id, on the frame grid.</summary>
    public static List<TimelineCue> Pasted(IReadOnlyList<TimelineCue> clipboard, double time, Func<string>? newId = null) =>
        clipboard.Select(cue =>
        {
            var pasted = cue.Copy();
            pasted.Id = (newId ?? NewId)();
            pasted.At = PresentationTimelines.SnappedToFrame(Math.Max(0, time) + cue.At);
            return pasted;
        }).ToList();

    private static TimelineCue TrimmedStartExactly(TimelineCue cue, double time, double end)
    {
        var cut = cue.Copy();
        if (IsClip(cue)) cut.InPoint = (cue.InPoint ?? 0) + (time - cue.At);
        cut.At = time;
        cut.Duration = double.IsFinite(end) ? end - time : null;
        cut.FadeIn = null;
        return cut;
    }

    /// <summary>
    /// Removes a stretch of time: what was in it goes, everything after moves up, and a clip
    /// that spans the cut becomes two pieces that butt together (its audio past the cut kept).
    /// </summary>
    public static PresentationTimeline RippleDeleted(PresentationTimeline timeline, ClosedRange range, Func<string>? newId = null)
    {
        double a = range.Lower, b = range.Upper, cut = b - a;
        if (cut <= 0) return timeline;
        var result = timeline.Copy();
        var cues = new List<TimelineCue>();
        var all = timeline.Cues;
        foreach (var cue in all)
        {
            if (!cue.IsBlock())
            {
                if (cue.At >= b)
                {
                    var moved = cue.Copy();
                    moved.At -= cut;
                    cues.Add(moved);
                }
                else if (cue.At < a)
                {
                    cues.Add(cue);
                }
                continue;
            }
            var end = PresentationTimelines.End(cue, all);
            var open = cue.Duration is null;
            if (cue.At >= b)
            {
                var moved = cue.Copy();
                moved.At -= cut;
                cues.Add(moved);
            }
            else if (end <= a && !(open && double.IsInfinity(end)))
            {
                cues.Add(cue);
            }
            else if (cue.At >= a && end <= b)
            {
                continue;
            }
            else if (cue.At < a)
            {
                var head = cue.Copy();
                if (!open || IsClip(cue))
                {
                    head.Duration = a - cue.At;
                    head.FadeOut = null;
                }
                if (end > b)
                {
                    if (IsClip(cue))
                    {
                        var tail = TrimmedStartExactly(cue, b, end);
                        tail.Id = (newId ?? NewId)();
                        tail.At = a;
                        tail.FadeOut = cue.FadeOut?.Copy();
                        cues.Add(Fitted(head));
                        cues.Add(Fitted(tail));
                        continue;
                    }
                    head.Duration = open ? null : (end - cue.At) - cut;
                    head.FadeOut = cue.FadeOut?.Copy();
                }
                cues.Add(Fitted(head));
            }
            else
            {
                var tail = IsClip(cue) ? TrimmedStartExactly(cue, b, end) : cue.Copy();
                tail.At = a;
                if (!IsClip(cue)) tail.Duration = open ? null : end - b;
                cues.Add(Fitted(tail));
            }
        }
        result.Cues = cues;
        if (timeline.Length is { } length && length > a) result.Length = length - (Math.Min(length, b) - a);
        return result;
    }

    internal static string NewId() => Guid.NewGuid().ToString().ToUpperInvariant();
}
