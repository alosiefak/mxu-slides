using MxU.Slides.Broadcast.Recording;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>Port of the grid and blackout cases of SyncTestReaderTests.swift.</summary>
public sealed class SyncTestReaderTests
{
    private const int GridSize = SyncTestReader.GridRows * SyncTestReader.GridColumns;

    [Fact]
    public void GridLumaSamplesTheBgraPicture()
    {
        const int width = 960, height = 540;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var bright = (byte)(x < width / 2 ? 255 : 0);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = bright;
                pixels[i + 3] = 255;
            }
        var lumas = SyncTestReader.GridLuma(pixels, width, height, width * 4);
        Assert.NotNull(lumas);
        Assert.Equal(GridSize, lumas.Length);
        Assert.Equal(1, lumas[0], 0.01); // left half white
        Assert.Equal(0, lumas[SyncTestReader.GridColumns - 1], 0.01); // right half black
        Assert.Equal(GridSize / 2, lumas.Count(l => l > 0.5));
        Assert.Null(SyncTestReader.GridLuma(pixels, width, height + 1, width * 4)); // too few bytes for that size
    }

    [Fact]
    public void BlackAboveTheLowerThird()
    {
        const int columns = SyncTestReader.GridColumns, rows = SyncTestReader.GridRows;
        var lumas = new double[rows * columns];
        for (var index = (rows - 5) * columns; index < rows * columns; index++) lumas[index] = 1;
        Assert.True(SyncTestReader.IsBlackAbove(lumas));
        lumas[columns * 10] = 0.3;
        Assert.False(SyncTestReader.IsBlackAbove(lumas));
        Assert.False(SyncTestReader.IsBlackAbove(new double[10])); // not a grid
    }
}
