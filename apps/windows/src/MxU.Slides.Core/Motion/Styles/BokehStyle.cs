using System.Numerics;
using static MxU.Slides.Core.Motion.StyleMath;

namespace MxU.Slides.Core.Motion.Styles;

/// <summary>
/// Out-of-focus lights ("bokeh"), as a camera lens draws points of light it isn't focused on:
/// soft-edged discs with a slightly brighter rim. Three depths: many small far ones, some
/// middle ones and a few huge, faint near ones, each drifting on its own slow closed path
/// (nearer ones further, which gives depth) and gently brightening and dimming. The discs
/// add their light together where they overlap, as light does.
/// </summary>
internal sealed class BokehStyle : MotionStyle
{
    public override string Id => "bokeh";
    public override string Name => "Bokeh";
    public override string Description => "Out-of-focus lights drifting and glowing, as if seen through a lens.";
    public override string DefaultPalette => "Ember";
    internal override float Grain => 0.3f;
    internal override float Vignette => 0.4f;

    // (count, smallest and largest radius, brightness, edge softness, rim, drift speed) per depth.
    private static readonly (int Count, float Min, float Max, float Light, float Soft, float Rim, float Drift)[] Depths =
    [
        (48, 0.012f, 0.026f, 0.55f, 0.3f, 0.12f, 0.006f),
        (24, 0.034f, 0.066f, 0.30f, 0.2f, 0.28f, 0.011f),
        (8, 0.10f, 0.17f, 0.11f, 0.32f, 0.18f, 0.018f),
    ];

    internal override MotionFrame Begin(MotionScene scene, LoopClock clock) => new Frame(scene, clock);

    private sealed class Frame : MotionFrame
    {
        private readonly Vector3 dark;
        private readonly Vector3 wash;
        private readonly Vector2 glowA;
        private readonly Vector2 glowB;

        public Frame(MotionScene scene, LoopClock clock)
        {
            var s = scene.Stops;
            dark = s[0];
            wash = s[1] * (0.25f + 0.35f * scene.Intensity);
            glowA = Orbit(clock, new Vector2(scene.Aspect * 0.3f, 0.35f), new Vector2(Reach(scene, 0.01f, 1)), 1, 1, 0.1f, 0.6f);
            glowB = Orbit(clock, new Vector2(scene.Aspect * 0.75f, 0.7f), new Vector2(Reach(scene, 0.01f, 1)), 1, 1, 0.5f, 0.2f);
            var strength = 0.35f + 1.0f * scene.Intensity;
            var index = 0;
            foreach (var depth in Depths)
            {
                for (var i = 0; i < depth.Count; i++, index++)
                {
                    var disc = index;
                    float r(int property) => scene.Random(disc, property);
                    var turnsX = 1 + (int)(r(0) * 2);
                    var turnsY = 1 + (int)(r(1) * 2);
                    var reach = Math.Min(0.25f, Reach(scene, depth.Drift, Math.Max(turnsX, turnsY)));
                    var centre = new Vector2(r(2) * (scene.Aspect + 0.2f) - 0.1f, r(3) * 1.15f - 0.075f);
                    var at = Orbit(clock, centre, new Vector2(reach * 1.4f, reach), turnsX, turnsY, r(4), r(5));
                    var breathe = 0.7f + 0.3f * clock.Wave(1 + (int)(r(6) * 3), r(7));
                    // Mostly the palette's warm middle-to-light colours, a few of its highlight.
                    var colour = scene.Ramp[0.5f + 0.5f * r(8)];
                    var light = depth.Light * (0.5f + 0.5f * r(9)) * breathe * strength;
                    Sprites.Add(new Sprite(at, Lerp(depth.Min, depth.Max, r(10)), colour * light, SpriteShape.Disc, depth.Soft, depth.Rim));
                }
            }
        }

        public override Vector3 Shade(int column, float x, float y) =>
            dark + wash * (Bump(x - glowA.X, y - glowA.Y, 0.55f) + 0.7f * Bump(x - glowB.X, y - glowB.Y, 0.45f));
    }
}
