using MxU.Slides.VideoIO.DeckLink;

namespace MxU.Slides.VideoIO.Tests.DeckLink;

/// <summary>
/// Windows: BGRA → UYVY for playout, BT.709 limited range, checked to the exact byte
/// against the standard BT.709 colour-bar values.
/// </summary>
public sealed class FrameConverterTests
{
    /// <summary>One row of BGRA from (b, g, r, a) pixels.</summary>
    private static byte[] Row(params (byte B, byte G, byte R, byte A)[] pixels) =>
        [.. pixels.SelectMany(p => new[] { p.B, p.G, p.R, p.A })];

    private static byte[] Uyvy(byte[] bgra, int width, int height = 1, int stride = 0)
    {
        var output = new byte[(width + 1) / 2 * 4 * height];
        Assert.True(DeckLinkFrameConverter.ConvertBgraToUyvy(bgra, stride == 0 ? width * 4 : stride, width, height, output, (width + 1) / 2 * 4));
        return output;
    }

    [Theory]
    [InlineData(0, 0, 0, 128, 16, 128)] // black
    [InlineData(255, 255, 255, 128, 235, 128)] // white
    [InlineData(255, 0, 0, 102, 63, 240)] // red
    [InlineData(0, 255, 0, 42, 173, 26)] // green
    [InlineData(0, 0, 255, 240, 32, 118)] // blue
    [InlineData(255, 255, 0, 16, 219, 138)] // yellow
    [InlineData(0, 255, 255, 154, 188, 16)] // cyan
    [InlineData(255, 0, 255, 214, 78, 230)] // magenta
    public void SolidColoursHitTheBt709Values(byte red, byte green, byte blue, byte u, byte y, byte v)
    {
        var bgra = Row((blue, green, red, 255), (blue, green, red, 255));
        Assert.Equal([u, y, v, y], Uyvy(bgra, 2));
    }

    [Fact]
    public void APairSharesTheAverageOfItsColours()
    {
        // White beside black: each keeps its own brightness, the colour sample is the average (grey: no colour).
        Assert.Equal([128, 235, 128, 16], Uyvy(Row((255, 255, 255, 255), (0, 0, 0, 255)), 2));
        // Red beside blue: U and V are halfway between red's (102, 240) and blue's (240, 118).
        Assert.Equal([171, 63, 179, 32], Uyvy(Row((0, 0, 255, 255), (255, 0, 0, 255)), 2));
    }

    [Fact]
    public void PremultipliedTransparencyComesOutBlack()
    {
        Assert.Equal([128, 16, 128, 16], Uyvy(Row((0, 0, 0, 0), (0, 0, 0, 0)), 2));
    }

    [Fact]
    public void AnOddLastPixelPairsWithItself()
    {
        var bgra = Row((0, 0, 0, 255), (0, 0, 0, 255), (0, 0, 255, 255));
        Assert.Equal([128, 16, 128, 16, 102, 63, 240, 63], Uyvy(bgra, 3));
    }

    [Fact]
    public void RowPaddingIsSkipped()
    {
        // Two rows of one white pair, each row padded to 12 bytes.
        byte[] bgra = [255, 255, 255, 255, 255, 255, 255, 255, 9, 9, 9, 9, 0, 0, 0, 255, 0, 0, 0, 255, 9, 9, 9, 9];
        Assert.Equal([128, 235, 128, 235, 128, 16, 128, 16], Uyvy(bgra, 2, height: 2, stride: 12));
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(255, 235)]
    [InlineData(128, 126)]
    public void TheKeyIsAlphaAsBrightness(byte alpha, byte luma)
    {
        var bgra = Row((10, 20, 30, alpha), (10, 20, 30, alpha));
        var key = new byte[4];
        Assert.True(DeckLinkFrameConverter.ExtractKeyToUyvy(bgra, 8, 2, 1, key, 4));
        Assert.Equal([128, luma, 128, luma], key);
    }

    [Fact]
    public void TooSmallABufferIsRefused()
    {
        Assert.False(DeckLinkFrameConverter.ConvertBgraToUyvy(new byte[8], 8, 2, 1, new byte[3], 4));
        Assert.False(DeckLinkFrameConverter.ConvertBgraToUyvy(new byte[4], 8, 2, 1, new byte[4], 4));
        Assert.False(DeckLinkFrameConverter.ConvertBgraToUyvy([], 0, 0, 0, new byte[4], 4));
    }

    [Fact]
    public void ARoundTripStaysWithinARoundingStep()
    {
        // BGRA → UYVY → BGRA for the six primaries and secondaries.
        (byte R, byte G, byte B)[] colours = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0), (0, 255, 255), (255, 0, 255)];
        foreach (var (r, g, b) in colours)
        {
            var row = Row((b, g, r, 255), (b, g, r, 255));
            var bgra = Enumerable.Repeat(row, 720).SelectMany(x => x).ToArray();
            var uyvy = new byte[4 * 720];
            DeckLinkFrameConverter.ConvertBgraToUyvy(bgra, 8, 2, 720, uyvy, 4);
            var back = new byte[8 * 720];
            Assert.True(new DeckLinkFrameConverter().ConvertToBgra(uyvy, 4, 2, 720, false, back, 8));
            Assert.InRange(back[0], b - 3, b + 3);
            Assert.InRange(back[1], g - 3, g + 3);
            Assert.InRange(back[2], r - 3, r + 3);
        }
    }
}
