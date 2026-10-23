using System.Windows.Media;

namespace MxU.Slides.PptxImport.Tests;

public class TextureBakerTests
{
    [Fact]
    public void LgGridBakesFieldAndLines()
    {
        using var temp = new TempFolder();
        var output = temp.File("baked.png");
        var bake = new TextureBake("p", new TextureBakeKind.Pattern("lgGrid", "000000FF", "FFFFFFFF"), K: 2);
        Assert.True(TextureBaker.Bake(bake, 64, 64, output));

        var pixels = TestPackages.Bgra(output, out var width, out var height);
        Assert.Equal((64, 64), (width, height));
        byte Red(int x, int y) => pixels[(y * 64 + x) * 4 + 2];

        Assert.True(Red(1, 1) < 40, "grid line is foreground ink");
        Assert.True(Red(8, 8) > 215, "cell interior is the background field");
        Assert.True(Red(17, 8) < 40, "next grid line lands one cell over");
    }

    [Fact]
    public void TiledTextureCoversTheCanvasOverWhite()
    {
        using var temp = new TempFolder();
        var source = temp.File("tile.png");
        File.WriteAllBytes(source, TestPackages.Png(Colors.Red, size: 4));
        var output = temp.File("baked.png");
        var bake = new TextureBake("p", new TextureBakeKind.Texture(source, Alpha: 0.5, ScaleX: 1, ScaleY: 1, Tiled: true), K: 1);
        Assert.True(TextureBaker.Bake(bake, 10, 10, output));

        var pixels = TestPackages.Bgra(output, out _, out _);
        var (blue, green, red) = (pixels[0], pixels[1], pixels[2]);
        Assert.True(red > 240, "half-transparent red over white keeps full red");
        Assert.InRange(green, 110, 145);
        Assert.InRange(blue, 110, 145);
    }

    [Fact]
    public void MissingTextureSourceFailsWithoutOutput()
    {
        using var temp = new TempFolder();
        var output = temp.File("baked.png");
        var bake = new TextureBake("p", new TextureBakeKind.Texture(temp.File("missing.png"), 1, 1, 1, true), K: 1);
        Assert.False(TextureBaker.Bake(bake, 10, 10, output));
        Assert.False(File.Exists(output));
    }
}
