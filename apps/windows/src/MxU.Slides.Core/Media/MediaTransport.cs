using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Media;

/// <summary>
/// Where a playing video is, read once: its length and position at <see cref="CapturedAt"/>,
/// whether it plays, loops, and how fast. Every later reading is worked out from that, so the
/// transport strip, the countdowns and auto-advance never ask the player 30 times a second.
/// Times are in the pass the operator sees: a trimmed video's pass starts at its in point and
/// ends at its out point (see <see cref="Pass"/>). Port of MediaEngine/MediaTransport.swift.
/// </summary>
public sealed record MediaTransportState(double Duration, double Position, bool IsPlaying, bool IsLooping, DateTimeOffset CapturedAt, double Rate = 1)
{
    /// <summary>A playing reading this close to another is the same video, not news (player jitter).</summary>
    public const double PlayingTolerance = 0.3;

    /// <summary>A paused playhead is exact to about a frame: a paused scrub is news.</summary>
    public const double PausedTolerance = 1.0 / 120;

    /// <summary>The position at <paramref name="date"/>: frozen while paused, clamped to the pass, wrapped while looping.</summary>
    public double PositionAt(DateTimeOffset date)
    {
        var clamped = Math.Min(Math.Max(0, Position), Math.Max(0, Duration));
        if (!IsPlaying || Duration <= 0) return clamped;
        var projected = Position + (date - CapturedAt).TotalSeconds * Math.Max(Rate, 0);
        if (IsLooping)
        {
            var wrapped = projected % Duration;
            return wrapped < 0 ? wrapped + Duration : wrapped;
        }
        return Math.Min(Math.Max(0, projected), Duration);
    }

    public double RemainingAt(DateTimeOffset date) => Math.Max(0, Duration - PositionAt(date));

    /// <summary>The same video in the same state, give or take jitter: a refresh that tracks needn't be published.</summary>
    public bool Tracks(MediaTransportState other, DateTimeOffset date)
    {
        var tolerance = IsPlaying ? PlayingTolerance : PausedTolerance;
        var gap = Math.Abs(PositionAt(date) - other.PositionAt(date));
        var distance = IsLooping && Duration > 0 ? Math.Min(gap, Duration - gap) : gap;
        return Duration == other.Duration && IsPlaying == other.IsPlaying && IsLooping == other.IsLooping
            && Rate == other.Rate && distance < tolerance;
    }

    /// <summary>The pass played out: stopped at its end, not looping. Port of MediaTransportController.isFinished.</summary>
    public bool IsFinished(DateTimeOffset date) => !IsPlaying && !IsLooping && Duration > 0 && PositionAt(date) >= Duration - 0.05;

    /// <summary>
    /// A reading from a player that knows only the file (its position in the file and the file's
    /// length), turned into the pass: from the in point to the out point (Windows-only; the Mac's
    /// player reports the pass itself). Use <see cref="FileTime"/> to seek back into the file.
    /// </summary>
    public static MediaTransportState Pass(
        double filePosition, double fileDuration, double? inPoint, double? outPoint, bool isPlaying, bool isLooping,
        DateTimeOffset capturedAt, double? rate = null)
    {
        var start = Math.Clamp(inPoint ?? 0, 0, Math.Max(0, fileDuration));
        var end = Math.Clamp(outPoint is { } trimmed && trimmed > start ? trimmed : fileDuration, start, Math.Max(start, fileDuration));
        return new MediaTransportState(end - start, Math.Clamp(filePosition - start, 0, end - start), isPlaying, isLooping, capturedAt,
            rate is { } r && r > 0 ? r : 1);
    }

    /// <summary>A time in the pass as a time in the file (for seeking a player that knows only the file).</summary>
    public static double FileTime(double passSeconds, double? inPoint) => Math.Max(0, inPoint ?? 0) + Math.Max(0, passSeconds);
}

/// <summary>
/// Where a playing video came from. When two videos start together the higher one is the
/// one the operator means (a slide's own video over a cue that came with it). Port of
/// TransportOrigin in SlideScene/MediaTransportTarget.swift.
/// </summary>
public enum TransportOrigin
{
    BackgroundCue = 0,
    BackgroundServiceItem,
    ForegroundCue,
    ForegroundServiceItem,
    Slides,
    Overlays,
    Alerts,
}

/// <summary>A video the transport strip could control.</summary>
public sealed record TransportCandidate(string MediaId, LayerKind Layer, TransportOrigin Origin, bool IsLooping, DateTimeOffset FiredAt);

/// <summary>A video that is live, before its player has been read: the media id, its layer and where it came from.</summary>
public sealed record TransportSeed(string MediaId, LayerKind Layer, TransportOrigin Origin);

/// <summary>
/// Which video the transport strip controls. The operator looks at one layer at a time
/// (Foreground Videos unless they pick another, or pin one video). On that layer the video
/// that started first wins, so a later fire never steals the strip; videos started within a
/// second of each other break the tie by origin. Loops are only chosen when nothing else plays,
/// unless the operator asks for them. Port of TransportTarget in SlideScene/MediaTransportTarget.swift.
/// </summary>
public static class TransportTarget
{
    public const LayerKind DefaultLayer = LayerKind.Videos;

    /// <summary>Videos started this close together count as started together.</summary>
    public const double SimultaneityWindow = 1.0;

    public static TransportCandidate? Resolve(
        IReadOnlyList<TransportCandidate> candidates, LayerKind viewedLayer = DefaultLayer, string? pinnedId = null, bool preferNonLooping = true)
    {
        if (pinnedId is not null && candidates.FirstOrDefault(c => c.MediaId == pinnedId) is { } pinned) return pinned;
        var pool = candidates.Where(c => c.Layer == viewedLayer).ToList();
        if (preferNonLooping && pool.Any(c => !c.IsLooping)) pool.RemoveAll(c => c.IsLooping);
        if (pool.Count == 0) return null;
        var earliest = pool.Min(c => c.FiredAt);
        TransportCandidate? best = null;
        foreach (var candidate in pool.Where(c => (c.FiredAt - earliest).TotalSeconds <= SimultaneityWindow))
            if (best is null || candidate.Origin > best.Origin) best = candidate;
        return best;
    }

    /// <summary>The video a layer's countdown reads (no pin; loops only when nothing else plays there).</summary>
    public static TransportCandidate? CountdownTarget(IReadOnlyList<TransportCandidate> candidates, LayerKind layer = DefaultLayer) =>
        Resolve(candidates, layer, null, preferNonLooping: true);
}
