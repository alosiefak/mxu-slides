using System.Numerics;
using static MxU.Slides.Core.Motion.StyleMath;

namespace MxU.Slides.Core.Motion.Styles;

/// <summary>
/// Volumetric light: shafts pouring down from a light above the top of the picture. For each
/// pixel the angle from the light picks a value from noise that varies around the circle, so
/// neighbouring directions get bright or dark together: those are the shafts. They fade with
/// distance and are broken up a little by slow haze, the dust in the air the light is
/// catching. Specks of dust drift through, glinting where a beam crosses them.
/// </summary>
internal sealed class LightRaysStyle : MotionStyle
{
    public override string Id => "light-rays";
    public override string Name => "Light Rays";
    public override string Description => "Shafts of light pouring down through soft haze, with dust drifting in the beams.";
    public override string DefaultPalette => "Easter";
    internal override int FieldDivisor => 2;
    internal override float Grain => 0.25f;
    internal override float Vignette => 0.35f;

    private const int Motes = 110;

    internal override MotionFrame Begin(MotionScene scene, LoopClock clock) => new Frame(scene, clock);

    private sealed class Frame : MotionFrame
    {
        private readonly LoopClock clock;
        private readonly MotionScene scene;
        private readonly Vector2 source;
        private readonly float turn;
        private readonly Vector3 dark;
        private readonly Vector3 near;
        private readonly Vector3 far;
        private readonly Vector3 room;
        private readonly float strength;

        public Frame(MotionScene scene, LoopClock clock)
        {
            this.clock = clock;
            this.scene = scene;
            var s = scene.Stops;
            // The light sways a touch from side to side over the loop.
            var sway = 0.025f * Math.Min(1, scene.Speed);
            source = new Vector2(scene.Aspect * (0.42f + 0.22f * scene.Random(0, 7)) + sway * clock.Wave(1), -0.38f);
            turn = scene.Turn(0.06f);
            dark = s[0];
            room = Vector3.Lerp(s[0], s[1], 0.45f);
            near = s[3];
            far = Vector3.Lerp(s[1], s[2], 0.6f);
            strength = 0.25f + 1.0f * scene.Intensity;

            // Dust: each speck lives a few seconds, drifting, then appears somewhere new.
            for (var i = 0; i < Motes; i++)
            {
                var life = 5 + 6 * scene.Random(i, 0);
                var rounds = Math.Max(1, (int)MathF.Round(scene.Seconds / life));
                var (through, round) = clock.Rounds(rounds, scene.Random(i, 1));
                var lifeSeconds = (float)scene.Seconds / rounds;
                var start = new Vector2(scene.Random(i, 10 + round) * scene.Aspect, 0.1f + 0.9f * scene.Random(i, 40 + round));
                var drift = new Vector2(0.006f * (scene.Random(i, 2) - 0.5f), -0.008f - 0.01f * scene.Random(i, 3)) * scene.Speed;
                var at = start + drift * (through * lifeSeconds)
                    + new Vector2(0.006f * MathF.Sin(Tau * (through * 1.5f + scene.Random(i, 4))), 0);
                var fade = Smooth(0, 0.2f, through) * Smooth(1, 0.8f, through);
                var depth = scene.Random(i, 5);
                var lit = Rays(at.X, at.Y);
                var glint = 0.6f + 0.4f * MathF.Sin(Tau * (through * 2 + scene.Random(i, 6)));
                var bright = fade * glint * (0.25f + 1.5f * lit) * (0.15f + 0.5f * depth) * strength;
                Sprites.Add(new Sprite(at, 0.0012f + 0.0035f * depth * depth, near * bright, SpriteShape.Glow));
            }
        }

        /// <summary>How much beam there is at (x, y): 0 between shafts, about 1 in the brightest.</summary>
        private float Rays(float x, float y)
        {
            var dx = x - source.X;
            var dy = y - source.Y;
            var angle = MathF.Atan2(dx, dy);
            var o = scene.Offset;
            // Broad groups of shafts, and finer shafts within them.
            var n = 0.45f * LoopNoise.Loop(angle * 3 + o.X, 0.5f, clock, turn, 1)
                + 0.4f * LoopNoise.Loop(angle * 9 + o.Y, 2.5f, clock, turn * 1.4f, 2)
                + 0.15f * LoopNoise.Loop(angle * 24 + o.Y, 4.5f, clock, turn * 2f, 3);
            var shafts = Smooth(-0.25f, 0.5f, n);
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            return shafts * shafts * MathF.Exp(-(distance - 0.38f) * 1.5f);
        }

        public override Vector3 Shade(int column, float x, float y)
        {
            var dx = x - source.X;
            var dy = y - source.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            // Haze: slow clouds of dust the beams light up unevenly.
            var haze = 0.62f + 0.38f * LoopNoise.Fbm(x * 1.5f + scene.Offset.Y, y * 1.5f, clock, turn, 3, lane: 5);
            var beam = Rays(x, y) * haze;
            var nearness = Saturate(1.6f - distance) / 1.6f;
            var tint = Vector3.Lerp(far, near, nearness * nearness);
            var background = Vector3.Lerp(dark, room, Smooth(1.5f, 0.3f, distance));
            // A bright bloom where the light comes in at the top.
            var bloom = MathF.Exp(-distance * distance * 3.5f) * 0.9f;
            return background + tint * ((beam * 0.9f + bloom) * strength);
        }
    }
}
