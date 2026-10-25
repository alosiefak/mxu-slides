using System.Numerics;
using static MxU.Slides.Core.Motion.StyleMath;

namespace MxU.Slides.Core.Motion.Styles;

/// <summary>
/// The northern lights: two curtains of light over a night sky. Each curtain hangs from a
/// wavy line, its foot (noise along x, so it bends and rolls). Just above the foot is a bright
/// edge; above that the light fades upward, broken into fine vertical rays by noise that is
/// stretched tall and thin. Below the foot the light stops sharply, as a real aurora's lower
/// edge does. Brightness comes and goes along the curtain, so it gathers into folds with dark
/// gaps between. The colour shifts from the palette's third colour at the foot to its fourth at
/// the top. Faint twinkling stars sit behind.
/// </summary>
internal sealed class AuroraStyle : MotionStyle
{
    public override string Id => "aurora";
    public override string Name => "Aurora";
    public override string Description => "Curtains of northern light rippling slowly across a night sky.";
    public override string DefaultPalette => "Northern Lights";
    internal override int FieldDivisor => 2;
    internal override float Grain => 0.2f;
    internal override float Vignette => 0.3f;

    private const int Curtains = 2;
    private const int Stars = 170;

    internal override MotionFrame Begin(MotionScene scene, LoopClock clock) => new Frame(scene, clock);

    private sealed class Frame : MotionFrame
    {
        private readonly LoopClock clock;
        private readonly MotionScene scene;
        private readonly float[][] foot = new float[Curtains][];
        private readonly float[][] glow = new float[Curtains][];
        private readonly float[][] reach = new float[Curtains][];
        private readonly float[][] sway = new float[Curtains][];
        private readonly float shimmer;
        private readonly Vector3 skyTop;
        private readonly Vector3 skyLow;
        private readonly Vector3 low;
        private readonly Vector3 high;
        private readonly float strength;

        public Frame(MotionScene scene, LoopClock clock)
        {
            this.clock = clock;
            this.scene = scene;
            var s = scene.Stops;
            var o = scene.Offset;
            var bend = scene.Turn(0.05f);
            shimmer = scene.Turn(0.1f);
            // Everything that only depends on x is worked out once per column, not per pixel.
            var xs = scene.ColumnX;
            for (var c = 0; c < Curtains; c++)
            {
                foot[c] = new float[xs.Length];
                glow[c] = new float[xs.Length];
                reach[c] = new float[xs.Length];
                sway[c] = new float[xs.Length];
                for (var i = 0; i < xs.Length; i++)
                {
                    var x = xs[i];
                    foot[c][i] = 0.5f - 0.17f * c + 0.16f * LoopNoise.Fbm(x * 0.5f + o.X + c * 11.7f, c * 3.3f + o.Y, clock, bend, 3, lane: c);
                    // Bright folds and dark gaps along the curtain.
                    var patch = LoopNoise.Fbm(x * 1.1f + o.Y + c * 5.1f, 7.7f + c, clock, bend * 1.3f, 3, lane: 10 + c);
                    glow[c][i] = Smooth(-0.35f, 0.45f, patch) * (1 - 0.35f * c);
                    reach[c][i] = 0.16f + 0.14f * Smooth(-0.5f, 0.6f, LoopNoise.Loop(x * 0.9f + c * 2.2f + o.X, 4.4f, clock, bend, 14 + c));
                    sway[c][i] = 0.03f * LoopNoise.Loop(x * 1.8f + c * 2.9f + o.X, 1.5f + c, clock, bend, 20 + c);
                }
            }
            skyTop = s[0] * 0.6f;
            skyLow = Vector3.Lerp(s[0], s[1], 0.5f);
            low = s[2];
            high = s[3];
            strength = 0.3f + 1.1f * scene.Intensity;

            for (var i = 0; i < Stars; i++)
            {
                var y = scene.Random(i, 1) * 0.8f;
                var twinkle = 0.65f + 0.35f * clock.Wave(1 + i % 3, scene.Random(i, 2));
                var bright = 0.04f + 0.7f * MathF.Pow(scene.Random(i, 3), 5);
                Sprites.Add(new Sprite(new Vector2(scene.Random(i, 0) * scene.Aspect, y), 0.0016f + 0.002f * scene.Random(i, 4),
                    new Vector3(bright * twinkle * (1 - y)), SpriteShape.Star));
            }
        }

        public override Vector3 Shade(int column, float x, float y)
        {
            var colour = Vector3.Lerp(skyTop, skyLow, Smooth(0.0f, 1.0f, y));
            Vector3 light = default;
            for (var c = 0; c < Curtains; c++)
            {
                var above = foot[c][column] - y;
                // A wide soft haze below and round the curtain: light scattered in the air.
                light += low * (0.06f * glow[c][column] * MathF.Exp(-MathF.Abs(above) / 0.2f));
                if (above < -0.1f) continue;
                var height = reach[c][column];
                // Fades upward; stops sharply below the foot; a bright band right at the foot.
                var fall = above > 0 ? MathF.Exp(-above / height) : MathF.Exp(above / 0.012f);
                var edge = MathF.Exp(-Square((above - 0.012f) / 0.02f));
                // Tall thin noise: the curtain's rays, slowly shimmering.
                var rays = Smooth(-0.5f, 0.6f, LoopNoise.Loop((x + sway[c][column]) * 26 + c * 17.3f + scene.Offset.X, y * 1.1f, clock, shimmer, 30 + c));
                rays = 0.2f + 0.8f * rays;
                var tint = Vector3.Lerp(low, high, Smooth(0.03f, height * 1.4f, above));
                light += tint * (glow[c][column] * (fall * rays + 0.55f * edge));
            }
            return colour + light * strength;
        }

        private static float Square(float v) => v * v;
    }
}
