using System.Numerics;
using System.Runtime.CompilerServices;
using MxU.Slides.Core.Motion.Styles;

namespace MxU.Slides.Core.Motion;

/// <summary>Every motion background style, in the order the gallery shows them.</summary>
public static class MotionStyles
{
    public static readonly IReadOnlyList<MotionStyle> All =
    [
        new GradientFlowStyle(),
        new AuroraStyle(),
        new LightRaysStyle(),
        new BokehStyle(),
        new DustStyle(),
        new SmokeStyle(),
        new WavesStyle(),
        new GeometricStyle(),
        new StarfieldStyle(),
        new FilmWashStyle(),
        new LiquidStyle(),
    ];

    /// <summary>The style with this id, or null (a recipe from a newer version, say).</summary>
    public static MotionStyle? Find(string? id) => All.FirstOrDefault(s => s.Id == id);
}

/// <summary>Small bits of maths the styles share.</summary>
internal static class StyleMath
{
    public const float Tau = 2 * MathF.PI;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Saturate(float v) => Math.Clamp(v, 0, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Smooth(float edge0, float edge1, float x) => MotionColor.Smoothstep(edge0, edge1, x);

    /// <summary>A soft round bump: 1 at distance 0, falling off over about <paramref name="width"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Bump(float dx, float dy, float width) => MathF.Exp(-(dx * dx + dy * dy) / (width * width));

    /// <summary>
    /// A point going round a closed path (a Lissajous figure) that's back where it started at the
    /// end of the loop: <paramref name="turnsX"/> and <paramref name="turnsY"/> whole swings per loop,
    /// <paramref name="reach"/> picture units from <paramref name="centre"/>.
    /// </summary>
    public static Vector2 Orbit(in LoopClock clock, Vector2 centre, Vector2 reach, int turnsX, int turnsY, float offsetX, float offsetY) =>
        centre + reach * new Vector2(clock.Wave(turnsX, offsetX), clock.Wave(turnsY, offsetY + 0.25f));

    /// <summary>
    /// How far a point on an <see cref="Orbit"/> should swing so it moves at about
    /// <paramref name="perSecond"/> picture units a second: its path is about 2π·reach·turns long per loop.
    /// </summary>
    public static float Reach(MotionScene scene, float perSecond, int turns) => scene.Speed * perSecond * scene.Seconds / (Tau * turns);
}
