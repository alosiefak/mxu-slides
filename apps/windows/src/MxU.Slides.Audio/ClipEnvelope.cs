using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Audio;

/// <summary>
/// The maths behind a clip's envelope: its fade in, fade out and the 5 ms "edge" ramps
/// that keep a hard cut from clicking, applied to the samples as they are read so a stem
/// or music clip is shaped exactly where it plays. The envelope itself is Core's
/// <see cref="ClipEnvelope"/> record (the timeline builds it); these extensions are the
/// audio half of the Mac's ClipEnvelope.swift.
/// </summary>
public static class ClipEnvelopes
{
    public const double EdgeSeconds = TimelineClips.EdgeSeconds;

    /// <summary>An envelope with the Mac's defaults: no fades, 5 ms edges on both ends.</summary>
    public static ClipEnvelope Make(ClipEnvelope.Fade? fadeIn = null, ClipEnvelope.Fade? fadeOut = null,
        double edge = EdgeSeconds, bool edgeIn = true, bool edgeOut = true) => new(fadeIn, fadeOut, edge, edgeIn, edgeOut);

    /// <summary>A fade with the Mac's defaults (linear, halfway level at the middle).</summary>
    public static ClipEnvelope.Fade Fade(double seconds, TimelineClips.Shape shape = TimelineClips.Shape.Linear, double midpoint = 0.5) =>
        new(seconds, shape, midpoint);

    /// <summary>A schema fade (a timeline cue's fadeIn/fadeOut).</summary>
    public static ClipEnvelope.Fade? FadeFrom(ClipFade? fade) =>
        fade is null ? null : new ClipEnvelope.Fade(fade.Seconds, TimelineClips.ShapeNamed(fade.Shape), fade.Midpoint ?? 0.5);

    /// <summary>
    /// The envelope for a timeline cue's fades. Which edges get the 5 ms ramp depends on the
    /// clips around it (Core's TimelineClips decides), so the caller says.
    /// </summary>
    public static ClipEnvelope FromCue(TimelineCue cue, bool edgeIn, bool edgeOut) =>
        new(FadeFrom(cue.FadeIn), FadeFrom(cue.FadeOut), EdgeSeconds, edgeIn, edgeOut);

    /// <summary>How far (0…1) a rising fade of this shape has come at <paramref name="x"/> (0…1).</summary>
    public static double Rising(this TimelineClips.Shape shape, double x, bool @out = false)
    {
        x = Math.Clamp(x, 0, 1);
        return shape switch
        {
            TimelineClips.Shape.SCurve => 0.5 * (1 - Math.Cos(Math.PI * x)),
            TimelineClips.Shape.EqualPower => Math.Sin(Math.PI / 2 * x),
            // "Fast" gets most of the change done early, "slow" late; a fade out mirrors that.
            TimelineClips.Shape.Fast => @out ? x * x : 1 - (1 - x) * (1 - x),
            TimelineClips.Shape.Slow => @out ? 1 - (1 - x) * (1 - x) : x * x,
            _ => x,
        };
    }

    /// <summary>A fade's level at <paramref name="x"/> (0…1 through the fade; for a fade out, 1 is its start).</summary>
    public static double Level(this ClipEnvelope.Fade fade, double x, bool @out = false)
    {
        var m = Math.Clamp(@out ? 1 - fade.Midpoint : fade.Midpoint, 0.05, 0.95);
        // Bending x by a power moves the halfway point to m without changing the ends.
        var warped = m == 0.5 ? x : Math.Pow(Math.Clamp(x, 0, 1), Math.Log(0.5) / Math.Log(m));
        return fade.Shape.Rising(warped, @out);
    }

    /// <summary>The gain at <paramref name="t"/> seconds into a clip <paramref name="length"/> seconds long.</summary>
    public static float Gain(this ClipEnvelope envelope, double t, double length)
    {
        var level = 1.0;
        if (envelope.FadeIn is { Seconds: > 0 } fadeIn && t < fadeIn.Seconds) level *= fadeIn.Level(t / fadeIn.Seconds);
        var left = length - t;
        if (envelope.FadeOut is { Seconds: > 0 } fadeOut && left < fadeOut.Seconds) level *= fadeOut.Level(left / fadeOut.Seconds, @out: true);
        if (envelope.Edge > 0)
        {
            if (envelope.EdgeIn && t < envelope.Edge) level *= Math.Max(t, 0) / envelope.Edge;
            if (envelope.EdgeOut && left < envelope.Edge) level *= Math.Max(left, 0) / envelope.Edge;
        }
        return (float)level;
    }

    /// <summary>True when nothing shapes <paramref name="t"/>…<paramref name="end"/>, so a read can skip the maths.</summary>
    internal static bool Flat(this ClipEnvelope envelope, double t, double end, double length)
    {
        var inEnd = Math.Max(envelope.FadeIn?.Seconds ?? 0, envelope.EdgeIn ? envelope.Edge : 0);
        var outStart = length - Math.Max(envelope.FadeOut?.Seconds ?? 0, envelope.EdgeOut ? envelope.Edge : 0);
        return t >= inEnd && end <= outStart;
    }

    /// <summary>
    /// Shapes <paramref name="count"/> frames in place, starting at index <paramref name="offset"/> of
    /// each channel, the first of them <paramref name="t0"/> seconds into the clip.
    /// </summary>
    internal static void Apply(this ClipEnvelope envelope, float[][] channels, int offset, int count, double t0, double rate, double length)
    {
        if (count <= 0 || envelope.Flat(t0, t0 + count / rate, length)) return;
        for (var index = 0; index < count; index++)
        {
            var gain = envelope.Gain(t0 + index / rate, length);
            if (gain == 1) continue;
            foreach (var channel in channels) channel[offset + index] *= gain;
        }
    }
}

/// <summary>A music clip's envelope and the stretch of the file (seconds) it shapes.</summary>
public sealed record AudioClipShape(ClipEnvelope Envelope, double Start, double End);
