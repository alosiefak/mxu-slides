using System.Numerics;
using static MxU.Slides.Core.Motion.StyleMath;

namespace MxU.Slides.Core.Motion.Styles;

/// <summary>
/// Ribbons of fine light rising and falling like a calm sea. Each ribbon is a bundle of thin
/// lines strung between two wavy edges. Where the edges cross, the bundle seems to twist
/// over, like silk turning in the light; the lines crowd together there and glow brighter.
/// The edges are slow loop noise plus a long wave that travels once across per loop. A soft
/// glow fills each ribbon, and light runs along the lines.
/// <para>
/// This style is drawn at every pixel (the lines are a pixel thin), but it's cheap: the edges
/// depend only on x, so they're worked out once per column, and each pixel only looks at the
/// few lines close enough to touch it.
/// </para>
/// </summary>
internal sealed class WavesStyle : MotionStyle
{
    public override string Id => "waves";
    public override string Name => "Gentle Waves";
    public override string Description => "Ribbons of fine light rising and falling like a calm sea.";
    public override string DefaultPalette => "Ocean";
    internal override int FieldDivisor => 1;
    internal override float Grain => 0.2f;
    internal override float Vignette => 0.35f;

    private const int Ribbons = 3;
    private const int Lines = 28;
    // Line half-width in picture units: about a pixel and a half at 1080p.
    private const float Width = 0.0013f;

    internal override MotionFrame Begin(MotionScene scene, LoopClock clock) => new Frame(scene, clock);

    private sealed class Frame : MotionFrame
    {
        private readonly float[][] edge = new float[Ribbons][];
        private readonly float[][] span = new float[Ribbons][];
        private readonly float[][] edgeSlope = new float[Ribbons][];
        private readonly float[][] spanSlope = new float[Ribbons][];
        private readonly float[][] shine = new float[Ribbons][];
        private readonly Vector3[] tint = new Vector3[Ribbons];
        private readonly Vector3 top;
        private readonly Vector3 bottom;
        private readonly float strength;

        public Frame(MotionScene scene, LoopClock clock)
        {
            var s = scene.Stops;
            var o = scene.Offset;
            var xs = scene.ColumnX;
            var turn = scene.Turn(0.05f);
            for (var k = 0; k < Ribbons; k++)
            {
                edge[k] = new float[xs.Length];
                span[k] = new float[xs.Length];
                shine[k] = new float[xs.Length];
                var baseline = 0.42f + 0.17f * k + 0.06f * (scene.Random(k, 0) - 0.5f);
                var waves = 0.6f + 0.6f * scene.Random(k, 1);
                for (var i = 0; i < xs.Length; i++)
                {
                    var x = xs[i];
                    // A long wave that rolls across once per loop, plus slow noise for the swell.
                    var roll = MathF.Sin(Tau * (x / scene.Aspect * waves - (float)clock.Phase + scene.Random(k, 2)));
                    edge[k][i] = baseline + 0.09f * LoopNoise.Fbm(x * 0.65f + o.X + k * 9.7f, k * 4.1f + o.Y, clock, turn, 2, lane: k)
                        + 0.03f * roll * Math.Min(1, scene.Speed);
                    // The ribbon's width swings through zero, which is where it twists.
                    span[k][i] = (0.11f + 0.04f * k) * LoopNoise.Fbm(x * 0.8f + 40 + k * 5.3f + o.Y, 3 + k, clock, turn * 1.2f, 2, lane: 10 + k);
                    // Light running along the lines.
                    shine[k][i] = 0.55f + 0.45f * MathF.Sin(Tau * (x / scene.Aspect * 1.3f - (float)clock.Phase * 2 + k * 0.37f));
                }
                edgeSlope[k] = Slopes(edge[k], xs);
                spanSlope[k] = Slopes(span[k], xs);
                tint[k] = scene.Ramp[0.55f + 0.2f * k];
            }
            top = s[0];
            bottom = Vector3.Lerp(s[0], s[1], 0.6f);
            strength = 0.3f + 1.0f * scene.Intensity;
        }

        private static float[] Slopes(float[] values, float[] xs)
        {
            var slopes = new float[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                int a = Math.Max(0, i - 1), b = Math.Min(values.Length - 1, i + 1);
                slopes[i] = b > a ? (values[b] - values[a]) / (xs[b] - xs[a]) : 0;
            }
            return slopes;
        }

        public override Vector3 Shade(int column, float x, float y)
        {
            var colour = Vector3.Lerp(top, bottom, Smooth(0.2f, 1.1f, y));
            for (var k = 0; k < Ribbons; k++)
            {
                var a = edge[k][column];
                var s = span[k][column];
                var lo = Math.Min(a, a + s);
                var hi = Math.Max(a, a + s);
                var outside = y < lo ? lo - y : y > hi ? y - hi : 0;
                // The soft glow inside the ribbon (and just outside it).
                var volume = MathF.Exp(-outside * outside / 0.0018f) * (0.05f + 0.1f * MathF.Abs(s) / 0.15f);

                // Only the lines within a few widths of y can touch this pixel.
                var lines = 0f;
                var step = s / (Lines - 1);
                const float reach = 3.5f * Width;
                int first, last;
                if (MathF.Abs(step) < 1e-6f)
                {
                    first = 0;
                    last = MathF.Abs(y - a) < reach ? Lines - 1 : -1;
                }
                else
                {
                    var from = (y - reach - a) / step;
                    var to = (y + reach - a) / step;
                    if (from > to) (from, to) = (to, from);
                    first = Math.Max(0, (int)MathF.Ceiling(from));
                    last = Math.Min(Lines - 1, (int)MathF.Floor(to));
                }
                for (var i = first; i <= last; i++)
                {
                    var along = (float)i / (Lines - 1);
                    var slope = edgeSlope[k][column] + spanSlope[k][column] * along;
                    // Distance across the line, not straight down, so steep parts stay thin.
                    var d = (y - (a + s * along)) / (Width * MathF.Sqrt(1 + slope * slope));
                    // The outer lines of the bundle are brighter: its edges catch the light.
                    lines += MathF.Exp(-d * d) * (0.45f + 0.55f * MathF.Abs(2 * along - 1));
                }
                // Where the ribbon twists, many lines pile up: let them glow, not blow out.
                lines /= 1 + 0.18f * lines;
                colour += tint[k] * ((lines * 0.55f * shine[k][column] + volume) * strength);
            }
            return colour;
        }
    }
}
