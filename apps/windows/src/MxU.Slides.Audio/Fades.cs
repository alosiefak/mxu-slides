using MxU.Slides.Core.Automation;

namespace MxU.Slides.Audio;

/// <summary>
/// The playlist crossfade shape (the "radio segue", ADR-007): the outgoing song recedes
/// early (-16 dB across the fade, then released to silence over the last 15%) while the
/// incoming one rises on a smoothstep, so the two never sum louder than one song.
/// </summary>
internal static class CrossfadeCurve
{
    public static (float Incoming, float Outgoing) Gains(double progress)
    {
        var clamped = Math.Clamp(progress, 0, 1);
        var smooth = clamped * clamped * (3 - 2 * clamped);
        var outgoing = Math.Pow(10, -16.0 * clamped / 20);
        const double releaseStart = 0.85;
        if (clamped > releaseStart) outgoing *= 1 - (clamped - releaseStart) / (1 - releaseStart);
        return ((float)smooth, (float)outgoing);
    }
}

/// <summary>How a transport fade falls, for Core's <see cref="AudioFadeCurve"/> (the Mac's AudioFadeCurve.falling).</summary>
public static class AudioFadeCurves
{
    /// <summary>The level (1 → 0) a falling fade has reached at <paramref name="progress"/> (0…1).</summary>
    public static float Falling(this AudioFadeCurve curve, double progress) => curve switch
    {
        AudioFadeCurve.EqualPower => (float)Math.Cos(progress * Math.PI / 2),
        AudioFadeCurve.Cosine => (float)(0.5 * (1 + Math.Cos(progress * Math.PI))),
        _ => (float)(1 - progress),
    };
}
