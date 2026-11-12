using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Media;

/// <summary>
/// Video marks ("Video Actions"): named points on a video's timeline. Playing across one runs
/// its actions; a stage countdown can count to the next mark or a named one; a text box can show
/// the coming mark's name. Times are seconds in the pass the operator sees (0 = the in point).
/// Port of the Mac's PresenterCore/Timeline/VideoMarks.swift.
/// </summary>
public static class VideoMarks
{
    /// <summary>A mark placed in the pass: <paramref name="At"/> is seconds after the in point.</summary>
    public sealed record Point(string Id, string Name, double At, string? ColorHex = null);

    /// <summary>A video that starts playing within this long of its top fires the marks it has passed.</summary>
    public const double StartGrace = 1.0;

    /// <summary>How much a reading may run back or beyond what the elapsed time explains and still count as playing.</summary>
    public const double Tolerance = 0.15;

    /// <summary>The marks inside the pass, as pass times, in time order (ties by id).</summary>
    public static List<Point> Points(IEnumerable<VideoMark>? marks, double? inPoint, double length)
    {
        var offset = inPoint ?? 0;
        return (marks ?? [])
            .Select(m => new Point(m.Id, m.Name, m.At - offset, m.ColorHex))
            .Where(p => p.At >= 0 && p.At <= length)
            .OrderBy(p => p.At).ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The marks crossed between two readings of a playing video. A jump (a seek, or more ground
    /// than <paramref name="elapsed"/> at <paramref name="rate"/> explains) fires nothing, except
    /// landing back near the top, which fires what's there. A loop's wrap fires both sides of it.
    /// </summary>
    public static List<Point> Crossed(IReadOnlyList<Point> points, double? from, double to, double elapsed, double rate,
        double length, bool looping)
    {
        if (length <= 0) return [];
        if (from is not { } start)
            return to < StartGrace ? points.Where(p => p.At <= to).ToList() : [];
        var allowed = Math.Max(0, elapsed) * Math.Max(rate, 0) + Tolerance;
        var forward = to - start;
        if (looping && forward < -Tolerance) forward += length;
        if (forward < -Tolerance || forward > allowed)
            return to < start && to < StartGrace ? points.Where(p => p.At <= to).ToList() : [];
        if (to >= start) return points.Where(p => p.At > start && p.At <= to).ToList();
        if (!looping || forward <= 0) return [];
        return [.. points.Where(p => p.At > start), .. points.Where(p => p.At <= to)];
    }

    /// <summary>The mark a countdown counts to: none for the end, else the next mark (of that name).</summary>
    public static Point? Target(IReadOnlyList<Point> points, double after, VideoCountdownTarget target, string? name)
    {
        var ahead = points.Where(p => p.At > after);
        return target switch
        {
            VideoCountdownTarget.NextMark => ahead.FirstOrDefault(),
            VideoCountdownTarget.NamedMark when !string.IsNullOrEmpty(name) =>
                ahead.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
    }

    /// <summary>Where a count-up starts: the last mark passed under the same rule, else the top.</summary>
    public static double CountUpStart(IReadOnlyList<Point> points, double before, VideoCountdownTarget target, string? name)
    {
        var behind = points.Where(p => p.At <= before).ToList();
        return target switch
        {
            VideoCountdownTarget.End => 0,
            VideoCountdownTarget.NamedMark when !string.IsNullOrEmpty(name) =>
                behind.LastOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.At ?? 0,
            _ => behind.LastOrDefault()?.At ?? 0,
        };
    }

    /// <summary>Seconds until the next mark, round the loop if the video loops; null when none is coming.</summary>
    public static double? UntilNext(IReadOnlyList<Point> points, double after, double length, bool looping)
    {
        if (points.FirstOrDefault(p => p.At > after) is { } next) return next.At - after;
        if (!looping || points.Count == 0 || length <= 0) return null;
        return length - after + points[0].At;
    }

    /// <summary>A mark made from an action is named after its target: "Fire Alert: Band on Stage" is "Band on Stage".</summary>
    public static string DefaultName(string actionLabel)
    {
        var colon = actionLabel.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0) return actionLabel;
        var target = actionLabel[(colon + 2)..].Trim();
        return target.Length == 0 ? actionLabel : target;
    }

    /// <summary>"Mark N": the next number no mark is already called.</summary>
    public static string DefaultName(IReadOnlyList<VideoMark>? marks)
    {
        var names = (marks ?? []).Select(m => m.Name.ToLowerInvariant()).ToHashSet();
        var n = (marks?.Count ?? 0) + 1;
        while (names.Contains($"mark {n}")) n++;
        return $"Mark {n}";
    }

    public static List<VideoMark> Adding(IEnumerable<VideoMark>? marks, string id, string name, double at, List<SlideAction> actions, double fileLength) =>
        Sorted([.. marks ?? [], new VideoMark { Id = id, Name = name, At = ClampedTime(at, fileLength), Actions = actions }]);

    public static List<VideoMark> Moving(IEnumerable<VideoMark>? marks, string id, double to, double fileLength) =>
        Sorted((marks ?? []).Select(m =>
        {
            if (m.Id != id) return m;
            var moved = Json.MxuJson.Clone(m);
            moved.At = ClampedTime(to, fileLength);
            return moved;
        }));

    // The three edits below are what the Mac's media editor does inline (MediaEditorView.swift's
    // writeMarks closures). Each returns a new list and leaves the given marks alone, so the
    // window can keep showing the old ones if the save fails.

    /// <summary>Renames a mark. A blank name is refused (the old one stays), as on the Mac.</summary>
    public static List<VideoMark> Renaming(IEnumerable<VideoMark>? marks, string id, string name)
    {
        var trimmed = name.Trim();
        return (marks ?? []).Select(m =>
        {
            if (m.Id != id || trimmed.Length == 0) return m;
            var renamed = Json.MxuJson.Clone(m);
            renamed.Name = trimmed;
            return renamed;
        }).ToList();
    }

    /// <summary>Gives a mark the actions it runs when playback crosses it.</summary>
    public static List<VideoMark> WithActions(IEnumerable<VideoMark>? marks, string id, IEnumerable<SlideAction> actions) =>
        (marks ?? []).Select(m =>
        {
            if (m.Id != id) return m;
            var changed = Json.MxuJson.Clone(m);
            changed.Actions = [.. actions.Select(Json.MxuJson.Clone)];
            return changed;
        }).ToList();

    public static List<VideoMark> Removing(IEnumerable<VideoMark>? marks, string id) =>
        (marks ?? []).Where(m => m.Id != id).ToList();

    /// <summary>A mark's time as the operator reads it: "0:07.5", "12:03.0", "1:02:03.0" (tenths, never rounded up past the mark).</summary>
    public static string Clock(double seconds)
    {
        var tenths = (long)Math.Floor(Math.Max(seconds, 0) * 10 + 1e-6);
        var (hours, minutes, rest) = (tenths / 36000, tenths / 600 % 60, tenths % 600);
        var tail = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{rest / 10:00}.{rest % 10}");
        return hours > 0 ? $"{hours}:{minutes:00}:{tail}" : $"{minutes}:{tail}";
    }

    /// <summary>Seconds from a typed time: "ss", "m:ss" or "h:mm:ss", with decimals ("1:02.5"). Null when it can't be read.</summary>
    public static double? ParseClock(string typed)
    {
        var parts = typed.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return null;
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var value)) return null;
            total = total * 60 + value;
        }
        return total;
    }

    // Inside the file, to the millisecond.
    private static double ClampedTime(double at, double fileLength)
    {
        var upper = fileLength > 0 ? fileLength : at;
        return Math.Round(Math.Min(Math.Max(at, 0), upper) * 1000, MidpointRounding.AwayFromZero) / 1000;
    }

    private static List<VideoMark> Sorted(IEnumerable<VideoMark> marks) =>
        marks.OrderBy(m => m.At).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
}
