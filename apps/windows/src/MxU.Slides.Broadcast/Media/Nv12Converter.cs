namespace MxU.Slides.Broadcast.Media;

/// <summary>
/// Turns a program frame (32-bit BGRA) into NV12, the layout every H.264 encoder takes: a
/// full-size plane of brightness (Y) followed by a half-size plane of interleaved colour (U,V)
/// shared by each 2×2 block of pixels. Uses the BT.709 "video range" maths HD video expects.
/// When the frame isn't the encoder's size it is scaled to fit, centred, with black bars
/// (letterboxed), like the Mac's PixelScaler. Not thread-safe; one per encoder.
/// </summary>
internal sealed class Nv12Converter
{
    private byte[] scaled = [];
    private int[] xIndex = [];
    private int[] xWeight = [];
    private int[] yIndex = [];
    private int[] yWeight = [];
    private (int SourceWidth, int SourceHeight) prepared;
    private (int X, int Y, int Width, int Height) inner;

    public Nv12Converter(int width, int height)
    {
        Width = Math.Max(2, width & ~1);
        Height = Math.Max(2, height & ~1);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Bytes in one NV12 frame: the Y plane plus half as much again for U,V.</summary>
    public int FrameSize => Width * Height * 3 / 2;

    public void Convert(ReadOnlySpan<byte> bgra, int sourceWidth, int sourceHeight, int stride, Span<byte> nv12)
    {
        if (nv12.Length < FrameSize) throw new ArgumentException("NV12 buffer too small", nameof(nv12));
        if (sourceWidth <= 0 || sourceHeight <= 0 || bgra.Length < stride * (sourceHeight - 1) + sourceWidth * 4)
            throw new ArgumentException("Frame is smaller than its size says", nameof(bgra));

        if (sourceWidth == Width && sourceHeight == Height)
        {
            ConvertRegion(bgra, stride, nv12, 0, 0, Width, Height);
            return;
        }

        Prepare(sourceWidth, sourceHeight);
        // Black bars: Y 16, U and V 128 is black in video range.
        nv12[..(Width * Height)].Fill(16);
        nv12[(Width * Height)..FrameSize].Fill(128);
        Scale(bgra, stride);
        ConvertRegion(scaled, inner.Width * 4, nv12, inner.X, inner.Y, inner.Width, inner.Height);
    }

    /// <summary>Works out the centred picture area and the bilinear sampling tables for this source size.</summary>
    private void Prepare(int sourceWidth, int sourceHeight)
    {
        if (prepared == (sourceWidth, sourceHeight)) return;
        prepared = (sourceWidth, sourceHeight);
        var scale = Math.Min((double)Width / sourceWidth, (double)Height / sourceHeight);
        var w = Math.Clamp((int)Math.Round(sourceWidth * scale) & ~1, 2, Width);
        var h = Math.Clamp((int)Math.Round(sourceHeight * scale) & ~1, 2, Height);
        inner = (((Width - w) / 2) & ~1, ((Height - h) / 2) & ~1, w, h);
        scaled = new byte[w * h * 4];
        (xIndex, xWeight) = Taps(sourceWidth, w);
        (yIndex, yWeight) = Taps(sourceHeight, h);
    }

    private static (int[] Index, int[] Weight) Taps(int source, int target)
    {
        var index = new int[target];
        var weight = new int[target];
        var step = (double)source / target;
        for (var i = 0; i < target; i++)
        {
            var position = Math.Max(0, (i + 0.5) * step - 0.5);
            var whole = Math.Min((int)position, source - 1);
            index[i] = whole;
            weight[i] = whole >= source - 1 ? 0 : (int)((position - whole) * 256);
        }
        return (index, weight);
    }

    private unsafe void Scale(ReadOnlySpan<byte> bgra, int stride)
    {
        var (w, h) = (inner.Width, inner.Height);
        fixed (byte* source = bgra)
        fixed (byte* target = scaled)
        {
            var src = source;
            var dst = target;
            Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = h >= 360 ? Environment.ProcessorCount : 1 }, y =>
            {
                var y0 = yIndex[y];
                var y1 = Math.Min(y0 + 1, prepared.SourceHeight - 1);
                var wy = yWeight[y];
                var row0 = src + (long)y0 * stride;
                var row1 = src + (long)y1 * stride;
                var output = dst + (long)y * w * 4;
                for (var x = 0; x < w; x++)
                {
                    var x0 = xIndex[x] * 4;
                    var x1 = Math.Min(xIndex[x] + 1, prepared.SourceWidth - 1) * 4;
                    var wx = xWeight[x];
                    for (var c = 0; c < 4; c++)
                    {
                        var top = row0[x0 + c] * (256 - wx) + row0[x1 + c] * wx;
                        var bottom = row1[x0 + c] * (256 - wx) + row1[x1 + c] * wx;
                        output[x * 4 + c] = (byte)((top * (256 - wy) + bottom * wy + 32768) >> 16);
                    }
                }
            });
        }
    }

    /// <summary>Converts a BGRA picture into the NV12 frame at (left, top), two rows at a time.</summary>
    private unsafe void ConvertRegion(ReadOnlySpan<byte> bgra, int stride, Span<byte> nv12, int left, int top, int w, int h)
    {
        var width = Width;
        var uvOffset = Width * Height;
        fixed (byte* source = bgra)
        fixed (byte* target = nv12)
        {
            var src = source;
            var dst = target;
            var pairs = h / 2;
            Parallel.For(0, pairs, new ParallelOptions { MaxDegreeOfParallelism = h >= 360 ? Environment.ProcessorCount : 1 }, pair =>
            {
                var y = pair * 2;
                var row0 = src + (long)y * stride;
                var row1 = row0 + stride;
                var luma0 = dst + (long)(top + y) * width + left;
                var luma1 = luma0 + width;
                var chroma = dst + uvOffset + (long)((top + y) / 2) * width + left;
                for (var x = 0; x < w; x += 2)
                {
                    var p = row0 + x * 4;
                    var q = row1 + x * 4;
                    int b00 = p[0], g00 = p[1], r00 = p[2];
                    int b01 = p[4], g01 = p[5], r01 = p[6];
                    int b10 = q[0], g10 = q[1], r10 = q[2];
                    int b11 = q[4], g11 = q[5], r11 = q[6];
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
            });
        }
    }

    private static byte Luma(int r, int g, int b) => (byte)(((11967 * r + 40252 * g + 4063 * b + 32768) >> 16) + 16);
}
