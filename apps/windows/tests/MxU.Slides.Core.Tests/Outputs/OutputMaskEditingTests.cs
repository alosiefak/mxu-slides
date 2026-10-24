using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Tests.Outputs.OutputFixtures;
using Point = MxU.Slides.Core.Scene.EditorGeometry.Point;

namespace MxU.Slides.Core.Tests.Outputs;

// The Mac's MaskEditor.swift has no tests of its own (it is app code); these pin down the pen
// (MaskPenNSView) and library edits (OutputMaskEditor) ported from it.
public class OutputMaskEditingTests
{
    private static readonly SceneSize View = new(800, 450);

    private static (OutputMaskPen Pen, List<(IReadOnlyList<PathAnchor> Anchors, bool Closed)> Done) Pen()
    {
        var pen = new OutputMaskPen();
        var done = new List<(IReadOnlyList<PathAnchor>, bool)>();
        pen.Completed += (anchors, closed) => done.Add((anchors, closed));
        return (pen, done);
    }

    private static void Click(OutputMaskPen pen, double x, double y, int count = 1)
    {
        pen.Press(new Point(x, y), View, count);
        pen.Release();
    }

    [Fact]
    public void ClickingPointsThenTheFirstOneClosesTheShape()
    {
        var (pen, done) = Pen();
        Click(pen, 0.25, 0);
        Click(pen, 0.75, 0);
        Click(pen, 0.75, 1);
        Click(pen, 0.25, 1);
        Assert.Empty(done); // still drawing
        Assert.Equal(4, pen.Anchors.Count);
        Assert.Equal(3, pen.Segments().Count); // not closed while drawing
        Click(pen, 0.251, 0.002); // within 7 px of the first point
        var (anchors, closed) = Assert.Single(done);
        Assert.True(closed);
        Assert.Equal(4, anchors.Count);
        Assert.Empty(pen.Anchors); // ready for the next shape
        var path = OutputMaskLibrary.PathData(anchors, closed)!;
        Assert.Equal("M 0.2500 0.0000 L 0.7500 0.0000 L 0.7500 1.0000 L 0.2500 1.0000 L 0.2500 0.0000 Z", path);

        // The drawn shape masks exactly the band the Mac's tests use.
        var coverage = OutputMaskRaster.Coverage([new OutputMask("Band", path)], 192, 108, FillPolygon)!;
        Assert.Equal(0, At(coverage, 192, 96, 54));
        Assert.Equal(255, At(coverage, 192, 10, 54));
    }

    [Fact]
    public void DoubleClickAndReturnCloseButOnlyWithThreePoints()
    {
        var (pen, done) = Pen();
        Click(pen, 0.1, 0.1);
        Click(pen, 0.5, 0.1);
        Assert.False(pen.PressReturn()); // two points can't close
        Click(pen, 0.5, 0.5, count: 2); // the first click of a double-click places the point…
        Assert.Empty(done);
        Click(pen, 0.5, 0.5, count: 2); // …the second closes the shape
        Assert.Single(done);

        Click(pen, 0.1, 0.1);
        Click(pen, 0.2, 0.1);
        Click(pen, 0.2, 0.2);
        Assert.True(pen.PressReturn());
        Assert.Equal(2, done.Count);
        Assert.True(done[1].Closed);
    }

    [Fact]
    public void EscapeGivesUpTheDrawing()
    {
        var (pen, done) = Pen();
        var cancelled = 0;
        pen.Cancelled += () => cancelled++;
        Click(pen, 0.1, 0.1);
        pen.Move(new Point(0.3, 0.3));
        Assert.NotNull(pen.RubberBand());
        Assert.True(pen.PressEscape());
        Assert.Equal(1, cancelled);
        Assert.Empty(pen.Anchors);
        Assert.Null(pen.RubberBand());
        Assert.Empty(done);
    }

    [Fact]
    public void DraggingWhilePlacingPullsOutMirroredHandles()
    {
        var (pen, _) = Pen();
        pen.Press(new Point(0.5, 0.5), View);
        Assert.True(pen.DragInProgress);
        pen.Drag(new Point(0.6, 0.5));
        pen.Release();
        var anchor = pen.Anchors.Single();
        Assert.Equal(new Point(0.6, 0.5), anchor.HandleOut);
        Assert.Equal(0.4, anchor.HandleIn!.Value.X, 9);
        Assert.Equal(0.5, anchor.HandleIn.Value.Y, 9);
        Click(pen, 0.9, 0.9);
        Assert.False(pen.Segments()[0].IsLine); // the curve leaves the first point along its handle
        pen.Move(new Point(0.2, 0.9));
        Assert.True(pen.RubberBand()!.Value.IsLine); // a point placed with a plain click gives a straight band
    }

    [Fact]
    public void EditingMovesPointsWithTheirHandlesAndSavesOnRelease()
    {
        var (pen, done) = Pen();
        Assert.True(pen.Edit("M 0.2 0.2 C 0.3 0.1 0.4 0.1 0.5 0.2 L 0.5 0.5 Z"));
        Assert.False(pen.IsDrawing);
        Assert.Equal(3, pen.Segments().Count); // closed: the last point joins the first

        // Drag the second point; its in-handle comes along.
        pen.Press(new Point(0.5, 0.2), View);
        pen.Drag(new Point(0.6, 0.3));
        pen.Release();
        var moved = pen.Anchors[1];
        Assert.Equal(new Point(0.6, 0.3), moved.Point);
        Assert.Equal(0.5, moved.HandleIn!.Value.X, 9);
        Assert.Equal(0.2, moved.HandleIn.Value.Y, 9);
        var (anchors, closed) = Assert.Single(done);
        Assert.True(closed);
        Assert.Equal(new Point(0.6, 0.3), anchors[1].Point);

        // Handles win over points: grab the first point's out-handle.
        pen.Press(new Point(0.3, 0.1), View);
        pen.Drag(new Point(0.3, 0.0));
        pen.Release();
        Assert.Equal(new Point(0.3, 0.0), pen.Anchors[0].HandleOut);
        Assert.Equal(new Point(0.2, 0.2), pen.Anchors[0].Point);

        // A press on nothing changes nothing, but still reports the (unchanged) shape.
        pen.Press(new Point(0.9, 0.9), View);
        Assert.False(pen.DragInProgress);
        pen.Release();
        Assert.Equal(3, done.Count);
        Assert.False(pen.Edit("M 0 0 L 1 0 M 0 1 L 1 1")); // two shapes can't be edited as one
    }

    [Fact]
    public void PointsCanBeRemovedDownToThree()
    {
        var (pen, done) = Pen();
        pen.Edit("M 0.1 0.1 L 0.9 0.1 L 0.9 0.9 L 0.1 0.9 Z");
        Assert.True(pen.RemovePoint(new Point(0.9, 0.9), View));
        Assert.Equal(3, pen.Anchors.Count);
        Assert.Single(done);
        Assert.False(pen.RemovePoint(new Point(0.1, 0.1), View)); // a mask keeps three points
        Assert.False(pen.RemovePoint(new Point(0.5, 0.5), View));
    }

    [Fact]
    public void ViewPositionsAreClampedIntoTheScreen()
    {
        Assert.Equal(new Point(0.5, 1), OutputMaskPen.Unit(400, 900, View));
        Assert.Equal(new Point(0, 0), OutputMaskPen.Unit(-20, -5, View));
        Assert.Equal(new Point(0, 0), OutputMaskPen.Unit(10, 10, new SceneSize(0, 0)));
        Assert.Equal(new Point(400, 225), OutputMaskPen.ToView(new Point(0.5, 0.5), View));
    }

    [Fact]
    public void LibraryEditsNameChangeAndRemoveMasks()
    {
        var library = OutputMaskLibrary.Add([], CenterBand);
        library = OutputMaskLibrary.Add(library, LeftHalf);
        Assert.Equal(["Mask 1", "Mask 2"], library.Select(m => m.Name));
        Assert.All(library, m => Assert.True(m is { Mode: OutputMaskMode.Out, AlwaysOn: true, Feather: 0, SliceIds: null }));
        var second = library[1].Id;
        var changed = OutputMaskLibrary.Change(library, second, m => m with { Name = "Pillar", Mode = OutputMaskMode.In });
        Assert.Equal("Pillar", changed[1].Name);
        Assert.Equal(OutputMaskMode.In, changed[1].Mode);
        Assert.Same(library[0], changed[0]);
        Assert.Equal(["Mask 1"], OutputMaskLibrary.Remove(changed, second).Select(m => m.Name));
        Assert.Equal(0.25, OutputMaskLibrary.ClampFeather(3));
        Assert.Equal(0, OutputMaskLibrary.ClampFeather(-1));
        Assert.Equal(0, OutputMaskLibrary.ClampFeather(double.NaN));
    }

    [Fact]
    public void QuickRectanglesAndWhichOutputsAMaskCovers()
    {
        Assert.Equal("M 0.2500 0.0000 L 0.7500 0.0000 L 0.7500 1.0000 L 0.2500 1.0000 L 0.2500 0.0000 Z",
            OutputMaskLibrary.Rectangle(0.75, 1.2, -0.5, -1.5)); // dragged backwards and past the edge
        Assert.Null(OutputMaskLibrary.Rectangle(0.5, 0.5, 0, 0.2));

        var mask = new OutputMask("Column", CenterBand);
        var left = Guid.NewGuid();
        string? Name(Guid id) => id == left ? "Left projector" : null;
        Assert.Equal("All outputs", OutputMaskLibrary.CoversLabel(mask, Name));
        var onLeft = OutputMaskLibrary.Covering(mask, left);
        Assert.True(onLeft.Covers(left));
        Assert.False(onLeft.Covers(Guid.NewGuid()));
        Assert.Equal("Left projector", OutputMaskLibrary.CoversLabel(onLeft, Name));
        Assert.Equal("2 outputs", OutputMaskLibrary.CoversLabel(mask with { SliceIds = [left, Guid.NewGuid()] }, Name));
        Assert.Equal("An output that's gone", OutputMaskLibrary.CoversLabel(OutputMaskLibrary.Covering(mask, Guid.NewGuid()), Name));
        Assert.Null(OutputMaskLibrary.Covering(onLeft, null).SliceIds);
    }

    [Fact]
    public void ADrawnMaskReachesTheScreensRaster()
    {
        // The editor's whole path: draw, encode, add to the screen's library, and the screen is masked.
        var outputs = new OutputConfiguration { MaskPathFill = FillPolygon };
        var screen = outputs.AddScreen("Main", 192, 108)!;
        var (pen, _) = Pen();
        pen.Completed += (anchors, closed) =>
            outputs.SetMasks(screen.Id, OutputMaskLibrary.Add(outputs.Masks(screen.Id), OutputMaskLibrary.PathData(anchors, closed)!));
        Click(pen, 0, 0);
        Click(pen, 0.5, 0);
        Click(pen, 0.5, 1);
        Click(pen, 0, 1);
        Assert.True(pen.PressReturn());
        var raster = outputs.MaskCoverage(screen.Id);
        Assert.NotNull(raster);
        Assert.Equal(0, At(raster, 192, 10, 54)); // the left half is black
        Assert.Equal(255, At(raster, 192, 150, 54));
    }
}
