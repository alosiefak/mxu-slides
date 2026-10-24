using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Audio.Timeline;

/// <summary>
/// Drawing and grabbing a music clip's fades in the timeline editor: the curve (on the same
/// maths the audio engine plays, so what you see is what you hear), the square handle that
/// sets a fade's length, and the round one that bends its curve. Port of ClipFadesOverlay in
/// Sources/TimelineClipEditing.swift. Levels run 0 (silent, bottom) to 1 (full, top).
/// </summary>
public static class ClipFadeGeometry
{
    /// <summary>A fade handle: which clip and fade, whether it bends the curve, and where it sits.</summary>
    public sealed record Handle(string CueId, bool Out, bool Midpoint, double Time, double Level, ClipFade Fade, double End);

    /// <summary>Fade curves keep 3 points clear of the row's top and bottom.</summary>
    public const double Inset = 3;

    /// <summary>A handle is 7 points across; a press within this many points of its centre grabs it.</summary>
    public const double Reach = 7.5;

    /// <summary>The y of a level on a row (top = full).</summary>
    public static double Y(double level, double rowTop, double rowHeight) =>
        rowTop + Inset + Math.Max(rowHeight - Inset * 2, 1) * (1 - level);

    private static ClipEnvelope.Fade Engine(ClipFade fade, double seconds) =>
        new(seconds, TimelineClips.ShapeNamed(fade.Shape), fade.Midpoint ?? 0.5);

    /// <summary>When a fade starts: a fade in at the clip's start, a fade out its length before the clip's end.</summary>
    public static double Start(TimelineCue clip, ClipFade fade, bool @out, double end) => @out ? end - fade.Seconds : clip.At;

    /// <summary>The fade's curve as (time, level) points, 33 of them, from its start to its end.</summary>
    public static List<(double Time, double Level)> Curve(ClipFade fade, bool @out, double start, int steps = 32)
    {
        var engine = Engine(fade, fade.Seconds);
        var points = new List<(double, double)>(steps + 1);
        for (var step = 0; step <= steps; step++)
        {
            var f = step / (double)steps;
            var level = @out ? engine.Level(1 - f, @out: true) : engine.Level(f);
            points.Add((start + fade.Seconds * f, level));
        }
        return points;
    }

    /// <summary>
    /// A clip's fade handles: for each fade, the length handle at its inner end (along the top)
    /// and the midpoint handle on the curve. <paramref name="live"/> gives a fade being dragged.
    /// A clip that runs on with no end has none.
    /// </summary>
    public static List<Handle> Handles(TimelineCue clip, IReadOnlyList<TimelineCue> cues, Func<string, bool, ClipFade?>? live = null)
    {
        var handles = new List<Handle>();
        var end = PresentationTimelines.End(clip, cues);
        if (!double.IsFinite(end)) return handles;
        foreach (var @out in new[] { false, true })
        {
            var fade = live?.Invoke(clip.Id, @out) ?? (@out ? clip.FadeOut : clip.FadeIn);
            if (fade is null || fade.Seconds <= 0) continue;
            var start = Start(clip, fade, @out, end);
            var edge = @out ? start : start + fade.Seconds;
            var mid = fade.Midpoint ?? 0.5;
            var engine = Engine(fade, 1);
            var level = @out ? engine.Level(1 - mid, @out: true) : engine.Level(mid);
            handles.Add(new Handle(clip.Id, @out, false, edge, 1, fade, end));
            handles.Add(new Handle(clip.Id, @out, true, start + fade.Seconds * mid, level, fade, end));
        }
        return handles;
    }

    /// <summary>Where a handle is drawn on its row: the length handle 4 points down, the midpoint handle on the curve.</summary>
    public static double HandleY(Handle handle, double rowTop, double rowHeight) =>
        handle.Midpoint ? rowTop + 3 + (rowHeight - 6) * (1 - handle.Level) : rowTop + 4;

    /// <summary>The handle under a point, if any (the nearest one when two are close).</summary>
    public static Handle? HandleAt(
        IEnumerable<Handle> handles, double x, double y, Func<double, double> xOfTime, double rowTop, double rowHeight)
    {
        Handle? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var handle in handles)
        {
            var dx = Math.Abs(xOfTime(handle.Time) - x);
            var dy = Math.Abs(HandleY(handle, rowTop, rowHeight) - y);
            if (dx > Reach || dy > Reach) continue;
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                best = handle;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>The fade as a drag of <paramref name="seconds"/> leaves it.</summary>
    public static ClipFade Dragged(Handle handle, double seconds, TimelineCue clip) =>
        TimelineClips.Dragged(handle.Fade, handle.Midpoint, handle.Out, seconds, clip, handle.End);

    /// <summary>The small picture of a fade shape in the menus: points across a 22 × 14 box.</summary>
    public static List<(double X, double Y)> ShapePicture(TimelineClips.Shape shape, bool @out, double width = 22, double height = 14)
    {
        var fade = new ClipEnvelope.Fade(1, shape, 0.5);
        var points = new List<(double, double)>();
        for (var step = 0; step <= 24; step++)
        {
            var x = step / 24.0;
            var level = @out ? fade.Level(1 - x, @out: true) : fade.Level(x);
            points.Add((1 + (width - 2) * x, height - 1 - (height - 2) * level));
        }
        return points;
    }
}
