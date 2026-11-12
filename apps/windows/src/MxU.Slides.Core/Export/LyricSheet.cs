using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Export;

/// <summary>
/// A song's words laid out on paper: the title, a line of credits (author, CCLI number, key), then
/// each section in the order it's sung, its name above its lines; and, for a chord chart, each
/// chord above the letter it falls on. At the foot of the last page, the copyright and CCLI lines
/// a printed sheet must carry. Windows only: the Mac exports a song's words as ChordPro text.
/// <para>
/// This only works out where each piece of text goes (so it's tested without a font file); the
/// text's widths come from <see cref="Measure"/>, and <see cref="Draw"/> writes the result into a
/// <see cref="PdfDocument"/>. Coordinates are PDF points, y up from the bottom of the page.
/// </para>
/// </summary>
public sealed class LyricSheet
{
    public const double Margin = 54;
    public const double TitleSize = 20;
    public const double CreditsSize = 10;
    public const double SectionSize = 11;
    public const double LyricSize = 12.5;
    public const double ChordSize = 10.5;
    public const double FooterSize = 8;

    /// <summary>How wide a piece of text is, in points: its text, size, and whether it's bold.</summary>
    public delegate double Measure(string text, double size, bool bold);

    /// <summary>One piece of text placed on a page: its baseline's start, size, weight and grey (0 black, 1 white).</summary>
    public sealed record Run(double X, double Y, double Size, bool Bold, double Gray, string Text, RunKind Kind);

    public enum RunKind
    {
        Title,
        Credits,
        Section,
        Lyric,
        Chord,
        Footer,
    }

    public sealed record Page(IReadOnlyList<Run> Runs);

    public LyricSheet(double pageWidth, double pageHeight, bool withChords)
    {
        PageWidth = pageWidth;
        PageHeight = pageHeight;
        WithChords = withChords;
    }

    public double PageWidth { get; }
    public double PageHeight { get; }
    public bool WithChords { get; }

    /// <summary>
    /// The song's pages. A section's name is never left alone at the foot of a page (it moves over
    /// with its first line), and a chord line never parts from the words it belongs to.
    /// </summary>
    public IReadOnlyList<Page> Layout(Presentation song, Measure measure)
    {
        var pages = new List<Page>();
        var runs = new List<Run>();
        var y = PageHeight - Margin;
        var width = PageWidth - 2 * Margin;

        void NewPage()
        {
            pages.Add(new Page(runs));
            runs = [];
            y = PageHeight - Margin;
        }

        // The title wraps onto a second line rather than run off a narrow page.
        foreach (var line in Wrap(song.Name.Length == 0 ? "Untitled" : song.Name, TitleSize, true, width, measure))
        {
            y -= TitleSize;
            runs.Add(new Run(Margin, y, TitleSize, true, 0, line, RunKind.Title));
            y -= 4;
        }
        if (Credits(song) is { Length: > 0 } credits)
        {
            y -= CreditsSize + 2;
            runs.Add(new Run(Margin, y, CreditsSize, false, 0.35, credits, RunKind.Credits));
        }
        y -= 10;

        var footer = Footer(song);
        var footerHeight = footer.Count * (FooterSize + 3) + (footer.Count > 0 ? 10 : 0);
        var bottom = Margin;
        var lineGap = LyricSize * 0.45;
        var chordLift = WithChords ? ChordSize + 2 : 0;

        foreach (var block in ChordProExport.Blocks(song))
        {
            var lines = block.Stanzas.SelectMany((stanza, i) => (i == 0 ? Array.Empty<string?>() : [null]).Concat(stanza.Split('\n'))).ToList();
            var pieces = lines.Select(line => line is null ? null : Piece(line, song, measure)).ToList();
            var first = pieces.FirstOrDefault(p => p is not null);
            var sectionHeight = block.Name is null ? 0 : SectionSize + 6;
            var firstHeight = first is null ? 0 : LineHeight(first.Chords.Count > 0);
            // The section's name and its first line go together.
            if (y - sectionHeight - firstHeight < bottom && runs.Count > 0) NewPage();
            if (block.Name is { } name)
            {
                y -= SectionSize;
                runs.Add(new Run(Margin, y, SectionSize, true, 0.3, name.ToUpperInvariant(), RunKind.Section));
                y -= 6;
            }
            foreach (var piece in pieces)
            {
                if (piece is null)
                {
                    y -= lineGap; // the gap between two stanzas of one section
                    continue;
                }
                foreach (var row in WrapPiece(piece, width, measure))
                {
                    var height = LineHeight(row.Chords.Count > 0);
                    if (y - height < bottom) NewPage();
                    if (row.Chords.Count > 0)
                    {
                        y -= ChordSize;
                        foreach (var (x, symbol) in row.Chords) runs.Add(new Run(Margin + x, y, ChordSize, true, 0.15, symbol, RunKind.Chord));
                        y -= 2;
                    }
                    y -= LyricSize;
                    if (row.Text.Length > 0) runs.Add(new Run(Margin, y, LyricSize, false, 0, row.Text, RunKind.Lyric));
                    y -= lineGap;
                }
            }
            y -= LyricSize * 0.8; // the space between sections
        }

        if (footer.Count > 0)
        {
            if (y - footerHeight < bottom) NewPage();
            var at = Margin + (footer.Count - 1) * (FooterSize + 3);
            foreach (var line in footer)
            {
                runs.Add(new Run(Margin, at, FooterSize, false, 0.45, line, RunKind.Footer));
                at -= FooterSize + 3;
            }
        }
        pages.Add(new Page(runs));
        return pages;

        double LineHeight(bool chords) => (chords ? chordLift : 0) + LyricSize + lineGap;
    }

    /// <summary>"John Newton · CCLI 22025 · Key G", the parts the song has.</summary>
    public static string Credits(Presentation song)
    {
        var parts = new List<string>();
        if (song.Ccli?.Author is { Length: > 0 } author) parts.Add(author);
        if (song.Ccli?.SongNumber is { } number) parts.Add($"CCLI {number}");
        if ((song.DisplayKey ?? song.MusicKey) is { Length: > 0 } key) parts.Add($"Key {key}");
        return string.Join("  ·  ", parts);
    }

    /// <summary>The copyright lines, then "CCLI Song # 22025" when the song has a number.</summary>
    public static IReadOnlyList<string> Footer(Presentation song)
    {
        var lines = (song.Ccli?.Copyright ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => l.StartsWith('©') ? l : "© " + l).ToList();
        if (song.Ccli?.SongNumber is { } number) lines.Add($"CCLI Song # {number}");
        return lines;
    }

    /// <summary>A lyric line with its chords placed: each chord's x (from the margin), never overlapping the one before.</summary>
    private sealed record Row(string Text, IReadOnlyList<(double X, string Symbol)> Chords);

    private sealed record LinePiece(string Text, IReadOnlyList<(int Column, string Symbol)> Chords);

    private LinePiece Piece(string line, Presentation song, Measure measure)
    {
        var (text, chords) = ChordMath.ExtractLine(line);
        if (!WithChords) return new LinePiece(text, []);
        // The chords in the key the song is shown in (a capo or a transposed key), as the slides show them.
        var shown = chords.Select(c => (c.Column, ChordMath.Display(c.Symbol, song.MusicKey, song.DisplayKey, ChordNotation.Chords))).ToList();
        return new LinePiece(text, shown);
    }

    /// <summary>
    /// A line too wide for the page is broken at a space, and its chords go with their letters. A
    /// chord sits at the width of the words before it; if the chord before it is still wider than
    /// that, it moves right just enough to leave a space.
    /// </summary>
    private IEnumerable<Row> WrapPiece(LinePiece piece, double width, Measure measure)
    {
        var text = piece.Text;
        var chords = piece.Chords.OrderBy(c => c.Column).ToList();
        var start = 0;
        while (true)
        {
            var rest = text[start..];
            var cut = rest.Length;
            if (measure(rest, LyricSize, false) > width)
            {
                // The last space that still fits.
                cut = 0;
                for (var i = rest.Length - 1; i > 0; i--)
                    if (rest[i] == ' ' && measure(rest[..i], LyricSize, false) <= width)
                    {
                        cut = i;
                        break;
                    }
                if (cut == 0) cut = rest.Length; // one long word: let it run over rather than split it
            }
            var end = start + cut;
            var last = end >= text.Length;
            var placed = new List<(double X, string Symbol)>();
            var minX = 0.0;
            // A chord on the space the line breaks at stays at the end of this row.
            foreach (var (column, symbol) in chords.Where(c => c.Column >= start && (last || c.Column <= end)))
            {
                var at = Math.Min(column, text.Length) - start;
                var x = Math.Max(measure(text.Substring(start, Math.Max(0, Math.Min(at, cut))), LyricSize, false), minX);
                placed.Add((x, symbol));
                minX = x + measure(symbol + " ", ChordSize, true);
            }
            yield return new Row(text.Substring(start, cut).TrimEnd(), placed);
            if (end >= text.Length) yield break;
            start = end + 1; // past the space it broke at
        }
    }

    private static IEnumerable<string> Wrap(string text, double size, bool bold, double width, Measure measure)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = "";
        foreach (var word in words)
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && measure(candidate, size, bold) > width)
            {
                yield return line;
                line = word;
            }
            else
            {
                line = candidate;
            }
        }
        if (line.Length > 0) yield return line;
    }

    /// <summary>Writes the laid-out pages into <paramref name="pdf"/> with a regular and a bold font.</summary>
    public static void Draw(PdfDocument pdf, IEnumerable<Page> pages, double pageWidth, double pageHeight, PdfFont regular, PdfFont bold)
    {
        foreach (var page in pages)
        {
            var target = pdf.AddPage(pageWidth, pageHeight);
            foreach (var run in page.Runs) target.Text(run.Bold ? bold : regular, run.Size, run.X, run.Y, run.Text, run.Gray);
        }
    }
}
