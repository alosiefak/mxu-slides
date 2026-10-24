using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Media;

/// <summary>
/// How a video plays: from its in point to its out point (seconds into the file), at a speed.
/// ProPresenter imports set these (a trimmed walk-in video, a slowed-down loop), and the Mac
/// honours them; a looping video loops inside the trim. Port of MediaEngine's PlaybackOptions.
/// </summary>
public sealed record PlaybackOptions(double? InPoint = null, double? OutPoint = null, double? Rate = null)
{
    /// <summary>The slowest and fastest a video plays.</summary>
    public const double MinimumRate = 0.25;

    public const double MaximumRate = 4;

    /// <summary>A media item's trim and speed, or null when it plays whole at normal speed.</summary>
    public static PlaybackOptions? Of(MediaItem item) =>
        item.InPoint is null && item.OutPoint is null && item.PlayRate is null ? null : new PlaybackOptions(item.InPoint, item.OutPoint, item.PlayRate);

    /// <summary>
    /// The window that plays and the speed, for a file <paramref name="duration"/> seconds long: the
    /// points clamped into the file, the speed into 0.25–4. A window shorter than a frame (or the
    /// points the wrong way round) plays the whole file instead, as on the Mac.
    /// </summary>
    public (double Start, double End, double Rate) Resolved(double duration, double frameDuration)
    {
        var rate = Math.Clamp(Rate ?? 1, MinimumRate, MaximumRate);
        var start = Math.Min(Math.Max(InPoint ?? 0, 0), Math.Max(0, duration));
        var end = Math.Min(Math.Max(OutPoint ?? duration, 0), duration);
        if (end - start < Math.Max(frameDuration, 0.05))
        {
            start = 0;
            end = duration;
        }
        return (start, end, rate);
    }

    /// <summary>How long one pass takes on the clock (the window divided by the speed).</summary>
    public double EffectiveWallClockDuration(double duration, double frameDuration)
    {
        var (start, end, rate) = Resolved(duration, frameDuration);
        return rate > 0 ? (end - start) / rate : end - start;
    }

    /// <summary>
    /// True once playback at <paramref name="position"/> has reached the window's end: the pass is
    /// over (a looping video goes back to the start; any other stops and says it ended). Half a frame
    /// early counts, so the last frame isn't shown twice.
    /// </summary>
    public static bool PassIsOver(double position, double end, double frameDuration) => position >= end - frameDuration / 2;
}
