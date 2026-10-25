using System.Numerics;
using static MxU.Slides.Core.Motion.StyleMath;

namespace MxU.Slides.Core.Motion.Styles;

/// <summary>
/// A slow wash of colour with the texture of film: broad pools of the palette's colours
/// drifting over its darkest, a warm light leak creeping along an edge, a faint flicker, and
/// heavy grain on top (the renderer adds it; this style just turns it up). The flicker is a sum
/// of fast waves that each go round a whole number of times per loop, so it looks random
/// but repeats with the loop.
/// </summary>
internal sealed class FilmWashStyle : MotionStyle
{
    public override string Id => "film-wash";
    public override string Name => "Film Wash";
    public override string Description => "A slow wash of colour with the warmth, flicker and grain of film.";
    public override string DefaultPalette => "Rose Gold";
    internal override float Grain => 1f;
    internal override float Vignette => 0.55f;

    internal override MotionFrame Begin(MotionScene scene, LoopClock clock) => new Frame(scene, clock);

    private sealed class Frame : MotionFrame
    {
        private readonly LoopClock clock;
        private readonly MotionScene scene;
        private readonly Vector2[] pools = new Vector2[3];
        private readonly Vector3[] colours = new Vector3[3];
        private readonly Vector2 leak;
        private readonly float leakLight;
        private readonly Vector3 leakTint;
        private readonly float flicker;
        private readonly float turn;
        private readonly float strength;

        public Frame(MotionScene scene, LoopClock clock)
        {
            this.clock = clock;
            this.scene = scene;
            var s = scene.Stops;
            turn = scene.Turn(0.04f);
            colours[0] = s[1] * 0.8f;
            colours[1] = s[2] * 0.42f;
            colours[2] = MotionColor.Mix(s[1], s[2], 0.5f) * 0.3f;
            for (var k = 0; k < 3; k++)
            {
                var centre = new Vector2(scene.Aspect * (0.2f + 0.6f * scene.Random(k, 0)), 0.2f + 0.6f * scene.Random(k, 1));
                pools[k] = Orbit(clock, centre, new Vector2(Reach(scene, 0.03f, 1) * 1.4f, Reach(scene, 0.03f, 1)), 1, 1 + k % 2, scene.Random(k, 2), scene.Random(k, 3));
            }
            // The leak slides along the left edge and back, brightening and fading.
            leak = new Vector2(0.02f, 0.45f + 0.3f * clock.Wave(1, scene.Random(4, 0)));
            leakLight = (0.55f + 0.45f * clock.Wave(2, scene.Random(4, 1))) * (0.25f + 0.75f * scene.Intensity);
            flicker = 1 + 0.012f * (clock.Wave(37) + 0.7f * clock.Wave(53, 0.3f) + 0.5f * clock.Wave(71, 0.7f));
            strength = 0.3f + 0.55f * scene.Intensity;
            leakTint = MotionColor.Mix(s[3], new Vector3(1, 0.45f, 0.12f), 0.45f) * 0.9f;
        }

        public override Vector3 Shade(int column, float x, float y)
        {
            var colour = scene.Stops[0];
            for (var k = 0; k < 3; k++)
                colour += colours[k] * (Bump(x - pools[k].X, y - pools[k].Y, 0.48f) * strength);
            var texture = 0.85f + 0.15f * LoopNoise.Fbm(x * 1.3f + scene.Offset.X, y * 1.3f, clock, turn, 3);
            // A warm leak: wide and faint, with a brighter heart against the edge.
            var lx = (x - leak.X) * 0.7f;
            var ly = y - leak.Y;
            var leakGlow = (Bump(lx, ly, 0.5f) * 0.6f + Bump(lx, ly, 0.18f) * 0.8f) * leakLight;
            // Leaks burn warm: the palette's highlight pushed towards orange.
            return (colour * texture + leakTint * leakGlow) * flicker;
        }
    }
}
