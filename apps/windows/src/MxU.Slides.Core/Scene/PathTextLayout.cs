using MxU.Slides.Core.Animation;
using Point = MxU.Slides.Core.Scene.EditorGeometry.Point;

namespace MxU.Slides.Core.Scene;

/// <summary>
/// Text on a path, and tickers (text flowing along a path). The text is laid out once as a
/// single-line "strip"; each character cluster is a tile of that strip, and every frame the
/// tiles are placed along the flattened path at the ticker's phase, each turned to the path's
/// direction there. This file is the arithmetic (pure, so it has unit tests); the renderer
/// supplies where each cluster sits on the strip and draws the tiles.
/// Port of RenderEngine/PathTextLayout.swift, with the Mac's strip normalisation
/// (PathTextStrip.normalizedText) and ticker phase (Compositor.drawPathText).
/// <para>
/// Distances are in the renderer's units: on Windows that's canvas units (WPF scales the
/// drawing), so the Mac's pixel scale is 1 here.
/// </para>
/// </summary>
public static class PathTextLayout
{
    /// <summary>
    /// One cluster's tile of the strip: <see cref="CenterAdvance"/> is where its middle sits along
    /// the run (0 = the run's start), <see cref="Uv"/> the part of the strip it shows (0–1 across).
    /// </summary>
    public sealed record GlyphCell(double CenterAdvance, SceneRect Uv, double TileWidth)
    {
        /// <summary>Which cluster (in the sorted positions given to <see cref="MakeRun"/>) the tile shows, so the renderer can draw it.</summary>
        public int Source { get; init; }
    }

    /// <summary>
    /// The laid-out strip: its tiles, the run's length, the strip's size (the run plus <see cref="PadX"/>
    /// at each end, so outlines and shadows aren't cut), and where the baseline is.
    /// <see cref="SeparatorStart"/> is where a stream's separator begins along the run (it's centred in the gap).
    /// </summary>
    public sealed record Run(
        IReadOnlyList<GlyphCell> Cells,
        double RunLength,
        double StripWidth,
        double StripHeight,
        double BaselineFromBottom,
        double PadX)
    {
        public double? SeparatorStart { get; init; }
    }

    /// <summary>
    /// Tiles from each cluster's x along the line (from the line's start). Each tile reaches from
    /// its cluster's start to the next one's, the first from the strip's left edge and the last to
    /// its right edge, so the tiles cover the whole strip with no gaps. Null for no clusters.
    /// </summary>
    public static Run? MakeRun(IEnumerable<double> clusterPositions, double stripWidth, double stripHeight, double baselineFromBottom, double padX)
    {
        var positions = clusterPositions.OrderBy(x => x).ToList();
        if (positions.Count == 0) return null;
        var runLength = stripWidth - 2 * padX;
        var edges = new List<double> { 0 };
        edges.AddRange(positions.Skip(1).Select(x => padX + x));
        edges.Add(stripWidth);
        var cells = new List<GlyphCell>(positions.Count);
        for (var index = 0; index < positions.Count; index++)
        {
            var left = edges[index];
            var right = edges[index + 1];
            var tile = right - left;
            if (tile <= 0) continue;
            cells.Add(new GlyphCell((left + right) / 2 - padX, new SceneRect(left / stripWidth, 0, tile / stripWidth, 1), tile) { Source = index });
        }
        return cells.Count == 0 ? null : new Run(cells, runLength, stripWidth, stripHeight, baselineFromBottom, padX);
    }

    /// <summary>How many straight pieces each curve is cut into.</summary>
    public const int CurveSubdivisions = 24;

    /// <summary>
    /// A path cut into straight pieces, with the distance along it at each point and a smoothed
    /// direction (the average of the pieces either side), so text turns smoothly round a curve.
    /// </summary>
    public sealed record FlatPath(IReadOnlyList<Point> Points, IReadOnlyList<double> Cumulative, IReadOnlyList<(double Dx, double Dy)> Tangents,
        double TotalLength, bool IsClosed)
    {
        /// <summary>The point <paramref name="arcLength"/> along the path and the direction there (radians).</summary>
        public (Point Point, double TangentRadians) Sample(double arcLength)
        {
            if (Points.Count < 2) return (Points.Count > 0 ? Points[0] : new Point(0, 0), 0);
            var target = Math.Clamp(arcLength, 0, TotalLength);
            var low = 1;
            var high = Points.Count - 1;
            while (low < high)
            {
                var mid = (low + high) / 2;
                if (Cumulative[mid] < target) low = mid + 1;
                else high = mid;
            }
            var start = Points[low - 1];
            var end = Points[low];
            var segment = Cumulative[low] - Cumulative[low - 1];
            var t = segment > 0 ? (target - Cumulative[low - 1]) / segment : 0;
            var point = new Point(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t);
            var from = Tangents[low - 1];
            var to = Tangents[low];
            var dx = from.Dx + (to.Dx - from.Dx) * t;
            var dy = from.Dy + (to.Dy - from.Dy) * t;
            if (dx * dx + dy * dy < 1e-9) return (point, Math.Atan2(end.Y - start.Y, end.X - start.X));
            return (point, Math.Atan2(dy, dx));
        }

        /// <summary>The flat path of path data in a frame, or null when it can't be read or has no length.</summary>
        public static FlatPath? Of(string? pathData, SceneRect frame) =>
            SvgPath.Parse(pathData, frame) is { } elements ? Flatten(elements) : null;
    }

    public static FlatPath? Flatten(IReadOnlyList<SvgPathElement> elements)
    {
        var points = new List<Point>();
        Point? subpathStart = null;
        var sawClose = false;

        void Append(Point point)
        {
            if (points.Count > 0 && Hypot(point.X - points[^1].X, point.Y - points[^1].Y) < 0.001) return;
            points.Add(point);
        }

        foreach (var element in elements)
        {
            switch (element)
            {
                case SvgPathElement.MoveTo move:
                    subpathStart = move.To;
                    Append(move.To);
                    break;
                case SvgPathElement.LineTo line:
                    Append(line.To);
                    break;
                case SvgPathElement.QuadTo quad when points.Count > 0:
                {
                    var from = points[^1];
                    for (var step = 1; step <= CurveSubdivisions; step++)
                    {
                        var t = (double)step / CurveSubdivisions;
                        var mt = 1 - t;
                        Append(new Point(
                            mt * mt * from.X + 2 * mt * t * quad.Control.X + t * t * quad.To.X,
                            mt * mt * from.Y + 2 * mt * t * quad.Control.Y + t * t * quad.To.Y));
                    }
                    break;
                }
                case SvgPathElement.CubicTo cubic when points.Count > 0:
                {
                    var from = points[^1];
                    for (var step = 1; step <= CurveSubdivisions; step++)
                    {
                        var t = (double)step / CurveSubdivisions;
                        var mt = 1 - t;
                        double a = mt * mt * mt, b = 3 * mt * mt * t, c = 3 * mt * t * t, d = t * t * t;
                        Append(new Point(
                            a * from.X + b * cubic.Control1.X + c * cubic.Control2.X + d * cubic.To.X,
                            a * from.Y + b * cubic.Control1.Y + c * cubic.Control2.Y + d * cubic.To.Y));
                    }
                    break;
                }
                case SvgPathElement.Close:
                    sawClose = true;
                    if (subpathStart is { } start) Append(start);
                    break;
            }
        }

        if (points.Count < 2) return null;
        var cumulative = new List<double>(points.Count) { 0 };
        for (var index = 1; index < points.Count; index++)
            cumulative.Add(cumulative[index - 1] + Hypot(points[index].X - points[index - 1].X, points[index].Y - points[index - 1].Y));
        var total = cumulative[^1];
        if (total <= 0) return null;
        var closed = sawClose || Hypot(points[^1].X - points[0].X, points[^1].Y - points[0].Y) < 0.01;

        (double Dx, double Dy) Direction(int index)
        {
            var a = points[index];
            var b = points[index + 1];
            var length = Hypot(b.X - a.X, b.Y - a.Y);
            return length > 0 ? ((b.X - a.X) / length, (b.Y - a.Y) / length) : (1, 0);
        }
        static (double Dx, double Dy) Averaged((double Dx, double Dy) a, (double Dx, double Dy) b)
        {
            var dx = a.Dx + b.Dx;
            var dy = a.Dy + b.Dy;
            var length = Hypot(dx, dy);
            return length > 1e-6 ? (dx / length, dy / length) : a; // a hairpin: keep the incoming direction
        }

        var tangents = new (double Dx, double Dy)[points.Count];
        var lastSegment = points.Count - 2;
        for (var index = 0; index < points.Count; index++)
        {
            if (index == 0) tangents[0] = closed ? Averaged(Direction(lastSegment), Direction(0)) : Direction(0);
            else if (index == points.Count - 1) tangents[index] = closed ? tangents[0] : Direction(lastSegment);
            else tangents[index] = Averaged(Direction(index - 1), Direction(index));
        }
        return new FlatPath(points, cumulative, tangents, total, closed);
    }

    /// <summary>One tile placed on the path: the part of the strip it shows, where its middle is on the baseline, and its direction.</summary>
    public sealed record PlacedGlyph(SceneRect Uv, Point Center, double TangentRadians, double TileWidth)
    {
        /// <summary>The cell's <see cref="GlyphCell.Source"/>: the cluster to draw.</summary>
        public int Source { get; init; }
    }

    /// <summary>
    /// Places the strip's tiles along the path at a phase (how far the ticker has travelled).
    /// <list type="bullet">
    /// <item>A closed path (ring, perimeter) wraps: the text goes round and round. A stream puts as
    /// many whole copies round it as fit with their gap.</item>
    /// <item>An open path scrolls the text in from one end and out the other (a marquee); a stream
    /// is a train of copies <c>run + gap</c> apart.</item>
    /// <item>Not scrolling, the text sits at its alignment along the path.</item>
    /// </list>
    /// Tiles past an open path's ends are cut, so letters slide out smoothly. <paramref name="reversed"/>
    /// runs the other way along the path (text inside a shape's edge); <paramref name="offset"/> lifts the
    /// text off the path along its "up" side.
    /// </summary>
    public static List<PlacedGlyph> Place(Run run, FlatPath path, double phase, SceneTextAlignment alignment, bool isScrolling,
        bool reversed = false, double offset = 0, bool stream = false, double gap = 0)
    {
        var placed = new List<PlacedGlyph>(run.Cells.Count);
        var total = path.TotalLength;

        (Point Point, double Tangent) Sampled(double arc)
        {
            var sample = reversed ? path.Sample(total - arc) : path.Sample(arc);
            var tangent = reversed ? sample.TangentRadians + Math.PI : sample.TangentRadians;
            var point = sample.Point;
            // "Up" from the baseline in a y-down canvas: (sin, −cos) of the direction.
            if (offset != 0) point = new Point(point.X + Math.Sin(tangent) * offset, point.Y - Math.Cos(tangent) * offset);
            return (point, tangent);
        }

        void Emit(double copyOffset, bool wrap, double separatorShift = 0)
        {
            foreach (var cell in run.Cells)
            {
                var arc = copyOffset + cell.CenterAdvance;
                if (separatorShift > 0 && run.SeparatorStart is { } separatorStart && cell.CenterAdvance >= separatorStart)
                    arc += separatorShift;
                if (wrap)
                {
                    var raw = arc % total;
                    arc = raw < 0 ? raw + total : raw;
                    var s = Sampled(arc);
                    placed.Add(new PlacedGlyph(cell.Uv, s.Point, s.Tangent, cell.TileWidth) { Source = cell.Source });
                    continue;
                }
                var width = cell.TileWidth;
                var leftEdge = arc - width / 2;
                var rightEdge = arc + width / 2;
                if (rightEdge <= 0 || leftEdge >= total) continue;
                // Trim the tile where it hangs off either end of the path.
                var cutLeft = Math.Max(0, -leftEdge);
                var cutRight = Math.Max(0, rightEdge - total);
                var visible = width - cutLeft - cutRight;
                if (visible <= 0.01) continue;
                var uv = cell.Uv;
                uv = uv with { X = uv.X + uv.Width * (cutLeft / width), Width = uv.Width * (visible / width) };
                var sample = Sampled(arc + (cutLeft - cutRight) / 2);
                placed.Add(new PlacedGlyph(uv, sample.Point, sample.Tangent, visible) { Source = cell.Source });
            }
        }

        if (path.IsClosed)
        {
            if (stream)
            {
                // Whole copies only, spread evenly, so the seam never shows two copies overlapping.
                var requested = Math.Max(run.RunLength + gap, 1);
                var mostThatFit = Math.Max(1, (int)(total / Math.Max(run.RunLength, 1)));
                var copies = Math.Max(1, Math.Min((int)Math.Round(total / requested, MidpointRounding.AwayFromZero), mostThatFit));
                var stride = total / copies;
                var separatorShift = Math.Max(0, stride - run.RunLength) / 2;
                for (var copy = 0; copy < copies; copy++) Emit(copy * stride + phase, wrap: true, separatorShift);
            }
            else
            {
                Emit(phase, wrap: true);
            }
            return placed;
        }

        if (stream && isScrolling)
        {
            var stride = Math.Max(run.RunLength + gap, 1);
            var looped = phase % stride;
            var start = (looped < 0 ? looped + stride : looped) - stride - run.RunLength;
            while (start < total + run.PadX)
            {
                Emit(start, wrap: false, gap / 2);
                start += stride;
            }
            return placed;
        }

        double baseOffset;
        if (isScrolling)
        {
            // A marquee: in from the far end, across, and out, then round again.
            var period = total + run.RunLength + 2 * run.PadX;
            var looped = phase % period;
            baseOffset = -(run.RunLength + run.PadX) + (looped < 0 ? looped + period : looped);
        }
        else
        {
            baseOffset = alignment switch
            {
                SceneTextAlignment.Left => 0,
                SceneTextAlignment.Center => (total - run.RunLength) / 2,
                _ => total - run.RunLength,
            };
        }
        Emit(baseOffset, wrap: false);
        return placed;
    }

    /// <summary>
    /// How far a ticker has travelled at <paramref name="hostTime"/>: speed × time since it went up
    /// (<paramref name="anchor"/>; without one, since the clock's start), negative for right-to-left.
    /// A ticker with a repeat count stops after that many passes: an open path is then empty
    /// (null: draw nothing) and a ring rests where it started. A ramp eases each counted pass.
    /// </summary>
    public static double? TickerPhase(StyledText text, FlatPath path, Run run, double hostTime, double? anchor)
    {
        var elapsed = anchor is { } start ? hostTime - start : hostTime;
        var direction = text.TickerLeftToRight ? 1.0 : -1.0;
        var phase = elapsed * text.TickerSpeed * direction;
        if (text.TickerRepeat > 0 && !text.TickerStream && anchor is not null && text.TickerSpeed != 0)
        {
            var period = path.IsClosed ? path.TotalLength : path.TotalLength + run.RunLength + 2 * run.PadX;
            var limit = text.TickerRepeat * period;
            if (Math.Abs(phase) >= limit) return path.IsClosed ? 0 : null;
            if (text.TickerRamp != SceneAnimationRamp.None && period > 0)
            {
                var travelled = Math.Abs(phase);
                var pass = Math.Floor(travelled / period);
                var inPass = (travelled - pass * period) / period;
                var eased = pass * period + text.TickerRamp.Apply(inPass) * period;
                phase = phase < 0 ? -eased : eased;
            }
        }
        return phase;
    }

    /// <summary>True for text drawn along a path (a ticker, or text on a curve or shape edge).</summary>
    public static bool IsPathText(StyledText text) => !string.IsNullOrEmpty(text.PathData);

    /// <summary>True for path text that moves with time.</summary>
    public static bool IsMoving(StyledText text) => IsPathText(text) && text.TickerSpeed != 0;

    /// <summary>
    /// The text as its strip is laid out: one line (paragraphs joined with spaces), left-aligned,
    /// with none of the block-layout or path fields, so changing the path, speed or ramp never
    /// lays the strip out again. A stream's separator is added to the end, so each copy carries one.
    /// Port of PathTextStrip.normalizedText.
    /// </summary>
    // Foundation's CharacterSet.newlines: line feed, vertical tab, form feed, carriage return, NEL, line and paragraph separators.
    private static readonly char[] Newlines = ['\n', '\v', '\f', '\r', (char)0x85, (char)0x2028, (char)0x2029];

    public static StyledText NormalizedText(StyledText text)
    {
        var flat = text with
        {
            String = string.Join(" ", text.String.Split(Newlines).Where(s => s.Length > 0)),
            Alignment = SceneTextAlignment.Left,
            VerticalAlignment = SceneTextVerticalAlignment.Middle,
            LineHeightMultiple = 1,
            AutoShrink = false,
            MinFontSize = StyledText.DefaultMinFontSize,
            LineOverrides = [],
            StyleRuns = [],
            Underline = false,
            Strikethrough = false,
            LineFill = null,
            InsetTop = 0,
            InsetLeft = 0,
            InsetBottom = 0,
            InsetRight = 0,
            FirstLineIndent = 0,
            LeftIndent = 0,
            RightIndent = 0,
            ParagraphSpacing = 0,
            PathData = null,
            TickerSpeed = 0,
            PathReversed = false,
            PathOffset = 0,
            TickerRepeat = 0,
            TickerLeftToRight = false,
            TickerStream = false,
            TickerGap = 0,
            TickerSeparator = "",
            TickerRamp = SceneAnimationRamp.None,
        };
        if (text.TickerStream && text.TickerSeparator.Length > 0)
            flat = flat with { String = flat.String + text.TickerSeparator, TickerSeparator = text.TickerSeparator };
        return flat;
    }

    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);
}
