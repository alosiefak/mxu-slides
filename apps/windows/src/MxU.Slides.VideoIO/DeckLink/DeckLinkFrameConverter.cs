namespace MxU.Slides.VideoIO.DeckLink;

/// <summary>
/// Converts between the program's BGRA pixels and the 8-bit YUV 4:2:2 ("UYVY", the
/// card's '2vuy') that SDI and HDMI carry.
/// <para>
/// In UYVY two neighbouring pixels share one colour sample: each four bytes are
/// U (blue difference, Cb), Y0 (brightness of the left pixel), V (red difference, Cr),
/// Y1 (brightness of the right pixel). "Limited range" means black is Y=16 and white
/// Y=235, and no colour is 128 (U and V run 16–240), as broadcast video expects.
/// HD (720 lines and up) uses the BT.709 colour matrix; SD uses BT.601.
/// </para>
/// <list type="bullet">
/// <item><see cref="ConvertToBgra"/>: capture. UYVY from a card to BGRA for the renderer,
/// blending the two fields of an interlaced signal so motion doesn't comb. Port of
/// DeckLinkKit/DeckLinkFrameConverter.swift (vImage there, lookup tables here).</item>
/// <item><see cref="ConvertBgraToUyvy"/>: playout to a mode a card won't take as BGRA
/// (BT.709, limited range). <see cref="ExtractKeyToUyvy"/> turns alpha into the
/// matching key signal (transparent = black, opaque = white). A keyed DeckLink output
/// itself is sent as BGRA: the card keys with the fill from the colour and the key
/// from alpha.</item>
/// </list>
/// An instance keeps a scratch buffer for deinterlacing, so use one per capture thread.
/// </summary>
public sealed class DeckLinkFrameConverter
{
    private static readonly Matrix Hd = new(1.5748, -0.4681, -0.1873, 1.8556);
    private static readonly Matrix Sd = new(1.402, -0.714136, -0.344136, 1.772);

    // BGRA → UYVY (BT.709, limited range), in 16.16 fixed point. Luma uses per-pixel
    // coefficients; chroma uses half coefficients (·32768) applied to the SUM of the pair,
    // which averages the two pixels' colour for free.
    private static readonly int YR = Fixed(219.0 / 255 * 0.2126, 65536);
    private static readonly int YG = Fixed(219.0 / 255 * 0.7152, 65536);
    private static readonly int YB = Fixed(219.0 / 255 * 0.0722, 65536);
    private static readonly int UR = Fixed(-0.2126 * 224.0 / 255 / 1.8556, 32768);
    private static readonly int UG = Fixed(-0.7152 * 224.0 / 255 / 1.8556, 32768);
    private static readonly int UB = Fixed(0.9278 * 224.0 / 255 / 1.8556, 32768);
    private static readonly int VR = Fixed(0.7874 * 224.0 / 255 / 1.5748, 32768);
    private static readonly int VG = Fixed(-0.7152 * 224.0 / 255 / 1.5748, 32768);
    private static readonly int VB = Fixed(-0.0722 * 224.0 / 255 / 1.5748, 32768);
    private const int Half = 1 << 15;

    private byte[] scratch = [];

    /// <summary>
    /// UYVY (<paramref name="rowBytes"/> apart) to BGRA with alpha 255. False — and nothing
    /// written — when the size is empty or <paramref name="destination"/> can't hold a
    /// BGRA image of that size.
    /// </summary>
    public unsafe bool ConvertToBgra(ReadOnlySpan<byte> uyvy, int rowBytes, int width, int height, bool interlaced,
        Span<byte> destination, int destinationStride)
    {
        if (width <= 0 || height <= 0 || rowBytes < width * 2 || uyvy.Length < rowBytes * (height - 1) + width * 2) return false;
        if (destinationStride < width * 4 || destination.Length < destinationStride * (height - 1) + width * 4) return false;
        if (interlaced)
        {
            if (scratch.Length < rowBytes * height) scratch = new byte[rowBytes * height];
            DeinterlaceBlend(uyvy, rowBytes, scratch, rowBytes, width * 2, height);
            uyvy = scratch;
        }
        var matrix = height >= 720 ? Hd : Sd;
        fixed (byte* source = uyvy)
        fixed (byte* target = destination)
        {
            var from = (nint)source;
            var to = (nint)target;
            // Split big frames into bands of rows converted in parallel; one 1080p frame is
            // about two million pixels, too many for one thread at 60 frames a second.
            var bands = height >= 360 ? Math.Min(Environment.ProcessorCount, 8) : 1;
            var rowsPerBand = (height + bands - 1) / bands;
            Parallel.For(0, bands, band =>
            {
                var end = Math.Min(height, (band + 1) * rowsPerBand);
                for (var row = band * rowsPerBand; row < end; row++)
                    UyvyRowToBgra((byte*)from + (long)row * rowBytes, (byte*)to + (long)row * destinationStride, width, matrix);
            });
        }
        return true;
    }

    /// <summary>
    /// Averages each line with the one below it (rounding up halves) so the two fields of
    /// an interlaced frame blend instead of combing; the last line is copied as is.
    /// <paramref name="rowLength"/> bytes of each row are processed. Port of dlk_deinterlace_blend.
    /// </summary>
    public static void DeinterlaceBlend(ReadOnlySpan<byte> source, int sourceRowBytes, Span<byte> destination, int destinationRowBytes,
        int rowLength, int height)
    {
        if (height <= 0) return;
        for (var row = 0; row + 1 < height; row++)
        {
            var upper = source.Slice(row * sourceRowBytes, rowLength);
            var lower = source.Slice((row + 1) * sourceRowBytes, rowLength);
            var output = destination.Slice(row * destinationRowBytes, rowLength);
            for (var i = 0; i < rowLength; i++) output[i] = (byte)((upper[i] + lower[i] + 1) >> 1);
        }
        source.Slice((height - 1) * sourceRowBytes, rowLength).CopyTo(destination.Slice((height - 1) * destinationRowBytes, rowLength));
    }

    /// <summary>
    /// BGRA (premultiplied, so transparent areas come out black) to UYVY, BT.709 limited
    /// range. An odd last pixel shares the colour sample with itself.
    /// </summary>
    public static bool ConvertBgraToUyvy(ReadOnlySpan<byte> bgra, int stride, int width, int height, Span<byte> uyvy, int uyvyRowBytes)
    {
        if (!Fits(bgra, stride, width, height, uyvy, uyvyRowBytes)) return false;
        for (var row = 0; row < height; row++)
        {
            var source = bgra.Slice(row * stride, width * 4);
            var target = uyvy.Slice(row * uyvyRowBytes, (width + 1) / 2 * 4);
            for (int x = 0, o = 0; x < width; x += 2, o += 4)
            {
                var left = x * 4;
                var right = x + 1 < width ? left + 4 : left;
                int b0 = source[left], g0 = source[left + 1], r0 = source[left + 2];
                int b1 = source[right], g1 = source[right + 1], r1 = source[right + 2];
                int rs = r0 + r1, gs = g0 + g1, bs = b0 + b1;
                target[o] = Clamp((UR * rs + UG * gs + UB * bs + (128 << 16) + Half) >> 16, 16, 240);
                target[o + 1] = Clamp((YR * r0 + YG * g0 + YB * b0 + (16 << 16) + Half) >> 16, 16, 235);
                target[o + 2] = Clamp((VR * rs + VG * gs + VB * bs + (128 << 16) + Half) >> 16, 16, 240);
                target[o + 3] = Clamp((YR * r1 + YG * g1 + YB * b1 + (16 << 16) + Half) >> 16, 16, 235);
            }
        }
        return true;
    }

    /// <summary>
    /// The key signal for a BGRA picture, as UYVY: brightness from alpha (0 → 16 black,
    /// 255 → 235 white), no colour. For keyers fed fill and key on two separate outputs.
    /// </summary>
    public static bool ExtractKeyToUyvy(ReadOnlySpan<byte> bgra, int stride, int width, int height, Span<byte> uyvy, int uyvyRowBytes)
    {
        if (!Fits(bgra, stride, width, height, uyvy, uyvyRowBytes)) return false;
        var gain = YR + YG + YB;
        for (var row = 0; row < height; row++)
        {
            var source = bgra.Slice(row * stride, width * 4);
            var target = uyvy.Slice(row * uyvyRowBytes, (width + 1) / 2 * 4);
            for (int x = 0, o = 0; x < width; x += 2, o += 4)
            {
                int a0 = source[x * 4 + 3], a1 = x + 1 < width ? source[x * 4 + 7] : a0;
                target[o] = 128;
                target[o + 1] = Clamp((gain * a0 + (16 << 16) + Half) >> 16, 16, 235);
                target[o + 2] = 128;
                target[o + 3] = Clamp((gain * a1 + (16 << 16) + Half) >> 16, 16, 235);
            }
        }
        return true;
    }

    private static bool Fits(ReadOnlySpan<byte> bgra, int stride, int width, int height, Span<byte> uyvy, int uyvyRowBytes) =>
        width > 0 && height > 0 && stride >= width * 4 && bgra.Length >= stride * (height - 1) + width * 4
        && uyvyRowBytes >= (width + 1) / 2 * 4 && uyvy.Length >= uyvyRowBytes * (height - 1) + (width + 1) / 2 * 4;

    private static unsafe void UyvyRowToBgra(byte* source, byte* target, int width, Matrix m)
    {
        for (var x = 0; x < width; x += 2, source += 4)
        {
            int u = source[0], y0 = m.Y[source[1]], v = source[2], y1 = m.Y[source[3]];
            int red = m.VR[v], green = m.VG[v] + m.UG[u], blue = m.UB[u];
            target[0] = Clamp((y0 + blue + Half) >> 16, 0, 255);
            target[1] = Clamp((y0 + green + Half) >> 16, 0, 255);
            target[2] = Clamp((y0 + red + Half) >> 16, 0, 255);
            target[3] = 255;
            target += 4;
            if (x + 1 >= width) break;
            target[0] = Clamp((y1 + blue + Half) >> 16, 0, 255);
            target[1] = Clamp((y1 + green + Half) >> 16, 0, 255);
            target[2] = Clamp((y1 + red + Half) >> 16, 0, 255);
            target[3] = 255;
            target += 4;
        }
    }

    private static byte Clamp(int value, int low, int high) => (byte)Math.Clamp(value, low, high);

    private static int Fixed(double value, double scale) => (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Lookup tables for one YUV → RGB matrix, in 16.16 fixed point: every possible byte
    /// already clamped to the legal range (Y 16–235, U/V 16–240), scaled to full range and
    /// multiplied by its coefficient, so a pixel costs three additions.
    /// </summary>
    private sealed class Matrix
    {
        public readonly int[] Y = new int[256], VR = new int[256], VG = new int[256], UG = new int[256], UB = new int[256];

        public Matrix(double crR, double crG, double cbG, double cbB)
        {
            for (var i = 0; i < 256; i++)
            {
                var luma = (Math.Clamp(i, 16, 235) - 16) * 255.0 / 219;
                var chroma = (Math.Clamp(i, 16, 240) - 128) * 255.0 / 224;
                Y[i] = Fixed(luma, 65536);
                VR[i] = Fixed(crR * chroma, 65536);
                VG[i] = Fixed(crG * chroma, 65536);
                UG[i] = Fixed(cbG * chroma, 65536);
                UB[i] = Fixed(cbB * chroma, 65536);
            }
        }
    }
}
