using System.Numerics;
using static MxU.Slides.Core.Motion.StyleMath;

namespace MxU.Slides.Core.Motion.Styles;

/// <summary>
/// Deep space: coloured nebula clouds with dark dust lanes, and stars drifting slowly past at
/// different depths (near ones faster and brighter: parallax). The nebula is two layers of
/// noise, one for the glowing gas and one, finer, for the dust in front of it. Stars live the
/// way <see cref="DustStyle"/>'s specks do, fading in and out over many seconds, which is how
/// they can drift for ever and still be back where they began when the loop ends.
/// </summary>
internal sealed class StarfieldStyle : MotionStyle
{
    public override string Id => "starfield";
    public override string Name => "Starfield";
    public override string Description => "A slow drift through stars and glowing nebula clouds.";
    public override string DefaultPalette => "Advent";
    internal override int FieldDivisor => 2;
    internal override float Grain => 0.15f;
    internal override float Vignette => 0.45f;

    private const int Stars = 520;

    internal override MotionFrame Begin(MotionScene scene, LoopClock clock) => new Frame(scene, clock);

    private sealed class Frame : MotionFrame
    {
        private readonly LoopClock clock;
        private readonly MotionScene scene;
        private readonly float turn;
        private readonly (float OffsetA, float WeightA, float OffsetB, float WeightB) drift;
        private readonly float travel;
        private readonly float strength;

        public Frame(MotionScene scene, LoopClock clock)
        {
            this.clock = clock;
            this.scene = scene;
            turn = scene.Turn(0.03f);
            var rounds = scene.RoundsOf(12);
            drift = LoopNoise.Drift(clock, rounds);
            // The nebula drifts as far as the farthest stars do.
            travel = 0.006f * scene.Speed * scene.Seconds / rounds;
            strength = 0.25f + 1.0f * scene.Intensity;

            var heading = new Vector2(-1, 0.18f + 0.2f * (scene.Random(0, 9) - 0.5f));
            for (var i = 0; i < Stars; i++)
            {
                var depth = scene.Random(i, 0);
                var life = 8 + 10 * scene.Random(i, 1);
                var rounds2 = Math.Max(1, (int)MathF.Round(scene.Seconds / life));
                var lifeSeconds = (float)scene.Seconds / rounds2;
                var (through, round) = clock.Rounds(rounds2, scene.Random(i, 2));
                var start = new Vector2(scene.Random(i, 10 + round) * (scene.Aspect + 0.3f) - 0.1f, scene.Random(i, 50 + round) * 1.1f - 0.05f);
                var speed = (0.006f + 0.03f * depth * depth * depth) * scene.Speed;
                var at = start + heading * (speed * through * lifeSeconds);
                var fade = Smooth(0, 0.25f, through) * Smooth(1, 0.75f, through);
                var twinkle = 0.75f + 0.25f * MathF.Sin(Tau * (through * (2 + 3 * scene.Random(i, 3)) + scene.Random(i, 4)));
                // Most stars are faint; a few are bright (brightness falls off steeply, as real star counts do).
                var bright = (0.08f + 1.6f * MathF.Pow(scene.Random(i, 5), 6)) * (0.45f + 0.55f * depth);
                var colour = Vector3.Lerp(new Vector3(1), scene.Stops[3], 0.35f * scene.Random(i, 6));
                var light = colour * (bright * fade * twinkle * (0.5f + 0.7f * scene.Intensity));
                Sprites.Add(new Sprite(at, 0.0016f + 0.004f * depth * depth, light, SpriteShape.Star));
                // The brightest few get a soft halo, as a lens gives a bright star.
                if (bright > 0.5f) Sprites.Add(new Sprite(at, 0.012f + 0.01f * depth, light * 0.06f, SpriteShape.Glow));
            }
        }

        public override Vector3 Shade(int column, float x, float y)
        {
            var o = scene.Offset;
            // Two copies of the clouds take turns drifting (LoopNoise.Drift), gas and dust alike.
            float xa = x + drift.OffsetA * travel, xb = x + drift.OffsetB * travel;
            var gas = LoopNoise.BlendKeepingContrast(
                LoopNoise.Fbm(xa * 1.3f + o.X, y * 1.3f + o.Y, clock, turn, 5), drift.WeightA,
                LoopNoise.Fbm(xb * 1.3f + o.X + 17, y * 1.3f + o.Y + 3, clock, turn, 5, lane: 6), drift.WeightB);
            var dust = LoopNoise.BlendKeepingContrast(
                LoopNoise.Fbm(xa * 2.6f + o.Y, y * 2.6f, clock, turn * 1.3f, 3, lane: 12), drift.WeightA,
                LoopNoise.Fbm(xb * 2.6f + o.Y + 29, y * 2.6f + 5, clock, turn * 1.3f, 3, lane: 18), drift.WeightB);
            var cloud = Smooth(-0.15f, 0.75f, gas);
            var lanes = Smooth(-0.05f, 0.45f, dust) * 0.8f;
            // The gas glows in the palette's middle colours, a little differently from place to place.
            var hue = 0.28f + 0.4f * cloud + 0.25f * dust;
            var colour = scene.Ramp[hue] * (cloud * cloud * (1 - lanes));
            // Bright cores where the gas is densest.
            colour += scene.Stops[3] * (MathF.Pow(cloud, 5) * 0.22f * (1 - lanes));
            return scene.Stops[0] * 0.7f + colour * (0.55f * strength);
        }
    }
}
