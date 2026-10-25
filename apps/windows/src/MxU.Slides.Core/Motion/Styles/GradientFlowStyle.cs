using System.Numerics;
using static MxU.Slides.Core.Motion.StyleMath;

namespace MxU.Slides.Core.Motion.Styles;

/// <summary>
/// Soft fields of colour that drift and melt into each other: a "mesh gradient". Six coloured
/// points wander on slow closed paths; every pixel takes a blend of their colours, weighted by
/// how near each one is. The blend is done in OKLab, so two colours meet through a true
/// in-between colour. Before measuring distance, each pixel is nudged by a little noise, which
/// bends the straight borders between colours into organic curves.
/// </summary>
internal sealed class GradientFlowStyle : MotionStyle
{
    public override string Id => "gradient-flow";
    public override string Name => "Gradient Flow";
    public override string Description => "Soft fields of colour that drift and melt into each other.";
    public override string DefaultPalette => "Dawn";
    internal override float Grain => 0.3f;
    internal override float Vignette => 0.3f;

    private const int Points = 6;

    internal override MotionFrame Begin(MotionScene scene, LoopClock clock) => new Frame(scene, clock);

    private sealed class Frame : MotionFrame
    {
        private readonly LoopClock clock;
        private readonly Vector2 offset;
        private readonly float turn;
        private readonly Vector2[] where = new Vector2[Points];
        private readonly Vector3[] lab = new Vector3[Points];
        private readonly float[] chroma = new float[Points];
        private readonly float[] reach = [5.5f, 5.5f, 5.5f, 9f, 5.5f, 5.5f];
        private readonly Vector3 floor;
        private readonly float strength;

        public Frame(MotionScene scene, LoopClock clock)
        {
            this.clock = clock;
            offset = scene.Offset;
            turn = scene.Turn(0.07f);
            var s = scene.Stops;
            // Two dark anchors keep somewhere calm for text; the rest are the palette's colours.
            Vector3[] colours = [s[0], s[1], s[2], s[3], MotionColor.Mix(s[1], s[2], 0.5f), s[0] * 1.2f];
            for (var k = 0; k < Points; k++)
            {
                // Spread over a 3 × 2 grid, jittered, so the colours don't bunch up.
                var centre = new Vector2(
                    ((k % 3) + 0.2f + 0.6f * scene.Random(k, 0)) / 3 * scene.Aspect,
                    ((k / 3) + 0.2f + 0.6f * scene.Random(k, 1)) / 2);
                var turnsX = 1 + (k & 1);
                var turnsY = 1 + ((k >> 1) & 1);
                var reach = Math.Min(0.3f, Reach(scene, 0.05f, Math.Max(turnsX, turnsY)));
                where[k] = Orbit(clock, centre, new Vector2(reach * 1.3f, reach), turnsX, turnsY, scene.Random(k, 2), scene.Random(k, 3));
                lab[k] = MotionColor.ToOklab(colours[k]);
                chroma[k] = MotionColor.Chroma(lab[k]);
            }
            floor = s[0];
            strength = 0.35f + 0.65f * scene.Intensity;
        }

        public override Vector3 Shade(int column, float x, float y)
        {
            // Nudge the point by noise first, so borders between colours curve and breathe.
            var nx = x * 0.6f + offset.X;
            var ny = y * 0.6f + offset.Y;
            var qx = x + 0.38f * LoopNoise.Fbm(nx, ny, clock, turn, 3);
            var qy = y + 0.38f * LoopNoise.Fbm(nx + 31.4f, ny - 12.7f, clock, turn, 3, lane: 3);
            Vector3 sum = default;
            float total = 0, colourfulness = 0;
            for (var k = 0; k < Points; k++)
            {
                var dx = qx - where[k].X;
                var dy = qy - where[k].Y;
                // Soft bell-shaped weights, wide enough that every colour reaches well into its
                // neighbours' areas: long, smooth blends rather than patches. The highlight colour
                // reaches less far, so it's a glow, not a field.
                var weight = MathF.Exp(-(dx * dx + dy * dy) * reach[k]) + 1e-4f;
                sum += lab[k] * weight;
                colourfulness += chroma[k] * weight;
                total += weight;
            }
            // An average of different hues comes out greyer than any of them; give it back the
            // average colourfulness, so where two colours meet it glows rather than going muddy.
            var colour = MotionColor.FromOklab(MotionColor.KeepChroma(sum / total, colourfulness / total));
            // A faint, slow shimmer of light and shade across the colours, for depth.
            colour *= 0.92f + 0.08f * LoopNoise.Loop(x * 2.2f + offset.Y, y * 2.2f, clock, turn * 1.5f, 9);
            return Vector3.Lerp(floor, colour, strength);
        }
    }
}
