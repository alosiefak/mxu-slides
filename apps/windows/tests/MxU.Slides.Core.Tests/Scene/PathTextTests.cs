using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Scene.PathTextLayout;

namespace MxU.Slides.Core.Tests.Scene;

// Ported from RenderEngine/Tests/PathTextTests.swift (the layout half; the Mac's pixel tests are
// covered by the ticker phase tests below and the self-test's pictures).
public class PathTextTests
{
    private static FlatPath FlatCircle(double size = 200) => FlatPath.Of(PathPresets.Ellipse, new SceneRect(0, 0, size, size))!;

    private static FlatPath FlatLine(double width, double height = 100) => FlatPath.Of(PathPresets.Line, new SceneRect(0, 0, width, height))!;

    /// <summary>A strip of glyphs with these widths (the Mac test's makeRun).</summary>
    private static Run MakeRun(double[] glyphWidths, double pad = 4)
    {
        var runLength = glyphWidths.Sum();
        var stripWidth = runLength + 2 * pad;
        var cells = new List<GlyphCell>();
        var penX = 0.0;
        for (var index = 0; index < glyphWidths.Length; index++)
        {
            var width = glyphWidths[index];
            var left = index == 0 ? 0 : pad + penX;
            var right = index == glyphWidths.Length - 1 ? stripWidth : pad + penX + width;
            cells.Add(new GlyphCell((left + right) / 2 - pad, new SceneRect(left / stripWidth, 0, (right - left) / stripWidth, 1), right - left));
            penX += width;
        }
        return new Run(cells, runLength, stripWidth, 40, 12, pad);
    }

    private static double[] Repeat(double width, int count) => Enumerable.Repeat(width, count).ToArray();

    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    [Fact]
    public void CirclePresetFlattensClosedWithCircumferentialLength()
    {
        var flat = FlatCircle();
        Assert.True(flat.IsClosed);
        Assert.Equal(Math.PI * 200, flat.TotalLength, Math.PI * 200 * 0.01);
    }

    [Fact]
    public void LinePresetFlattensOpenWithExactLength()
    {
        var flat = FlatLine(300);
        Assert.False(flat.IsClosed);
        Assert.Equal(300, flat.TotalLength, 0.001);
    }

    [Fact]
    public void FlatteningIsDeterministic()
    {
        var first = FlatCircle();
        var second = FlatCircle();
        Assert.Equal(first.Points, second.Points);
        Assert.Equal(first.Cumulative, second.Cumulative);
        Assert.Equal(first.Tangents, second.Tangents);
    }

    [Fact]
    public void ClosedPathWrapIsContinuousAcrossTheSeam()
    {
        var flat = FlatCircle();
        var run = MakeRun([10]);
        var total = flat.TotalLength;
        EditorGeometry.Point? previous = null;
        for (var step = 0; step <= 40; step++)
        {
            var phase = total - 2 + step * 0.1 - run.Cells[0].CenterAdvance;
            var placed = Place(run, flat, phase, SceneTextAlignment.Left, isScrolling: true);
            Assert.Single(placed); // a glyph must never vanish at the seam
            if (previous is { } p) Assert.True(Hypot(placed[0].Center.X - p.X, placed[0].Center.Y - p.Y) < 1.0, "seam crossing must be continuous");
            previous = placed[0].Center;
        }
    }

    [Fact]
    public void OpenPathCullsGlyphsBeyondTheEnds()
    {
        var flat = FlatLine(100);
        var run = MakeRun(Repeat(10, 15));
        Assert.Empty(Place(run, flat, 0, SceneTextAlignment.Left, isScrolling: true));

        var midway = Place(run, flat, run.RunLength, SceneTextAlignment.Left, isScrolling: true);
        Assert.NotEmpty(midway);
        Assert.True(midway.Count <= 11);
        Assert.All(midway, g => Assert.InRange(g.Center.X, -6, 106));
    }

    [Fact]
    public void MarqueeLoopsSeamlessly()
    {
        var flat = FlatLine(100);
        var run = MakeRun(Repeat(10, 5));
        var period = flat.TotalLength + run.RunLength + 2 * run.PadX;
        var first = Place(run, flat, 30, SceneTextAlignment.Left, isScrolling: true);
        var looped = Place(run, flat, 30 + period, SceneTextAlignment.Left, isScrolling: true);
        Assert.Equal(first, looped);
    }

    [Fact]
    public void StaticOpenPathAnchorsByAlignment()
    {
        var flat = FlatLine(200);
        var run = MakeRun(Repeat(10, 4));
        double FirstCenterX(SceneTextAlignment alignment) => Place(run, flat, 0, alignment, isScrolling: false)[0].Center.X;
        Assert.Equal(5, FirstCenterX(SceneTextAlignment.Left), 0.01);
        Assert.Equal(83, FirstCenterX(SceneTextAlignment.Center), 0.01);
        Assert.Equal(163, FirstCenterX(SceneTextAlignment.Right), 0.01);
    }

    [Fact]
    public void GlyphsTrimSmoothlyAtOpenPathEnds()
    {
        var flat = FlatLine(100);
        var run = MakeRun([10]);
        var cell = run.Cells[0];
        var phase = run.RunLength + run.PadX + flat.TotalLength - cell.CenterAdvance;
        var placed = Place(run, flat, phase, SceneTextAlignment.Left, isScrolling: true);
        var glyph = Assert.Single(placed);
        Assert.Equal(cell.TileWidth / 2, glyph.TileWidth, 0.1);
        Assert.Equal(cell.Uv.Width / 2, glyph.Uv.Width, 0.001);
        Assert.Equal(cell.Uv.X, glyph.Uv.X, 0.001); // the exit end trims the far side
        Assert.Equal(flat.TotalLength - cell.TileWidth / 4, glyph.Center.X, 0.1);
    }

    [Fact]
    public void TangentTurnsContinuouslyAlongACurve()
    {
        var flat = FlatCircle();
        var quarter = flat.TotalLength / 4;
        var previous = flat.Sample(0).TangentRadians;
        var maxStep = 0.0;
        const int steps = 400;
        for (var step = 1; step <= steps; step++)
        {
            var tangent = flat.Sample(quarter * step / steps).TangentRadians;
            var delta = tangent - previous;
            while (delta > Math.PI) delta -= 2 * Math.PI;
            while (delta < -Math.PI) delta += 2 * Math.PI;
            maxStep = Math.Max(maxStep, Math.Abs(delta));
            previous = tangent;
        }
        Assert.True(maxStep < 0.02, "the tangent must not snap between flattened segments");
    }

    [Fact]
    public void NormalizedTextCollapsesNewlinesAndPlacementFields()
    {
        var text = new StyledText
        {
            String = "line one\nline two", FontSize = 24, Alignment = SceneTextAlignment.Right,
            VerticalAlignment = SceneTextVerticalAlignment.Top, AutoShrink = true, PathData = PathPresets.Ellipse, TickerSpeed = 40,
        };
        var normalized = NormalizedText(text);
        Assert.Equal("line one line two", normalized.String);
        Assert.Equal(SceneTextAlignment.Left, normalized.Alignment);
        Assert.Null(normalized.PathData);
        Assert.Equal(0, normalized.TickerSpeed);
        Assert.False(normalized.AutoShrink);
    }

    [Fact]
    public void ClosedStreamAutoFitsWithNoWrapOverlap()
    {
        var flat = FlatCircle();
        var run = MakeRun(Repeat(10, 5));
        var placed = Place(run, flat, 0, SceneTextAlignment.Left, isScrolling: true, stream: true, gap: 100);
        Assert.Equal(0, placed.Count % run.Cells.Count); // whole copies only
        Assert.Equal((int)Math.Round(flat.TotalLength / 150, MidpointRounding.AwayFromZero), placed.Count / run.Cells.Count);
        for (var i = 0; i < placed.Count; i++)
            for (var j = i + 1; j < placed.Count; j++)
                Assert.True(Hypot(placed[i].Center.X - placed[j].Center.X, placed[i].Center.Y - placed[j].Center.Y) > 0.5,
                    "wrap overlap: two copies landed on the same arc");
    }

    [Fact]
    public void ClosedStreamWithHugeGapDegradesToSingleCopy()
    {
        var run = MakeRun([10]);
        Assert.Equal(run.Cells.Count, Place(run, FlatCircle(), 0, SceneTextAlignment.Left, isScrolling: true, stream: true, gap: 10_000).Count);
    }

    [Fact]
    public void OpenStreamTrainIsStridePeriodic()
    {
        var flat = FlatLine(300);
        var run = MakeRun(Repeat(10, 4));
        var at30 = Place(run, flat, 30, SceneTextAlignment.Left, isScrolling: true, stream: true, gap: 20);
        var oneStrideLater = Place(run, flat, 90, SceneTextAlignment.Left, isScrolling: true, stream: true, gap: 20);
        Assert.Equal(at30, oneStrideLater); // phase mod stride: the train glides seamlessly
        Assert.True(at30.Count > run.Cells.Count, "several copies show on the path");
    }

    [Fact]
    public void PathOffsetDisplacesAlongAscentNormal()
    {
        var flat = FlatLine(200);
        var run = MakeRun([10]);
        var on = Place(run, flat, 0, SceneTextAlignment.Center, isScrolling: false)[0];
        var raised = Place(run, flat, 0, SceneTextAlignment.Center, isScrolling: false, offset: 12)[0];
        Assert.Equal(on.Center.X, raised.Center.X, 0.001);
        Assert.Equal(on.Center.Y - 12, raised.Center.Y, 0.001);
    }

    [Fact]
    public void StreamSeparatorEntersTheStripAndTheKey()
    {
        var plain = new StyledText { String = "NEWS", FontSize = 24, TickerStream = true };
        var separated = plain with { TickerSeparator = " | " };
        Assert.Equal("NEWS", NormalizedText(plain).String);
        Assert.Equal("NEWS | ", NormalizedText(separated).String);
        Assert.Equal(" | ", NormalizedText(separated).TickerSeparator);
        Assert.NotEqual(NormalizedText(plain), NormalizedText(separated));

        var nonStream = separated with { TickerStream = false };
        Assert.Equal("NEWS", NormalizedText(nonStream).String);
        Assert.Equal("", NormalizedText(nonStream).TickerSeparator);
    }

    [Fact]
    public void StreamSeparatorCentersInTheGap()
    {
        var flat = FlatLine(300);
        var run = MakeRun(Repeat(10, 4)) with { SeparatorStart = 30 };
        const double gap = 20;
        var placed = Place(run, flat, run.RunLength + run.PadX, SceneTextAlignment.Left, isScrolling: true, stream: true, gap: gap);
        var expected = run.Cells[3].CenterAdvance - run.Cells[2].CenterAdvance + gap / 2;
        var sorted = placed.Select(p => p.Center.X).OrderBy(x => x).ToList();
        Assert.Contains(Enumerable.Range(1, sorted.Count - 1), i => Math.Abs(sorted[i] - sorted[i - 1] - expected) < 0.5);
    }

    [Fact]
    public void StripKeyIgnoresRampAndScroll()
    {
        // BlockScrollTests.testStripKeyIgnoresRampAndScroll: neither the ramp nor a block scroll lays the strip out again.
        var a = new StyledText { String = "Ticker", FontSize = 10, TickerRamp = SceneAnimationRamp.EaseInOut };
        Assert.Equal(NormalizedText(a), NormalizedText(a with { TickerRamp = SceneAnimationRamp.None, Scroll = new SceneBlockScroll(3) }));
    }

    // MARK: Ticker phase (the Compositor half of drawPathText)

    private static StyledText Ticker(double speed, int repeats = 0, bool leftToRight = false) =>
        new() { String = "REPEAT TEST", FontSize = 18, PathData = PathPresets.Line, TickerSpeed = speed, TickerRepeat = repeats, TickerLeftToRight = leftToRight };

    [Fact]
    public void ARepeatLimitBlanksAMarqueeAndRestsARing()
    {
        var run = MakeRun(Repeat(10, 5));
        var line = FlatLine(256);
        Assert.NotNull(TickerPhase(Ticker(100, 1), line, run, hostTime: 102, anchor: 100)); // mid-pass the marquee shows
        Assert.Null(TickerPhase(Ticker(100, 1), line, run, hostTime: 1000, anchor: 100)); // after its last exit it rests empty
        Assert.Equal((double?)0, TickerPhase(Ticker(100, 1) with { PathData = PathPresets.Ellipse }, FlatCircle(), run, hostTime: 1000, anchor: 100));
        // A preview with no anchor never counts passes.
        Assert.NotNull(TickerPhase(Ticker(100, 1), line, run, hostTime: 2.5, anchor: null));
    }

    [Fact]
    public void TickerDirectionDefaultsToReadingOrderAndFlips()
    {
        var run = MakeRun(Repeat(10, 5));
        var line = FlatLine(300);
        Assert.Equal((double?)-50, TickerPhase(Ticker(50), line, run, 1, anchor: 0));
        Assert.Equal((double?)50, TickerPhase(Ticker(50, leftToRight: true), line, run, 1, anchor: 0));
        Assert.Equal((double?)0, TickerPhase(Ticker(0), line, run, 1, anchor: 0)); // speed 0 never moves
    }

    [Fact]
    public void AFiniteTickerRampEasesEachPass()
    {
        // testFiniteTickerRampEasesEachPass: an eased pass lags the linear one early on; a
        // continuous ticker has no pass edge to ease.
        var run = MakeRun(Repeat(10, 5));
        var line = FlatLine(256);
        var linear = TickerPhase(Ticker(100, 1), line, run, 100.4, anchor: 100)!.Value;
        var eased = TickerPhase(Ticker(100, 1) with { TickerRamp = SceneAnimationRamp.EaseIn }, line, run, 100.4, anchor: 100)!.Value;
        Assert.True(Math.Abs(eased) < Math.Abs(linear));
        Assert.Equal(TickerPhase(Ticker(100), line, run, 100.4, anchor: 100),
            TickerPhase(Ticker(100) with { TickerRamp = SceneAnimationRamp.EaseIn }, line, run, 100.4, anchor: 100));
    }

    [Fact]
    public void ARunCoversTheStripWithTilesFromClusterPositions()
    {
        // The Windows half of PathTextLayout.run(line:): tiles reach from one cluster to the next.
        var run = MakeRun([0, 12, 30], 50, 40, 10, 4)!;
        Assert.Equal(42, run.RunLength);
        Assert.Equal(new double[] { 0, 16, 34 }, run.Cells.Select(c => c.Uv.X * 50).Select(x => Math.Round(x, 6)));
        Assert.Equal(50, run.Cells.Sum(c => c.TileWidth), 1e-9);
        Assert.Equal(16.0 / 2 - 4, run.Cells[0].CenterAdvance, 1e-9);
        Assert.Null(MakeRun([], 10, 10, 2, 1));
    }

    private static Run? MakeRun(double[] positions, double width, double height, double baseline, double pad) =>
        PathTextLayout.MakeRun(positions, width, height, baseline, pad);

    // MARK: The scene builder carries the fields (SlideSceneBuilder.styledText, renderItems)

    [Fact]
    public void TheBuilderCarriesTickerFieldsFromTheStyle()
    {
        var o = new SlideObject
        {
            Id = "t", ObjectKind = SlideObjectKind.Text, Name = "Ticker", Text = "News",
            TextStyle = new TextStyle
            {
                PathData = PathPresets.Line, TickerSpeed = 80, TickerDirection = TickerDirection.LeftToRight, TickerRepeat = 2,
                TickerStream = true, TickerGap = 40, TickerSeparator = " • ", PathSide = TextPathSide.Inside, PathOffset = 6,
                TickerRamp = AnimationRamp.Both, WordSpacing = 3,
            },
        };
        var text = SlideSceneBuilder.StyledTextFor(o, null);
        Assert.Equal(PathPresets.Line, text.PathData);
        Assert.Equal(80, text.TickerSpeed);
        Assert.True(text.TickerLeftToRight && text.TickerStream && text.PathReversed);
        Assert.Equal(-6, text.PathOffset); // inside: the offset points inwards
        Assert.Equal((2, 40.0, " • ", 3.0), (text.TickerRepeat, text.TickerGap, text.TickerSeparator, text.WordSpacing));
        Assert.Equal(SceneAnimationRamp.EaseInOut, text.TickerRamp);
    }

    [Fact]
    public void AThemesPlaceholderGivesItsTickerToTheSlidesText()
    {
        var placeholder = new SlideObject
        {
            Id = "p", ObjectKind = SlideObjectKind.Text, Name = "Ticker", Text = "",
            TextStyle = new TextStyle { PathData = PathPresets.Ellipse, TickerSpeed = 30 },
        };
        var o = new SlideObject { Id = "t", ObjectKind = SlideObjectKind.Text, Name = "Ticker", Text = "Welcome" };
        var text = SlideSceneBuilder.StyledTextFor(o, null, placeholder);
        Assert.Equal(PathPresets.Ellipse, text.PathData);
        Assert.Equal(30, text.TickerSpeed);
    }

    [Fact]
    public void TextOnAShapesEdgeRunsAlongItsOutline()
    {
        SlideObject Shape(ShapeTextPlacement placement) => new()
        {
            Id = "s", ObjectKind = SlideObjectKind.Shape, Name = "Ring", Text = "AROUND THE EDGE", ShapeKind = ShapeKind.Ellipse,
            X = 100, Y = 100, Width = 400, Height = 400, ShapeTextPlacement = placement,
            TextStyle = new TextStyle { PathOffset = 10, PathData = PathPresets.Line },
        };
        static StyledText TextOf(SlideObject o) => SlideSceneBuilder.RenderItems(o, null)
            .Select(i => i.Content).OfType<ItemContent.Text>().Single().Styled;

        var inside = TextOf(Shape(ShapeTextPlacement.Inside));
        Assert.Null(inside.PathData); // text inside the shape is a block, whatever its style says

        var outside = TextOf(Shape(ShapeTextPlacement.EdgeOutside));
        Assert.Equal(ShapeOutlineSvg.PathData(ShapeKind.Ellipse, new SceneSize(400, 400), null, null, ShapeTextPlacement.EdgeOutside), outside.PathData);
        Assert.False(outside.PathReversed);
        Assert.Equal(10, outside.PathOffset);

        var edgeInside = TextOf(Shape(ShapeTextPlacement.EdgeInside));
        Assert.True(edgeInside.PathReversed);
        Assert.Equal(-10, edgeInside.PathOffset);
        Assert.StartsWith("M 0.5 1", edgeInside.PathData); // starts at the bottom so it reads upright
    }

    [Fact]
    public void PathTextNeverBlockScrollsAndATickerKeepsTheFramesComing()
    {
        var text = new StyledText { String = "News", FontSize = 30, PathData = PathPresets.Line, TickerSpeed = 50 };
        AnimatedItem item = new RenderItem { Id = "t", Frame = new SceneRect(0, 0, 400, 80), Content = new ItemContent.Text(text) };
        item = item with { Scroll = new SceneBlockScroll(40, SceneScrollAxis.Left) };
        var resolved = AnimationEvaluator.Resolve(item, SceneSize.Hd, 10);
        Assert.Equal(SceneVector.Zero, Assert.Single(resolved).Motion.Translate); // no block roll

        var scene = new AnimatedScene(SceneSize.Hd);
        scene.AddItem(item with { Scroll = null }, LayerKind.Slide);
        Assert.True(AnimationTimeline.IsAnimating(scene, 10));
        Assert.True(scene.AnimatesOnHostTime);

        var still = new AnimatedScene(SceneSize.Hd);
        still.AddItem(item with { Scroll = null, Render = item.Render with { Content = new ItemContent.Text(text with { TickerSpeed = 0 }) } }, LayerKind.Slide);
        Assert.False(AnimationTimeline.IsAnimating(still, 10));
    }

    [Fact]
    public void SvgPathReadsRelativeAndRepeatedCommands()
    {
        var elements = SvgPath.Parse("M 0 0 l 0.5 0 0.5 0.5 Z", new SceneRect(10, 20, 100, 200))!;
        Assert.Equal(new SvgPathElement.MoveTo(new EditorGeometry.Point(10, 20)), elements[0]);
        Assert.Equal(new SvgPathElement.LineTo(new EditorGeometry.Point(60, 20)), elements[1]);
        Assert.Equal(new SvgPathElement.LineTo(new EditorGeometry.Point(110, 120)), elements[2]); // a bare pair repeats the l
        Assert.IsType<SvgPathElement.Close>(elements[3]);
        Assert.Null(SvgPath.Parse("M 0 0 A 1 1 0 0 0 1 1", new SceneRect(0, 0, 1, 1))); // arcs aren't understood, as on the Mac
        Assert.Null(SvgPath.Parse("", new SceneRect(0, 0, 1, 1)));
    }
}
