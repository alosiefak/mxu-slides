using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using Point = MxU.Slides.Core.Scene.EditorGeometry.Point;

namespace MxU.Slides.Core.Tests.Scene;

// Ported from the ShapeOutlineSVG cases in SlideSceneTests/SlideSceneBuilderTests.swift. The Mac's
// second test flattens the outline with Core Graphics; here it is read back with PathAnchorCodec and
// each curve sampled.
public class ShapeOutlineSvgTests
{
    [Fact]
    public void RoundedRectOutlineUsesPerAxisRadiiAndRasterizerClamp()
    {
        var data = ShapeOutlineSvg.PathData(ShapeKind.RoundedRectangle, new SceneSize(400, 100), 30, null, ShapeTextPlacement.EdgeOutside)!;
        Assert.Contains("L 0.925000 0", data); // 30 / 400 across
        Assert.Contains("1.000000 0.300000", data); // 30 / 100 down
        Assert.EndsWith("Z", data);

        var clamped = ShapeOutlineSvg.PathData(ShapeKind.RoundedRectangle, new SceneSize(400, 100), 500, null, ShapeTextPlacement.EdgeOutside)!;
        Assert.Contains("L 0.875000 0", clamped); // clamped to half the short side, 50
    }

    [Fact]
    public void GeneratedOutlineParsesFlattensClosedAndMatchesCornerRadius()
    {
        var frame = new SceneSize(400, 100);
        var data = ShapeOutlineSvg.PathData(ShapeKind.RoundedRectangle, frame, 30, null, ShapeTextPlacement.EdgeOutside)!;
        var parsed = PathAnchorCodec.Parse(data, new SceneRect(frame));
        Assert.NotNull(parsed);
        Assert.True(parsed.Value.Closed);

        var center = new Point(370, 30);
        foreach (var point in Flatten(parsed.Value.Anchors).Where(p => p.X > 370 && p.Y < 30))
        {
            var radial = Math.Sqrt((point.X - center.X) * (point.X - center.X) + (point.Y - center.Y) * (point.Y - center.Y));
            Assert.True(Math.Abs(radial - 30) < 0.35, $"corner point must ride the r=30 circle (got {radial})");
        }
    }

    // ── Beyond the Mac tests ────────────────────────────────────────────────

    [Fact]
    public void ShapesStartAtTheTopUnlessTextSitsInsideTheEdge()
    {
        Assert.StartsWith("M 0.5 0 ", ShapeOutlineSvg.PathData(null, new SceneSize(100, 100), null, null, ShapeTextPlacement.Inside));
        Assert.StartsWith("M 0.5 1 ", ShapeOutlineSvg.PathData(ShapeKind.Rectangle, new SceneSize(100, 100), null, null, ShapeTextPlacement.EdgeInside));
        Assert.Equal(
            "M 0.5 0 C 0.776142 0 1 0.223858 1 0.5 C 1 0.776142 0.776142 1 0.5 1 C 0.223858 1 0 0.776142 0 0.5 C 0 0.223858 0.223858 0 0.5 0 Z",
            ShapeOutlineSvg.PathData(ShapeKind.Ellipse, new SceneSize(80, 40), null, null, ShapeTextPlacement.EdgeOutside));
    }

    [Fact]
    public void AnEllipseOutlineRidesTheEllipse()
    {
        var data = ShapeOutlineSvg.PathData(ShapeKind.Ellipse, new SceneSize(200, 200), null, null, ShapeTextPlacement.EdgeOutside)!;
        var parsed = PathAnchorCodec.Parse(data, new SceneRect(0, 0, 200, 200));
        Assert.NotNull(parsed);
        Assert.Equal(4, parsed.Value.Anchors.Count); // the closing point folds onto the first
        foreach (var p in Flatten(parsed.Value.Anchors))
            Assert.InRange(Math.Sqrt((p.X - 100) * (p.X - 100) + (p.Y - 100) * (p.Y - 100)), 99.9, 100.1);
    }

    [Fact]
    public void CustomPathsAndEmptyFrames()
    {
        Assert.Equal("M 0 0 L 1 1 Z", ShapeOutlineSvg.PathData(ShapeKind.Path, new SceneSize(10, 10), null, "M 0 0 L 1 1 Z", ShapeTextPlacement.Inside));
        Assert.Null(ShapeOutlineSvg.PathData(ShapeKind.Path, new SceneSize(10, 10), null, "", ShapeTextPlacement.Inside));
        Assert.Null(ShapeOutlineSvg.PathData(ShapeKind.Rectangle, new SceneSize(0, 10), null, null, ShapeTextPlacement.Inside));
    }

    /// <summary>Samples every segment of a closed anchor path: lines at their ends, curves at 32 points.</summary>
    private static List<Point> Flatten(IReadOnlyList<PathAnchor> anchors)
    {
        var points = new List<Point>();
        for (var i = 0; i < anchors.Count; i++)
        {
            var from = anchors[i];
            var to = anchors[(i + 1) % anchors.Count];
            if (from.HandleOut is null && to.HandleIn is null)
            {
                points.Add(to.Point);
                continue;
            }
            var c1 = from.HandleOut ?? from.Point;
            var c2 = to.HandleIn ?? to.Point;
            for (var step = 1; step <= 32; step++)
            {
                var t = step / 32.0;
                var u = 1 - t;
                points.Add(new Point(
                    u * u * u * from.Point.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * to.Point.X,
                    u * u * u * from.Point.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * to.Point.Y));
            }
        }
        return points;
    }
}
