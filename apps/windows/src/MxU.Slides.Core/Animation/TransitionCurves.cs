namespace MxU.Slides.Core.Animation;

/// <summary>
/// Easing: how far through a transition looks at each moment. The engine's clock runs straight
/// (half the time gone = 0.5); a good transition doesn't. A push that moved at one speed would
/// start with a jolt and stop dead, so it starts gently, is quickest in the middle and settles.
/// Each kind gets the curve that suits it (<see cref="Eased"/>). Windows only.
/// </summary>
public static class TransitionCurves
{
    public static double Clamp01(double t) => Math.Clamp(double.IsNaN(t) ? 0 : t, 0, 1);

    /// <summary>0 before <paramref name="from"/>, 1 after <paramref name="to"/>, an S-curve between (HLSL's smoothstep).</summary>
    public static double Smoothstep(double from, double to, double t)
    {
        var x = Clamp01((t - from) / (to - from));
        return x * x * (3 - 2 * x);
    }

    public static double Lerp(double from, double to, double t) => from + (to - from) * t;

    /// <summary>The gentlest S-curve: half a cosine wave.</summary>
    public static double InOutSine(double t) => (1 - Math.Cos(Math.PI * Clamp01(t))) / 2;

    /// <summary>A firmer S-curve: slow out, quick through the middle, slow in (pushes, slides, the cube).</summary>
    public static double InOutCubic(double t)
    {
        var x = Clamp01(t);
        return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
    }

    /// <summary>Firmer again: most of the movement happens around the middle (a zoom through).</summary>
    public static double InOutQuart(double t)
    {
        var x = Clamp01(t);
        return x < 0.5 ? 8 * x * x * x * x : 1 - Math.Pow(-2 * x + 2, 4) / 2;
    }

    /// <summary>Almost still at the ends and very fast in the middle: a whip pan.</summary>
    public static double InOutQuint(double t)
    {
        var x = Clamp01(t);
        return x < 0.5 ? 16 * Math.Pow(x, 5) : 1 - Math.Pow(-2 * x + 2, 5) / 2;
    }

    /// <summary>0 at both ends and 1 in the middle: how strong a blur, glow or glitch is.</summary>
    public static double Bell(double t) => Math.Sin(Math.PI * Clamp01(t));

    /// <summary>A kind's eased progress at <paramref name="t"/>, the share of its time gone (0–1).</summary>
    public static double Eased(WindowsTransitionKind kind, double t) => kind switch
    {
        WindowsTransitionKind.Push or WindowsTransitionKind.SlideOver or WindowsTransitionKind.SlideReveal
            or WindowsTransitionKind.ZoomIn or WindowsTransitionKind.ZoomOut
            or WindowsTransitionKind.Cube or WindowsTransitionKind.Flip => InOutCubic(t),
        WindowsTransitionKind.WhipPan => InOutQuint(t),
        WindowsTransitionKind.ZoomThrough => InOutQuart(t),
        WindowsTransitionKind.Iris or WindowsTransitionKind.Diamond or WindowsTransitionKind.Clock
            or WindowsTransitionKind.SoftWipe or WindowsTransitionKind.LumaWipe or WindowsTransitionKind.Ripple => InOutSine(t),
        // Cross Zoom, Light Leak and Glitch shape their own strength with a bell; the Mac's kinds run straight, as on the Mac.
        _ => Clamp01(t),
    };

    /// <summary>
    /// How fast the eased progress changes at <paramref name="t"/> (1 = the straight clock's speed):
    /// a whip pan blurs by how fast it's moving.
    /// </summary>
    public static double Speed(WindowsTransitionKind kind, double t)
    {
        const double step = 1e-3;
        var before = Clamp01(t - step);
        var after = Clamp01(t + step);
        return after <= before ? 0 : (Eased(kind, after) - Eased(kind, before)) / (after - before);
    }
}
