using System.Text.Json;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Tests.Outputs.OutputFixtures;

namespace MxU.Slides.Core.Tests.Outputs;

// Ported from RenderEngineTests/OutputAdjustTests.swift. The Mac renders on the
// GPU and reads pixels back; these run the same cases through the pass's
// geometry (where each target pixel samples the canvas) and the CPU reference
// of its shader, at the same pixels and with the same expectations.
public class OutputAdjustTests
{
    private static OutputPixel WhiteAt(double u, double v) => White;

    [Fact]
    public void NeutralAdjustmentsPassThrough()
    {
        Assert.Null(OutputPass.Plan(new OutputAdjustments(), null, null, false, new SceneSize(192, 108), 192, 108)); // no pass at all
        var center = RenderPixel(WhiteAt, new OutputAdjustments(), 192, 108, 96, 54);
        Assert.True(center.R > 0.95 && center.B > 0.95);
    }

    [Fact]
    public void SourceRectShowsTheSliceEdgeToEdge()
    {
        var slice = new SceneRect(0, 0, 0.5, 1);
        var center = RenderPixel(Halves, new OutputAdjustments(), 96, 108, 48, 54, slice);
        var nearRight = RenderPixel(Halves, new OutputAdjustments(), 96, 108, 92, 54, slice);
        Assert.True(center.R > 0.9 && center.B < 0.1, "slice shows red");
        Assert.True(nearRight.R > 0.9, "red runs to the slice's edge — no mid-canvas cut");
        Assert.True(nearRight.B < 0.1, "no blue leak from the neighbor slice");
    }

    [Fact]
    public void BlendRampSitsAtTheSliceEdge()
    {
        var adjustments = new OutputAdjustments { BlendRight = new OutputEdgeBlend(0.3, curve: 2) };
        var slice = new SceneRect(0, 0, 0.5, 1);
        Assert.True(RenderPixel(Halves, adjustments, 96, 108, 30, 54, slice).R > 0.9, "outside the ramp stays full");
        Assert.True(RenderPixel(Halves, adjustments, 96, 108, 94, 54, slice).R < 0.2, "the seam edge ramps toward black");
    }

    [Fact]
    public void QuarterTurnRotationFillsTheFrame()
    {
        static OutputPixel TopRedBottomBlue(double u, double v) => v < 0.5 ? Red : Blue;
        var adjustments = new OutputAdjustments { RotationDegrees = 90 };
        var right = RenderPixel(TopRedBottomBlue, adjustments, 192, 108, 170, 54);
        var left = RenderPixel(TopRedBottomBlue, adjustments, 192, 108, 20, 54);
        Assert.True(right.R > 0.9 && right.B < 0.1, "canvas top rotates onto the right");
        Assert.True(left.B > 0.9 && left.R < 0.1, "canvas bottom rotates onto the left");
        // The turned picture is squeezed to fit, so its corners are still the frame's corners.
        var corners = OutputPass.PixelCorners(adjustments, null, 192, 108);
        Assert.Equal((192.0, 0.0), Round(corners[0])); // the canvas's top-left is now top-right
        Assert.Equal((0.0, 108.0), Round(corners[3])); // and its bottom-right is now bottom-left
    }

    [Fact]
    public void AdjustedPassPreservesAlpha()
    {
        static OutputPixel LeftHalfWhite(double u, double v) => u < 0.5 ? White : OutputPixel.Transparent;
        var adjustments = new OutputAdjustments { Brightness = 0.2 };
        var covered = RenderPixel(LeftHalfWhite, adjustments, 192, 108, 40, 54);
        var empty = RenderPixel(LeftHalfWhite, adjustments, 192, 108, 150, 54);
        Assert.True(covered.A > 0.95, "content keeps its key");
        Assert.True(empty.A < 0.05, "the keyer still cuts where nothing rendered");
        Assert.True(empty.R < 0.05, "a brightness lift never paints the transparent region");
    }

    [Fact]
    public void BlendIntensityBacksTheRampOff()
    {
        var adjustments = new OutputAdjustments { BlendRight = new OutputEdgeBlend(0.3, curve: 1, intensity: 0.5) };
        Assert.Equal(0.5, RenderPixel(WhiteAt, adjustments, 192, 108, 191, 54).R, 0.06); // a half-depth ramp bottoms out at half
        Assert.True(RenderPixel(WhiteAt, adjustments, 192, 108, 60, 54).R > 0.95, "outside the ramp untouched");
    }

    [Fact]
    public void BlackLiftCompensatesOutsideTheOverlap()
    {
        static OutputPixel Black(double u, double v) => OpaqueBlack;
        var adjustments = new OutputAdjustments { BlendRight = new OutputEdgeBlend(0.3, curve: 1, blackLift: 0.2) };
        // non-overlap black lifts to match the doubled seam black
        Assert.Equal(0.2, RenderPixel(Black, adjustments, 192, 108, 40, 54).R, 0.02);
        // at the seam the lift tapers out: the neighbour supplies it
        Assert.True(RenderPixel(Black, adjustments, 192, 108, 190, 54).R < 0.03);
    }

    [Fact]
    public void PlacementLandsContentInTheFrameRect()
    {
        static OutputPixel RedAt(double u, double v) => Red;
        var placement = new SceneRect(0, 0, 0.5, 0.5);
        Assert.True(RenderPixel(RedAt, new OutputAdjustments(), 192, 108, 40, 25, placement: placement).R > 0.9, "content inside the placed rect");
        var outside = RenderPixel(RedAt, new OutputAdjustments(), 192, 108, 150, 25, placement: placement);
        Assert.True(outside.R < 0.05, "outside the rect is black");
        Assert.True(outside.A < 0.05, "…and carries no key");
        Assert.True(RenderPixel(RedAt, new OutputAdjustments(), 192, 108, 40, 80, placement: placement).R < 0.05);
        // The 96×54 canvas lands at exactly its own size, so it is drawn at 96×54 first.
        var plan = OutputPass.Plan(null, null, placement, false, new SceneSize(96, 54), 192, 108);
        Assert.NotNull(plan);
        Assert.Equal((96, 54), (plan.IntermediateWidth, plan.IntermediateHeight));
    }

    [Fact]
    public void PlacementComposesWithSliceAndRotation()
    {
        var adjustments = new OutputAdjustments { RotationDegrees = 180 };
        var slice = new SceneRect(0, 0, 0.5, 1);
        var placement = new SceneRect(0.5, 0, 0.5, 0.5);
        Assert.True(RenderPixel(Halves, adjustments, 192, 108, 150, 25, slice, placement).R > 0.9, "placed region carries the slice");
        Assert.True(RenderPixel(Halves, adjustments, 192, 108, 40, 25, slice, placement).R < 0.05, "outside stays black");
        Assert.True(RenderPixel(Halves, adjustments, 192, 108, 150, 80, slice, placement).R < 0.05);
    }

    [Fact]
    public void CompositePacksLayersIntoOneFrame()
    {
        static OutputPixel RedAt(double u, double v) => Red;
        static OutputPixel BlueAt(double u, double v) => Blue;
        var none = new OutputAdjustments();
        var first = new SceneRect(0, 0, 0.5, 0.5);
        var second = new SceneRect(0.75, 0, 0.25, 0.9);
        // Layers draw one over the other; a pixel outside a layer's quad is transparent for it.
        OutputPixel Frame(int x, int y)
        {
            var top = RenderPixel(BlueAt, none, 192, 108, x, y, placement: second);
            return top.A > 0 ? top : RenderPixel(RedAt, none, 192, 108, x, y, placement: first);
        }
        Assert.True(Frame(40, 25).R > 0.9, "first layer lands at its rect");
        Assert.True(Frame(170, 40).B > 0.9, "second layer lands at its rect");
        Assert.True(Frame(120, 25) is { R: < 0.05, B: < 0.05 }, "between the regions is black");
        Assert.True(Frame(40, 90).R < 0.05, "below the first region is black");
        // A 48×96 canvas landing in a 48×97.2 px region is drawn a little larger, rounded to whole pixels.
        var plan = OutputPass.Plan(null, null, second, false, new SceneSize(48, 96), 192, 108);
        Assert.NotNull(plan);
        Assert.Equal((49, 97), (plan.IntermediateWidth, plan.IntermediateHeight));
    }

    [Fact]
    public void CompletionOverloadAppliesAdjustments()
    {
        var center = RenderPixel(WhiteAt, new OutputAdjustments { Brightness = -1 }, 192, 108, 96, 54);
        Assert.True(center.R < 0.05 && center.B < 0.05, "brightness -1 zeroes a white frame on the feed path");
    }

    [Fact]
    public void ColorMathLandsWhereTheSlidersSay()
    {
        Assert.Equal(0.5, RenderPixel(WhiteAt, new OutputAdjustments { Brightness = -0.5 }, 192, 108, 96, 54).R, 0.02);

        var trimmed = RenderPixel(WhiteAt, new OutputAdjustments { BlueLevel = -1 }, 192, 108, 96, 54);
        Assert.True(trimmed.R > 0.95, "red untouched");
        Assert.Equal(0, trimmed.B, 0.02); // blue gain -1 = channel off

        Assert.Equal(1, RenderPixel(WhiteAt, new OutputAdjustments { Gamma = 1 }, 192, 108, 96, 54).R, 0.02);
    }

    [Fact]
    public void CornerPinLeavesProjectorBlackOutsideTheQuad()
    {
        var adjustments = new OutputAdjustments { TopLeft = new OutputPoint(60, 40) };
        var outside = RenderPixel(WhiteAt, adjustments, 192, 108, 4, 4);
        Assert.Equal(0, outside.R, 0.01); // outside the pinned quad is black
        Assert.Equal(0, outside.A, 0.01); // …and carries no key
        Assert.True(RenderPixel(WhiteAt, adjustments, 192, 108, 96, 54).R > 0.9, "center still content");
        Assert.True(RenderPixel(WhiteAt, adjustments, 192, 108, 186, 102).R > 0.9, "unpinned corner untouched");
    }

    [Fact]
    public void EdgeBlendRampsTheEdge()
    {
        var adjustments = new OutputAdjustments { BlendLeft = new OutputEdgeBlend(0.5, curve: 1) };
        var nearEdge = RenderPixel(WhiteAt, adjustments, 192, 108, 19, 54).R;
        var midRamp = RenderPixel(WhiteAt, adjustments, 192, 108, 48, 54).R;
        var center = RenderPixel(WhiteAt, adjustments, 192, 108, 130, 54).R;
        Assert.Equal(0.2, nearEdge, 0.05);
        Assert.Equal(0.5, midRamp, 0.05);
        Assert.True(center > 0.95);
        Assert.True(nearEdge < midRamp);
    }

    [Fact]
    public void HomographyIdentityAndDegenerateFallback()
    {
        (double X, double Y)[] corners = [(-1, 1), (1, 1), (-1, -1), (1, -1)];
        (double X, double Y)[] sources = [(0, 0), (1, 0), (0, 1), (1, 1)];
        var identity = OutputWarp.ProjectiveUvs(corners, sources);
        Assert.NotNull(identity);
        foreach (var (uvq, source) in identity.Zip(sources))
        {
            Assert.Equal(source.X, uvq.Uq / uvq.Q, 1e-6);
            Assert.Equal(source.Y, uvq.Vq / uvq.Q, 1e-6);
        }

        (double X, double Y)[] collapsed = [(0, 0), (0, 0), (0, 0), (0, 0)];
        Assert.Null(OutputWarp.ProjectiveUvs(collapsed, sources));
        Assert.Null(OutputWarp.Homography(collapsed, sources));
        Assert.Null(OutputPass.Geometry(new OutputAdjustments
        {
            TopLeft = new(96, 54), TopRight = new(-96, 54), BottomLeft = new(96, -54), BottomRight = new(-96, -54),
        }, OutputPass.FullSourceRect, null, 192, 108).Homography); // every corner pinned to the centre
    }

    // ── The homography the WPF pixel shader evaluates ───────────────────────

    [Fact]
    public void HomographyMapsEachDestinationCornerOntoItsSource()
    {
        (double X, double Y)[] destinations = [(0.1, 0.05), (0.92, 0.12), (0.03, 0.97), (0.88, 0.9)];
        (double X, double Y)[] sources = [(0.5, 0), (1, 0), (0.5, 1), (1, 1)];
        var h = OutputWarp.Homography(destinations, sources);
        Assert.NotNull(h);
        Assert.Equal(9, h.Length);
        Assert.Equal(1, h[8]);
        for (var i = 0; i < 4; i++)
        {
            var (x, y) = destinations[i];
            var q = h[6] * x + h[7] * y + 1;
            Assert.Equal(sources[i].X, (h[0] * x + h[1] * y + h[2]) / q, 1e-9);
            Assert.Equal(sources[i].Y, (h[3] * x + h[4] * y + h[5]) / q, 1e-9);
        }
    }

    [Fact]
    public void PassGeometryIsInTheTargetsUnitSquareAndMapsCornersToTheSlice()
    {
        var adjustments = new OutputAdjustments
        {
            TopLeft = new(40, 20), BottomRight = new(-30, -10), RotationDegrees = 7,
        };
        var slice = new SceneRect(0.5, 0, 0.5, 1);
        var placement = new SceneRect(0.1, 0.1, 0.8, 0.8);
        var geometry = OutputPass.Geometry(adjustments, slice, placement, 1920, 1080);
        var pixels = OutputPass.PixelCorners(adjustments, placement, 1920, 1080);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(pixels[i].X / 1920, geometry.UnitCorners[i].X, 1e-12);
            Assert.Equal(pixels[i].Y / 1080, geometry.UnitCorners[i].Y, 1e-12);
            var uv = geometry.SourceUv(geometry.UnitCorners[i].X, geometry.UnitCorners[i].Y);
            Assert.NotNull(uv);
            Assert.Equal(geometry.SourceCorners[i].X, uv.Value.U, 1e-9);
            Assert.Equal(geometry.SourceCorners[i].Y, uv.Value.V, 1e-9);
        }
        Assert.Equal([(0.5, 0.0), (1.0, 0.0), (0.5, 1.0), (1.0, 1.0)], geometry.SourceCorners);
    }

    [Fact]
    public void CornersStartAtThePlacementAndMoveByTheirPins()
    {
        var pinned = OutputPass.PixelCorners(new OutputAdjustments { TopRight = new(-10, 5) }, new SceneRect(0.5, 0, 0.5, 0.5), 200, 100);
        Assert.Equal([(100.0, 0.0), (190.0, 5.0), (100.0, 50.0), (200.0, 50.0)], pinned.Select(Round));
        // Clip space is the same corners with y flipped, −1…1: the Mac's vertex positions.
        var ndc = OutputPass.NdcCorners(new OutputAdjustments(), null, 200, 100);
        Assert.Equal([(-1.0, 1.0), (1.0, 1.0), (-1.0, -1.0), (1.0, -1.0)], ndc.Select(Round));
    }

    [Fact]
    public void UnitSpaceAndClipSpaceWarpsAgree()
    {
        // The Mac solves the warp in clip space; Windows in unit space. Both must show the same uv at any point.
        var adjustments = new OutputAdjustments { TopLeft = new(60, 40), RotationDegrees = -12 };
        var unit = OutputPass.Geometry(adjustments, OutputPass.FullSourceRect, null, 192, 108);
        var clip = OutputWarp.Homography(OutputPass.NdcCorners(adjustments, null, 192, 108), OutputPass.SourceCorners(OutputPass.FullSourceRect))!;
        foreach (var (x, y) in new[] { (0.5, 0.5), (0.3, 0.7), (0.8, 0.2) })
        {
            var a = unit.SourceUv(x, y)!.Value;
            var b = OutputWarp.Map(clip, x * 2 - 1, 1 - y * 2)!.Value;
            Assert.Equal(b.U, a.U, 1e-9);
            Assert.Equal(b.V, a.V, 1e-9);
        }
    }

    [Fact]
    public void QCanBeNegativeInsideTheQuadUnlessTheMatrixIsCentred()
    {
        // With h8 = 1 the matrix is pinned at the unit origin, the target's top-left
        // corner. Pull that corner in (here by 31% × 37%) and the origin ends up beyond
        // the warp's horizon, so q is negative over the whole picture. The ratio is still
        // right, but a shader that clamps q (max(q, 1e-6)) would lose the picture.
        var adjustments = new OutputAdjustments { TopLeft = new OutputPoint(60, 40) };
        var corners = OutputPass.UnitCorners(adjustments, null, 192, 108);
        var sources = OutputPass.SourceCorners(OutputPass.FullSourceRect);
        var pinned = OutputWarp.Homography(corners, sources)!;
        static double Q(double[] h, double x, double y) => h[6] * x + h[7] * y + h[8];
        Assert.True(Q(pinned, 0.6, 0.6) < 0, "q inside the picture, h8 = 1");
        var uv = OutputWarp.Map(pinned, 0.6, 0.6)!.Value;
        Assert.InRange(uv.U, 0, 1);
        Assert.InRange(uv.V, 0, 1);

        // The pass geometry's matrix is the same transform scaled to q = 1 at the picture's centre.
        var centred = OutputPass.Geometry(adjustments, OutputPass.FullSourceRect, null, 192, 108).Homography!;
        Assert.Equal(1, Q(centred, corners.Average(c => c.X), corners.Average(c => c.Y)), 1e-9);
        Assert.All(corners, c => Assert.True(Q(centred, c.X, c.Y) > 0));
        var same = OutputWarp.Map(centred, 0.6, 0.6)!.Value;
        Assert.Equal(uv.U, same.U, 1e-9);
        Assert.Equal(uv.V, same.V, 1e-9);
        // Evaluated the way the WPF shader does, clamp included.
        var q = Math.Max(Q(centred, 0.6, 0.6), 1e-6);
        Assert.Equal(uv.U, (centred[0] * 0.6 + centred[1] * 0.6 + centred[2]) / q, 1e-9);

        // In clip space (the Mac's) the origin is the centre, inside the picture, so q is positive at every corner.
        var clip = OutputWarp.ProjectiveUvs(OutputPass.NdcCorners(adjustments, null, 192, 108), sources)!;
        Assert.All(clip, uvq => Assert.True(uvq.Q > 0));
    }

    [Fact]
    public void EdgeUniformsAreClampedAsTheMacHandsThemToTheShader()
    {
        Assert.Equal(OutputEdgeUniform.Off, OutputPass.Edge(null));
        Assert.Equal(OutputEdgeUniform.Off, OutputPass.Edge(new OutputEdgeBlend(0)));
        Assert.Equal(new OutputEdgeUniform(1, 1, 0, 1), OutputPass.Edge(new OutputEdgeBlend(3, curve: -1, intensity: -2, blackLift: 9)));
        Assert.Equal(new OutputEdgeUniform(0.2, 2.2, 1, 0), OutputPass.Edge(new OutputEdgeBlend(0.2)));
        var uniforms = OutputPass.Uniforms(new OutputAdjustments { Contrast = 0.3 }, new SceneRect(0.5, 0, 0, 1), hasMask: true);
        Assert.Equal(0.3, uniforms.Contrast);
        Assert.True(uniforms.HasMask);
        Assert.Equal(1e-6, uniforms.SourceRect.Width); // never divides by zero
    }

    [Fact]
    public void NeutralityIgnoresFullTurnsAndZeroWidthBlends()
    {
        Assert.True(new OutputAdjustments().IsNeutral);
        Assert.True(new OutputAdjustments { RotationDegrees = 720, BlendTop = new OutputEdgeBlend(0), TopLeft = OutputPoint.Zero }.IsNeutral);
        Assert.False(new OutputAdjustments { RotationDegrees = 90 }.IsNeutral);
        Assert.False(new OutputAdjustments { BottomRight = new(0, 1) }.IsNeutral);
        Assert.False(new OutputAdjustments { RedLevel = 0.1 }.IsNeutral);
    }

    [Fact]
    public void PassIsPlannedForACropAMaskOrACorrection()
    {
        var canvas = new SceneSize(1920, 1080);
        Assert.Null(OutputPass.Plan(null, OutputPass.FullSourceRect, OutputPass.FullSourceRect, false, canvas, 1920, 1080));
        var masked = OutputPass.Plan(null, null, null, true, canvas, 1280, 720)!;
        Assert.Equal((1280, 720, OutputPass.FullSourceRect), (masked.IntermediateWidth, masked.IntermediateHeight, masked.SourceRect));
        // A half-canvas slice on a 1920×1080 projector draws the scene at twice its width so the half is full resolution.
        var slice = OutputPass.Plan(null, new SceneRect(0, 0, 0.5, 1), null, false, canvas, 1920, 1080)!;
        Assert.Equal((3840, 2160), (slice.IntermediateWidth, slice.IntermediateHeight));
        Assert.Equal(new OutputAdjustments(), slice.Adjustments);
    }

    [Fact]
    public void AdjustmentsEncodeLikeTheMac()
    {
        var adjustments = new OutputAdjustments
        {
            TopLeft = new(60, 40), Brightness = -0.5, BlendRight = new OutputEdgeBlend(0.3, curve: 1, intensity: 0.5),
        };
        var json = MxuJson.Serialize(adjustments, indented: false);
        Assert.Equal("""{"topLeft":[60,40],"brightness":-0.5,"blendRight":{"width":0.3,"curve":1,"intensity":0.5}}""", json);
        Assert.Equal(adjustments, MxuJson.Deserialize<OutputAdjustments>(json));
        // Mac-written JSON with a blend missing nothing optional reads back the same.
        var mac = MxuJson.Deserialize<OutputAdjustments>("""{"rotationDegrees":90,"blendLeft":{"width":0.1,"curve":2.2},"bottomRight":[-3.5,2]}""");
        Assert.Equal(new OutputAdjustments { RotationDegrees = 90, BlendLeft = new OutputEdgeBlend(0.1), BottomRight = new(-3.5, 2) }, mac);
        Assert.Throws<JsonException>(() => MxuJson.Deserialize<OutputAdjustments>("""{"topLeft":{"x":1,"y":2}}"""));
    }

    private static (double X, double Y) Round((double X, double Y) p) => (Math.Round(p.X, 9) + 0.0, Math.Round(p.Y, 9) + 0.0);
}
