using System.Numerics;
using static MxU.Slides.Core.Motion.StyleMath;

namespace MxU.Slides.Core.Motion.Styles;

/// <summary>
/// Fine lines of hexagons in three layers at different depths, drifting slowly and lit by a
/// soft light that wanders across them. A hexagon grid looks exactly the same after it moves
/// by one cell, so each layer moves one cell per loop (nearer layers have bigger cells, so they
/// move further: that's the depth). Some cells glow faintly; which ones is decided by smooth
/// waves at the cell's place on screen, so after a whole cell's drift the same places glow
/// and the loop is seamless.
/// <para>
/// For each pixel and layer, <see cref="Hex"/> finds the nearest cell centre and how far the
/// pixel is from the cell's edge (the "hexagonal tiling" construction Inigo Quilez and Martijn
/// Steinrucken describe): the lines are where that distance is near 0.
/// </para>
/// </summary>
internal sealed class GeometricStyle : MotionStyle
{
    public override string Id => "geometric";
    public override string Name => "Geometric Drift";
    public override string Description => "Fine lines of hexagons drifting in layers, with light passing across them.";
    public override string DefaultPalette => "Midnight";
    internal override int FieldDivisor => 1;
    internal override float Grain => 0.2f;
    internal override float Vignette => 0.45f;

    // Cell size, line half-width (picture units) and brightness, far to near.
    private static readonly (float Size, float Width, float Light)[] Layers =
    [
        (0.06f, 0.0009f, 0.22f),
        (0.12f, 0.0012f, 0.38f),
        (0.24f, 0.0016f, 0.6f),
    ];

    private const float Root3 = 1.7320508f;

    // The six directions a hexagon grid repeats along (one cell step each).
    private static readonly Vector2[] Steps =
    [
        new(1, 0), new(0.5f, Root3 / 2), new(-0.5f, Root3 / 2), new(-1, 0), new(-0.5f, -Root3 / 2), new(0.5f, -Root3 / 2),
    ];

    internal override MotionFrame Begin(MotionScene scene, LoopClock clock) => new Frame(scene, clock);

    /// <summary>
    /// Where <paramref name="p"/> (in cell units) sits in its hexagon: its offset from the cell's
    /// centre, and its distance in from the cell's edge (0 on the edge, 0.5 at the centre).
    /// </summary>
    private static (Vector2 Local, float EdgeDistance) Hex(Vector2 p)
    {
        var a = new Vector2(Mod(p.X, 1) - 0.5f, Mod(p.Y, Root3) - Root3 / 2);
        var b = new Vector2(Mod(p.X - 0.5f, 1) - 0.5f, Mod(p.Y - Root3 / 2, Root3) - Root3 / 2);
        var local = a.LengthSquared() < b.LengthSquared() ? a : b;
        var ax = MathF.Abs(local.X);
        var ay = MathF.Abs(local.Y);
        var fromCentre = MathF.Max(ax * 0.5f + ay * (Root3 / 2), ax);
        return (local, 0.5f - fromCentre);
    }

    private static float Mod(float a, float m) => a - m * MathF.Floor(a / m);

    private sealed class Frame : MotionFrame
    {
        private readonly MotionScene scene;
        private readonly Vector2 centre;
        private readonly Vector2[] cos = new Vector2[3];
        private readonly Vector2[] drift = new Vector2[3];
        private readonly Vector3[] tint = new Vector3[3];
        private readonly Vector2 spot;
        private readonly float phase;
        private readonly Vector3 dark;
        private readonly Vector3 room;
        private readonly float strength;

        public Frame(MotionScene scene, LoopClock clock)
        {
            this.scene = scene;
            centre = new Vector2(scene.Aspect / 2, 0.5f);
            var cells = Math.Max(1, (int)MathF.Round(scene.Speed * scene.Seconds / 20));
            for (var k = 0; k < Layers.Length; k++)
            {
                var angle = scene.Random(k, 0) * MathF.PI / 3;
                cos[k] = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                // A whole number of cell steps per loop: the grid then lines up with itself again.
                drift[k] = Steps[(int)(scene.Random(k, 1) * 6) % 6] * (cells * (float)clock.Phase);
                tint[k] = scene.Ramp[0.45f + 0.2f * k];
            }
            spot = Orbit(clock, centre, new Vector2(scene.Aspect * 0.32f, 0.22f), 1, 1, scene.Random(5, 0), scene.Random(5, 1));
            phase = (float)clock.Phase;
            dark = scene.Stops[0];
            room = Vector3.Lerp(scene.Stops[0], scene.Stops[1], 0.4f);
            strength = 0.3f + 1.0f * scene.Intensity;
        }

        public override Vector3 Shade(int column, float x, float y)
        {
            var light = Bump(x - spot.X, y - spot.Y, 0.62f);
            var colour = Vector3.Lerp(dark, room, light);
            // The lines show mostly where the light is; far from it they fade almost away.
            var lit = 0.06f + 0.94f * light * light;
            var picture = new Vector2(x, y) - centre;
            for (var k = 0; k < Layers.Length; k++)
            {
                var (size, width, bright) = Layers[k];
                var r = cos[k];
                // Into the layer's cell units: turned, scaled, then moved by the drift.
                var p = new Vector2(picture.X * r.X + picture.Y * r.Y, -picture.X * r.Y + picture.Y * r.X) / size + drift[k];
                var (local, edgeDistance) = Hex(p);
                var d = edgeDistance * size / width;
                var line = MathF.Exp(-d * d) + 0.2f * MathF.Exp(-d * d / 25);

                // Where this cell's centre is on screen (the same place after a whole cell's drift).
                var cell = (p - local - drift[k]) * size;
                var cx = cell.X * r.X - cell.Y * r.Y;
                var cy = cell.X * r.Y + cell.Y * r.X;
                var glow = MathF.Sin(cx * 3.1f + Tau * phase + k) * MathF.Sin(cy * 4.3f - Tau * phase * 2 + k * 1.7f);
                var fill = Smooth(0.5f, 1, glow) * (0.3f + 1.4f * edgeDistance) * 0.1f;

                colour += tint[k] * ((line * lit + fill * (0.3f + 0.7f * light)) * bright * strength);
            }
            return colour;
        }
    }
}
