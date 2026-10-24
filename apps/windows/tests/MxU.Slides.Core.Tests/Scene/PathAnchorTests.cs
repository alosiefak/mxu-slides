using MxU.Slides.Core.Scene;
using Point = MxU.Slides.Core.Scene.EditorGeometry.Point;

namespace MxU.Slides.Core.Tests.Scene;

// Ported from SlideSceneTests/PathAnchorTests.swift, plus a few cases the mask editor leans on.
public class PathAnchorTests
{
    private static readonly SceneRect Unit = new(0, 0, 1, 1);

    [Fact]
    public void EncodeParseRoundTripsAnchors()
    {
        PathAnchor[] anchors =
        [
            new(new Point(100, 200), HandleOut: new Point(180, 120)),
            new(new Point(400, 200), HandleIn: new Point(320, 120)),
            new(new Point(250, 420)),
        ];
        var encoded = PathAnchorCodec.Encode(anchors, closed: true);
        Assert.NotNull(encoded);
        var decoded = PathAnchorCodec.Parse(encoded.Value.PathData, encoded.Value.Frame);
        Assert.NotNull(decoded);
        Assert.True(decoded.Value.Closed);
        Assert.Equal(3, decoded.Value.Anchors.Count);
        foreach (var (a, b) in anchors.Zip(decoded.Value.Anchors))
        {
            Assert.True(Math.Abs(a.Point.X - b.Point.X) < 0.5);
            Assert.True(Math.Abs(a.Point.Y - b.Point.Y) < 0.5);
            Assert.Equal(a.HandleOut is null, b.HandleOut is null);
            if (a.HandleOut is { } ha && b.HandleOut is { } hb)
            {
                Assert.True(Math.Abs(ha.X - hb.X) < 0.5);
                Assert.True(Math.Abs(ha.Y - hb.Y) < 0.5);
            }
        }
    }

    [Fact]
    public void StraightSegmentsEncodeAsLines()
    {
        PathAnchor[] anchors = [new(new Point(0, 0)), new(new Point(100, 0)), new(new Point(100, 100))];
        var encoded = PathAnchorCodec.Encode(anchors, closed: false);
        Assert.Contains(" L ", encoded?.PathData);
        Assert.DoesNotContain(" C ", encoded?.PathData);
        Assert.False(encoded?.PathData.EndsWith('Z'));
    }

    [Fact]
    public void QuadraticsParseAsExactCubics()
    {
        var parsed = PathAnchorCodec.Parse("M 0 0 Q 0.5 1 1 0", new SceneRect(0, 0, 100, 100));
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed.Value.Anchors.Count);
        Assert.NotNull(parsed.Value.Anchors[0].HandleOut);
        Assert.NotNull(parsed.Value.Anchors[1].HandleIn);
    }

    [Fact]
    public void MultipleSubpathsAreRefusedNotFlattened() =>
        Assert.Null(PathAnchorCodec.Parse("M 0 0 L 1 0 M 0 1 L 1 1", new SceneRect(0, 0, 100, 100)));

    [Fact]
    public void DegenerateSpansKeepMinimumExtent()
    {
        PathAnchor[] anchors = [new(new Point(50, 0)), new(new Point(50, 300))];
        var encoded = PathAnchorCodec.Encode(anchors, closed: false);
        Assert.True((encoded?.Frame.Width ?? 0) >= 1);
    }

    [Fact]
    public void EncodeAbsoluteRoundTripsCanvasSpaceMasks()
    {
        PathAnchor[] anchors =
        [
            new(new Point(0.25, 0.4)),
            new(new Point(0.75, 0.4), HandleIn: new Point(0.6, 0.3), HandleOut: new Point(0.9, 0.5)),
            new(new Point(0.5, 0.8)),
        ];
        var data = PathAnchorCodec.EncodeAbsolute(anchors, closed: true);
        Assert.NotNull(data);
        var parsed = PathAnchorCodec.Parse(data, Unit);
        Assert.True(parsed?.Closed);
        Assert.Equal(3, parsed?.Anchors.Count);
        var first = parsed!.Value.Anchors[0].Point;
        Assert.True(Math.Abs(first.X - 0.25) < 0.001 && Math.Abs(first.Y - 0.4) < 0.001);
        var curved = parsed.Value.Anchors[1];
        Assert.True(Math.Abs((curved.HandleIn?.X ?? 0) - 0.6) < 0.001);
        Assert.True(Math.Abs((curved.HandleOut?.Y ?? 0) - 0.5) < 0.001);
    }

    [Fact]
    public void EncodeAbsoluteRefusesDegenerateInput() =>
        Assert.Null(PathAnchorCodec.EncodeAbsolute([new PathAnchor(new Point(0, 0))], closed: true));

    // ── Beyond the Mac tests ────────────────────────────────────────────────

    [Fact]
    public void EncodeAbsoluteWritesTheMacsFourDecimals()
    {
        PathAnchor[] anchors = [new(new Point(0.25, 0)), new(new Point(0.75, 0)), new(new Point(0.75, 1)), new(new Point(0.25, 1))];
        Assert.Equal("M 0.2500 0.0000 L 0.7500 0.0000 L 0.7500 1.0000 L 0.2500 1.0000 L 0.2500 0.0000 Z",
            PathAnchorCodec.EncodeAbsolute(anchors, closed: true));
        // The closing line back to the start is written out, and reading it back folds it away again.
        Assert.Equal(4, PathAnchorCodec.Parse(PathAnchorCodec.EncodeAbsolute(anchors, closed: true)!, Unit)?.Anchors.Count);
    }

    [Fact]
    public void ParsesRelativeAndShorthandCommandsAndImplicitLines()
    {
        // "m" then bare pairs are relative lines; H and V keep the other coordinate; commas separate too.
        var parsed = PathAnchorCodec.Parse("m 0.1,0.1 0.5 0 v 0.5 H 0.1 z", Unit);
        Assert.NotNull(parsed);
        Assert.True(parsed.Value.Closed);
        var points = parsed.Value.Anchors.Select(a => (Math.Round(a.Point.X, 4), Math.Round(a.Point.Y, 4))).ToArray();
        Assert.Equal([(0.1, 0.1), (0.6, 0.1), (0.6, 0.6), (0.1, 0.6)], points);
    }

    [Fact]
    public void ParsesIntoAFrame()
    {
        var parsed = PathAnchorCodec.Parse("M 0 0 L 1 1", new SceneRect(100, 50, 200, 100));
        Assert.Equal(new Point(100, 50), parsed?.Anchors[0].Point);
        Assert.Equal(new Point(300, 150), parsed?.Anchors[1].Point);
    }

    [Fact]
    public void RefusesWhatItCannotRead()
    {
        Assert.Null(PathAnchorCodec.Parse("", Unit));
        Assert.Null(PathAnchorCodec.Parse("M 0 0", Unit)); // one point is not a path
        Assert.Null(PathAnchorCodec.Parse("0 0 L 1 1", Unit)); // numbers before any command
        Assert.Null(PathAnchorCodec.Parse("M 0 0 A 1 1 0 0 1 1 1", Unit)); // arcs
        Assert.Null(PathAnchorCodec.Parse("M 0 0 L 1", Unit)); // a half point
        Assert.NotNull(PathAnchorCodec.Parse("M 1e-1 0 L 1E0 0.5", Unit)); // exponents are numbers
    }

    [Fact]
    public void AClosedCurveBackToTheStartKeepsItsArrivingHandle()
    {
        var parsed = PathAnchorCodec.Parse("M 0 0 L 1 0 L 1 1 C 0.5 1 0 0.5 0 0 Z", Unit);
        Assert.NotNull(parsed);
        Assert.Equal(3, parsed.Value.Anchors.Count);
        Assert.Equal(new Point(0, 0.5), parsed.Value.Anchors[0].HandleIn);
    }
}
