namespace MxU.Slides.Core.Outputs;

/// <summary>
/// How to cut a capture that's bigger than the screen into screen-sized pieces.
/// <para>
/// Recording, streaming and NDI draw the picture in a hidden window and copy it, but WPF only
/// draws the part of a window that is on a screen. A 1080p stream from a 1366 × 768 laptop,
/// or 4K NDI from a 1080p one, is therefore drawn as several hidden windows, each no bigger
/// than the screen and each showing its own piece, and the pieces are put back together.
/// Windows only: the Mac draws off screen on the graphics card and needs none of this.
/// </para>
/// </summary>
public static class CaptureTiles
{
    /// <summary>One piece of the picture, in pixels from its top-left corner.</summary>
    public readonly record struct Tile(int X, int Y, int Width, int Height);

    /// <summary>
    /// Pieces covering <paramref name="width"/> × <paramref name="height"/>, each no bigger than
    /// the screen: one piece when it fits, otherwise as few as possible, cut evenly (3840 across
    /// a 1366 screen makes three 1280s rather than 1366 + 1366 + 1108). Rows go top to bottom,
    /// pieces left to right; the first starts at the corner.
    /// </summary>
    public static IReadOnlyList<Tile> Plan(int width, int height, int screenWidth, int screenHeight)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "A capture needs a size.");
        // A screen size we couldn't read: one piece, as before tiling existed.
        if (screenWidth <= 0 || screenHeight <= 0) return [new Tile(0, 0, width, height)];
        var columns = (width + screenWidth - 1) / screenWidth;
        var rows = (height + screenHeight - 1) / screenHeight;
        var tiles = new List<Tile>(columns * rows);
        for (var row = 0; row < rows; row++)
        {
            var top = (int)((long)height * row / rows);
            var bottom = (int)((long)height * (row + 1) / rows);
            for (var column = 0; column < columns; column++)
            {
                var left = (int)((long)width * column / columns);
                var right = (int)((long)width * (column + 1) / columns);
                tiles.Add(new Tile(left, top, right - left, bottom - top));
            }
        }
        return tiles;
    }
}
