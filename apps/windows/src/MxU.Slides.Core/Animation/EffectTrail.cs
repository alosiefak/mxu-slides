namespace MxU.Slides.Core.Animation;

/// <summary>
/// The memory behind the Echo and Ghost Trails effects: what an item looked like over the
/// last moments, fading away. Port of the history half of the Mac's Compositor
/// (<c>effectHistory</c>) and its <c>compositor_echo</c> and <c>compositor_ghost_trails</c>
/// shaders, done on the processor because WPF can't hand a shader the previous frame.
/// <para>
/// Each frame the renderer gives <see cref="Advance"/> a small picture of the item as it
/// looks now (<i>fresh</i>) and gets back the <i>trail</i>: what to combine the live picture
/// with. Echo shows the brighter of the two (<c>max(fresh, trail)</c>), so anything that moves
/// or changes inside the item leaves a fading copy. Ghost Trails mixes them
/// (<c>lerp(fresh, trail, keep)</c>) after pushing the trail around with slow noise, so the
/// item smokes and drifts. How much of the trail is kept, <see cref="Keep"/>, drops to a tenth
/// after <c>fade</c> seconds, however fast the frames come.
/// </para>
/// <para>
/// Pictures are premultiplied BGRA, one <see cref="uint"/> per pixel (blue in the low byte),
/// as WPF's <c>Pbgra32</c> bitmaps hold them. The history follows the item, not the screen:
/// moving the item doesn't smear it, exactly as on the Mac (its effects run before the motion).
/// </para>
/// </summary>
public sealed class EffectTrail
{
    /// <summary>A history older than this is stale (the item was off screen): start again. The Mac's Compositor.historyGap.</summary>
    public const double HistoryGap = 0.5;

    // The history, four channels a pixel (blue, green, red, alpha, premultiplied, 0-255), kept as
    // fractions rather than whole steps. The Mac keeps it on the graphics card in floating point. With
    // whole steps, mixing every frame either rounded a step away each time (eating the trail on a PC
    // that draws often) or never faded the faintest values (leaving a haze). Only the trail handed back
    // is rounded.
    private float[] history = [];
    private float[] moved = [];
    private double lastTime = double.NaN;

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>When the trail last took a frame (host-time seconds), or NaN before the first.</summary>
    public double LastTime => lastTime;

    /// <summary>The share of the trail kept after <paramref name="elapsed"/> seconds: a tenth after <paramref name="fade"/> seconds.</summary>
    public static double Keep(double elapsed, double fade) => Math.Min(Math.Pow(0.1, elapsed / Math.Max(fade, 0.01)), 0.999);

    /// <summary>Forgets the history (the next frame starts a new trail).</summary>
    public void Reset()
    {
        history = [];
        moved = [];
        Width = Height = 0;
        lastTime = double.NaN;
    }

    /// <summary>
    /// Takes this frame's picture and writes the trail into <paramref name="trail"/> (same size).
    /// Returns how much of the trail to keep for this frame, or null when there was no history
    /// to draw (the first frame, a new size, or a long gap): show the fresh picture as it is.
    /// </summary>
    /// <param name="effect">An <see cref="AnimationEffect.Echo"/> or <see cref="AnimationEffect.GhostTrails"/>.</param>
    /// <param name="pixelsPerUnit">How many of these pictures' pixels one canvas unit covers (for the ghost's drift and noise size).</param>
    public double? Advance(ReadOnlySpan<uint> fresh, int width, int height, double now, AnimationEffect effect, double pixelsPerUnit, Span<uint> trail)
    {
        var count = width * height;
        if (width <= 0 || height <= 0 || fresh.Length < count || trail.Length < count)
            throw new ArgumentException("The pictures don't match the size given.");
        var (fade, ghost) = effect switch
        {
            AnimationEffect.Echo echo => (echo.Fade, (AnimationEffect.GhostTrails?)null),
            AnimationEffect.GhostTrails g => (g.Fade, g),
            _ => throw new ArgumentException("Only Echo and Ghost Trails keep a trail.", nameof(effect)),
        };
        var elapsed = now - lastTime;
        var usable = width == Width && height == Height && history.Length == count * 4 && elapsed is >= 0 and <= HistoryGap;
        lastTime = now;
        if (!usable)
        {
            // Start a new trail from this frame; there is nothing to draw behind it yet.
            Width = width;
            Height = height;
            history = new float[count * 4];
            moved = new float[count * 4];
            for (var i = 0; i < count; i++) Unpack(fresh[i], history, i * 4);
            trail[..count].Clear();
            return null;
        }
        var keep = Keep(Math.Max(elapsed, 1.0 / 240), fade);
        var k = (float)keep;
        if (ghost is null)
        {
            // compositor_echo: trail = history x keep; history = max(fresh, trail).
            for (var i = 0; i < count; i++)
            {
                var o = i * 4;
                var f = fresh[i];
                for (var c = 0; c < 4; c++)
                {
                    var t = history[o + c] * k;
                    moved[o + c] = t;
                    history[o + c] = Math.Max((f >> (c * 8)) & 0xFF, t);
                }
                trail[i] = Pack(moved, o);
            }
        }
        else
        {
            // compositor_ghost_trails: the history sampled where the noise pushes it, then mixed with fresh.
            // The Mac pushes it once per 60 Hz frame; here the push is scaled by the time since the last
            // frame, so the smoke drifts at the same speed however often the trail is advanced.
            var share = Math.Clamp(elapsed * 60, 0.05, 4);
            Displace(width, height, now, ghost, pixelsPerUnit, share);
            for (var i = 0; i < count; i++)
            {
                var o = i * 4;
                trail[i] = Pack(moved, o);
                var f = fresh[i];
                for (var c = 0; c < 4; c++)
                    history[o + c] = ((f >> (c * 8)) & 0xFF) * (1 - k) + moved[o + c] * k;
            }
        }
        return keep;
    }

    // Samples the history at each pixel moved by the warp noise (2 octaves, as the Mac's warp_noise),
    // with bilinear filtering and edge clamping, into `moved`. The offsets come from a coarse grid,
    // smoothly filled in. driftShare scales only how far it moves this frame (the noise's size stays put).
    private void Displace(int width, int height, double now, AnimationEffect.GhostTrails ghost, double pixelsPerUnit, double driftShare)
    {
        var drift = ghost.Drift * pixelsPerUnit * driftShare;
        var cell = Math.Max(ghost.Scale * pixelsPerUnit, 1);
        var z = now * ghost.Speed;
        var step = Math.Clamp((int)(cell / 4), 1, 8);
        var gw = width / step + 2;
        var gh = height / step + 2;
        var ox = new float[gw * gh];
        var oy = new float[gw * gh];
        for (var gy = 0; gy < gh; gy++)
        {
            for (var gx = 0; gx < gw; gx++)
            {
                var (nx, ny) = NoiseOffset((gx * step + 0.5) / cell, (gy * step + 0.5) / cell, z);
                ox[gy * gw + gx] = (float)(nx * 2 * drift);
                oy[gy * gw + gx] = (float)(ny * 2 * drift);
            }
        }
        for (var y = 0; y < height; y++)
        {
            var gy = y / step;
            var fy = (float)(y - gy * step) / step;
            for (var x = 0; x < width; x++)
            {
                var gx = x / step;
                var fx = (float)(x - gx * step) / step;
                var i00 = gy * gw + gx;
                var dx = Bilerp(ox[i00], ox[i00 + 1], ox[i00 + gw], ox[i00 + gw + 1], fx, fy);
                var dy = Bilerp(oy[i00], oy[i00 + 1], oy[i00 + gw], oy[i00 + gw + 1], fx, fy);
                Sample(x + dx, y + dy, width, height, (y * width + x) * 4);
            }
        }
    }

    private static float Bilerp(float a, float b, float c, float d, float fx, float fy) =>
        (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fy;

    /// <summary>The noise push at a point (in noise cells), each axis in -0.5..0.5. Port of the ghost shader's nx/ny.</summary>
    public static (double X, double Y) NoiseOffset(double px, double py, double z)
    {
        var nx = Noise(px, py, z) * 0.7 + Noise(px * 2 + 17, py * 2 + 17, z * 2 + 17) * 0.3 - 0.5;
        var ny = Noise(px + 41.7, py + 13.3, z + 7.9) * 0.7 + Noise(px * 2 + 29.1, py * 2 + 5.2, z * 2 + 3.3) * 0.3 - 0.5;
        return (nx, ny);
    }

    // Bilinear sample of the history at a pixel position, clamped to the edges, into moved[into..into+3].
    private void Sample(double x, double y, int width, int height, int into)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        var x0 = (int)x;
        var y0 = (int)y;
        var x1 = Math.Min(x0 + 1, width - 1);
        var y1 = Math.Min(y0 + 1, height - 1);
        var wx = (float)(x - x0);
        var wy = (float)(y - y0);
        int a = (y0 * width + x0) * 4, b = (y0 * width + x1) * 4, c = (y1 * width + x0) * 4, d = (y1 * width + x1) * 4;
        for (var ch = 0; ch < 4; ch++)
        {
            var top = history[a + ch] + (history[b + ch] - history[a + ch]) * wx;
            var bottom = history[c + ch] + (history[d + ch] - history[c + ch]) * wx;
            moved[into + ch] = top + (bottom - top) * wy;
        }
    }

    private static void Unpack(uint pixel, float[] into, int at)
    {
        for (var c = 0; c < 4; c++) into[at + c] = (pixel >> (c * 8)) & 0xFF;
    }

    // Four channels back into one premultiplied BGRA pixel, each rounded to the nearest step.
    private static uint Pack(float[] channels, int at)
    {
        uint pixel = 0;
        for (var c = 0; c < 4; c++) pixel |= (uint)Math.Clamp((int)(channels[at + c] + 0.5f), 0, 255) << (c * 8);
        return pixel;
    }

    // The Mac's warp_hash and warp_noise (value noise with a smoothstep blend).
    private static double Hash(double x, double y, double z)
    {
        x = Fract(x * 0.1031);
        y = Fract(y * 0.1030);
        z = Fract(z * 0.0973);
        var d = x * (y + 33.33) + y * (x + 33.33) + z * (z + 33.33);
        x += d;
        y += d;
        z += d;
        return Fract((x + y) * z);
    }

    private static double Noise(double x, double y, double z)
    {
        double ix = Math.Floor(x), iy = Math.Floor(y), iz = Math.Floor(z);
        double fx = x - ix, fy = y - iy, fz = z - iz;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        fz = fz * fz * (3 - 2 * fz);
        double L(double a, double b, double t) => a + (b - a) * t;
        var x00 = L(Hash(ix, iy, iz), Hash(ix + 1, iy, iz), fx);
        var x10 = L(Hash(ix, iy + 1, iz), Hash(ix + 1, iy + 1, iz), fx);
        var x01 = L(Hash(ix, iy, iz + 1), Hash(ix + 1, iy, iz + 1), fx);
        var x11 = L(Hash(ix, iy + 1, iz + 1), Hash(ix + 1, iy + 1, iz + 1), fx);
        return L(L(x00, x10, fy), L(x01, x11, fy), fz);
    }

    private static double Fract(double v) => v - Math.Floor(v);
}
