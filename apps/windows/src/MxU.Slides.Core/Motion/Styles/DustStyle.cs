using System.Numerics;
using static MxU.Slides.Core.Motion.StyleMath;

namespace MxU.Slides.Core.Motion.Styles;

/// <summary>
/// Specks of dust drifting up through a soft shaft of light. Each speck has a short life: it
/// fades in somewhere, floats up and sideways with a little wobble, and fades out, then
/// starts again somewhere new. Its life is chosen to fit a whole number of times into the
/// loop, and where it starts depends only on which life it's on, so at the end of the loop
/// every speck is back at the beginning of its first life. Nearer specks are bigger, softer
/// and dimmer (out of focus); specks near the light are lit more.
/// </summary>
internal sealed class DustStyle : MotionStyle
{
    public override string Id => "dust";
    public override string Name => "Floating Dust";
    public override string Description => "Tiny specks of light drifting up through a soft glow.";
    public override string DefaultPalette => "Golden Hour";
    internal override float Grain => 0.25f;
    internal override float Vignette => 0.4f;

    private const int Specks = 560;
    private const int Blurred = 34;

    internal override MotionFrame Begin(MotionScene scene, LoopClock clock) => new Frame(scene, clock);

    private sealed class Frame : MotionFrame
    {
        private readonly LoopClock clock;
        private readonly MotionScene scene;
        private readonly Vector2 light;
        private readonly Vector3 dark;
        private readonly Vector3 room;
        private readonly Vector3 warm;
        private readonly float turn;
        private readonly float strength;

        public Frame(MotionScene scene, LoopClock clock)
        {
            this.clock = clock;
            this.scene = scene;
            var s = scene.Stops;
            dark = s[0];
            room = s[1] * 0.55f;
            warm = s[2];
            turn = scene.Turn(0.05f);
            strength = 0.3f + 1.0f * scene.Intensity;
            light = Orbit(clock, new Vector2(scene.Aspect * (0.62f + 0.2f * scene.Random(0, 9)), 0.18f), new Vector2(Reach(scene, 0.006f, 1)), 1, 1, 0, 0.3f);

            for (var i = 0; i < Specks + Blurred; i++)
            {
                var blurred = i >= Specks;
                var life = 6 + 6 * scene.Random(i, 0);
                var rounds = Math.Max(1, (int)MathF.Round(scene.Seconds / life));
                var lifeSeconds = (float)scene.Seconds / rounds;
                var (through, round) = clock.Rounds(rounds, scene.Random(i, 1));
                var start = new Vector2(scene.Random(i, 10 + round) * (scene.Aspect + 0.1f) - 0.05f, 0.05f + 1.0f * scene.Random(i, 50 + round));
                var rise = (0.012f + 0.022f * scene.Random(i, 2)) * scene.Speed;
                var across = (scene.Random(i, 3) - 0.5f) * 0.014f * scene.Speed;
                var wobble = 0.012f * MathF.Sin(Tau * (through * (1 + scene.Random(i, 4)) + scene.Random(i, 5)));
                var at = start + new Vector2(across * through * lifeSeconds + wobble, -rise * through * lifeSeconds);
                var fade = Smooth(0, 0.18f, through) * Smooth(1, 0.75f, through);
                var twinkle = 0.7f + 0.3f * MathF.Sin(Tau * (through * 3 + scene.Random(i, 6)));
                var dx = at.X - light.X;
                var dy = at.Y - light.Y;
                var lit = 0.25f + 1.1f * MathF.Exp(-(dx * dx + dy * dy) / 0.35f);
                var depth = scene.Random(i, 7);
                var colour = scene.Ramp[0.65f + 0.35f * scene.Random(i, 8)];
                if (blurred)
                {
                    Sprites.Add(new Sprite(at, 0.012f + 0.022f * depth, colour * (0.09f * fade * lit * strength), SpriteShape.Disc, 0.6f, 0.1f));
                }
                else
                {
                    var size = 0.0012f + 0.003f * depth * depth;
                    Sprites.Add(new Sprite(at, size, colour * ((0.3f + 1.2f * (1 - depth)) * fade * twinkle * lit * strength), SpriteShape.Glow));
                }
            }
        }

        public override Vector3 Shade(int column, float x, float y)
        {
            var dx = x - light.X;
            var dy = y - light.Y;
            var d2 = dx * dx + dy * dy;
            // A broad glow and a brighter heart where the light is, through a little slow haze.
            var haze = 0.8f + 0.2f * LoopNoise.Fbm(x * 1.4f + scene.Offset.X, y * 1.4f, clock, turn, 3);
            var glow = MathF.Exp(-d2 / 0.45f) * 0.8f + MathF.Exp(-d2 / 0.06f) * 0.45f * scene.Intensity;
            return dark + (room + warm * (0.35f * glow)) * (glow * haze);
        }
    }
}
