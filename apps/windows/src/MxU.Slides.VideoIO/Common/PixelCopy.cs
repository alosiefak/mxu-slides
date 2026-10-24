using System.Numerics;
using System.Runtime.InteropServices;

namespace MxU.Slides.VideoIO.Common;

/// <summary>Row-by-row copies between images whose rows are a different number of bytes apart.</summary>
internal static class PixelCopy
{
    /// <summary>
    /// Copies <paramref name="rows"/> rows of <paramref name="rowBytes"/> bytes. Strides can
    /// differ (a driver often pads its rows), so each row is copied on its own.
    /// </summary>
    public static void CopyRows(ReadOnlySpan<byte> source, int sourceStride, Span<byte> destination, int destinationStride, int rowBytes, int rows)
    {
        if (sourceStride == destinationStride && sourceStride == rowBytes)
        {
            source[..(rowBytes * rows)].CopyTo(destination);
            return;
        }
        for (var row = 0; row < rows; row++)
            source.Slice(row * sourceStride, rowBytes).CopyTo(destination.Slice(row * destinationStride, rowBytes));
    }

    /// <summary>The same copy from memory a native library owns (valid only while it lends it to us).</summary>
    public static unsafe void CopyRows(byte* source, int sourceStride, Span<byte> destination, int destinationStride, int rowBytes, int rows) =>
        CopyRows(new ReadOnlySpan<byte>(source, sourceStride * (rows - 1) + rowBytes), sourceStride, destination, destinationStride, rowBytes, rows);

    /// <summary>
    /// Resizes a BGRA image by picking, for each destination pixel, the nearest source
    /// pixel. Crude but fast; only used when a card runs at a different size from the
    /// program feed (1080p program into a 720p mode, say), which the operator chose.
    /// </summary>
    public static void ScaleNearest(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int sourceStride,
        Span<byte> destination, int width, int height, int destinationStride)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || width <= 0 || height <= 0) return;
        Span<int> columns = width <= 8192 ? stackalloc int[width] : new int[width];
        for (var x = 0; x < width; x++) columns[x] = (int)((long)x * sourceWidth / width);
        for (var y = 0; y < height; y++)
        {
            var from = MemoryMarshal.Cast<byte, uint>(source.Slice((int)((long)y * sourceHeight / height) * sourceStride, sourceWidth * 4));
            var to = MemoryMarshal.Cast<byte, uint>(destination.Slice(y * destinationStride, width * 4));
            for (var x = 0; x < width; x++) to[x] = from[columns[x]];
        }
    }

    /// <summary>
    /// Sets every alpha byte of a BGRA image to 255. Cameras and "BGRX" NDI frames leave
    /// that byte undefined, and a zero there would draw the input fully transparent.
    /// Each pixel is one little-endian uint whose top byte is alpha, so OR-ing in
    /// 0xFF000000 does it, many pixels at a time with <see cref="Vector{T}"/>.
    /// </summary>
    public static void ForceOpaque(Span<byte> bgra, int stride, int width, int height)
    {
        var mask = new Vector<uint>(0xFF000000u);
        for (var row = 0; row < height; row++)
        {
            var pixels = MemoryMarshal.Cast<byte, uint>(bgra.Slice(row * stride, width * 4));
            var i = 0;
            for (; i <= pixels.Length - Vector<uint>.Count; i += Vector<uint>.Count)
                (new Vector<uint>(pixels[i..]) | mask).CopyTo(pixels[i..]);
            for (; i < pixels.Length; i++) pixels[i] |= 0xFF000000u;
        }
    }
}
