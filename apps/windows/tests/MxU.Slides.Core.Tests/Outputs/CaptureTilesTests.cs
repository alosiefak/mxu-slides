using MxU.Slides.Core.Outputs;

namespace MxU.Slides.Core.Tests.Outputs;

public sealed class CaptureTilesTests
{
    [Fact]
    public void APictureThatFitsIsOnePiece()
    {
        var tiles = CaptureTiles.Plan(1920, 1080, 1920, 1080);
        Assert.Equal([new CaptureTiles.Tile(0, 0, 1920, 1080)], tiles);
    }

    [Fact]
    public void FourKOnAFullHdScreenIsFourQuarters()
    {
        var tiles = CaptureTiles.Plan(3840, 2160, 1920, 1080);
        Assert.Equal(
        [
            new CaptureTiles.Tile(0, 0, 1920, 1080), new CaptureTiles.Tile(1920, 0, 1920, 1080),
            new CaptureTiles.Tile(0, 1080, 1920, 1080), new CaptureTiles.Tile(1920, 1080, 1920, 1080),
        ], tiles);
    }

    [Fact]
    public void PiecesAreCutEvenly()
    {
        // 1080p from a 1366 × 768 laptop: two columns of 960 and two rows of 540.
        var tiles = CaptureTiles.Plan(1920, 1080, 1366, 768);
        Assert.Equal(4, tiles.Count);
        Assert.All(tiles, t => Assert.Equal((960, 540), (t.Width, t.Height)));
    }

    [Theory]
    [InlineData(1920, 1080, 1366, 768)]
    [InlineData(3840, 2160, 1366, 768)]
    [InlineData(3840, 2160, 1024, 768)]
    [InlineData(1080, 1920, 1920, 1080)] // a portrait picture on a landscape screen
    [InlineData(7680, 4320, 2560, 1440)]
    [InlineData(1921, 1081, 1920, 1080)]
    public void PiecesFitTheScreenAndCoverEveryPixelOnce(int width, int height, int screenWidth, int screenHeight)
    {
        var tiles = CaptureTiles.Plan(width, height, screenWidth, screenHeight);
        Assert.All(tiles, t => Assert.True(t.Width <= screenWidth && t.Height <= screenHeight, $"{t} is bigger than the screen"));
        var covered = new int[width * height];
        foreach (var t in tiles)
            for (var y = t.Y; y < t.Y + t.Height; y++)
                for (var x = t.X; x < t.X + t.Width; x++)
                    covered[y * width + x]++;
        Assert.All(covered, count => Assert.Equal(1, count));
        Assert.Equal(new CaptureTiles.Tile(0, 0, tiles[0].Width, tiles[0].Height), tiles[0]);
    }

    [Fact]
    public void AnUnknownScreenSizeIsOnePiece()
    {
        Assert.Equal([new CaptureTiles.Tile(0, 0, 1920, 1080)], CaptureTiles.Plan(1920, 1080, 0, 0));
    }
}
