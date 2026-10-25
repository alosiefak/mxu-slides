using System.Numerics;
using static MxU.Slides.Core.Motion.StyleMath;

namespace MxU.Slides.Core.Motion.Styles;

/// <summary>
/// Marbled colour folding and swirling like ink in oil. It's "domain warping" (Inigo Quilez,
/// 2002): noise looked up at a point that has been pushed by noise, which was itself looked
/// up at a pushed point. Each push folds the pattern further, which is what makes the swirls.
/// The two pushes (q and r) also colour it: where they're strong, the palette's highlight and
/// shadow come through. A thin bright line where the pattern crosses one value gives the
/// glossy folds.
/// </summary>
internal sealed class LiquidStyle : MotionStyle
{
    public override string Id => "liquid";
    public override string Name => "Liquid";
    public override string Description => "Marbled colour folding and swirling like ink in oil.";
    public override string DefaultPalette => "Royal";
    internal override int FieldDivisor => 2;
    internal override float Grain => 0.2f;
    internal override float Vignette => 0.35f;

    internal override MotionFrame Begin(MotionScene scene, LoopClock clock) => new Frame(scene, clock);

    private sealed class Frame(MotionScene scene, LoopClock clock) : MotionFrame
    {
        private readonly float turn = scene.Turn(0.02f);
        private readonly float strength = 0.35f + 0.65f * scene.Intensity;

        public override Vector3 Shade(int column, float x, float y)
        {
            // Large, slow shapes: a low frequency, two folds, few layers.
            var px = x * 0.4f + scene.Offset.X;
            var py = y * 0.4f + scene.Offset.Y;
            var qx = LoopNoise.Fbm(px, py, clock, turn, 2);
            var qy = LoopNoise.Fbm(px + 5.2f, py + 1.3f, clock, turn, 2, lane: 1);
            var rx = LoopNoise.Fbm(px + 1.1f * qx + 1.7f, py + 1.1f * qy + 9.2f, clock, turn * 1.2f, 3, lane: 2);
            var ry = LoopNoise.Fbm(px + 1.1f * qx + 8.3f, py + 1.1f * qy + 2.8f, clock, turn * 1.2f, 3, lane: 3);
            var f = LoopNoise.Fbm(px + 1.2f * rx, py + 1.2f * ry, clock, turn * 1.4f, 3, lane: 4, gain: 0.4f);

            var colour = scene.Ramp[0.15f + 0.55f * Saturate(0.5f + 0.8f * f)];
            // Mixed in OKLab, so purple meeting gold goes through a warm rose, not a muddy olive.
            colour = MotionColor.Mix(colour, scene.Stops[3], Smooth(0.15f, 0.55f, qx * qx + qy * qy) * 0.28f);
            colour = Vector3.Lerp(colour, scene.Stops[0], Smooth(-0.35f, 0.55f, rx) * 0.65f);
            // The glossy folds: a soft bright line where the pattern crosses one value.
            var gloss = MathF.Exp(-Square((f - 0.3f) / 0.12f)) * 0.1f * scene.Intensity;
            return Vector3.Lerp(scene.Stops[0], colour, strength) + scene.Stops[3] * gloss;
        }

        private static float Square(float v) => v * v;
    }
}
