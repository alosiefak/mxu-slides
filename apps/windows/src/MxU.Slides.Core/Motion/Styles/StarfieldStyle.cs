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
            turn = scene.Turn(0.012f);
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

        // Rich fine detail (a slow fall-off between layers), so the clouds have structure inside them.
        private float Gas(float x, float y) => LoopNoise.Fbm(x * 1.2f + scene.Offset.X, y * 1.2f + scene.Offset.Y, clock, turn, 5, gain: 0.62f);

        private float Dust(float x, float y) => LoopNoise.Fbm(x * 2.6f + scene.Offset.Y, y * 2.6f, clock, turn * 1.3f, 3, lane: 12);

        public override Vector3 Shade(int column, float x, float y)
        {
            var o = scene.Offset;
            // Two copies of the clouds take turns drifting (LoopNoise.Drift), gas and dust alike.
            float xa = x + drift.OffsetA * travel, xb = x + drift.OffsetB * travel;
            var gas = Gas(xa, y) * drift.WeightA + Gas(xb, y) * drift.WeightB;
            var dust = Dust(xa, y) * drift.WeightA + Dust(xb, y) * drift.WeightB;
            var tint = LoopNoise.Loop(x * 0.6f + o.Y, y * 0.6f + 9, clock, turn, 24);
            var cloud = Smooth(-0.35f, 0.95f, gas);
            var lanes = Smooth(-0.15f, 0.45f, dust) * 0.85f;
            // The gas glows in the palette's deep and middle colours, shifting from place to place;
            // only the densest cores reach its highlight.
            var colour = scene.Ramp[0.3f + 0.3f * cloud + 0.25f * tint] * (MathF.Pow(cloud, 1.7f) * 1.25f * (1 - lanes));
            colour += scene.Stops[3] * (MathF.Pow(cloud, 6) * 0.18f * (1 - lanes));
            return scene.Stops[0] * 0.7f + colour * (0.55f * strength);
        }
    }
}
