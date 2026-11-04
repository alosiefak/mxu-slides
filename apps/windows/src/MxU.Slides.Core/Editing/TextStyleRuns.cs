using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Editing;

/// <summary>
/// Styling part of a text box's words: bold on one word, a colour on a line. The shared format keeps
/// these as <see cref="TextStyleRun"/>s (line, column, length, and what's different), counted in
/// characters as the Mac counts them: what you see as one letter, even an emoji that's two UTF-16
/// units. A port of the Mac's TextStyleRuns.swift: applying a style to a selection, finding the
/// style at a point, and keeping runs (and chords, and animation ranges) on the right words while
/// the text is edited around them.
/// <para>
/// A position is a character offset into the whole text, each line break counting one, as on the Mac.
/// A run never spans a line break: a selection over two lines makes a run on each.
/// </para>
/// </summary>
public static class TextStyleRuns
{
    /// <summary>A range of characters, start inclusive, end exclusive.</summary>
    public readonly record struct Range(int Start, int End)
    {
        public int Length => End - Start;

        public bool IsEmpty => End <= Start;

        public bool Contains(int position) => position >= Start && position < End;

        public Range Clamped(Range to) => new(Math.Clamp(Start, to.Start, to.End), Math.Clamp(End, to.Start, to.End));
    }

    /// <summary>
    /// A text box's selection (UTF-16 offsets, as WPF counts) as characters. A selection that ends
    /// inside a two-unit character takes all of it (the Mac's characterRange(fromUTF16:)).
    /// </summary>
    public static Range CharacterRange(int utf16Location, int utf16Length, string text)
    {
        int Characters(int offset, bool roundDown)
        {
            var clamped = Math.Clamp(offset, 0, text.Length);
            var characters = 0;
            var units = 0;
            var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext())
            {
                var size = e.GetTextElement().Length;
                if (units + size > clamped) return units == clamped || roundDown ? characters : characters + 1;
                units += size;
                characters++;
            }
            return characters;
        }
        var start = Characters(utf16Location, roundDown: true);
        var end = Characters(utf16Location + utf16Length, roundDown: false);
        return new Range(start, Math.Max(start, end));
    }

    /// <summary>The UTF-16 offset of a character position (to put a caret or a selection back).</summary>
    public static int Utf16Offset(int characters, string text) => text.Utf16Offset(characters);

    // MARK: Styles

    private sealed record RunStyle(bool? Underline, bool? Strikethrough, string? FontName, double? FontSize, string? ColorHex, string? HighlightColorHex, double? Tracking)
    {
        public static readonly RunStyle None = new(null, null, null, null, null, null, null);

        public static RunStyle Of(TextStyleRun run) =>
            new(run.Underline, run.Strikethrough, run.FontName, run.FontSize, run.ColorHex, run.HighlightColorHex, run.Tracking);

        public bool IsEmpty => this == None;

        /// <summary>A later run's values win where it has them.</summary>
        public RunStyle Fold(RunStyle other) => new(
            other.Underline ?? Underline, other.Strikethrough ?? Strikethrough, other.FontName ?? FontName, other.FontSize ?? FontSize,
            other.ColorHex ?? ColorHex, other.HighlightColorHex ?? HighlightColorHex, other.Tracking ?? Tracking);

        public TextStyleRun Run(int line, int column, int length) => new()
        {
            Line = line, Column = column, Length = length,
            Underline = Underline, Strikethrough = Strikethrough, FontName = FontName, FontSize = FontSize,
            ColorHex = ColorHex, HighlightColorHex = HighlightColorHex, Tracking = Tracking,
        };
    }

    private readonly record struct Segment(Range Range, RunStyle Style);

    private static string[] Lines(string text) => text.Split('\n');

    private static int[] LineStarts(string[] lines)
    {
        var starts = new int[lines.Length];
        var running = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            starts[i] = running;
            running += lines[i].CharacterCount() + 1;
        }
        return starts;
    }

    private static List<Segment> Segments(IEnumerable<TextStyleRun> runs, string[] lines, int[] starts)
    {
        var segments = new List<Segment>();
        foreach (var run in runs)
        {
            if (run.Line < 0 || run.Line >= lines.Length || run.Length <= 0) continue;
            var count = lines[run.Line].CharacterCount();
            var column = Math.Clamp(run.Column, 0, count);
            var end = Math.Min(column + run.Length, count);
            if (end <= column) continue;
            segments.Add(new Segment(new Range(starts[run.Line] + column, starts[run.Line] + end), RunStyle.Of(run)));
        }
        return segments;
    }

    private static List<TextStyleRun> Runs(IEnumerable<Segment> segments, int[] starts)
    {
        var merged = new List<Segment>();
        foreach (var segment in segments.OrderBy(s => s.Range.Start))
        {
            if (segment.Style.IsEmpty || segment.Range.IsEmpty) continue;
            if (merged.Count > 0 && merged[^1].Range.End == segment.Range.Start && merged[^1].Style == segment.Style)
                merged[^1] = merged[^1] with { Range = new Range(merged[^1].Range.Start, segment.Range.End) };
            else
                merged.Add(segment);
        }
        var runs = new List<TextStyleRun>();
        foreach (var segment in merged)
        {
            var line = Array.FindLastIndex(starts, s => s <= segment.Range.Start);
            if (line < 0) continue;
            runs.Add(segment.Style.Run(line, segment.Range.Start - starts[line], segment.Range.Length));
        }
        return runs;
    }

    /// <summary>The style at a character (every run over it folded together), or null when it's plain.</summary>
    public static TextStyleRun? EffectiveRun(int position, IReadOnlyList<TextStyleRun> runs, string text)
    {
        var lines = Lines(text);
        var starts = LineStarts(lines);
        var folded = RunStyle.None;
        foreach (var segment in Segments(runs, lines, starts).Where(s => s.Range.Contains(position))) folded = folded.Fold(segment.Style);
        return folded.IsEmpty ? null : folded.Run(0, 0, 0);
    }

    /// <summary>
    /// Changes the style of the selected characters: <paramref name="mutate"/> is given each piece's
    /// current style to change (set a colour, clear the underline). Existing runs are split where the
    /// selection cuts them, lines it doesn't reach pass through as they are, and neighbours that end
    /// up the same join into one run.
    /// </summary>
    public static List<TextStyleRun> Applying(Action<TextStyleRun> mutate, IReadOnlyList<TextStyleRun> runs, string text, Range selection)
    {
        if (selection.IsEmpty) return [.. runs];
        var lines = Lines(text);
        var starts = LineStarts(lines);
        var passthrough = new List<TextStyleRun>();
        var rebuilt = new List<Segment>();
        for (var index = 0; index < lines.Length; index++)
        {
            var content = new Range(starts[index], starts[index] + lines[index].CharacterCount());
            var selected = selection.Clamped(content);
            var lineRuns = runs.Where(r => r.Line == index).ToList();
            if (selected.IsEmpty)
            {
                passthrough.AddRange(lineRuns);
                continue;
            }
            var lineSegments = Segments(lineRuns, lines, starts);
            var boundaries = new SortedSet<int> { selected.Start, selected.End };
            foreach (var segment in lineSegments)
            {
                boundaries.Add(segment.Range.Start);
                boundaries.Add(segment.Range.End);
            }
            var sorted = boundaries.ToList();
            for (var i = 0; i + 1 < sorted.Count; i++)
            {
                var (start, end) = (sorted[i], sorted[i + 1]);
                var folded = RunStyle.None;
                foreach (var segment in lineSegments.Where(s => s.Range.Start <= start && end <= s.Range.End)) folded = folded.Fold(segment.Style);
                if (selected.Start <= start && end <= selected.End)
                {
                    var run = folded.Run(index, start - starts[index], end - start);
                    mutate(run);
                    folded = RunStyle.Of(run);
                }
                rebuilt.Add(new Segment(new Range(start, end), folded));
            }
        }
        return [.. passthrough.Concat(Runs(rebuilt, starts)).OrderBy(r => r.Line).ThenBy(r => r.Column)];
    }

    // MARK: Editing the text under the runs

    /// <summary>
    /// The runs after <paramref name="replaced"/> (characters of <paramref name="oldText"/>) is
    /// replaced with <paramref name="replacement"/>: runs after it shift, runs it cuts are trimmed,
    /// a run it falls inside grows or shrinks, and a run it covers entirely goes.
    /// </summary>
    public static List<TextStyleRun> Replacing(IReadOnlyList<TextStyleRun> runs, Range replaced, string replacement, string oldText)
    {
        var oldLines = Lines(oldText);
        var oldStarts = LineStarts(oldLines);
        var newText = Splice(oldText, replaced, replacement);
        var newLines = Lines(newText);
        var newStarts = LineStarts(newLines);
        var delta = replacement.CharacterCount() - replaced.Length;

        var shifted = new List<Segment>();
        foreach (var segment in Segments(runs, oldLines, oldStarts))
        {
            var range = segment.Range;
            if (range.Start < replaced.Start && replaced.End < range.End)
            {
                shifted.Add(segment with { Range = new Range(range.Start, range.End + delta) });
                continue;
            }
            var before = range.Clamped(new Range(0, replaced.Start));
            if (!before.IsEmpty) shifted.Add(segment with { Range = before });
            var after = range.Clamped(new Range(replaced.End, Math.Max(replaced.End, range.End)));
            if (!after.IsEmpty) shifted.Add(segment with { Range = new Range(after.Start + delta, after.End + delta) });
        }

        // A run that now spans a line break (a newline was typed inside it) becomes one per line.
        var pieces = new List<Segment>();
        foreach (var segment in shifted)
        {
            for (var index = 0; index < newLines.Length; index++)
            {
                var content = new Range(newStarts[index], newStarts[index] + newLines[index].CharacterCount());
                var overlap = segment.Range.Clamped(content);
                if (!overlap.IsEmpty) pieces.Add(segment with { Range = overlap });
            }
        }
        return Runs(pieces, newStarts);
    }

    /// <summary>The chords after the same edit: those after it move with their words; those on replaced words go.</summary>
    public static List<ChordPlacement> Replacing(IReadOnlyList<ChordPlacement> chords, Range replaced, string replacement, string oldText)
    {
        var oldLines = Lines(oldText);
        var oldStarts = LineStarts(oldLines);
        var newText = Splice(oldText, replaced, replacement);
        var newLines = Lines(newText);
        var newStarts = LineStarts(newLines);
        var delta = replacement.CharacterCount() - replaced.Length;
        var result = new List<ChordPlacement>();
        foreach (var chord in chords)
        {
            if (chord.Line < 0 || chord.Line >= oldLines.Length)
            {
                result.Add(chord);
                continue;
            }
            var position = oldStarts[chord.Line] + Math.Clamp(chord.Column, 0, oldLines[chord.Line].CharacterCount());
            int moved;
            if (position < replaced.Start) moved = position;
            else if (position >= replaced.End) moved = position + delta;
            else continue;
            var line = Array.FindLastIndex(newStarts, s => s <= moved);
            if (line < 0) continue;
            result.Add(new ChordPlacement { Line = line, Column = Math.Min(moved - newStarts[line], newLines[line].CharacterCount()), Symbol = chord.Symbol });
        }
        return result;
    }

    /// <summary>Animation ranges after the same edit (the Mac's AnimationRanges.replacing).</summary>
    public static List<AnimationRange> Replacing(IReadOnlyList<AnimationRange> ranges, Range replaced, string replacement, string oldText) =>
        [.. ranges.SelectMany(range => Replacing([new TextStyleRun { Line = range.Line, Column = range.Column, Length = range.Length, Underline = true }], replaced, replacement, oldText)
            .Select(r => new AnimationRange { Line = r.Line, Column = r.Column, Length = r.Length }))];

    /// <summary>
    /// Steps aimed at part of the text, kept on their words through an edit; a step whose words are
    /// all gone goes too, and a step on the whole object stays (the Mac's AnimationRanges.reanchor).
    /// </summary>
    public static List<AnimationStep> Reanchor(IReadOnlyList<AnimationStep> steps, Range replaced, string replacement, string oldText)
    {
        var result = new List<AnimationStep>();
        foreach (var step in steps)
        {
            if (step.Ranges is not { Count: > 0 } ranges)
            {
                result.Add(step);
                continue;
            }
            var moved = Replacing(ranges, replaced, replacement, oldText);
            if (moved.Count == 0) continue;
            step.Ranges = moved;
            result.Add(step);
        }
        return result;
    }

    /// <summary>The text with <paramref name="range"/> (characters) replaced.</summary>
    public static string Splice(string text, Range range, string replacement)
    {
        var characters = text.Characters();
        var start = Math.Clamp(range.Start, 0, characters.Count);
        var end = Math.Clamp(range.End, start, characters.Count);
        return string.Concat(characters.Take(start)) + replacement + string.Concat(characters.Skip(end));
    }
}

/// <summary>Editing a text box's words while its styling, chords and animation ranges stay on the right words.</summary>
public static class ObjectText
{
    /// <summary>Replaces characters of the object's text (the Mac's SlideObject.replaceText).</summary>
    public static void ReplaceText(SlideObject o, TextStyleRuns.Range replaced, string replacement)
    {
        var old = o.Text;
        if (o.StyleRuns is { Count: > 0 } runs)
        {
            var moved = TextStyleRuns.Replacing(runs, replaced, replacement, old);
            o.StyleRuns = moved.Count > 0 ? moved : null;
        }
        if (o.Chords is { Count: > 0 } chords)
        {
            var moved = TextStyleRuns.Replacing(chords, replaced, replacement, old);
            o.Chords = moved.Count > 0 ? moved : null;
        }
        if (o.AnimationSteps is { } steps && steps.Any(s => s.Ranges is { Count: > 0 }))
        {
            var moved = TextStyleRuns.Reanchor(steps, replaced, replacement, old);
            o.AnimationSteps = moved.Count > 0 ? moved : null;
        }
        o.Text = TextStyleRuns.Splice(old, replaced, replacement);
    }

    /// <summary>
    /// Sets the whole text, as a text box does on every keystroke, working out which characters
    /// changed (what's the same at the start and at the end is kept) so styling and chords follow.
    /// </summary>
    public static void SetText(SlideObject o, string text)
    {
        if (o.Text == text) return;
        if (o.StyleRuns is not { Count: > 0 } && o.Chords is not { Count: > 0 } && o.AnimationSteps?.Any(s => s.Ranges is { Count: > 0 }) != true)
        {
            o.Text = text;
            return;
        }
        var (replaced, replacement) = Difference(o.Text, text);
        ReplaceText(o, replaced, replacement);
    }

    /// <summary>The one stretch of characters that changed between two texts, and what replaced it.</summary>
    public static (TextStyleRuns.Range Replaced, string Replacement) Difference(string before, string after)
    {
        var a = before.Characters();
        var b = after.Characters();
        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix]) suffix++;
        return (new TextStyleRuns.Range(prefix, a.Count - suffix), string.Concat(b.Skip(prefix).Take(b.Count - prefix - suffix)));
    }
}
