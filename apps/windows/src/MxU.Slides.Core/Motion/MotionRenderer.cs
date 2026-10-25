using System.Numerics;
using System.Runtime.CompilerServices;

namespace MxU.Slides.Core.Motion;

/// <summary>The pixel layouts <see cref="MotionRenderer"/> writes.</summary>
public enum MotionPixelFormat
{
    /// <summary>4 bytes a pixel, blue-green-red-alpha, top row first: for previews and pictures.</summary>
    Bgra32,

    /// <summary>
    /// The layout H.264 encoders take: a full-size plane of brightness (Y), then a half-size
    /// plane of colour (U and V interleaved) shared by each 2 × 2 block, in BT.709 video range.
    /// </summary>
    Nv12,
}

/// <summary>
/// Draws the frames of a motion background, one at a time, from its recipe. Each frame goes
/// through the same steps:
/// <list type="number">
/// <item><b>The smooth part.</b> The style says what colour each point is. For soft styles it's
/// asked on a coarse grid (<see cref="MotionStyle.FieldDivisor"/>), which is then enlarged with a
/// cubic B-spline: the smoothest way to enlarge, so no grid or ripple shows.</item>
/// <item><b>Sprites</b> (stars, dust, out-of-focus lights) are added at full sharpness.</item>
/// <item><b>Finishing:</b> the corners darkened, very bright light rolled off gently instead of
/// clipping, film grain, then the light turned into sRGB values.</item>
/// <item><b>Pixels</b>, as BGRA or straight into the encoder's NV12, with a little random
/// "dither" so a slow gradient doesn't break into visible bands.</item>
/// </list>
/// Steps 2–4 run in bands of 16 rows, so even 4K needs only a few small scratch buffers. Every
/// step is shared across <see cref="RenderCrew"/>'s threads, and each part of the picture is
/// written by exactly one of them, so the result doesn't depend on how the work was split.
/// </summary>
public sealed class MotionRenderer
{
    private const int BandRows = 16;
    // Field samples beyond each edge, so the enlarging filter always has neighbours to read.
    private const int Margin = 2;

    private readonly MotionStyle style;
    private readonly RenderCrew crew;
    private readonly int divisor;
    private readonly int fieldWidth;
    private readonly int fieldHeight;
    private readonly float[] fieldX;
    private readonly float[] fieldY;
    private readonly Vector3[] field;
    private readonly int[] tapX;
    private readonly Vector4[] weightX;
    private readonly int[] tapY;
    private readonly Vector4[] weightY;
    private readonly float[] pixelX;
    private readonly float[] vignetteX;
    private readonly float[] vignetteY;
    private readonly Vector3[][] bands;
    private readonly Vector3[][] rowScratch;
    private readonly float grain;
    private readonly float vignette;

    /// <param name="width">Picture width (made even, as NV12 needs).</param>
    /// <param name="height">Picture height (made even).</param>
    public MotionRenderer(MotionRecipe recipe, int width, int height, RenderCrew crew)
    {
        Recipe = recipe.Normalized();
        style = MotionStyles.Find(Recipe.Style)!;
        this.crew = crew;
        Width = Math.Max(2, width & ~1);
        Height = Math.Max(2, height & ~1);
        FrameCount = Recipe.FrameCount;

        // A tiny preview doesn't need a coarse grid: keep at least ~60 rows of detail.
        divisor = Math.Max(1, Math.Min(style.FieldDivisor, Height / 60));
        pixelX = new float[Width];
        for (var x = 0; x < Width; x++) pixelX[x] = (x + 0.5f) / Height;
        if (divisor > 1)
        {
            fieldWidth = (Width + divisor - 1) / divisor + 2 * Margin;
            fieldHeight = (Height + divisor - 1) / divisor + 2 * Margin;
            fieldX = new float[fieldWidth];
            fieldY = new float[fieldHeight];
            for (var i = 0; i < fieldWidth; i++) fieldX[i] = (i - Margin + 0.5f) * divisor / Height;
            for (var j = 0; j < fieldHeight; j++) fieldY[j] = (j - Margin + 0.5f) * divisor / Height;
            field = new Vector3[fieldWidth * fieldHeight];
            (tapX, weightX) = Taps(Width);
            (tapY, weightY) = Taps(Height);
        }
        else
        {
            fieldX = pixelX;
            fieldY = [];
            field = [];
            tapX = tapY = [];
            weightX = weightY = [];
        }
        Scene = new MotionScene(Recipe, style, Width, Height, fieldX);

        vignetteX = new float[Width];
        vignetteY = new float[Height];
        for (var x = 0; x < Width; x++) vignetteX[x] = Square((x + 0.5f) / Width * 2 - 1);
        for (var y = 0; y < Height; y++) vignetteY[y] = Square((y + 0.5f) / Height * 2 - 1);
        grain = style.Grain * 0.05f;
        vignette = style.Vignette;

        bands = new Vector3[crew.Size][];
        rowScratch = new Vector3[crew.Size][];
        for (var i = 0; i < crew.Size; i++)
        {
            bands[i] = new Vector3[Width * BandRows];
            rowScratch[i] = new Vector3[Math.Max(1, fieldWidth)];
        }
    }

    /// <summary>The recipe, with everything in range.</summary>
    public MotionRecipe Recipe { get; }

    public MotionScene Scene { get; }

    public int Width { get; }

    public int Height { get; }

    public int FrameCount { get; }

    /// <summary>Bytes one frame takes in <paramref name="format"/>.</summary>
    public int FrameBytes(MotionPixelFormat format) => format == MotionPixelFormat.Bgra32 ? Width * Height * 4 : Width * Height * 3 / 2;

    /// <summary>Draws frame <paramref name="frame"/> of the loop (any number: frame <see cref="FrameCount"/> is frame 0 again).</summary>
    public void RenderFrame(long frame, MotionPixelFormat format, byte[] destination) =>
        Render(LoopTiming.Phase(frame, FrameCount), frame, format, destination);

    /// <summary>
    /// Draws the loop at <paramref name="phase"/> (0–1), for the live preview, which runs on the
    /// clock rather than frame by frame. <paramref name="grainFrame"/> picks the film grain's pattern.
    /// </summary>
    public void Render(double phase, long grainFrame, MotionPixelFormat format, byte[] destination)
    {
        if (destination.Length < FrameBytes(format)) throw new ArgumentException("The buffer is smaller than a frame", nameof(destination));
        var clock = new LoopClock(phase);
        var frame = style.Begin(Scene, clock);
        if (divisor > 1)
            crew.For(fieldHeight, (row, _) => frame.ShadeRow(fieldY[row], fieldX, field.AsSpan(row * fieldWidth, fieldWidth)));
        var grainSeed = LoopNoise.Hash((uint)(((grainFrame % FrameCount) + FrameCount) % FrameCount), Scene.Seed);
        var bandCount = (Height + BandRows - 1) / BandRows;
        crew.For(bandCount, (band, worker) => Band(frame, band, worker, grainSeed, format, destination));
    }

    // MARK: One band of rows

    private void Band(MotionFrame frame, int band, int worker, uint grainSeed, MotionPixelFormat format, byte[] destination)
    {
        var top = band * BandRows;
        var rows = Math.Min(BandRows, Height - top);
        var buffer = bands[worker];
        for (var r = 0; r < rows; r++)
        {
            var row = buffer.AsSpan(r * Width, Width);
            if (divisor > 1) Enlarge(top + r, row, rowScratch[worker]);
            else frame.ShadeRow((top + r + 0.5f) / Height, pixelX, row);
        }
        foreach (var sprite in frame.Sprites) Splat(sprite, buffer, top, rows);
        Finish(buffer, top, rows, grainSeed);
        if (format == MotionPixelFormat.Bgra32) PackBgra(buffer, top, rows, grainSeed, destination);
        else PackNv12(buffer, top, rows, grainSeed, destination);
    }

    // MARK: Enlarging the smooth part

    /// <summary>Which four field samples each picture row (or column) reads, and how much of each: a cubic B-spline.</summary>
    private (int[] Taps, Vector4[] Weights) Taps(int pixels)
    {
        var taps = new int[pixels];
        var weights = new Vector4[pixels];
        for (var p = 0; p < pixels; p++)
        {
            var at = (p + 0.5f) / divisor - 0.5f + Margin;
            var whole = (int)MathF.Floor(at);
            var t = at - whole;
            var u = 1 - t;
            taps[p] = whole - 1;
            weights[p] = new Vector4(u * u * u, 3 * t * t * t - 6 * t * t + 4, -3 * t * t * t + 3 * t * t + 3 * t + 1, t * t * t) / 6;
        }
        return (taps, weights);
    }

    private void Enlarge(int y, Span<Vector3> row, Vector3[] scratch)
    {
        // Down first (four field rows into one), then across.
        var w = weightY[y];
        var r0 = tapY[y] * fieldWidth;
        var f = field;
        for (var i = 0; i < fieldWidth; i++)
            scratch[i] = f[r0 + i] * w.X + f[r0 + fieldWidth + i] * w.Y + f[r0 + 2 * fieldWidth + i] * w.Z + f[r0 + 3 * fieldWidth + i] * w.W;
        for (var x = 0; x < row.Length; x++)
        {
            var t = tapX[x];
            var wx = weightX[x];
            row[x] = scratch[t] * wx.X + scratch[t + 1] * wx.Y + scratch[t + 2] * wx.Z + scratch[t + 3] * wx.W;
        }
    }

    // MARK: Sprites

    private void Splat(in Sprite sprite, Vector3[] buffer, int top, int rows)
    {
        var cx = sprite.Center.X * Height - 0.5f;
        var cy = sprite.Center.Y * Height - 0.5f;
        var radius = sprite.Radius * Height;
        var light = sprite.Light;
        // Smaller than a pixel or two, a dot would flicker as it crosses pixels. Draw it at a
        // size that can't, with its brightness lowered to keep the same total light.
        var smallest = sprite.Shape == SpriteShape.Disc ? 1.5f : 2.2f;
        if (radius < smallest)
        {
            light *= radius * radius / (smallest * smallest);
            radius = smallest;
        }
        var y0 = Math.Max(top, (int)MathF.Floor(cy - radius));
        var y1 = Math.Min(top + rows - 1, (int)MathF.Ceiling(cy + radius));
        if (y0 > y1) return;
        var x0 = Math.Max(0, (int)MathF.Floor(cx - radius));
        var x1 = Math.Min(Width - 1, (int)MathF.Ceiling(cx + radius));
        if (x0 > x1) return;
        var inverse = 1 / (radius * radius);
        // A disc's edge is at least a pixel and a bit wide, so it's smooth however small the disc.
        var soft = Math.Clamp(Math.Max(sprite.Softness, 1.3f / radius), 0.02f, 1);
        // A star's core: about half a pixel across at the least, or an eighth of its glow.
        var core = 1 / Square(Math.Max(0.55f, radius * 0.12f));
        for (var y = y0; y <= y1; y++)
        {
            var dy = y - cy;
            var rowStart = (y - top) * Width;
            for (var x = x0; x <= x1; x++)
            {
                var dx = x - cx;
                var d2 = (dx * dx + dy * dy) * inverse;
                if (d2 >= 1) continue;
                float value;
                switch (sprite.Shape)
                {
                    case SpriteShape.Disc:
                    {
                        var d = MathF.Sqrt(d2);
                        var body = 1 - MotionColor.Smoothstep(1 - soft, 1, d);
                        var ring = sprite.Rim * MathF.Exp(-Square((d - (1 - soft * 0.7f)) / (soft * 0.45f + 0.04f)));
                        value = body * (0.82f + 0.18f * (1 - d2) + ring);
                        break;
                    }
                    case SpriteShape.Star:
                        value = MathF.Exp(-(dx * dx + dy * dy) * core * 0.5f) + 0.07f * Glow(d2);
                        break;
                    default:
                        value = Glow(d2);
                        break;
                }
                buffer[rowStart + x] += light * value;
            }
        }
    }

    /// <summary>A Gaussian bump that reaches exactly 0 at the edge (d² = 1), so a glow has no visible rim.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Glow(float d2) => (MathF.Exp(-4.5f * d2) - 0.011109f) * 1.011234f;

    // MARK: Finishing

    private static readonly float[] SrgbTable = BuildSrgbTable();

    private static float[] BuildSrgbTable()
    {
        var table = new float[4097];
        for (var i = 0; i < table.Length; i++) table[i] = MotionColor.ToSrgb(i / 4096f);
        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Srgb(float linear)
    {
        var at = Math.Clamp(linear, 0, 1) * 4096;
        var i = Math.Min((int)at, 4095);
        return SrgbTable[i] + (SrgbTable[i + 1] - SrgbTable[i]) * (at - i);
    }

    /// <summary>
    /// Light brighter than the screen can show is rolled off smoothly towards white instead of
    /// being cut, the way film handles a bright window: no flat blown-out patches.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Shoulder(float x) => x <= 0.8f ? x : 0.8f + 0.2f * (1 - MathF.Exp((0.8f - x) * 5));

    /// <summary>Vignette, highlight roll-off, conversion to sRGB (0–1) and film grain, in place.</summary>
    private void Finish(Vector3[] buffer, int top, int rows, uint grainSeed)
    {
        for (var r = 0; r < rows; r++)
        {
            var y = top + r;
            var vy = vignetteY[y];
            var start = r * Width;
            for (var x = 0; x < Width; x++)
            {
                var c = buffer[start + x];
                // 0 in the middle, 1 halfway to an edge's middle, 2 in the corners.
                var shade = 1 - vignette * MotionColor.Smoothstep(0.15f, 2.1f, vignetteX[x] + vy);
                var s = new Vector3(Srgb(Shoulder(c.X * shade)), Srgb(Shoulder(c.Y * shade)), Srgb(Shoulder(c.Z * shade)));
                if (grain > 0)
                {
                    // Film grain shows most in the mid-tones, least in deep shadow and highlight.
                    var luma = 0.2126f * s.X + 0.7152f * s.Y + 0.0722f * s.Z;
                    s += new Vector3(grain * Grain(x, y, grainSeed) * (0.3f + 2.8f * luma * (1 - luma)));
                }
                buffer[start + x] = s;
            }
        }
    }

    /// <summary>
    /// One frame's film grain at a pixel: fine random speckle plus a slightly coarser, softer
    /// clump (random values on a 2-pixel grid, blended), which reads as film rather than as
    /// digital noise. A different pattern every frame, but the same pattern for the same frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Grain(int x, int y, uint seed)
    {
        var fine = LoopNoise.ToUnit(LoopNoise.Hash((uint)x, (uint)y, seed)) - 0.5f;
        int cx = x >> 1, cy = y >> 1;
        var fx = (x & 1) * 0.5f + 0.25f;
        var fy = (y & 1) * 0.5f + 0.25f;
        var cell = seed ^ 0xC1u;
        var a = LoopNoise.ToUnit(LoopNoise.Hash((uint)cx, (uint)cy, cell));
        var b = LoopNoise.ToUnit(LoopNoise.Hash((uint)cx + 1, (uint)cy, cell));
        var c = LoopNoise.ToUnit(LoopNoise.Hash((uint)cx, (uint)cy + 1, cell));
        var d = LoopNoise.ToUnit(LoopNoise.Hash((uint)cx + 1, (uint)cy + 1, cell));
        var clump = a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy - 0.5f;
        return 0.6f * fine + 1.1f * clump;
    }

    /// <summary>
    /// Random ±1 step (triangular), added before rounding to whole values. Without it a slow
    /// gradient across 1920 pixels with only 20 shades to use shows as 20 stripes; with it, the
    /// stripes melt into an even grain too fine to see.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Dither(uint hash) => ((hash & 0xFF) + ((hash >> 8) & 0xFF)) * (1f / 255f) - 1f;

    // MARK: Pixels

    private void PackBgra(Vector3[] buffer, int top, int rows, uint seed, byte[] destination)
    {
        var ditherSeed = seed ^ 0xD17u;
        for (var r = 0; r < rows; r++)
        {
            var y = top + r;
            var output = y * Width * 4;
            var start = r * Width;
            for (var x = 0; x < Width; x++)
            {
                var s = buffer[start + x] * 255;
                var dither = Dither(LoopNoise.Hash((uint)x, (uint)y, ditherSeed)) + 0.5f;
                destination[output++] = ToByte(s.Z + dither);
                destination[output++] = ToByte(s.Y + dither);
                destination[output++] = ToByte(s.X + dither);
                destination[output++] = 255;
            }
        }
    }

    /// <summary>
    /// BT.709 video range, worked out from the full-precision colour (not from 8-bit RGB) so
    /// there's only one rounding. Same maths as Broadcast's Nv12Converter: Y = 16 + 219·Y′,
    /// U and V = 128 + 224·(colour difference), U and V shared by each 2 × 2 block.
    /// </summary>
    private void PackNv12(Vector3[] buffer, int top, int rows, uint seed, byte[] destination)
    {
        var ditherSeed = seed ^ 0xD17u;
        var chromaPlane = Width * Height;
        for (var r = 0; r + 1 < rows; r += 2)
        {
            var y = top + r;
            var lumaRow = y * Width;
            var chromaRow = chromaPlane + y / 2 * Width;
            var upper = r * Width;
            var lower = upper + Width;
            for (var x = 0; x < Width; x += 2)
            {
                Vector3 a = buffer[upper + x], b = buffer[upper + x + 1], c = buffer[lower + x], d = buffer[lower + x + 1];
                destination[lumaRow + x] = Luma(a, LoopNoise.Hash((uint)x, (uint)y, ditherSeed));
                destination[lumaRow + x + 1] = Luma(b, LoopNoise.Hash((uint)x + 1, (uint)y, ditherSeed));
                destination[lumaRow + Width + x] = Luma(c, LoopNoise.Hash((uint)x, (uint)y + 1, ditherSeed));
                destination[lumaRow + Width + x + 1] = Luma(d, LoopNoise.Hash((uint)x + 1, (uint)y + 1, ditherSeed));
                var mean = (a + b + c + d) * 0.25f;
                var meanLuma = 0.2126f * mean.X + 0.7152f * mean.Y + 0.0722f * mean.Z;
                var hash = LoopNoise.Hash((uint)x, (uint)y, ditherSeed ^ 0xC0FFEEu);
                destination[chromaRow + x] = ToByte(128.5f + 224 * (mean.Z - meanLuma) / 1.8556f + Dither(hash) * 0.5f);
                destination[chromaRow + x + 1] = ToByte(128.5f + 224 * (mean.X - meanLuma) / 1.5748f + Dither(hash >> 16) * 0.5f);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Luma(Vector3 s, uint hash) =>
        ToByte(16.5f + 219 * (0.2126f * s.X + 0.7152f * s.Y + 0.0722f * s.Z) + Dither(hash));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToByte(float value) => (byte)Math.Clamp((int)value, 0, 255);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Square(float v) => v * v;
}
