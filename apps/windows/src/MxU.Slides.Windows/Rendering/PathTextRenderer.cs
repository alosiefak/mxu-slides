using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Scene.PathTextLayout;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// Draws text on a path and tickers with WPF, the way the Mac's Compositor.drawPathText does:
/// <list type="number">
/// <item>The text is laid out once as a single-line strip (<see cref="PathTextLayout.NormalizedText"/>):
/// where each character cluster sits along it, and each cluster's own little drawing (glyphs and outline).</item>
/// <item>The path is flattened once per frame size (<see cref="PathTextLayout.FlatPath"/>).</item>
/// <item>Each frame, Core's <see cref="PathTextLayout.TickerPhase"/> says how far the ticker has travelled and
/// <see cref="PathTextLayout.Place"/> where every cluster lands; each is drawn there, turned to the path.</item>
/// </list>
/// The Mac draws tiles of one rasterised strip on the GPU; WPF draws each cluster's glyphs with a
/// transform instead (vector, so it stays crisp at any size). A cluster cut by the end of an open
/// path is clipped to its visible part, exactly like the Mac's trimmed tile. Shadows come from the
/// item's own drop-shadow effect, as for block text.
/// </summary>
public static class PathTextRenderer
{
    /// <summary>How the text sits this frame: how far along it has travelled, and whether it scrolls at all.</summary>
    public readonly record struct Pose(double Phase, bool Scrolling);

    /// <summary>One cluster: its x along the line and its frozen drawing in strip coordinates (null for a space).</summary>
    private sealed record Cluster(double X, Drawing? Drawing);

    /// <param name="UvWidths">Each cluster's whole tile width (0–1 of the strip), to tell when a placed tile was cut.</param>
    private sealed record Strip(Run Run, IReadOnlyList<Cluster> Clusters, double BaselineY, double[] UvWidths);

    private static readonly Dictionary<string, Strip?> Strips = new(StringComparer.Ordinal);
    private static readonly Dictionary<(string Path, SceneRect Frame), FlatPath?> Paths = [];

    /// <summary>
    /// Where the text is this frame. <paramref name="moving"/> is false for views that show things at
    /// rest (thumbnails, the editor): the text then sits still at its alignment. Null when there is
    /// nothing to draw now (a marquee that has made all its passes).
    /// </summary>
    public static Pose? PoseAt(StyledText text, SceneRect frame, double pixelsPerDip, double hostTime, double? anchor, bool moving)
    {
        if (!moving || text.TickerSpeed == 0) return new Pose(0, Scrolling: false);
        if (StripFor(text, pixelsPerDip) is not { } strip || PathFor(text.PathData!, frame) is not { } path) return new Pose(0, Scrolling: false);
        return TickerPhase(text, path, strip.Run, hostTime, anchor) is { } phase ? new Pose(phase, Scrolling: true) : null;
    }

    /// <summary>Draws the text along its path in the item's <paramref name="frame"/> (canvas coordinates).</summary>
    public static void Draw(DrawingContext dc, StyledText text, SceneRect frame, double pixelsPerDip, Pose pose)
    {
        if (string.IsNullOrEmpty(text.String) || text.FontSize <= 0 || frame.Width <= 0 || frame.Height <= 0) return;
        if (StripFor(text, pixelsPerDip) is not { } strip || PathFor(text.PathData!, frame) is not { } path) return;
        var run = strip.Run;
        var placed = Place(run, path, pose.Phase, text.Alignment, pose.Scrolling, text.PathReversed, text.PathOffset, text.TickerStream, text.TickerGap);
        foreach (var glyph in placed)
        {
            if (glyph.Source >= strip.Clusters.Count || strip.Clusters[glyph.Source].Drawing is not { } drawing) continue;
            // The tile's middle on the baseline goes to the placed point, turned to the path's direction there.
            var tileCenter = (glyph.Uv.X + glyph.Uv.Width / 2) * run.StripWidth;
            var m = Matrix.Identity;
            m.Translate(-tileCenter, -strip.BaselineY);
            m.Rotate(glyph.TangentRadians * 180 / Math.PI);
            m.Translate(glyph.Center.X, glyph.Center.Y);
            dc.PushTransform(new MatrixTransform(m));
            var trimmed = glyph.Uv.Width < strip.UvWidths[glyph.Source] - 1e-9;
            if (trimmed)
                dc.PushClip(new RectangleGeometry(new Rect(glyph.Uv.X * run.StripWidth, -run.StripHeight, glyph.Uv.Width * run.StripWidth, run.StripHeight * 3)));
            dc.DrawDrawing(drawing);
            if (trimmed) dc.Pop();
            dc.Pop();
        }
    }

    private static FlatPath? PathFor(string pathData, SceneRect frame)
    {
        lock (Paths)
        {
            if (Paths.TryGetValue((pathData, frame), out var cached)) return cached;
            var path = FlatPath.Of(pathData, frame);
            if (Paths.Count > 256) Paths.Clear();
            Paths[(pathData, frame)] = path;
            return path;
        }
    }

    private static Strip? StripFor(StyledText text, double ppd)
    {
        var normalized = NormalizedText(text);
        var key = Key(normalized, ppd);
        lock (Strips)
        {
            if (Strips.TryGetValue(key, out var cached)) return cached;
        }
        Strip? strip;
        try
        {
            strip = MakeStrip(normalized, ppd);
        }
        catch (Exception ex)
        {
            // Never take the frame down over one ticker: it just doesn't draw.
            Infrastructure.Log.Warn($"Couldn't lay out text on a path: {ex.Message}");
            strip = null;
        }
        lock (Strips)
        {
            if (Strips.Count > 128) Strips.Clear();
            Strips[key] = strip;
        }
        return strip;
    }

    /// <summary>The strip, as PathTextStrip.make lays it out: one line, padded for outline and shadow.</summary>
    private static Strip? MakeStrip(StyledText text, double ppd)
    {
        var display = text.DisplayString;
        if (display.Length == 0 || text.FontSize <= 0) return null;
        var face = FontMap.Resolve(text.FontName);
        var brush = text.Fill is SceneFill.NoFill ? null : (Brush)Painting.Brush(text.Color);
        FormattedText Format(string s)
        {
            var formatted = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face.Typeface,
                text.FontSize, brush ?? Brushes.Transparent, ppd) { Trimming = TextTrimming.None };
            if (text.TabularFigures) formatted.SetNumberSubstitution(new NumberSubstitution());
            return formatted;
        }

        var line = Format(display);
        var starts = StringInfo.ParseCombiningCharacters(display);
        // Each cluster's x on the line, plus tracking after every character and word spacing after every space (as Core Text kerns them).
        var positions = new List<(double X, int Start, int Length)>(starts.Length);
        var spaces = 0;
        for (var i = 0; i < starts.Length; i++)
        {
            var start = starts[i];
            var length = (i + 1 < starts.Length ? starts[i + 1] : display.Length) - start;
            var x = line.BuildHighlightGeometry(new Point(0, 0), start, length)?.Bounds.Left
                ?? (start == 0 ? 0 : Format(display[..start]).WidthIncludingTrailingWhitespace);
            positions.Add((x + text.Tracking * i + text.WordSpacing * spaces, start, length));
            if (display.AsSpan(start, length).IsWhiteSpace()) spaces++;
        }
        positions.Sort((a, b) => a.X.CompareTo(b.X));
        var advance = line.WidthIncludingTrailingWhitespace + text.Tracking * starts.Length + text.WordSpacing * spaces;
        var ascent = line.Baseline;
        var descent = Math.Max(0, line.Height - line.Baseline);
        if (advance <= 0 || ascent + descent <= 0) return null;

        var outlineWidth = text.Outline?.Width ?? 0;
        var shadowSpread = text.Shadow is { } shadow ? shadow.BlurRadius + Math.Max(Math.Abs(shadow.OffsetX), Math.Abs(shadow.OffsetY)) : 0;
        var pad = Math.Ceiling(4 + outlineWidth + shadowSpread);
        var width = Math.Ceiling(advance) + 2 * pad;
        var height = Math.Ceiling(ascent + descent) + 2 * pad;
        var baselineFromBottom = pad + descent;
        var baselineY = height - baselineFromBottom;
        if (MakeRun(positions.Select(p => p.X), width, height, baselineFromBottom, pad) is not { } run) return null;

        if (text.TickerSeparator.Length > 0 && text.String.EndsWith(text.TickerSeparator, StringComparison.Ordinal))
        {
            var phrase = text.String[..^text.TickerSeparator.Length];
            var displayPhrase = text.Transform == SceneTextTransform.Uppercase ? phrase.ToUpper(CultureInfo.CurrentCulture) : phrase;
            var phraseClusters = new StringInfo(displayPhrase).LengthInTextElements;
            var phraseSpaces = displayPhrase.Count(char.IsWhiteSpace);
            run = run with
            {
                SeparatorStart = (displayPhrase.Length == 0 ? 0 : Format(displayPhrase).WidthIncludingTrailingWhitespace)
                    + text.Tracking * phraseClusters + text.WordSpacing * phraseSpaces,
            };
        }

        // Each cluster's drawing, in strip coordinates: its glyphs on the baseline at its x.
        var fill = text.Fill is { } f and not SceneFill.NoFill ? Painting.FillBrush(f, new Rect(0, 0, width, height)) : null;
        Pen? outlinePen = null;
        if (text.Outline is { Width: > 0 } outline)
        {
            outlinePen = new Pen(Painting.Brush(outline.Color), outline.Width * 2) { LineJoin = PenLineJoin.Round };
            outlinePen.Freeze();
        }
        var clusters = new List<Cluster>(positions.Count);
        foreach (var (x, start, length) in positions)
        {
            var s = display.Substring(start, length);
            if (string.IsNullOrWhiteSpace(s))
            {
                clusters.Add(new Cluster(x, null));
                continue;
            }
            var glyphs = Format(s);
            if (fill is not null) glyphs.SetForegroundBrush(fill);
            var origin = new Point(pad + x, baselineY - glyphs.Baseline);
            var group = new DrawingGroup();
            using (var ctx = group.Open())
            {
                if (outlinePen is not null) ctx.DrawGeometry(null, outlinePen, glyphs.BuildGeometry(origin));
                if (brush is not null) ctx.DrawText(glyphs, origin);
            }
            group.Freeze();
            clusters.Add(new Cluster(x, group));
        }
        var uvWidths = new double[clusters.Count];
        foreach (var cell in run.Cells) if (cell.Source < uvWidths.Length) uvWidths[cell.Source] = cell.Uv.Width;
        return new Strip(run, clusters, baselineY, uvWidths);
    }

    private static string Key(StyledText t, double ppd)
    {
        var b = new StringBuilder(128);
        b.Append(t.String).Append('\u0001').Append(t.FontName).Append('|').Append(t.FontSize).Append('|').Append(t.Color).Append('|')
            .Append(t.Transform).Append('|').Append(t.Tracking).Append('|').Append(t.WordSpacing).Append('|').Append(t.TabularFigures).Append('|')
            .Append(t.Fill).Append('|').Append(t.Outline).Append('|').Append(t.Shadow).Append('|').Append(t.TickerSeparator).Append('|').Append(ppd);
        if (t.Fill is SceneFill.LinearGradient gradient) foreach (var stop in gradient.Stops) b.Append("|g").Append(stop);
        return b.ToString();
    }
}
