using System.Globalization;

namespace MxU.Slides.Core.Automation;

// Timeline drawing maths shared by every timeline editor: zoom, mapping time to x,
// dragging blocks, and keeping the playhead in view. Port of
// PresenterCore/Timeline/TimelineGeometry.swift (Swift's CGFloat is a double here).

/// <summary>Zoom in points per second.</summary>
public static class TimelineZoom
{
    public static readonly ClosedRange Range = new(24, 480);

    /// <summary>Each zoom button press scales by 30%.</summary>
    public const double StepFactor = 1.3;

    /// <summary>Points kept free at the right when fitting the whole timeline in view.</summary>
    public const double FitReserve = 116;

    public const double FitMinimumViewport = 140;

    /// <summary>The song map's zoom range.</summary>
    public static readonly ClosedRange SongRange = new(2, 120);

    public static double Clamped(double pointsPerSecond, ClosedRange? range = null)
    {
        var r = range ?? Range;
        return Math.Min(Math.Max(pointsPerSecond, r.Lower), r.Upper);
    }

    /// <summary>A pinch scales the zoom it started at.</summary>
    public static double Pinched(double @base, double magnification, ClosedRange? range = null) => Clamped(@base * magnification, range);

    public static double ZoomedIn(double pointsPerSecond, ClosedRange? range = null) => Math.Min(pointsPerSecond * StepFactor, (range ?? Range).Upper);

    public static double ZoomedOut(double pointsPerSecond, ClosedRange? range = null) => Math.Max(pointsPerSecond / StepFactor, (range ?? Range).Lower);

    /// <summary>The zoom that fits <paramref name="seconds"/> in the viewport; null when there is nothing to fit or no room.</summary>
    public static double? Fit(double seconds, double viewportWidth, double reserve = FitReserve, ClosedRange? range = null)
    {
        if (seconds <= 0 || viewportWidth <= FitMinimumViewport) return null;
        return Clamped((viewportWidth - reserve) / seconds, range);
    }
}

/// <summary>A column of the timeline: a stretch of time drawn at some width (an empty stretch gets a minimum width).</summary>
public sealed record TimelineSegment(double Start, double DisplayDuration, double Width)
{
    public static double WidthFor(double displayDuration, bool hasContent, double pointsPerSecond, double floor)
    {
        var natural = displayDuration * pointsPerSecond;
        return hasContent ? natural : Math.Max(natural, floor);
    }
}

/// <summary>Maps time to x and back across a timeline's segments.</summary>
public sealed record TimelineMap(IReadOnlyList<TimelineSegment> Segments, double PointsPerSecond)
{
    public double Width => Segments.Sum(s => s.Width);

    public double XOfSegment(int index) => Segments.Take(index).Sum(s => s.Width);

    /// <summary>The x of a time; a time in the gap between segments pins to the previous segment's end.</summary>
    public double XOfTime(double t)
    {
        var x = 0.0;
        for (var index = 0; index < Segments.Count; index++)
        {
            var segment = Segments[index];
            var next = index + 1 < Segments.Count ? Segments[index + 1].Start : double.PositiveInfinity;
            if (t < next)
            {
                var offset = Math.Max(t - segment.Start, 0);
                return x + Math.Min(offset * PointsPerSecond, segment.Width);
            }
            x += segment.Width;
        }
        return x;
    }

    /// <summary>The time under an x and its segment; past the end lands at the last segment's end.</summary>
    public (double Time, int Segment)? Locate(double x)
    {
        var cursor = 0.0;
        for (var index = 0; index < Segments.Count; index++)
        {
            var segment = Segments[index];
            if (x < cursor + segment.Width || index == Segments.Count - 1)
            {
                var offset = Math.Max(x - cursor, 0) / PointsPerSecond;
                return (segment.Start + Math.Min(offset, segment.DisplayDuration), index);
            }
            cursor += segment.Width;
        }
        return null;
    }

    public int? SegmentIndex(double x)
    {
        var cursor = 0.0;
        for (var index = 0; index < Segments.Count; index++)
        {
            cursor += Segments[index].Width;
            if (x < cursor) return index;
        }
        return Segments.Count > 0 ? Segments.Count - 1 : null;
    }

    /// <summary>The segment boundary within <paramref name="tolerance"/> of an x (never the trailing edge).</summary>
    public int? Boundary(double x, double tolerance)
    {
        if (Segments.Count <= 1) return null;
        for (var boundary = 1; boundary < Segments.Count; boundary++)
            if (Math.Abs(x - XOfSegment(boundary)) <= tolerance) return boundary;
        return null;
    }
}

/// <summary>Dragging a block card: which edge was grabbed, how it follows the pointer, and snapping.</summary>
public static class TimelineBlockEdit
{
    public enum Mode { Move, Stretch, Trim }

    /// <summary>Points the pointer moves before a press becomes a drag.</summary>
    public const double EngageDistance = 3;

    public const double MinimumWidth = 26;

    /// <summary>Cards narrower than this have no trim handle (it would hide the move).</summary>
    public const double MinimumTrimWidth = 34;

    public const double MinimumDuration = 0.05;

    /// <summary>The right edge stretches, the left edge trims (on a wide enough card), the middle moves.</summary>
    public static Mode ModeFor(double pressX, double frameMinX, double frameWidth)
    {
        var baseWidth = Math.Max(frameWidth, MinimumWidth);
        var edgeZone = Math.Min(12, Math.Max(baseWidth / 3, 6));
        var local = pressX - frameMinX;
        if (local > baseWidth - edgeZone) return Mode.Stretch;
        if (local < edgeZone && baseWidth >= MinimumTrimWidth) return Mode.Trim;
        return Mode.Move;
    }

    public static (double X, double Width) LiveSpan(double x, double width, Mode? mode, double delta) => mode switch
    {
        Mode.Move => (x + delta, Math.Max(width, MinimumWidth)),
        Mode.Stretch => (x, Math.Max(width + delta, MinimumWidth)),
        Mode.Trim => (x + delta, Math.Max(width - delta, MinimumWidth)),
        _ => (x, Math.Max(width, MinimumWidth)),
    };

    public const double SnapDistance = 6;

    /// <summary>The drag adjusted so the edge nearest a target (within <see cref="SnapDistance"/>) lands on it.</summary>
    public static double SnappedDelta(double delta, IEnumerable<double> edges, IReadOnlyList<double> targets)
    {
        double? pull = null;
        foreach (var edge in edges)
        {
            foreach (var target in targets)
            {
                var gap = target - (edge + delta);
                if (Math.Abs(gap) <= SnapDistance && Math.Abs(gap) < Math.Abs(pull ?? double.PositiveInfinity)) pull = gap;
            }
        }
        return delta + (pull ?? 0);
    }

    /// <summary>A duration on the twentieth-of-a-second grid, at least <see cref="MinimumDuration"/>.</summary>
    public static double Snapped(double seconds) => Math.Max(SwiftMath.Rounded(seconds * 20) / 20, MinimumDuration);

    public static double LiveDuration(double duration, Mode? mode, double delta, double pointsPerSecond)
    {
        var seconds = delta / pointsPerSecond;
        return mode switch
        {
            Mode.Stretch => Snapped(duration + seconds),
            Mode.Trim => Snapped(duration - seconds),
            _ => duration,
        };
    }

    /// <summary>"0.4s", or "0.45s" when a second decimal is needed.</summary>
    public static string DurationLabel(double seconds)
    {
        var tenths = SwiftMath.Rounded(seconds * 10) / 10;
        return Math.Abs(tenths - seconds) < 0.001
            ? seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s"
            : seconds.ToString("0.00", CultureInfo.InvariantCulture) + "s";
    }
}

/// <summary>Keeping the playhead in view while playing: scroll only when it leaves the view.</summary>
public static class TimelineFollow
{
    /// <summary>After a scroll the playhead sits this fraction in from the left.</summary>
    public const double Lead = 0.15;

    /// <summary>Points from the right edge that count as leaving the view.</summary>
    public const double Edge = 16.0;

    /// <summary>The new scroll offset, or null to stay put.</summary>
    public static double? Scroll(double playheadX, ClosedRange shown, double contentWidth)
    {
        if (!(playheadX < shown.Lower || playheadX > shown.Upper - Edge)) return null;
        var width = shown.Upper - shown.Lower;
        return Math.Min(Math.Max(playheadX - width * Lead, 0), Math.Max(contentWidth - width, 0));
    }
}
