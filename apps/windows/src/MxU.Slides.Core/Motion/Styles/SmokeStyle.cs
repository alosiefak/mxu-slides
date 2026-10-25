using System.Numerics;
using static MxU.Slides.Core.Motion.StyleMath;

namespace MxU.Slides.Core.Motion.Styles;

/// <summary>
/// Smoke and fog rolling slowly upward through light from below. The density is noise
/// "warped" by more noise (each point looks up the smoke somewhere it has been pushed to),
/// which gives the curls and wisps of real smoke rather than plain blobs. The upward drift
/// uses <see cref="LoopNoise.Drift"/>: two copies of the smoke rise and take turns, so it seems
/// to rise for ever but repeats exactly. The light falls off from the bottom of the picture,
/// and the palette's ramp colours the smoke from shadow to its lit edges.
/// </summary>
internal sealed class SmokeStyle : MotionStyle
{
    public override string Id => "smoke";
    public override string Name => "Smoke & Fog";
    public override string Description => "Slow smoke and fog rolling upward through soft light.";
    public override string DefaultPalette => "Silver";
    internal override float Grain => 0.3f;
    internal override float Vignette => 0.42f;

    internal override MotionFrame Begin(MotionScene scene, LoopClock clock) => new Frame(scene, clock);

    private sealed class Frame : MotionFrame
    {
        private readonly LoopClock clock;
        private readonly MotionScene scene;
        private readonly float turn;
        private readonly (float OffsetA, float WeightA, float OffsetB, float WeightB) drift;
        private readonly float rise;
        private readonly Vector2 lamp;
        private readonly float strength;

        public Frame(MotionScene scene, LoopClock clock)
        {
            this.clock = clock;
            this.scene = scene;
            turn = scene.Turn(0.05f);
            var rounds = scene.RoundsOf(9);
            drift = LoopNoise.Drift(clock, rounds);
            // How far the smoke rises in one round, so it rises at the same pace whatever the length.
            rise = 0.045f * scene.Speed * scene.Seconds / rounds;
            lamp = new Vector2(scene.Aspect * (0.3f + 0.4f * scene.Random(0, 3)), 1.15f);
            strength = 0.35f + 0.65f * scene.Intensity;
        }

        private float Density(float x, float y, float lane)
        {
            var px = x * 0.85f + scene.Offset.X + lane * 13.1f;
            var py = y * 0.85f + scene.Offset.Y + lane * 7.7f;
            var wx = LoopNoise.Fbm(px, py, clock, turn, 2, lane: lane + 1);
            var wy = LoopNoise.Fbm(px + 5.2f, py + 1.3f, clock, turn, 2, lane: lane + 2);
            return LoopNoise.Fbm(px + 0.6f * wx, py + 0.6f * wy, clock, turn * 1.3f, 4, lane: lane + 3, gain: 0.55f);
        }

        public override Vector3 Shade(int column, float x, float y)
        {
            // Sampling further down as the round goes on makes the smoke move up.
            var a = Density(x, y + drift.OffsetA * rise, 0);
            var b = Density(x, y + drift.OffsetB * rise, 40);
            var n = LoopNoise.BlendKeepingContrast(a, drift.WeightA, b, drift.WeightB);
            var smoke = Smooth(-0.7f, 0.7f, n);
            var dx = x - lamp.X;
            var dy = y - lamp.Y;
            var lit = 0.25f + 0.85f * MathF.Exp(-(dx * dx * 0.3f + dy * dy) / 0.8f);
            // Thick smoke catches the light; thin smoke lets the dark through. Never quite black:
            // there's always a little fog.
            var shown = (0.15f + 0.85f * smoke * smoke) * lit;
            var colour = scene.Ramp[0.1f + 0.7f * shown] * (0.5f + 0.95f * shown);
            return Vector3.Lerp(scene.Stops[0], colour, strength);
        }
    }
}
