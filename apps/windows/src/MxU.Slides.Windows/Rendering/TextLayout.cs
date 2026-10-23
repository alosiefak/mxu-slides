using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// Lays out a <see cref="StyledText"/> inside a frame the way the Mac's
/// TextRasterizer does: insets, word wrap, line height multiple, vertical and
/// horizontal alignment, auto-shrink (binary search down to the minimum size),
/// keep-lines-whole, per-line overrides, style runs and chords above lines.
/// Each wrapped line is its own <see cref="FormattedText"/>, so line fills and
/// alignment are exact while WPF still handles shaping, combining marks and
/// font fallback within a line.
/// </summary>
public sealed class TextLayout
{
    /// <summary>
    /// One laid-out line. <paramref name="Glyphs"/> is set when the text has
    /// letter tracking: each character cluster is then drawn at its own x.
    /// </summary>
    public sealed record Line(FormattedText Text, double X, double Y, double Width, double Height, double BoxTop, double BoxHeight, int Paragraph,
        IReadOnlyList<(FormattedText Text, double X)>? Glyphs = null, int Start = 0, IReadOnlyList<int>? GlyphStarts = null)
    {
        /// <summary>The paragraph (source line) this line was wrapped from, for mapping character ranges.</summary>
        public string Source { get; init; } = "";
    }

    public sealed record Chord(FormattedText Text, double X, double Y);

    public IReadOnlyList<Line> Lines { get; private init; } = [];
    public IReadOnlyList<Chord> Chords { get; private init; } = [];
    public double FontScale { get; private init; } = 1;
    public SceneRect Bounds { get; private init; }
    public bool Overflows { get; private init; }

    private const double ChordScale = 0.62;

    private static readonly Dictionary<string, TextLayout> Cache = new(StringComparer.Ordinal);

    public static TextLayout Build(StyledText text, SceneRect frame, double pixelsPerDip)
    {
        var key = Signature(text, frame, pixelsPerDip);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }
        var layout = Compute(text, frame, pixelsPerDip);
        lock (Cache)
        {
            if (Cache.Count > 600) Cache.Clear();
            Cache[key] = layout;
        }
        return layout;
    }

    private static TextLayout Compute(StyledText text, SceneRect frame, double ppd)
    {
        var inner = new SceneRect(
            frame.X + text.InsetLeft,
            frame.Y + text.InsetTop,
            Math.Max(frame.Width - text.InsetLeft - text.InsetRight, 1),
            Math.Max(frame.Height - text.InsetTop - text.InsetBottom, 1));
        var display = text.DisplayString.Replace("\r\n", "\n").Replace('\r', '\n');
        if (display.Length == 0) return new TextLayout { Bounds = inner };

        var builder = new Builder(text, display, ppd);
        var scale = 1.0;
        if (text.AutoShrink && text.FontSize > 0 && !builder.Fits(1, inner))
        {
            var lo = Math.Min(1, Math.Max(0.01, text.MinFontSize / text.FontSize));
            var hi = 1.0;
            for (var i = 0; i < 10; i++)
            {
                var mid = (lo + hi) / 2;
                if (builder.Fits(mid, inner)) lo = mid; else hi = mid;
            }
            scale = Math.Floor(lo * 1000) / 1000;
        }
        return builder.Arrange(scale, inner);
    }

    private sealed class Builder(StyledText text, string display, double ppd)
    {
        private readonly string[] paragraphs = display.Split('\n');
        private readonly bool widthConstrained = text.Chords.Count > 0 || text.KeepLinesWhole;

        // Tracking spaces out character clusters; scripts whose letters join (Arabic, Indic, Thai…)
        // keep their shaping and ignore it rather than break apart.
        private readonly bool tracked = Math.Abs(text.Tracking) > 0.01 && !NeedsShaping(display);

        private sealed record Wrapped(int Paragraph, int Start, string Text, FormattedText Formatted, double ChordRow, double Width);

        private Wrapped Line(int paragraph, int start, string s, double scale, double chordRow)
        {
            var formatted = Format(paragraph, start, s, scale);
            return new Wrapped(paragraph, start, s, formatted, chordRow, WidthOf(formatted, s, scale));
        }

        private double WidthOf(FormattedText formatted, string s, double scale) =>
            formatted.Width + (tracked ? text.Tracking * scale * Math.Max(0, new StringInfo(s.TrimEnd()).LengthInTextElements - 1) : 0);

        private double Measure(int paragraph, int start, string s, double scale) => WidthOf(Format(paragraph, start, s, scale), s, scale);

        private static bool NeedsShaping(string s) => s.Any(c => c is >= '֐' and <= 'ࣿ' or >= 'ऀ' and <= '෿'
            or >= '฀' and <= '࿿' or >= 'က' and <= '႟' or >= 'ក' and <= '៿' or >= 'יִ' and <= '﻿');

        public bool Fits(double scale, SceneRect inner)
        {
            var lines = Wrap(scale, inner.Width);
            if (widthConstrained && lines.Any(l => l.Width > inner.Width - 1)) return false;
            return TotalHeight(lines, scale) <= inner.Height + 0.5;
        }

        /// <summary>Each character cluster of a tracked line with its x offset from the line start.</summary>
        private (List<(FormattedText Text, double X)> Glyphs, List<int> Starts) Glyphs(Wrapped line, double scale)
        {
            var glyphs = new List<(FormattedText, double)>();
            var starts = new List<int>();
            var x = 0.0;
            var elements = StringInfo.GetTextElementEnumerator(line.Text);
            while (elements.MoveNext())
            {
                var cluster = (string)elements.Current;
                var formatted = Format(line.Paragraph, line.Start + elements.ElementIndex, cluster, scale);
                if (!string.IsNullOrWhiteSpace(cluster))
                {
                    glyphs.Add((formatted, x));
                    starts.Add(elements.ElementIndex);
                }
                x += formatted.WidthIncludingTrailingWhitespace + text.Tracking * scale;
            }
            return (glyphs, starts);
        }

        private double LineBoxHeight(Wrapped line) => line.Formatted.Height * text.LineHeightMultiple + line.ChordRow;

        private double TotalHeight(IReadOnlyList<Wrapped> lines, double scale)
        {
            double height = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                height += LineBoxHeight(lines[i]);
                if (i > 0 && lines[i].Paragraph != lines[i - 1].Paragraph) height += text.ParagraphSpacing * scale;
            }
            return height;
        }

        public TextLayout Arrange(double scale, SceneRect inner)
        {
            var wrapped = Wrap(scale, inner.Width);
            var total = TotalHeight(wrapped, scale);
            var y = text.VerticalAlignment switch
            {
                SceneTextVerticalAlignment.Top => inner.Y,
                SceneTextVerticalAlignment.Bottom => inner.Bottom - total,
                _ => inner.Y + (inner.Height - total) / 2,
            };
            var lines = new List<Line>();
            var chords = new List<Chord>();
            double minX = double.MaxValue, maxX = double.MinValue;
            for (var i = 0; i < wrapped.Count; i++)
            {
                var w = wrapped[i];
                if (i > 0 && w.Paragraph != wrapped[i - 1].Paragraph) y += text.ParagraphSpacing * scale;
                var box = LineBoxHeight(w);
                var indentLeft = text.LeftIndent + (w.Start == 0 ? text.FirstLineIndent : 0);
                var available = Math.Max(1, inner.Width - indentLeft - text.RightIndent);
                var width = w.Width;
                var x = inner.X + indentLeft + text.Alignment switch
                {
                    SceneTextAlignment.Left => 0,
                    SceneTextAlignment.Right => available - width,
                    _ => (available - width) / 2,
                };
                // Extra line height goes above the glyphs, as in Core Text.
                var textTop = y + box - w.Formatted.Height;
                var glyphs = tracked && w.Text.Length > 0 ? Glyphs(w, scale) : default;
                lines.Add(new Line(w.Formatted, x, textTop, width, w.Formatted.Height, y, box, w.Paragraph,
                    glyphs.Glyphs, w.Start, glyphs.Starts) { Source = paragraphs[w.Paragraph] });
                if (w.ChordRow > 0) chords.AddRange(ChordsFor(w, x, y, scale));
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x + width);
                y += box;
            }
            var top = lines.Count > 0 ? lines[0].BoxTop : inner.Y;
            return new TextLayout
            {
                Lines = lines,
                Chords = chords,
                FontScale = scale,
                Bounds = lines.Count > 0 ? new SceneRect(minX, top, Math.Max(0, maxX - minX), y - top) : inner,
                Overflows = total > inner.Height + 0.5 || (widthConstrained && wrapped.Any(l => l.Width > inner.Width - 1)),
            };
        }

        private IEnumerable<Chord> ChordsFor(Wrapped line, double x, double boxTop, double scale)
        {
            var color = text.ChordColor ?? text.Color;
            // Chord columns count visible characters (grapheme clusters), as on the Mac.
            var elements = StringInfo.ParseCombiningCharacters(line.Text);
            foreach (var chord in text.Chords.Where(c => c.Line == line.Paragraph))
            {
                var visible = Math.Max(0, chord.Column);
                var column = visible < elements.Length ? elements[visible] : line.Text.Length;
                var prefix = column == 0 ? 0 : Format(line.Paragraph, line.Start, line.Text[..column], scale).WidthIncludingTrailingWhitespace
                    + (tracked ? text.Tracking * scale * visible : 0);
                var formatted = new FormattedText(chord.Symbol, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    FontMap.Resolve(BoldOf(text.FontName)).Typeface, text.FontSize * scale * ChordScale, Brushes.White, ppd);
                formatted.SetForegroundBrush(Painting.Brush(color));
                yield return new Chord(formatted, x + prefix, boxTop);
            }
        }

        private static string BoldOf(string fontName) => FontMap.WithTraits(fontName, bold: true, italic: false);

        private List<Wrapped> Wrap(double scale, double width)
        {
            var result = new List<Wrapped>();
            for (var p = 0; p < paragraphs.Length; p++)
            {
                var paragraph = paragraphs[p];
                var chordRow = text.Chords.Any(c => c.Line == p)
                    ? Format(p, 0, "Ag", scale).Height * ChordScale
                    : 0;
                if (paragraph.Length == 0 || widthConstrained)
                {
                    result.Add(Line(p, 0, paragraph, scale, chordRow));
                    continue;
                }
                var start = 0;
                var firstAvailable = Math.Max(1, width - text.LeftIndent - text.FirstLineIndent - text.RightIndent);
                var restAvailable = Math.Max(1, width - text.LeftIndent - text.RightIndent);
                while (start < paragraph.Length)
                {
                    var available = start == 0 ? firstAvailable : restAvailable;
                    var end = LineEnd(p, paragraph, start, available, scale);
                    var lineText = paragraph[start..end].TrimEnd(' ');
                    result.Add(Line(p, start, lineText, scale, chordRow));
                    start = end;
                    while (start < paragraph.Length && paragraph[start] == ' ') start++;
                }
            }
            return result;
        }

        /// <summary>The end index of the longest run from <paramref name="start"/> that fits, breaking at spaces.</summary>
        private int LineEnd(int p, string paragraph, int start, double available, double scale)
        {
            if (Measure(p, start, paragraph[start..], scale) <= available) return paragraph.Length;
            var end = start;
            var index = start;
            while (index < paragraph.Length)
            {
                var next = paragraph.IndexOf(' ', index);
                var wordEnd = next < 0 ? paragraph.Length : next;
                if (Measure(p, start, paragraph[start..wordEnd], scale) > available)
                {
                    if (end > start) return end;
                    return BreakWord(p, paragraph, start, wordEnd, available, scale);
                }
                end = wordEnd;
                if (next < 0) return paragraph.Length;
                index = next + 1;
            }
            return end > start ? end : paragraph.Length;
        }

        /// <summary>A single word wider than the line breaks between characters (never mid-grapheme).</summary>
        private int BreakWord(int p, string paragraph, int start, int wordEnd, double available, double scale)
        {
            var boundaries = StringInfo.ParseCombiningCharacters(paragraph[start..wordEnd]).Select(i => start + i).Append(wordEnd).ToArray();
            var fit = boundaries.Length > 1 ? boundaries[1] : wordEnd;
            foreach (var boundary in boundaries.Skip(1))
            {
                if (Measure(p, start, paragraph[start..boundary], scale) > available) break;
                fit = boundary;
            }
            return fit;
        }

        public FormattedText Format(int paragraph, int start, string s, double scale)
        {
            var lineOverride = text.LineOverrides.FirstOrDefault(o => o.LineIndex == paragraph);
            var fontName = lineOverride?.FontName ?? text.FontName;
            var size = (lineOverride?.FontSize ?? text.FontSize) * scale;
            var color = lineOverride?.Color ?? text.Color;
            var face = FontMap.Resolve(fontName);
            var measured = s.Length == 0 ? " " : s;
            var formatted = new FormattedText(measured, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                face.Typeface, Math.Max(size, 0.5), Painting.Brush(color), ppd)
            {
                Trimming = TextTrimming.None,
            };
            if (s.Length == 0) formatted.SetForegroundBrush(Brushes.Transparent);
            if (text.TabularFigures) formatted.SetNumberSubstitution(new NumberSubstitution());
            var decorations = new TextDecorationCollection();
            if (text.Underline) decorations.Add(TextDecorations.Underline);
            if (text.Strikethrough) decorations.Add(TextDecorations.Strikethrough);
            if (decorations.Count > 0 && s.Length > 0) formatted.SetTextDecorations(decorations);

            foreach (var run in text.StyleRuns.Where(r => r.Line == paragraph))
            {
                var from = Math.Max(run.Column, start) - start;
                var to = Math.Min(run.Column + run.Length, start + s.Length) - start;
                if (to <= from || s.Length == 0) continue;
                var count = to - from;
                if (run.FontName is { } runFont)
                {
                    var runFace = FontMap.Resolve(runFont);
                    formatted.SetFontFamily(runFace.Family, from, count);
                    formatted.SetFontWeight(runFace.Weight, from, count);
                    formatted.SetFontStyle(runFace.Style, from, count);
                }
                if (run.FontSize is { } runSize) formatted.SetFontSize(Math.Max(runSize * scale, 0.5), from, count);
                if (run.Color is { } runColor) formatted.SetForegroundBrush(Painting.Brush(runColor), from, count);
                var runDecorations = new TextDecorationCollection();
                if (run.Underline == true) runDecorations.Add(TextDecorations.Underline);
                if (run.Strikethrough == true) runDecorations.Add(TextDecorations.Strikethrough);
                if (runDecorations.Count > 0) formatted.SetTextDecorations(runDecorations, from, count);
            }
            return formatted;
        }
    }

    private static string Signature(StyledText t, SceneRect frame, double ppd)
    {
        var b = new StringBuilder(256);
        b.Append(t.String).Append('\u0001').Append(t.FontName).Append('|').Append(t.FontSize).Append('|')
            .Append(t.Color).Append('|').Append(t.Alignment).Append(t.VerticalAlignment).Append(t.Transform).Append('|')
            .Append(t.LineHeightMultiple).Append('|').Append(t.Tracking).Append('|').Append(t.AutoShrink).Append(t.MinFontSize).Append(t.KeepLinesWhole).Append('|')
            .Append(t.InsetTop).Append(',').Append(t.InsetLeft).Append(',').Append(t.InsetBottom).Append(',').Append(t.InsetRight).Append('|')
            .Append(t.FirstLineIndent).Append(',').Append(t.LeftIndent).Append(',').Append(t.RightIndent).Append(',').Append(t.ParagraphSpacing).Append('|')
            .Append(t.Underline).Append(t.Strikethrough).Append(t.TabularFigures).Append('|').Append(t.ChordColor).Append('|')
            .Append(frame.Width).Append('x').Append(frame.Height).Append('@').Append(frame.X).Append(',').Append(frame.Y).Append('|').Append(ppd);
        foreach (var o in t.LineOverrides) b.Append("|o").Append(o);
        foreach (var r in t.StyleRuns) b.Append("|r").Append(r);
        foreach (var c in t.Chords) b.Append("|c").Append(c);
        return b.ToString();
    }
}
