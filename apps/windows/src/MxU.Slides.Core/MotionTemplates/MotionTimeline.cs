using System.Text.Json.Serialization;

namespace MxU.Slides.Core.MotionTemplates;

/// <summary>What a template does once it has animated in.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MotionPlayback>))]
public enum MotionPlayback
{
    /// <summary>Keeps going until it's taken down: the Hold part loops (the whole animation, if it has no markers).</summary>
    Loop,

    /// <summary>Plays to the end of its Hold (or of the animation) and stays on that frame until it's taken down.</summary>
    HoldLast,

    /// <summary>Plays through once (In, Hold, Out) and then takes itself down.</summary>
    Once,
}

/// <summary>
/// The parts of a template's animation, in frames of the animation's own frame rate: it animates
/// in over [<see cref="InStart"/>, <see cref="InEnd"/>), holds over [<see cref="HoldStart"/>,
/// <see cref="HoldEnd"/>), and animates out over [<see cref="OutStart"/>, <see cref="End"/>).
/// </summary>
public sealed record MotionSegments(double InStart, double InEnd, double HoldStart, double HoldEnd, double OutStart, double End, bool HasMarkers, bool HasOut)
{
    /// <summary>The hold has movement in it (more than a frame), so looping it means something.</summary>
    public bool HoldMoves => HoldEnd - HoldStart > 1;
}

/// <summary>
/// Which frame of a template to show at each moment, from when it was put up and when it was
/// taken down. A pure function of those two times, like the slide animation engine
/// (<see cref="Animation.AnimationEvaluator"/>), so every output shows the same frame, a busy PC
/// just shows fewer of them, and it can be tested without a clock.
/// <para>
/// <b>Markers.</b> After Effects templates mark their parts with composition markers named
/// <c>In</c>, <c>Hold</c> and <c>Out</c> (Bodymovin exports them; LottieFiles' tools use names like
/// "intro", "loop", "outro", which work too). Then a lower third animates in when it's put up,
/// holds (looping the Hold part if it moves), and when it's taken down jumps to its Out and
/// plays it before it goes. Without markers the animation is one piece: it loops, holds its last
/// frame, or plays once, and taking it down fades it (<see cref="FadeSeconds"/>).
/// </para>
/// Windows only: the Mac has no Lottie support.
/// </summary>
public sealed record MotionTimeline(MotionSegments Segments, double FrameRate, MotionPlayback Playback)
{
    /// <summary>How long a template without an Out takes to fade when it's taken down.</summary>
    public const double FadeSeconds = 0.3;

    private static readonly string[] InNames = ["in", "intro", "start", "build in", "animate in", "anim in", "enter"];
    private static readonly string[] HoldNames = ["hold", "loop", "idle", "stay", "static", "middle", "main"];
    private static readonly string[] OutNames = ["out", "outro", "end", "exit", "build out", "animate out", "anim out", "leave"];

    public static MotionTimeline For(LottieDocument document, MotionPlayback playback) =>
        new(SegmentsOf(document), document.FrameRate, playback);

    /// <summary>The In, Hold and Out parts of an animation, from its markers (see the class summary).</summary>
    public static MotionSegments SegmentsOf(LottieDocument document)
    {
        var first = document.InPoint;
        var end = document.OutPoint;
        LottieMarker? Find(string[] names) => document.Markers.FirstOrDefault(m => names.Contains(m.Name.Trim().ToLowerInvariant()));
        var inMarker = Find(InNames);
        var holdMarker = Find(HoldNames);
        var outMarker = Find(OutNames);
        if (inMarker is null && holdMarker is null && outMarker is null)
            return new MotionSegments(first, end, end, end, end, end, HasMarkers: false, HasOut: false);

        double Clamp(double frame) => Math.Clamp(frame, first, end);
        // Where the next marked part starts: a marker with no length runs up to it.
        double NextStart(double after) => new[] { holdMarker?.Frame, outMarker?.Frame, end }
            .Where(f => f is not null && f > after).Select(f => f!.Value).DefaultIfEmpty(end).Min();

        var outStart = outMarker is null ? end : Clamp(outMarker.Frame);
        var outEnd = outMarker is null ? end : Clamp(outMarker.DurationFrames > 0 ? outMarker.Frame + outMarker.DurationFrames : end);

        double inStart, inEnd;
        if (inMarker is not null)
        {
            inStart = Clamp(inMarker.Frame);
            inEnd = Clamp(inMarker.DurationFrames > 0 ? inMarker.Frame + inMarker.DurationFrames : NextStart(inMarker.Frame));
        }
        else
        {
            inStart = first;
            inEnd = Clamp(holdMarker?.Frame ?? outStart);
        }

        double holdStart, holdEnd;
        if (holdMarker is not null)
        {
            holdStart = Clamp(holdMarker.Frame);
            holdEnd = Clamp(holdMarker.DurationFrames > 0 ? holdMarker.Frame + holdMarker.DurationFrames : holdStart);
        }
        else
        {
            // No Hold marker: it holds on the moment the In finishes (the settled design).
            holdStart = holdEnd = inEnd;
        }
        return new MotionSegments(inStart, inEnd, holdStart, Math.Max(holdStart, holdEnd), outStart, Math.Max(outStart, outEnd),
            HasMarkers: true, HasOut: outMarker is not null && outEnd > outStart);
    }

    /// <summary>How long taking it down takes: its Out, or the fade when it has none.</summary>
    public double TakeDownSeconds => Segments.HasOut ? (Segments.End - Segments.OutStart) / FrameRate : FadeSeconds;

    /// <summary>How long its In takes (for the still the Mac shows instead: it fades in over the same time).</summary>
    public double InSeconds => (Segments.InEnd - Segments.InStart) / FrameRate;

    /// <summary>For <see cref="MotionPlayback.Once"/>: seconds from putting it up until it has finished by itself. Null for the others.</summary>
    public double? OnceSeconds => Playback != MotionPlayback.Once ? null : Segments.HasMarkers
        ? (Segments.InEnd - Segments.InStart + Math.Max(0, Segments.HoldEnd - Segments.HoldStart) + (Segments.HasOut ? Segments.End - Segments.OutStart : 0)) / FrameRate
        : (Segments.End - Segments.InStart) / FrameRate;

    /// <summary>
    /// The frame that stands for the whole template in a still picture: the design once it has
    /// settled (the start of its Hold), or for one without markers its last frame (when it holds
    /// that) or its middle (when it loops or plays once: most lower thirds are fully in by then).
    /// </summary>
    public double SettledFrame => Segments.HasMarkers
        ? Math.Min(Segments.HoldStart, LastFrameBefore(Segments.End))
        : Playback == MotionPlayback.HoldLast ? LastFrameBefore(Segments.End) : Math.Floor((Segments.InStart + Segments.End) / 2);

    /// <summary>
    /// The frame to show <paramref name="secondsUp"/> after it was put up. <paramref name="secondsDown"/>
    /// is the time since it was taken down (null while it's still up). <c>Finished</c> is true once
    /// it has nothing more to show: its Out (or fade) is over, or a Once has played through.
    /// </summary>
    public (double Frame, bool Finished) FrameAt(double secondsUp, double? secondsDown = null)
    {
        var s = Segments;
        if (secondsDown is { } down && s.HasOut)
        {
            // Taken down: straight to the Out, from wherever it was (a quick take-down cuts the In short).
            var frame = s.OutStart + Math.Max(0, down) * FrameRate;
            return frame >= s.End ? (LastFrameBefore(s.End), true) : (frame, false);
        }
        var up = Up(Math.Max(0, secondsUp));
        // Without an Out, taking it down fades what was showing; it's gone once the fade is over.
        if (secondsDown is { } faded) return (up.Frame, up.Finished || faded >= FadeSeconds);
        return up;
    }

    /// <summary>Where it is while it's up (not taken down).</summary>
    private (double Frame, bool Finished) Up(double seconds)
    {
        var s = Segments;
        var frame = s.InStart + seconds * FrameRate;
        if (!s.HasMarkers)
        {
            var length = s.End - s.InStart;
            return Playback switch
            {
                MotionPlayback.Loop => (s.InStart + Wrap(frame - s.InStart, length), false),
                MotionPlayback.HoldLast => (Math.Min(frame, LastFrameBefore(s.End)), false),
                _ => frame >= s.End ? (LastFrameBefore(s.End), true) : (frame, false),
            };
        }

        if (frame < s.InEnd) return (frame, false);
        var intoHold = frame - s.InEnd;
        var holdLength = s.HoldEnd - s.HoldStart;
        switch (Playback)
        {
            case MotionPlayback.Loop:
                return (s.HoldMoves ? s.HoldStart + Wrap(intoHold, holdLength) : s.HoldStart, false);
            case MotionPlayback.HoldLast:
                return (s.HoldMoves ? Math.Min(s.HoldStart + intoHold, LastFrameBefore(s.HoldEnd)) : s.HoldStart, false);
            default:
                // Once: through the hold a single time, then the Out (if any), then done.
                if (intoHold < holdLength) return (s.HoldStart + intoHold, false);
                if (!s.HasOut) return (s.HoldMoves ? LastFrameBefore(s.HoldEnd) : s.HoldStart, true);
                var intoOut = s.OutStart + (intoHold - holdLength);
                return intoOut >= s.End ? (LastFrameBefore(s.End), true) : (intoOut, false);
        }
    }

    /// <summary>
    /// The frames of a play-through for a video file: In, the Hold for <paramref name="holdSeconds"/>
    /// (looping if it moves), then the Out, at <paramref name="frameRate"/> frames a second. Without
    /// markers it's the whole animation, <paramref name="passes"/> times.
    /// </summary>
    public IReadOnlyList<double> PlayThrough(double holdSeconds, int frameRate, int passes = 1)
    {
        var s = Segments;
        var frames = new List<double>();
        var step = FrameRate / frameRate;
        if (!s.HasMarkers)
        {
            var count = (int)Math.Round((s.End - s.InStart) / step);
            for (var pass = 0; pass < Math.Max(1, passes); pass++)
                for (var i = 0; i < count; i++) frames.Add(s.InStart + i * step);
            return frames;
        }
        for (var f = s.InStart; f < s.InEnd - 1e-9; f += step) frames.Add(f);
        var holdFrames = (int)Math.Round(Math.Max(0, holdSeconds) * frameRate);
        for (var i = 0; i < holdFrames; i++)
            frames.Add(s.HoldMoves ? s.HoldStart + Wrap(i * step, s.HoldEnd - s.HoldStart) : s.HoldStart);
        if (s.HasOut)
            for (var f = s.OutStart; f < s.End - 1e-9; f += step) frames.Add(f);
        return frames;
    }

    /// <summary>The last whole frame before <paramref name="end"/> (Lottie shows frames up to, not including, its out point).</summary>
    private double LastFrameBefore(double end) => Math.Max(Segments.InStart, Math.Ceiling(end) - 1);

    private static double Wrap(double value, double length) => length <= 0 ? 0 : ((value % length) + length) % length;
}
