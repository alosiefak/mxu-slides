using MxU.Slides.Core.Motion;

namespace MxU.Slides.MotionTemplates.Export;

/// <summary>
/// Puts a drawn template (premultiplied BGRA, see-through where there's no design) over a solid
/// colour and turns the result into NV12, the layout the H.264 encoder takes. H.264 has no
/// transparency, so a lower third for a video file goes over a colour: black, or a chroma-key
/// green a vision mixer can key out.
/// <para>
/// Over a colour, with premultiplied alpha, is one sum per channel: <c>out = design + colour × (1 − alpha)</c>.
/// Then the BT.709 "video range" maths the app's recorder uses (Broadcast/Media/Nv12Converter.cs):
/// brightness for every pixel, colour shared by each 2 × 2 block. Rows are shared out across a
/// <see cref="RenderCrew"/> (threads of its own, below normal priority), never the shared pool.
/// </para>
/// </summary>
internal static class Nv12Over
{
    public static int FrameBytes(int width, int height) => width * height * 3 / 2;

    /// <param name="bgra">The design: <paramref name="width"/> × <paramref name="height"/>, premultiplied, <paramref name="stride"/> bytes a row.</param>
    /// <param name="background">The colour behind, 0xRRGGBB.</param>
    public static unsafe void Convert(ReadOnlySpan<byte> bgra, int width, int height, int stride, int background, Span<byte> nv12, RenderCrew? crew = null)
    {
        if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("NV12 needs an even width and height");
        if (nv12.Length < FrameBytes(width, height)) throw new ArgumentException("NV12 buffer too small", nameof(nv12));
        int backR = (background >> 16) & 0xFF, backG = (background >> 8) & 0xFF, backB = background & 0xFF;
        fixed (byte* source = bgra)
        fixed (byte* target = nv12)
        {
            var src = source;
            var dst = target;
            var uvOffset = width * height;
            void Pair(int pair)
            {
                var y = pair * 2;
                var row0 = src + (long)y * stride;
                var row1 = row0 + stride;
                var luma0 = dst + (long)y * width;
                var luma1 = luma0 + width;
                var chroma = dst + uvOffset + (long)pair * width;
                for (var x = 0; x < width; x += 2)
                {
                    Over(row0 + x * 4, backR, backG, backB, out var r00, out var g00, out var b00);
                    Over(row0 + x * 4 + 4, backR, backG, backB, out var r01, out var g01, out var b01);
                    Over(row1 + x * 4, backR, backG, backB, out var r10, out var g10, out var b10);
                    Over(row1 + x * 4 + 4, backR, backG, backB, out var r11, out var g11, out var b11);
                    luma0[x] = Luma(r00, g00, b00);
                    luma0[x + 1] = Luma(r01, g01, b01);
                    luma1[x] = Luma(r10, g10, b10);
                    luma1[x + 1] = Luma(r11, g11, b11);
                    var r = r00 + r01 + r10 + r11;
                    var g = g00 + g01 + g10 + g11;
                    var b = b00 + b01 + b10 + b11;
                    chroma[x] = (byte)(((-6596 * r - 22189 * g + 28784 * b + 131072) >> 18) + 128);
                    chroma[x + 1] = (byte)(((28784 * r - 26145 * g - 2639 * b + 131072) >> 18) + 128);
                }
            }
            if (crew is null)
                for (var pair = 0; pair < height / 2; pair++) Pair(pair);
            else
                // Bands of 16 row pairs: big enough to be worth handing out, small enough to share evenly.
                crew.For((height / 2 + 15) / 16, (band, _) =>
                {
                    for (var pair = band * 16; pair < Math.Min(height / 2, band * 16 + 16); pair++) Pair(pair);
                });
        }
    }

    private static unsafe void Over(byte* p, int backR, int backG, int backB, out int r, out int g, out int b)
    {
        var keep = 255 - p[3];
        // (x + 127) / 255 rounds to nearest; premultiplied channels can't exceed their alpha, so the sum stays ≤ 255.
        b = p[0] + (backB * keep + 127) / 255;
        g = p[1] + (backG * keep + 127) / 255;
        r = p[2] + (backR * keep + 127) / 255;
    }

    private static byte Luma(int r, int g, int b) => (byte)(((11967 * r + 40252 * g + 4063 * b + 32768) >> 16) + 16);
}
