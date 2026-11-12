namespace MxU.Slides.Broadcast.Recording;

/// <summary>
/// Reads a picture as a coarse grid of brightnesses, cheaply enough to do for every recorded
/// frame, and tells a picture that is black above its lower third: the screen gone dark while
/// at most a caption is up. The recorder uses it to note blackouts in the file (with the time
/// in the file, so the moment can be found in the recording). Port of the static half of the
/// Mac's OutputEngine/Export/SyncTestReader.swift; its flash-and-click sync measurement (the
/// "Check Sync…" sheet) isn't on Windows yet.
/// </summary>
public static class SyncTestReader
{
    public const int GridColumns = 96;
    public const int GridRows = 54;

    /// <summary>
    /// The brightness (0…1, Rec. 709 weights) of one pixel at the centre of each grid cell, row
    /// by row from the top. Null when the picture is too small to read.
    /// </summary>
    /// <param name="bgra">32-bit BGRA pixels, rows <paramref name="stride"/> bytes apart.</param>
    public static double[]? GridLuma(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < stride * (height - 1) + width * 4) return null;
        var lumas = new double[GridRows * GridColumns];
        for (var row = 0; row < GridRows; row++)
        {
            var y = Math.Min(height - 1, (row * 2 + 1) * height / (GridRows * 2));
            for (var column = 0; column < GridColumns; column++)
            {
                var x = Math.Min(width - 1, (column * 2 + 1) * width / (GridColumns * 2));
                var pixel = y * stride + x * 4;
                lumas[row * GridColumns + column] =
                    (0.0722 * bgra[pixel] + 0.7152 * bgra[pixel + 1] + 0.2126 * bgra[pixel + 2]) / 255;
            }
        }
        return lumas;
    }

    /// <summary>
    /// True when nothing in the top seven tenths of the grid is brighter than a near-black: a
    /// lower third (or a caption) may still be up below it, as at the end of a service.
    /// </summary>
    public static bool IsBlackAbove(IReadOnlyList<double> lumasOfLowerThird)
    {
        if (lumasOfLowerThird.Count != GridRows * GridColumns) return false;
        var above = GridRows * 7 / 10 * GridColumns;
        for (var index = 0; index < above; index++)
            if (lumasOfLowerThird[index] > 0.04) return false;
        return true;
    }
}
