using System.Globalization;

namespace MxU.Slides.Core.Media;

/// <summary>
/// The media editor's trim rules: where the trim window sits on the scrub bar, turning a drag of
/// a trim handle or a typed time into in and out points, and the times as text ("1:05.5"). A point
/// within <see cref="EdgeSlack"/> of the start or end of the file is no trim at all, so it's stored
/// as absent. Line-for-line port of the Mac's SlideScene/MediaTrimEdit.swift; the play-rate choices
/// and the "Plays" length come from Sources/MediaEditorView.swift.
/// </summary>
public static class MediaTrimEdit
{
    /// <summary>Seconds from the start or end within which a point counts as that end.</summary>
    public const double EdgeSlack = 0.05;

    /// <summary>The trim window in seconds, or null when the item plays whole (or the points are inverted).</summary>
    public static (double Start, double End)? Window(double? inPoint, double? outPoint, double duration)
    {
        var start = Math.Max(inPoint ?? 0, 0);
        var end = Math.Min(outPoint ?? duration, duration);
        return (start > EdgeSlack || end < duration - EdgeSlack) && end > start ? (start, end) : null;
    }

    /// <summary>The window as fractions of the scrub bar (where to draw the handles), or null.</summary>
    public static (double Start, double End)? Fractions(double? inPoint, double? outPoint, double duration) =>
        duration > 0 && Window(inPoint, outPoint, duration) is { } window ? (window.Start / duration, window.End / duration) : null;

    /// <summary>The points a drag of the handles to these fractions makes: tidied, and absent at the ends.</summary>
    public static (double? InPoint, double? OutPoint) Points((double Start, double End) fractions, double duration)
    {
        var start = fractions.Start * duration;
        var end = fractions.End * duration;
        return (start > EdgeSlack ? Tidy(start) : null, end < duration - EdgeSlack ? Tidy(end) : null);
    }

    /// <summary>A typed in point: clamped into the file; at the start it clears; past the out point the out goes too.</summary>
    public static (double? InPoint, double? OutPoint) TypedIn(double? seconds, double? outPoint, double duration)
    {
        var start = Math.Min(Math.Max(seconds ?? 0, 0), duration);
        var keepsOut = outPoint is not { } o || start < o;
        return (start > EdgeSlack ? Tidy(start) : null, keepsOut ? outPoint : null);
    }

    /// <summary>A typed out point, or null when it would come before the in point (refused).</summary>
    public static (double? InPoint, double? OutPoint)? TypedOut(double? seconds, double? inPoint, double duration)
    {
        var end = Math.Min(Math.Max(seconds ?? duration, 0), duration);
        return end > (inPoint ?? 0) + EdgeSlack ? (inPoint, end < duration - EdgeSlack ? Tidy(end) : null) : null;
    }

    /// <summary>"1:05.5", "65.5" or "1:02:03" as seconds; null for anything else (a negative, "1:", words).</summary>
    public static double? Seconds(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return null;
        var total = 0.0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && !double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)) return null;
            if (value < 0 || !double.IsFinite(value)) return null;
            total = total * 60 + value;
        }
        return total;
    }

    /// <summary>Minutes and seconds to a tenth: "1:05.5", "0:15.4".</summary>
    public static string Timecode(double seconds)
    {
        var total = Math.Max(0, seconds);
        var minutes = (int)(total / 60);
        return $"{minutes}:{(total - minutes * 60).ToString("00.0", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Hundredths of a second: what's stored.</summary>
    public static double Tidy(double seconds) => Math.Round(seconds * 100, MidpointRounding.AwayFromZero) / 100;

    // MARK: Play rate (MediaEditorView.swift)

    /// <summary>The speeds the Play Rate menu offers.</summary>
    public static readonly IReadOnlyList<double> RateChoices = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

    /// <summary>"Normal" for 1, else "0.5×".</summary>
    public static string RateLabel(double rate) =>
        Math.Abs(rate - 1) < 1e-9 ? "Normal" : rate.ToString("0.##", CultureInfo.InvariantCulture) + "×";

    /// <summary>The rate as stored: absent for normal speed.</summary>
    public static double? StoredRate(double rate) => Math.Abs(rate - 1) < 1e-9 ? null : rate;

    /// <summary>How long a trimmed item plays at its rate, in seconds ("Plays 0:25"), or null when it isn't trimmed or its length isn't known.</summary>
    public static double? PlaysFor(double? inPoint, double? outPoint, double duration, double? rate)
    {
        if (inPoint is null && outPoint is null || duration <= 0) return null;
        var length = Math.Min(outPoint ?? duration, duration) - Math.Max(inPoint ?? 0, 0);
        return Math.Max(0, length) / Math.Max(rate ?? 1, 0.01);
    }

    /// <summary>Whole minutes and seconds ("1:05"), as the editor's File section shows a length.</summary>
    public static string Length(double seconds)
    {
        var total = (int)Math.Round(seconds, MidpointRounding.AwayFromZero);
        return $"{total / 60}:{total % 60:00}";
    }
}
