using MxU.Slides.Core.MotionTemplates;
using MxU.Slides.MotionTemplates.Lottie;
using static MxU.Slides.Core.MotionTemplates.LottieBuilder;

namespace MxU.Slides.MotionTemplates.Tests;

// Drawing templates with Skia's Skottie (Phase 11): the same frame always gives the same
// picture, whatever was drawn before and on whichever renderer; what isn't the design stays
// see-through; filled-in fields show; fonts Windows hasn't got come from the library's folder.
public sealed class LottieRendererTests
{
    internal static readonly FolderFonts ShippedFonts = new(Path.Combine(AppContext.BaseDirectory, "Fonts"));

    private static byte[] Draw(LottieRenderer renderer, double frame, int width = 480, int height = 270)
    {
        using var target = new LottieFrame(width, height);
        renderer.Render(frame, target);
        return [.. target.Pixels];
    }

    /// <summary>The BGRA of pixel (x, y) in a picture <paramref name="width"/> wide.</summary>
    internal static (byte B, byte G, byte R, byte A) Pixel(byte[] pixels, int width, int x, int y)
    {
        var i = (y * width + x) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
    }

    [Fact]
    public void TheSameFrameAlwaysGivesTheSamePicture()
    {
        using var renderer = new LottieRenderer(StarterMotionTemplates.NameStrap(), ShippedFonts);
        var first = Draw(renderer, 30);
        Draw(renderer, 200);
        Draw(renderer, 5);
        Assert.Equal(first, Draw(renderer, 30));
        using var another = new LottieRenderer(StarterMotionTemplates.NameStrap(), ShippedFonts);
        Assert.Equal(first, Draw(another, 30));
        Assert.NotEqual(first, Draw(another, 10));
    }

    [Fact]
    public void WhatIsntTheDesignStaysSeeThrough()
    {
        using var renderer = new LottieRenderer(StarterMotionTemplates.NameStrap(), ShippedFonts);
        var settled = Draw(renderer, 48);
        // The top of the picture is empty; the plate (navy #0F1F3A at 94%) is premultiplied by its alpha.
        Assert.Equal((0, 0, 0, 0), Pixel(settled, 480, 240, 40));
        var plate = Pixel(settled, 480, 200, 235);
        Assert.InRange(plate.A, 235, 242);
        Assert.InRange(plate.B, 0x3A * plate.A / 255 - 2, 0x3A * plate.A / 255 + 2);
        Assert.InRange(plate.G, 0x1F * plate.A / 255 - 2, 0x1F * plate.A / 255 + 2);
        Assert.InRange(plate.R, 0x0F * plate.A / 255 - 2, 0x0F * plate.A / 255 + 2);
        // On its first frame nothing has come in yet: the whole picture is see-through.
        var first = Draw(renderer, 0);
        Assert.All(Enumerable.Range(0, first.Length / 4), i => Assert.Equal(0, first[i * 4 + 3]));
    }

    [Fact]
    public void FilledInFieldsShowAndOnlyChangeTheirPart()
    {
        var document = StarterMotionTemplates.NameStrap();
        using var plain = new LottieRenderer(document, ShippedFonts);
        using var filled = new LottieRenderer(MotionFields.Apply(document, new Dictionary<string, string>
        {
            ["text:L0"] = "Rev. Dr. Ade Johnson",
            ["color:#0F1F3A"] = "#7A1F2B",
        }), ShippedFonts);
        var before = Draw(plain, 60);
        var after = Draw(filled, 60);
        // The plate is maroon now.
        var plate = Pixel(after, 480, 200, 230);
        Assert.True(plate.R > plate.B * 2, $"plate is {plate}");
        // The empty top of the picture is the same.
        Assert.Equal(before.AsSpan(0, 480 * 4 * 150).ToArray(), after.AsSpan(0, 480 * 4 * 150).ToArray());
    }

    [Fact]
    public void AStillHoldReportsNoChange()
    {
        using var renderer = new LottieRenderer(StarterMotionTemplates.NameStrap(), ShippedFonts);
        Assert.True(renderer.Seek(10));
        Assert.True(renderer.Seek(11));
        Assert.True(renderer.Seek(60));
        Assert.False(renderer.Seek(61));
        Assert.False(renderer.Seek(150));
        Assert.True(renderer.Seek(230));
    }

    [Fact]
    public void FramesAreTheAnimationsOwnEvenWhenItDoesntStartAtZero()
    {
        // The same animation, trimmed to start at frame 10 (Skottie counts from the in point; the app counts as the file does).
        var trimmed = StarterMotionTemplates.NameStrap().Clone().Root;
        trimmed["ip"] = 10;
        using var original = new LottieRenderer(StarterMotionTemplates.NameStrap(), ShippedFonts);
        using var later = new LottieRenderer(LottieDocument.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(trimmed)), ShippedFonts);
        Assert.Equal(Draw(original, 20), Draw(later, 20));
        Assert.NotEqual(Draw(original, 10), Draw(later, 20));
    }

    [Fact]
    public void AFontWindowsHasntGotIsTakenFromTheFolder()
    {
        // The template asks for Bebas Neue by its PostScript name, under a family name no PC has
        // installed, so only the folder (the fonts the app ships) can supply it.
        static LottieDocument Asking(string family)
        {
            var b = new LottieBuilder("Font", 480, 270, 30, 30).Font("BebasNeue-Regular", family, "Regular");
            b.Layer(b.TextLayer("Word", Transform(Static(20, 80, 0)), "WWWWW", "BebasNeue-Regular", 90, "#FFFFFF", 440, 120));
            return b.Document();
        }
        using var fromFolder = new LottieRenderer(Asking("MxU Not Installed Family"), ShippedFonts);
        using var fallback = new LottieRenderer(Asking("MxU Not Installed Family"));
        // Bebas Neue is narrow: five W's in it end well before where the fallback font's do.
        static int RightmostInk(byte[] pixels)
        {
            var right = 0;
            for (var y = 0; y < 270; y++)
                for (var x = 0; x < 480; x++)
                    if (Pixel(pixels, 480, x, y).A > 128) right = Math.Max(right, x);
            return right;
        }
        Assert.True(RightmostInk(Draw(fromFolder, 0)) + 40 < RightmostInk(Draw(fallback, 0)));
    }

    [Fact]
    public void ThePngStillKeepsItsTransparency()
    {
        var png = LottieStills.Png(StarterMotionTemplates.NameStrap(), 48, 480, 270, ShippedFonts);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png.Take(4));
        using var decoded = SkiaSharp.SKBitmap.Decode(png);
        Assert.Equal((480, 270), (decoded.Width, decoded.Height));
        Assert.Equal(0, decoded.GetPixel(240, 40).Alpha);
        Assert.True(decoded.GetPixel(200, 235).Alpha > 230);
    }

    [Fact]
    public void AFullScreenDesignIsABackgroundAndALowerThirdIsnt()
    {
        Assert.Equal(MotionUse.Background, LottieStills.SuggestUse(StarterMotionTemplates.TitleCard(), ShippedFonts));
        Assert.Equal(MotionUse.Overlay, LottieStills.SuggestUse(StarterMotionTemplates.NameStrap(), ShippedFonts));
        Assert.InRange(LottieStills.Coverage(StarterMotionTemplates.NameStrap(), 48, ShippedFonts), 0.02, 0.2);
    }
}
