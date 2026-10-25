using MxU.Slides.Core.Motion;

namespace MxU.Slides.Core.Tests.Motion;

/// <summary>
/// The motion background renderer: every style loops seamlessly (drawing past the end comes back
/// to frame 0, and the step across the seam is no bigger than any other step), drawing is
/// deterministic whatever the threads, palettes really colour the picture, and the NV12 the
/// encoder gets matches the BGRA the previews show.
/// </summary>
public sealed class MotionRendererTests : IDisposable
{
    private const int Width = 160;
    private const int Height = 90;
    private readonly RenderCrew crew = new("test", 3);

    public void Dispose() => crew.Dispose();

    public static TheoryData<string> Styles() => [.. MotionStyles.All.Select(s => s.Id)];

    private static MotionRecipe Recipe(string style, int seconds = 10, int frameRate = 30) =>
        MotionRecipe.For(MotionStyles.Find(style)!) with { Seconds = seconds, FrameRate = frameRate };

    private byte[] Frame(MotionRenderer renderer, long frame, MotionPixelFormat format = MotionPixelFormat.Bgra32)
    {
        var pixels = new byte[renderer.FrameBytes(format)];
        renderer.RenderFrame(frame, format, pixels);
        return pixels;
    }

    /// <summary>
    /// The picture as 8 × 8 block averages (per colour), so film grain and dither (fresh every
    /// frame by design) average out and what's left is the motion.
    /// </summary>
    private static double[] Blocks(byte[] bgra)
    {
        var blocks = new double[Width / 8 * (Height / 8) * 3];
        for (var y = 0; y < Height / 8 * 8; y++)
            for (var x = 0; x < Width / 8 * 8; x++)
                for (var c = 0; c < 3; c++)
                    blocks[((y / 8) * (Width / 8) + x / 8) * 3 + c] += bgra[(y * Width + x) * 4 + c] / 64.0;
        return blocks;
    }

    private static double Step(double[] a, double[] b) => a.Zip(b, (p, q) => Math.Abs(p - q)).Average();

    [Theory]
    [MemberData(nameof(Styles))]
    public void EveryStyleLoopsSeamlessly(string style)
    {
        var renderer = new MotionRenderer(Recipe(style), Width, Height, crew);
        var last = renderer.FrameCount - 1;
        // Drawing one frame past the end is frame 0 again, pixel for pixel (grain and all).
        Assert.Equal(Frame(renderer, 0), Frame(renderer, last + 1));

        // And the jump from the last frame to the first is an ordinary step, like any other.
        var frames = new[] { last - 1, last, 0, 1, 2, 150, 151 }.ToDictionary(f => f, f => Blocks(Frame(renderer, f)));
        var seam = Step(frames[last], frames[0]);
        var ordinary = new[] { Step(frames[last - 1], frames[last]), Step(frames[0], frames[1]), Step(frames[1], frames[2]), Step(frames[150], frames[151]) };
        Assert.True(seam <= 2.5 * ordinary.Average() + 0.15, $"{style}: the seam moves {seam:0.000} levels, an ordinary step {ordinary.Average():0.000}");

        // It does move: half a loop later the picture is clearly different.
        Assert.True(Step(frames[0], frames[150]) > 0.4, $"{style} barely moves ({Step(frames[0], frames[150]):0.000})");
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public void DrawingIsTheSameWhateverTheThreadsOrOrder(string style)
    {
        var recipe = Recipe(style) with { Variation = 42 };
        using var alone = new RenderCrew("alone", 0);
        var first = new MotionRenderer(recipe, Width, Height, crew);
        var second = new MotionRenderer(recipe, Width, Height, alone);
        var seven = Frame(first, 7);
        var three = Frame(first, 3);
        Assert.Equal(three, Frame(second, 3));
        Assert.Equal(seven, Frame(second, 7));
        Assert.Equal(seven, Frame(first, 7));
        Assert.NotEqual(seven, three);
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public void PalettesColourThePicture(string style)
    {
        var reds = Recipe(style) with { Palette = null, Colors = ["#1A0000", "#6E0A0A", "#E0302A", "#FFB4A0"] };
        var blues = Recipe(style) with { Palette = null, Colors = ["#00061A", "#0A1E6E", "#2A6AE0", "#A0C8FF"] };
        var red = Means(Frame(new MotionRenderer(reds, Width, Height, crew), 40));
        var blue = Means(Frame(new MotionRenderer(blues, Width, Height, crew), 40));
        Assert.True(red.R > 1.3 * red.B && red.R > 1.3 * red.G, $"{style} in reds: R {red.R:0.0} G {red.G:0.0} B {red.B:0.0}");
        Assert.True(blue.B > 1.3 * blue.R, $"{style} in blues: R {blue.R:0.0} G {blue.G:0.0} B {blue.B:0.0}");
    }

    private static (double R, double G, double B) Means(byte[] bgra)
    {
        double r = 0, g = 0, b = 0;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            b += bgra[i];
            g += bgra[i + 1];
            r += bgra[i + 2];
        }
        var n = bgra.Length / 4.0;
        return (r / n, g / n, b / n);
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public void IntensityMakesItBrighter(string style)
    {
        var faint = Means(Frame(new MotionRenderer(Recipe(style) with { Intensity = 0 }, Width, Height, crew), 20));
        var strong = Means(Frame(new MotionRenderer(Recipe(style) with { Intensity = 1 }, Width, Height, crew), 20));
        Assert.True(strong.R + strong.G + strong.B > faint.R + faint.G + faint.B + 3, $"{style}: {faint} → {strong}");
    }

    [Fact]
    public void AnotherVariationIsAnotherPicture()
    {
        var one = Blocks(Frame(new MotionRenderer(Recipe("bokeh") with { Variation = 1 }, Width, Height, crew), 0));
        var two = Blocks(Frame(new MotionRenderer(Recipe("bokeh") with { Variation = 2 }, Width, Height, crew), 0));
        Assert.True(Step(one, two) > 1);
    }

    [Fact]
    public void FasterMovesFurtherInTheSameTime()
    {
        double Moved(double speed)
        {
            var renderer = new MotionRenderer(Recipe("smoke") with { Speed = speed }, Width, Height, crew);
            return Step(Blocks(Frame(renderer, 0)), Blocks(Frame(renderer, 30)));
        }
        Assert.True(Moved(2) > 1.5 * Moved(0.5), $"speed ½ moves {Moved(0.5):0.00}, speed 2 {Moved(2):0.00}");
    }

    [Fact]
    public void Nv12IsTheSamePictureAsBgra()
    {
        var renderer = new MotionRenderer(Recipe("gradient-flow"), Width, Height, crew);
        var bgra = Frame(renderer, 12);
        var nv12 = Frame(renderer, 12, MotionPixelFormat.Nv12);
        Assert.Equal(Width * Height * 3 / 2, nv12.Length);
        // BT.709 video range from the 8-bit picture, as Broadcast's Nv12Converter does it.
        double difference = 0;
        for (var i = 0; i < Width * Height; i++)
        {
            var luma = 16 + (0.2126 * bgra[i * 4 + 2] + 0.7152 * bgra[i * 4 + 1] + 0.0722 * bgra[i * 4]) * 219 / 255;
            difference += Math.Abs(luma - nv12[i]);
        }
        Assert.InRange(difference / (Width * Height), 0, 1.2);
        // Colour planes are centred on 128 and say "warm" for this palette (Dawn: V above U on average).
        var chroma = nv12.AsSpan(Width * Height).ToArray();
        var u = chroma.Where((_, i) => i % 2 == 0).Average(b => (double)b);
        var v = chroma.Where((_, i) => i % 2 == 1).Average(b => (double)b);
        Assert.True(v > u, $"U {u:0.0} V {v:0.0}");
    }

    [Fact]
    public void OddSizesAreMadeEven()
    {
        var renderer = new MotionRenderer(Recipe("waves"), 101, 57, crew);
        Assert.Equal((100, 56), (renderer.Width, renderer.Height));
        Assert.Equal(100 * 56 * 3 / 2, Frame(renderer, 0, MotionPixelFormat.Nv12).Length);
    }

    [Fact]
    public void ARecipeFromANewerVersionStillDraws()
    {
        var renderer = new MotionRenderer(new MotionRecipe { Style = "fireworks", Colors = ["not a colour"], Seconds = 99, Speed = double.NaN }, Width, Height, crew);
        Assert.Equal((MotionStyles.All[0].Id, 30 * 30), (renderer.Recipe.Style, renderer.FrameCount));
        Assert.Contains(Frame(renderer, 0).Where((_, i) => i % 4 == 1), g => g > 10);
    }
}
