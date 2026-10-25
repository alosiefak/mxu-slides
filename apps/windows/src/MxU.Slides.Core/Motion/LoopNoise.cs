using System.Runtime.CompilerServices;

namespace MxU.Slides.Core.Motion;

/// <summary>
/// Where a frame sits in its loop, as a point going round a circle: <see cref="Phase"/> runs
/// from 0 to 1 over the loop, and the point is (<see cref="Cos"/>, <see cref="Sin"/>) of
/// 2π·phase. Anything that only depends on that point repeats exactly when the loop starts
/// again, because at phase 1 the point is back where it was at phase 0.
/// </summary>
public readonly struct LoopClock
{
    public LoopClock(double phase)
    {
        Phase = phase - Math.Floor(phase);
        var angle = 2 * Math.PI * Phase;
        Cos = (float)Math.Cos(angle);
        Sin = (float)Math.Sin(angle);
    }

    /// <summary>0 at the start of the loop, just under 1 at its end.</summary>
    public double Phase { get; }

    public float Cos { get; }

    public float Sin { get; }

    /// <summary>A wave that goes round <paramref name="turns"/> whole times per loop (so it loops too), between −1 and 1.</summary>
    public float Wave(int turns, float offset = 0) => MathF.Sin((float)(2 * Math.PI * (Phase * turns + offset)));

    /// <summary>
    /// For things that repeat several times in a loop (a mote's life, a drift that restarts):
    /// how far through its current round it is (0–1) and which round it's on (0 to
    /// <paramref name="rounds"/> − 1). At the end of the loop it is back on round 0, at 0.
    /// </summary>
    public (float Through, int Round) Rounds(int rounds, float offset = 0)
    {
        var at = Phase * rounds + offset;
        var round = (int)Math.Floor(at);
        return ((float)(at - round), ((round % rounds) + rounds) % rounds);
    }
}

/// <summary>
/// The randomness the styles are made of, all of it repeatable: the same inputs give the same
/// numbers every time and on every PC (no <see cref="Random"/>, no clock), so frame 412 of a
/// recipe is the same picture however often, and on however many threads, it's drawn.
/// <list type="bullet">
/// <item><see cref="Hash(uint)"/> and <see cref="Unit(uint, int, int)"/>: scrambled whole numbers,
/// for choosing where each star or speck of dust goes.</item>
/// <item><see cref="Simplex"/>: Ken Perlin's simplex noise in four dimensions, as described by
/// Stefan Gustavson ("Simplex noise demystified", 2005, public domain). Noise is randomness
/// that changes smoothly: nearby points get nearby values. Smoke, aurora curtains and liquid are
/// all noise, shaped.</item>
/// </list>
/// <para>
/// <b>Why four dimensions?</b> Two are the picture (x, y). The other two are time, but not as
/// a line: time goes round a circle (<see cref="LoopClock"/>). Sampling noise at
/// (x, y, r·cos 2πt, r·sin 2πt) gives a picture that changes smoothly as t goes from 0 to 1 and
/// is back exactly where it started at t = 1. That's what makes a loop seamless. The circle's
/// radius r says how much it changes over the loop: a bigger circle is a faster flow.
/// </para>
/// </summary>
public static class LoopNoise
{
    // MARK: Hashing

    /// <summary>A well-mixed 32-bit hash (Chris Wellons' "lowbias32"): change one bit of the input and about half the output's bits change.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Hash(uint a, uint b) => Hash(a ^ Hash(b + 0x9E3779B9u));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Hash(uint a, uint b, uint c) => Hash(a ^ Hash(b ^ Hash(c + 0x9E3779B9u)));

    /// <summary>A repeatable "random" number from 0 up to (not including) 1: the <paramref name="channel"/>th property of thing <paramref name="index"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Unit(uint seed, int index, int channel) => ToUnit(Hash(seed, (uint)index, (uint)channel));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ToUnit(uint hash) => (hash >> 8) * (1f / 16_777_216f);

    // MARK: Simplex noise

    // Gustavson's permutation table, doubled so lookups never need wrapping. It's a fixed shuffle
    // of 0–255; a recipe's own variation comes from where it samples, not from a different table.
    private static readonly byte[] Perm = BuildPermutation();

    // The 32 directions a corner's slope can point in: every way of picking three of x, y, z, w as ±1.
    private static readonly sbyte[] Gradients =
    [
        0, 1, 1, 1, 0, 1, 1, -1, 0, 1, -1, 1, 0, 1, -1, -1,
        0, -1, 1, 1, 0, -1, 1, -1, 0, -1, -1, 1, 0, -1, -1, -1,
        1, 0, 1, 1, 1, 0, 1, -1, 1, 0, -1, 1, 1, 0, -1, -1,
        -1, 0, 1, 1, -1, 0, 1, -1, -1, 0, -1, 1, -1, 0, -1, -1,
        1, 1, 0, 1, 1, 1, 0, -1, 1, -1, 0, 1, 1, -1, 0, -1,
        -1, 1, 0, 1, -1, 1, 0, -1, -1, -1, 0, 1, -1, -1, 0, -1,
        1, 1, 1, 0, 1, 1, -1, 0, 1, -1, 1, 0, 1, -1, -1, 0,
        -1, 1, 1, 0, -1, 1, -1, 0, -1, -1, 1, 0, -1, -1, -1, 0,
    ];

    // Gustavson uses 0.6 for each corner's reach; in four dimensions that lets a corner's
    // influence end slightly outside its simplex, which shows as faint creases in a smooth
    // gradient. 0.5 ends exactly at the edge, so the noise is smooth everywhere. The scale
    // brings the result back to about −1…1 (measured: SimplexStaysInRange).
    private const float Reach = 0.5f;
    private const float Scale = 62f;

    private static byte[] BuildPermutation()
    {
        var values = new byte[256];
        for (var i = 0; i < 256; i++) values[i] = (byte)i;
        // A Fisher–Yates shuffle driven by the hash above, so it's the same table everywhere.
        for (var i = 255; i > 0; i--)
        {
            var j = (int)(Hash(0x5EEDu, (uint)i) % (uint)(i + 1));
            (values[i], values[j]) = (values[j], values[i]);
        }
        var doubled = new byte[512];
        for (var i = 0; i < 512; i++) doubled[i] = values[i & 255];
        return doubled;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Floor(float v)
    {
        var i = (int)v;
        return v < i ? i - 1 : i;
    }

    /// <summary>Smooth noise at a point in four dimensions, between about −1 and 1.</summary>
    public static float Simplex(float x, float y, float z, float w)
    {
        // Skew the grid so its cells become simplices (the 4-D cousin of triangles), find the cell.
        const float F4 = 0.309016994f; // (√5 − 1) / 4
        const float G4 = 0.138196601f; // (5 − √5) / 20
        var s = (x + y + z + w) * F4;
        int i = Floor(x + s), j = Floor(y + s), k = Floor(z + s), l = Floor(w + s);
        var t = (i + j + k + l) * G4;
        float x0 = x - (i - t), y0 = y - (j - t), z0 = z - (k - t), w0 = w - (l - t);

        // Which of the 24 simplices in the cell: rank the coordinates by size.
        int rx = 0, ry = 0, rz = 0, rw = 0;
        if (x0 > y0) rx++; else ry++;
        if (x0 > z0) rx++; else rz++;
        if (x0 > w0) rx++; else rw++;
        if (y0 > z0) ry++; else rz++;
        if (y0 > w0) ry++; else rw++;
        if (z0 > w0) rz++; else rw++;
        int i1 = rx >= 3 ? 1 : 0, j1 = ry >= 3 ? 1 : 0, k1 = rz >= 3 ? 1 : 0, l1 = rw >= 3 ? 1 : 0;
        int i2 = rx >= 2 ? 1 : 0, j2 = ry >= 2 ? 1 : 0, k2 = rz >= 2 ? 1 : 0, l2 = rw >= 2 ? 1 : 0;
        int i3 = rx >= 1 ? 1 : 0, j3 = ry >= 1 ? 1 : 0, k3 = rz >= 1 ? 1 : 0, l3 = rw >= 1 ? 1 : 0;

        // The point relative to each of the simplex's five corners.
        float x1 = x0 - i1 + G4, y1 = y0 - j1 + G4, z1 = z0 - k1 + G4, w1 = w0 - l1 + G4;
        float x2 = x0 - i2 + 2 * G4, y2 = y0 - j2 + 2 * G4, z2 = z0 - k2 + 2 * G4, w2 = w0 - l2 + 2 * G4;
        float x3 = x0 - i3 + 3 * G4, y3 = y0 - j3 + 3 * G4, z3 = z0 - k3 + 3 * G4, w3 = w0 - l3 + 3 * G4;
        float x4 = x0 - 1 + 4 * G4, y4 = y0 - 1 + 4 * G4, z4 = z0 - 1 + 4 * G4, w4 = w0 - 1 + 4 * G4;

        int ii = i & 255, jj = j & 255, kk = k & 255, ll = l & 255;
        var p = Perm;
        var n = Corner(x0, y0, z0, w0, p[ii + p[jj + p[kk + p[ll]]]])
            + Corner(x1, y1, z1, w1, p[ii + i1 + p[jj + j1 + p[kk + k1 + p[ll + l1]]]])
            + Corner(x2, y2, z2, w2, p[ii + i2 + p[jj + j2 + p[kk + k2 + p[ll + l2]]]])
            + Corner(x3, y3, z3, w3, p[ii + i3 + p[jj + j3 + p[kk + k3 + p[ll + l3]]]])
            + Corner(x4, y4, z4, w4, p[ii + 1 + p[jj + 1 + p[kk + 1 + p[ll + 1]]]]);
        return n * Scale;
    }

    /// <summary>One corner's share: its slope dotted with the offset, faded out smoothly by distance.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Corner(float x, float y, float z, float w, int hash)
    {
        var t = Reach - x * x - y * y - z * z - w * w;
        if (t <= 0) return 0;
        t *= t;
        var g = (hash & 31) * 4;
        var gradients = Gradients;
        return t * t * (gradients[g] * x + gradients[g + 1] * y + gradients[g + 2] * z + gradients[g + 3] * w);
    }

    // MARK: Noise in a loop

    // Raw simplex noise spreads its values thinly (most within ±0.5). The loop noise below is
    // scaled up, and keeps the same spread whatever the number of layers: centred on 0, typically
    // within ±0.25 to ±0.4, nearly all within ±0.8. How wide exactly depends a little on the lane:
    // a small circle of time sits closer to or further from the noise's grid, and the noise is
    // calmer near its grid lines (measured: NoiseIsSpreadAsDocumented).
    private const float Spread = 1.4f;

    /// <summary>
    /// Noise at picture point (x, y) on <paramref name="clock"/>'s circle of time, of radius
    /// <paramref name="radius"/>. <paramref name="lane"/> moves the circle somewhere else in the
    /// noise, so two layers (or two recipes' variations) don't move in step. Centred on 0, nearly
    /// always within ±0.8.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Loop(float x, float y, in LoopClock clock, float radius, float lane = 0) =>
        Spread * Simplex(x, y, lane + radius * clock.Cos, lane * 0.618f + radius * clock.Sin);

    /// <summary>
    /// Fractal noise ("fBm"): a few layers of loop noise, each twice as fine and half as strong
    /// as the one before, turned a little so their grids don't line up. Large shapes from the
    /// first layer, detail from the rest. Finer layers also travel further round the circle
    /// of time, so fine detail churns faster than the big shapes, as real smoke does. Spread like
    /// <see cref="Loop"/>: centred on 0, nearly always within ±0.8.
    /// </summary>
    public static float Fbm(float x, float y, in LoopClock clock, float radius, int octaves, float lane = 0, float gain = 0.5f)
    {
        float sum = 0, amplitude = 0.5f, total = 0;
        var frequency = 1f;
        for (var o = 0; o < octaves; o++)
        {
            sum += amplitude * Loop(x, y, clock, radius * frequency, lane + o * 7.31f);
            // Independent layers add up like the sides of a right-angled triangle, so dividing by
            // the square root of the summed squares keeps the spread the same for any count.
            total += amplitude * amplitude;
            // Turn by about 37° and scale up, with an offset so each layer's origin is somewhere else.
            var nx = 1.6f * x - 1.2f * y + 3.1f;
            var ny = 1.2f * x + 1.6f * y - 1.7f;
            x = nx;
            y = ny;
            frequency *= 2f;
            amplitude *= gain;
        }
        return sum / MathF.Sqrt(total);
    }

    /// <summary>
    /// For styles that drift one way (smoke rising, clouds passing): two copies of the <i>same</i>
    /// pattern, half a drift apart, slide along and take turns. Each fades out before it jumps
    /// back to its start, so the drift seems endless but repeats exactly <paramref name="rounds"/>
    /// times per loop (the "flow map" trick from water shaders). Returns each copy's offset along
    /// the drift (−½ to ½ of the drift length) and its weight; the weights add up to 1, so mix the
    /// two looks up with them. Keep a round's drift small next to the pattern's shapes, and the
    /// hand-over can't be seen.
    /// </summary>
    public static (float OffsetA, float WeightA, float OffsetB, float WeightB) Drift(in LoopClock clock, int rounds)
    {
        var (a, _) = clock.Rounds(rounds);
        var (b, _) = clock.Rounds(rounds, 0.5f);
        return (a - 0.5f, 1 - MathF.Abs(2 * a - 1), b - 0.5f, 1 - MathF.Abs(2 * b - 1));
    }
}
