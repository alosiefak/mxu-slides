using MxU.Slides.Core.Json;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Tests.Outputs.OutputFixtures;

namespace MxU.Slides.Core.Tests.Outputs;

// Ported from RenderEngineTests/OutputMaskTests.swift. The raster tests run
// the coverage maths over a test polygon filler (the app fills real paths);
// the two pass tests run the shader reference with that coverage.
public class OutputMaskTests
{
    private static byte[] Raster(params OutputMask[] masks) =>
        OutputMaskRaster.Coverage(masks, 192, 108, FillPolygon) ?? throw new InvalidOperationException("no raster");

    [Fact]
    public void RasterModeOutBlacksInsideThePath()
    {
        var coverage = Raster(new OutputMask("Band", CenterBand, OutputMaskMode.Out));
        Assert.Equal(0, At(coverage, 192, 96, 54)); // inside the path = blacked out
        Assert.Equal(255, At(coverage, 192, 10, 54)); // outside stays shown
        Assert.Equal(255, At(coverage, 192, 182, 54));
    }

    [Fact]
    public void RasterModeInShowsOnlyInsideThePath()
    {
        var coverage = Raster(new OutputMask("Window", CenterBand, OutputMaskMode.In));
        Assert.Equal(255, At(coverage, 192, 96, 54)); // inside the window shows
        Assert.Equal(0, At(coverage, 192, 10, 54)); // outside goes black
    }

    [Fact]
    public void RasterFeatherSoftensTheEdge()
    {
        var coverage = Raster(new OutputMask("Soft", CenterBand, OutputMaskMode.Out, feather: 0.15));
        var edge = At(coverage, 192, 48, 54);
        Assert.True(edge is > 20 and < 235, $"feather ramps instead of stepping (got {edge})");
    }

    [Fact]
    public void MasksCombine()
    {
        var coverage = Raster(
            new OutputMask("Band", CenterBand, OutputMaskMode.Out),
            new OutputMask("Left", LeftHalf, OutputMaskMode.Out));
        Assert.Equal(0, At(coverage, 192, 96, 54)); // band blacked
        Assert.Equal(0, At(coverage, 192, 10, 54)); // left half blacked
        Assert.Equal(255, At(coverage, 192, 182, 54)); // right quarter shows
        Assert.Null(OutputMaskRaster.Coverage([], 192, 108, FillPolygon));
    }

    [Fact]
    public void MaskAloneTriggersThePassAndBlacksTheRegion()
    {
        Assert.NotNull(OutputPass.Plan(null, null, null, hasMask: true, new SceneSize(192, 108), 192, 108));
        var coverage = Raster(new OutputMask("Band", CenterBand, OutputMaskMode.Out));
        double Mask(double u, double v) => At(coverage, 192, (int)(u * 192), (int)(v * 108)) / 255.0;
        var masked = RenderPixel((_, _) => White, new OutputAdjustments(), 192, 108, 96, 54, mask: Mask);
        Assert.True(masked.R < 0.05, "masked region is black");
        Assert.True(masked.A < 0.05, "…and carries no key");
        Assert.True(RenderPixel((_, _) => White, new OutputAdjustments(), 192, 108, 10, 54, mask: Mask).R > 0.9, "unmasked region untouched");
    }

    [Fact]
    public void MaskIsCanvasSpaceAcrossSlices()
    {
        var coverage = Raster(new OutputMask("Left", LeftHalf, OutputMaskMode.Out));
        double Mask(double u, double v) => At(coverage, 192, Math.Min((int)(u * 192), 191), Math.Min((int)(v * 108), 107)) / 255.0;
        var center = RenderPixel((_, _) => White, new OutputAdjustments(), 96, 108, 48, 54, new SceneRect(0.5, 0, 0.5, 1), mask: Mask);
        Assert.True(center.R > 0.9, "a left-half mask leaves the right slice alone");
    }

    // ── Beyond the Mac tests ────────────────────────────────────────────────

    [Fact]
    public void CombiningUsesTheMacsIntegerMaths()
    {
        byte[] coverage = [255, 255, 200, 100];
        OutputMaskRaster.Apply(coverage, [0, 128, 255, 51], OutputMaskMode.Out);
        Assert.Equal([255, 127, 0, 80], coverage); // 255·127/255, 200·0/255, 100·204/255 = 80 (truncated)
        byte[] window = [255, 255];
        OutputMaskRaster.Apply(window, [128, 0], OutputMaskMode.In);
        Assert.Equal([128, 0], window);
    }

    [Fact]
    public void UnreadablePathsAreSkipped()
    {
        Assert.Null(OutputMaskRaster.Coverage([new OutputMask("Bad", "Q 1 2")], 192, 108, FillPolygon));
        var coverage = OutputMaskRaster.Coverage([new OutputMask("Bad", "Q 1 2"), new OutputMask("Band", CenterBand)], 192, 108, FillPolygon);
        Assert.NotNull(coverage);
        Assert.Equal(0, At(coverage, 192, 96, 54));
    }

    [Fact]
    public void BlurKernelIsOddAndAtLeastThree()
    {
        Assert.Equal(3, OutputMaskRaster.BlurKernelSize(0.001, 192, 108));
        Assert.Equal(17, OutputMaskRaster.BlurKernelSize(0.15, 192, 108)); // 16.2 → 16 → 17
        Assert.Equal(17, OutputMaskRaster.BlurKernelSize(0.157, 192, 108)); // 16.96 → 17
    }

    [Fact]
    public void TentBlurKeepsFlatAreasAndRampsSymmetrically()
    {
        var flat = Enumerable.Repeat((byte)200, 30 * 20).ToArray();
        Assert.All(OutputMaskRaster.TentBlur(flat, 30, 20, 9), value => Assert.Equal(200, value));

        // A vertical step from 0 to 255: the blurred edge is mid-grey and rises monotonically.
        var step = new byte[40 * 4];
        for (var y = 0; y < 4; y++)
            for (var x = 20; x < 40; x++) step[y * 40 + x] = 255;
        var blurred = OutputMaskRaster.TentBlur(step, 40, 4, 9);
        var row = blurred.Take(40).ToArray();
        Assert.Equal(0, row[10]);
        Assert.Equal(255, row[30]);
        Assert.InRange(row[20], 140, 165); // the tent's weights 1…5…1: 15/25 of the kernel is lit at x = 20
        for (var x = 1; x < 40; x++) Assert.True(row[x] >= row[x - 1]);
        Assert.Equal(255 - row[19], row[20]); // symmetric about the edge
    }

    [Fact]
    public void MaskEncodesLikeTheMac()
    {
        var slice = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var mask = new OutputMask("Column", CenterBand, OutputMaskMode.In, 0.1, alwaysOn: false, sliceIds: [slice],
            id: Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"));
        var json = MxuJson.Serialize(mask, indented: false);
        Assert.Equal(
            """{"id":"7C9E6679-7425-40DE-944B-E07FC1F90AE7","name":"Column","pathData":"M 0.25 0 L 0.75 0 L 0.75 1 L 0.25 1 Z","mode":"in","feather":0.1,"alwaysOn":false,"sliceIds":["0F8FAD5B-D9CB-469F-A165-70867728950E"]}""",
            json);
        Assert.Equal(mask, MxuJson.Deserialize<OutputMask>(json)); // lists compare by value
        Assert.DoesNotContain("sliceIds", MxuJson.Serialize(mask with { SliceIds = null }));
    }

    [Fact]
    public void PlansWithTheSameMasksShareAFingerprint()
    {
        var mask = new OutputMask("Column", CenterBand);
        var a = new OutputMaskPlan([mask], 192, 108);
        var b = new OutputMaskPlan(new List<OutputMask> { mask }, 192, 108);
        Assert.Equal(a, b);
        Assert.NotEqual(a.Fingerprint, new OutputMaskPlan([mask], 96, 54).Fingerprint);
        Assert.NotEqual(a.Fingerprint, new OutputMaskPlan([mask with { Feather = 0.2 }], 192, 108).Fingerprint);
    }

    [Fact]
    public void RasterCacheSharesAndPrunes()
    {
        var cache = new OutputMaskRasterCache();
        var fills = 0;
        byte[]? Counting(string path, int w, int h) { fills++; return FillPolygon(path, w, h); }
        var plan = new OutputMaskPlan([new OutputMask("Column", CenterBand)], 192, 108);
        var first = cache.Get(plan, Counting);
        Assert.Same(first, cache.Get(plan with { }, Counting));
        Assert.Equal(1, fills);
        cache.Retain([]);
        Assert.Equal(0, cache.Count);
        Assert.NotSame(first, cache.Get(plan, Counting));
        Assert.Equal(2, fills);
    }
}
