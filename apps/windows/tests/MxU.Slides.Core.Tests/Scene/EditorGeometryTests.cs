using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Scene.EditorGeometry;

namespace MxU.Slides.Core.Tests.Scene;

// Ported from SlideSceneTests/EditorGeometryTests.swift (perspective and homography cases are not ported).
public class EditorGeometryTests
{
    private static SlideObject Obj(string id, double x, double y, double w, double h, double? rotation = null, string? group = null) => new()
    {
        Id = id, ObjectKind = SlideObjectKind.Shape, Name = id, Text = "",
        X = x, Y = y, Width = w, Height = h, RotationDegrees = rotation, GroupId = group,
    };

    private static void Near(double expected, double actual) => Assert.True(Math.Abs(expected - actual) < 0.001, $"expected {expected}, got {actual}");

    [Fact]
    public void HitTestPicksTopmost()
    {
        var objects = new[] { Obj("under", 0, 0, 400, 400), Obj("over", 100, 100, 400, 400) };
        Assert.Equal("over", HitObject(new(200, 200), objects)?.Id);
        Assert.Equal("under", HitObject(new(50, 50), objects)?.Id);
        Assert.Null(HitObject(new(900, 900), objects));
    }

    [Fact]
    public void PressTargetKeepsASelectedObjectUnderAnother()
    {
        var objects = new[] { Obj("text", 100, 100, 200, 100), Obj("outline", 0, 0, 400, 400) };
        var overlap = new EditorGeometry.Point(150, 150);
        Assert.Equal("text", PressTarget(overlap, objects, new HashSet<string> { "text" })?.Id);
        Assert.Equal("outline", PressTarget(overlap, objects, new HashSet<string>())?.Id);
        Assert.Equal("outline", PressTarget(new(350, 350), objects, new HashSet<string> { "text" })?.Id);
        Assert.Null(PressTarget(new(900, 900), objects, new HashSet<string> { "text" }));
    }

    [Fact]
    public void LetterboxCentersTheCanvasInABiggerView()
    {
        var canvas = new SceneSize(1920, 1080);
        var view = new SceneSize(1000, 1000);
        var (scale, origin) = Letterbox(canvas, view);
        Near(1000.0 / 1920.0, scale);
        Near(0, origin.X);
        Near((1000 - 1080 * scale) / 2, origin.Y);
        var center = ScenePoint(new(500, 500), view, canvas);
        Assert.True(Math.Abs(center.X - 960) < 0.01 && Math.Abs(center.Y - 540) < 0.01);
        Assert.True(ScenePoint(new(500, 10), view, canvas).Y < 0);

        var inset = Letterbox(canvas, view, 40);
        Near(920.0 / 1920.0, inset.Scale);
        Near(40, inset.Origin.X);
        Assert.True(ScenePoint(new(20, 500), view, canvas, 40).X < 0, "the band left of the slide is off-canvas scene space");
    }

    [Fact]
    public void HitTestHonorsRotation()
    {
        var bar = Obj("bar", 460, 490, 1000, 20, rotation: 90);
        Assert.Equal("bar", HitObject(new(960, 500), [bar])?.Id);
        Assert.Null(HitObject(new(500, 500), [bar]));
        Assert.Equal("bar", HitObject(new(960, 200), [bar])?.Id);
    }

    [Fact]
    public void SelectionExpandsToWholeGroup()
    {
        var objects = new[] { Obj("a", 0, 0, 10, 10, group: "g1"), Obj("b", 0, 0, 10, 10, group: "g1"), Obj("c", 0, 0, 10, 10, group: ""), Obj("d", 0, 0, 10, 10) };
        Assert.Equal(new HashSet<string> { "a", "b" }, ExpandSelectionToGroups(new HashSet<string> { "a" }, objects));
        Assert.Equal(new HashSet<string> { "c" }, ExpandSelectionToGroups(new HashSet<string> { "c" }, objects));
        Assert.Equal(new HashSet<string> { "d" }, ExpandSelectionToGroups(new HashSet<string> { "d" }, objects));
    }

    [Fact]
    public void ResizeFromCornerMovesOriginAndSize()
    {
        var frame = new SceneRect(100, 100, 200, 100);
        Assert.Equal(new SceneRect(120, 110, 180, 90), Resize(frame, Handle.TopLeft, new(20, 10)));
        Assert.Equal(new SceneRect(100, 100, 250, 130), Resize(frame, Handle.BottomRight, new(50, 30)));
    }

    [Fact]
    public void ResizeEnforcesMinimumSize()
    {
        var frame = new SceneRect(0, 0, 100, 100);
        Assert.Equal(16, Resize(frame, Handle.Right, new(-500, 0)).Width);
        var squeezedLeft = Resize(frame, Handle.Left, new(500, 0));
        Assert.Equal(16, squeezedLeft.Width);
        Assert.Equal(100, squeezedLeft.Right);
    }

    [Fact]
    public void EdgeHandlesResizeOneAxis() =>
        Assert.Equal(new SceneRect(100, 100, 200, 140), Resize(new SceneRect(100, 100, 200, 100), Handle.Bottom, new(999, 40)));

    [Fact]
    public void MoveSnapsToCanvasCenterWithGuides()
    {
        var result = SnapMove(new SceneRect(911, 300, 100, 100), []);
        Assert.Equal(960, result.Frame.X + result.Frame.Width / 2);
        Assert.Contains(new Guide.Vertical(960), result.Guides);
    }

    [Fact]
    public void MoveSnapsToNeighborEdges()
    {
        var result = SnapMove(new SceneRect(805, 600, 100, 100), [new SceneRect(500, 200, 300, 200)]);
        Assert.Equal(800, result.Frame.X);
        Assert.Contains(new Guide.Vertical(800), result.Guides);
    }

    [Fact]
    public void MoveBeyondThresholdDoesNotSnap()
    {
        var frame = new SceneRect(300, 391, 100, 100);
        var result = SnapMove(frame, [], threshold: 8);
        Assert.Equal(frame, result.Frame);
        Assert.Empty(result.Guides);
    }

    [Fact]
    public void ResizeSnapsOnlyTheDraggedEdge()
    {
        var result = SnapResize(new SceneRect(300, 500, 296, 100), Handle.Right, [new SceneRect(0, 0, 600, 400)]);
        Assert.Equal(600, result.Frame.Right);
        Assert.Equal(300, result.Frame.X);
        Assert.Equal([new Guide.Vertical(600)], result.Guides);
    }

    [Fact]
    public void SnapPrefersTheNearestTarget()
    {
        var result = SnapMove(new SceneRect(6, 300, 1912, 100), []);
        Assert.Equal(960, result.Frame.X + result.Frame.Width / 2);
    }

    [Fact]
    public void RotatedBoundsSwapAxesAtNinetyDegrees()
    {
        var frame = new SceneRect(420, -419.9, 1080, 1919.879);
        var bounds = RotatedBounds(frame, 90);
        Near(1919.879, bounds.Width);
        Near(1080, bounds.Height);
        Near(frame.X + frame.Width / 2, bounds.X + bounds.Width / 2);
        Near(frame.Y + frame.Height / 2, bounds.Y + bounds.Height / 2);
        Assert.Equal(frame, RotatedBounds(frame, 0));
    }

    [Fact]
    public void MoveSnapsRotatedVisualBoundsToCanvasEdge()
    {
        var visual = RotatedBounds(new SceneRect(420, -419.9, 1080, 1919.879), 90);
        visual = visual with { X = visual.X + 3 };
        var result = SnapMove(visual, []);
        Near(1920, result.Frame.Right);
        Assert.Contains(new Guide.Vertical(1920), result.Guides);
    }

    [Fact]
    public void ResizeDoesNotSnapRotatedObjects()
    {
        var frame = new SceneRect(300, 500, 296, 100);
        var result = SnapResize(frame, Handle.Right, [], rotationDegrees: 90);
        Assert.Equal(frame, result.Frame);
        Assert.Empty(result.Guides);
    }

    private static AlignUnit Unit(string id, double x, double y, double w, double h) => new([id], new SceneRect(x, y, w, h));

    [Fact]
    public void AlignLeftSnapsUnitsToReferenceEdge()
    {
        var units = new[] { Unit("a", 100, 0, 50, 50), Unit("b", 300, 100, 80, 50) };
        var deltas = AlignDeltas(units, AlignEdge.Left, new SceneRect(100, 0, 280, 150));
        Assert.False(deltas.ContainsKey("a"), "already on the edge — no write, no churn");
        Assert.Equal(new EditorGeometry.Vector(-200, 0), deltas["b"]);
    }

    [Fact]
    public void AlignCenterYCentersEveryUnit()
    {
        var deltas = AlignDeltas([Unit("a", 0, 0, 50, 100), Unit("b", 100, 300, 50, 40)], AlignEdge.CenterY, new SceneRect(0, 0, 1920, 1080));
        Assert.Equal(new EditorGeometry.Vector(0, 490), deltas["a"]);
        Assert.Equal(new EditorGeometry.Vector(0, 220), deltas["b"]);
    }

    [Fact]
    public void AlignMovesGroupMembersByOneDelta()
    {
        var deltas = AlignDeltas([new AlignUnit(["g1", "g2"], new SceneRect(200, 0, 100, 50))], AlignEdge.Right, new SceneRect(0, 0, 1920, 1080));
        Assert.Equal(new EditorGeometry.Vector(1620, 0), deltas["g1"]);
        Assert.Equal(new EditorGeometry.Vector(1620, 0), deltas["g2"]);
    }

    [Fact]
    public void DistributeEqualizesGapsAndPinsEnds()
    {
        var deltas = DistributeDeltas([Unit("a", 0, 0, 100, 50), Unit("b", 120, 0, 50, 50), Unit("c", 500, 0, 100, 50)], horizontally: true);
        Assert.False(deltas.ContainsKey("a"));
        Assert.False(deltas.ContainsKey("c"));
        Assert.Equal(new EditorGeometry.Vector(155, 0), deltas["b"]);
    }

    [Fact]
    public void DistributeSortsByPositionNotSelectionOrder()
    {
        var deltas = DistributeDeltas([Unit("right", 800, 0, 100, 50), Unit("left", 0, 0, 100, 50), Unit("mid", 150, 0, 100, 50)], horizontally: true);
        Assert.Equal(new EditorGeometry.Vector(250, 0), deltas["mid"]);
        Assert.False(deltas.ContainsKey("left"));
        Assert.False(deltas.ContainsKey("right"));
    }

    [Fact]
    public void DistributeNeedsThreeUnits() =>
        Assert.Empty(DistributeDeltas([Unit("a", 0, 0, 100, 50), Unit("b", 500, 0, 100, 50)], horizontally: true));

    private static SceneRect Frames(SlideObject o) => new(o.X ?? 0, o.Y ?? 0, o.Width ?? 0, o.Height ?? 0);

    [Fact]
    public void RubberBandSweepsWhatItTouches()
    {
        var objects = new[] { Obj("inside", 100, 100, 50, 50), Obj("edge", 190, 190, 100, 100), Obj("outside", 500, 500, 50, 50) };
        Assert.Equal(new HashSet<string> { "inside", "edge" }, SweptObjectIds(new SceneRect(0, 0, 200, 200), objects, Frames));
        Assert.Empty(SweptObjectIds(new SceneRect(0, 0, 10, 10), objects, Frames));
    }

    [Fact]
    public void RubberBandHonorsRotationAndSkipsHidden()
    {
        var bar = Obj("bar", 460, 490, 1000, 20, rotation: 90);
        var hidden = Obj("hidden", 0, 0, 100, 100);
        hidden.Hidden = true;
        Assert.Equal(new HashSet<string> { "bar" }, SweptObjectIds(new SceneRect(900, 0, 120, 150), [bar, hidden], Frames));
        Assert.Empty(SweptObjectIds(new SceneRect(0, 0, 50, 50), [hidden], Frames));
    }

    [Fact]
    public void SelectAllTakesEveryObjectOrWhatTheCanvasDraws()
    {
        var hidden = Obj("hidden", 0, 0, 100, 100);
        hidden.Hidden = true;
        var objects = new[] { Obj("title", 0, 0, 10, 10), hidden };
        Assert.Equal(new HashSet<string> { "title", "hidden" }, AllObjectIds(objects, includingHidden: true));
        Assert.Equal(new HashSet<string> { "title" }, AllObjectIds(objects, includingHidden: false));
    }

    [Fact]
    public void RubberBandResolvesFramesThroughTheCanvas() =>
        Assert.Equal(new HashSet<string> { "placed" },
            SweptObjectIds(new SceneRect(800, 800, 100, 100), [Obj("placed", 0, 0, 10, 10)], _ => new SceneRect(850, 850, 20, 20)));
}
