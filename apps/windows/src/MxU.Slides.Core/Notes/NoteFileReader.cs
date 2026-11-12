using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Notes;

/// <summary>
/// Turns a file someone drops or imports into a note: Word, ODT, RTF, PDF, PowerPoint or text.
/// Every reader gives the same thing, a list of <see cref="Line"/>s (runs with their marks, the
/// text's size, list and heading hints, pictures), and <see cref="Body(IReadOnlyList{Line})"/>
/// makes the note's blocks from them: the biggest type is a heading, "1." and "•" lines are lists.
/// Port of NoteFileReader.swift. The Mac reads Word, ODT and RTF through Cocoa's
/// NSAttributedString; Windows has a reader of its own for each (DocxNoteReader, OdtNoteReader,
/// and RtfNoteLines in ProImport), and PDF through PdfNoteReader.
/// </summary>
public static class NoteFileReader
{
    /// <summary>Saves a picture or video read out of a file and gives back its URL; null when it can't.</summary>
    public delegate string? SaveMedia(byte[] data, string fileName);

    /// <summary>A picture or video found in a file, saved where the note can show it.</summary>
    public sealed record Media(string Url, string FileName, bool Video)
    {
        /// <summary>True for a video, false for a picture, null for anything else.</summary>
        public static bool? IsVideo(string fileName) => MediaFileTypes.IsVideoFile(fileName);

        /// <summary>
        /// Saved through <paramref name="save"/>, unless it's no picture or video, or an icon (a
        /// picture under 48 pixels a side: mail apps' file-type chips).
        /// </summary>
        public static Media? Saving(byte[] data, string fileName, SaveMedia? save)
        {
            if (save is null || IsVideo(fileName) is not { } video || (!video && IsIcon(data, fileName))) return null;
            return save(data, fileName) is { } url ? new Media(url, fileName, video) : null;
        }

        internal static bool IsIcon(byte[] data, string fileName)
        {
            if (PictureSize(data) is not var (width, height)) return false;
            return Math.Min(width, height) < 48 || (fileName == "Attachment.png" && Math.Max(width, height) <= 48);
        }

        public NoteBlock Block => Video
            ? new NoteBlock { Type = NoteBlockType.Attachment, Url = Url, AttachmentKind = "video", Filename = FileName }
            : new NoteBlock { Type = NoteBlockType.Image, Url = Url };
    }

    /// <summary>A PNG's, JPEG's or GIF's size in pixels, read from its header; null for anything else.</summary>
    public static (int Width, int Height)? PictureSize(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 24 && data[..8].SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }))
            return ((int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]));
        if (data.Length >= 10 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
            return (BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            // A JPEG's size is in its start-of-frame segment.
            var at = 2;
            while (at + 9 < data.Length && data[at] == 0xFF)
            {
                var marker = data[at + 1];
                var length = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return (BinaryPrimitives.ReadUInt16BigEndian(data[(at + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(data[(at + 5)..]));
                at += 2 + length;
            }
        }
        return null;
    }

    /// <summary>One line (paragraph) read from a file.</summary>
    /// <param name="Size">Its text's size in points (the size most of its letters have); 0 when unknown.</param>
    /// <param name="Heading">A heading level the file itself gives (a PowerPoint title).</param>
    /// <param name="List">A list item: true numbered, false bulleted, null no list.</param>
    /// <param name="Media">A picture or video line instead of words.</param>
    /// <param name="Gap">An empty line.</param>
    public sealed record Line(List<NoteRun> Runs, double Size = 0, int? Heading = null, bool? List = null, Media? Media = null, bool Gap = false);

    /// <summary>The files a note can be made from.</summary>
    public static readonly IReadOnlyList<string> FileExtensions = ["docx", "rtf", "odt", "pdf", "txt", "text", "md", "pptx"];

    /// <summary>A dropped picture or video file as its own block.</summary>
    public static NoteBlock? MediaBlock(string path, SaveMedia? save)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return Media.Saving(data, Path.GetFileName(path), save)?.Block;
    }

    /// <summary>Plain text: a line each, blank lines as gaps.</summary>
    public static NoteBody Body(string text) =>
        Body(text.ComponentsSeparatedByNewlinesLocal().Select(l => new Line([new NoteRun { Text = l }], Gap: l.Trim(' ', '\t').Length == 0)).ToList());

    private static string[] ComponentsSeparatedByNewlinesLocal(this string text) => Lyrics.SwiftString.ComponentsSeparatedByNewlines(text);

    private static readonly Regex TabbedMarker = new(@"^\t(\d{1,3}[.)]?|[•◦▪▫‣⁃●○■□*-])\t", RegexOptions.CultureInvariant);
    private static readonly Regex ListTab = new(@"^\s*\S*\t", RegexOptions.CultureInvariant);

    /// <summary>
    /// A line from a rich-text file with its list read: a list the file marks (its own list
    /// marker, up to the tab, dropped), or "\t1\t" / "\t•\t" typed by hand.
    /// </summary>
    public static Line ReadingList(Line line, bool? listFromFile)
    {
        var head = NoteReading.Text(line.Runs);
        if (listFromFile is { } ordered)
        {
            var match = ListTab.Match(head);
            return line with { List = ordered, Runs = match.Success ? Dropping(match.Length, line.Runs) : line.Runs };
        }
        var typed = TabbedMarker.Match(head);
        if (!typed.Success) return line;
        return line with { List = head.Length > 1 && char.IsDigit(head[1]), Runs = Dropping(typed.Length, line.Runs) };
    }

    /// <summary>Pictures found apart from the words (after how many lines with words), put in among the lines.</summary>
    public static List<Line> Placing(IReadOnlyList<(int AfterWordLines, Media Media)> media, IReadOnlyList<Line> lines)
    {
        if (media.Count == 0) return lines.ToList();
        var result = new List<Line>();
        var words = 0;
        var pending = new Queue<(int AfterWordLines, Media Media)>(media);
        void Flush()
        {
            while (pending.Count > 0 && pending.Peek().AfterWordLines <= words) result.Add(new Line([], Media: pending.Dequeue().Media));
        }
        Flush();
        foreach (var line in lines)
        {
            result.Add(line);
            if (NoteReading.Text(line.Runs).TrimmingWhitespacesAndNewlinesLocal().Length > 0) words++;
            Flush();
        }
        result.AddRange(pending.Select(p => new Line([], Media: p.Media)));
        return result;
    }

    private static string TrimmingWhitespacesAndNewlinesLocal(this string text) => Lyrics.SwiftString.TrimmingWhitespacesAndNewlines(text);

    // PDF. A page's glyphs (from PdfNoteReader) are gathered into printed lines, and lines that
    // wrapped (the same size, close below, the line before running to the right edge) are joined
    // into their paragraph again.

    /// <summary>One glyph printed on a PDF page, in PDF points (y up).</summary>
    public sealed record PdfGlyph(string Text, double X, double Y, double Width, double Size, bool Bold = false, bool Light = false, string Font = "")
    {
        public double MaxX => X + Width;
    }

    /// <summary>A rectangle in PDF points (y up).</summary>
    public readonly record struct PdfRect(double X, double Y, double Width, double Height)
    {
        public double MinX => X;
        public double MaxX => X + Width;
        public double MaxY => Y + Height;
        public double MidY => Y + Height / 2;
    }

    /// <summary>A page's glyphs, its thin filled or stroked lines (underlines), and where its pictures are.</summary>
    public sealed record PdfPageText(IReadOnlyList<PdfGlyph> Glyphs, IReadOnlyList<PdfRect> Rules, IReadOnlyList<PdfRect> Images);

    /// <summary>A picture on a PDF page, by its page and its top (to place it among the lines).</summary>
    public sealed record PdfPicture(int Page, double Y, Media Media);

    private sealed record PrintedLine(List<NoteRun> Runs, double Size, int Page, double Y, double MaxX, double FirstWord);

    public static NoteBody Body(IReadOnlyList<PdfPageText> pages, IReadOnlyList<PdfPicture>? pictures = null)
    {
        var printed = pages.SelectMany((page, index) => PrintedLines(page, index)).ToList();
        var right = printed.Count == 0 ? 0 : printed.Max(l => l.MaxX);
        var lines = new List<Line>();
        PrintedLine? previous = null;
        var pending = new Queue<PdfPicture>((pictures ?? []).OrderBy(p => p.Page).ThenBy(p => -p.Y));
        foreach (var line in printed)
        {
            while (pending.Count > 0 && (pending.Peek().Page < line.Page || (pending.Peek().Page == line.Page && -pending.Peek().Y < -line.Y)))
            {
                lines.Add(new Line([], Media: pending.Dequeue().Media));
                previous = null;
            }
            if (previous is { } before && lines.Count > 0 && Math.Abs(before.Size - line.Size) < 0.6
                && (before.Page != line.Page || before.Y - line.Y <= line.Size * 1.45)
                && before.MaxX + line.Size * 0.6 + line.FirstWord > right
                && Marker(NoteReading.Text(line.Runs)) is null)
            {
                var last = lines[^1];
                lines[^1] = last with { Runs = [.. last.Runs, new NoteRun { Text = " " }, .. line.Runs] };
            }
            else
            {
                lines.Add(new Line(line.Runs, line.Size));
            }
            previous = line;
        }
        lines.AddRange(pending.Select(p => new Line([], Media: p.Media)));
        return Body(lines);
    }

    private static List<PrintedLine> PrintedLines(PdfPageText page, int index)
    {
        var glyphs = page.Glyphs.Where(g => g.Text.Length > 0).OrderByDescending(g => g.Y).ToList();
        var rows = new List<List<PdfGlyph>>();
        foreach (var glyph in glyphs)
        {
            // A glyph within half a size of the row's biggest is on that row.
            if (rows.Count > 0 && rows[^1].MaxBy(g => g.Size) is { } anchor && anchor.Y - glyph.Y <= Math.Max(anchor.Size, glyph.Size) * 0.5)
                rows[^1].Add(glyph);
            else
                rows.Add([glyph]);
        }
        var result = new List<PrintedLine>();
        foreach (var unsorted in rows)
        {
            var row = unsorted.OrderBy(g => g.X).ToList();
            var sizes = new Dictionary<double, int>();
            foreach (var glyph in row.Where(g => g.Text != " "))
            {
                var key = Math.Round(glyph.Size * 2, MidpointRounding.AwayFromZero) / 2;
                sizes[key] = sizes.GetValueOrDefault(key) + 1;
            }
            var size = sizes.Count == 0 ? 0 : sizes.MaxBy(p => p.Value).Key;
            var runs = new List<NoteRun>();
            PdfGlyph? last = null;
            foreach (var glyph in row)
            {
                // A gap wider than a fifth of the size is a space the PDF didn't print.
                if (last is not null && glyph.X - last.MaxX > size * 0.18 && last.Text != " " && glyph.Text != " ")
                    runs.Add(runs[^1].WithText(" "));
                var run = new NoteRun { Text = glyph.Text };
                var font = glyph.Font.ToLowerInvariant();
                if (glyph.Bold) run.Bold = true;
                if (font.Contains("italic") || font.Contains("oblique")) run.Italic = true;
                var middle = glyph.X + glyph.Width / 2;
                if (page.Rules.Any(r => r.MinX <= middle && middle <= r.MaxX && r.MidY < glyph.Y + size * 0.05 && r.MidY > glyph.Y - size * 0.4))
                    run.Underline = true;
                runs.Add(run);
                last = glyph;
            }
            var text = NoteReading.Text(runs).Trim(' ', '\t');
            // Page numbers aren't the note's.
            if (text.Length == 0 || text.All(char.IsDigit)) continue;
            var firstSpace = row.FindIndex(g => g.Text == " ");
            if (firstSpace < 0) firstSpace = row.Count;
            var start = row.Count > 0 ? row[0].X : 0;
            var firstWord = firstSpace > 0 ? row[firstSpace - 1].MaxX - start : 0;
            result.Add(new PrintedLine(runs, size, index, row.MaxBy(g => g.Size)?.Y ?? 0, row.Count > 0 ? row[^1].MaxX : 0, firstWord));
        }
        return result;
    }

    /// <summary>
    /// The note's blocks from its lines: words tidied, blank lines dropped (kept once between
    /// marker passages), the biggest sizes as headings, "•" and "1." lines (or lines the file
    /// says are lists) as lists, pictures as their blocks.
    /// </summary>
    public static NoteBody Body(IReadOnlyList<Line> lines)
    {
        var markers = lines.Any(l => NoteSlides.StartsSlide(new NoteBlock { Type = NoteBlockType.Paragraph, Runs = Tidied(l.Runs) }));
        var kept = new List<Line>();
        foreach (var original in lines)
        {
            var line = original with { Runs = Tidied(original.Runs) };
            if (line.Gap || (line.Runs.Count == 0 && line.Media is null))
            {
                // Under markers an empty line ends a "Slide 3:" passage, so one is kept.
                if (line.Gap && markers && kept.Count > 0 && !kept[^1].Gap) kept.Add(new Line([], Gap: true));
                continue;
            }
            kept.Add(line);
        }
        if (kept.Count > 0 && kept[^1].Gap) kept.RemoveAt(kept.Count - 1);
        var counts = new Dictionary<double, int>();
        foreach (var line in kept.Where(l => l.Size > 0)) counts[line.Size] = counts.GetValueOrDefault(line.Size) + NoteReading.Text(line.Runs).Length;
        var bodySize = counts.Count == 0 ? 0 : counts.MaxBy(p => p.Value).Key;
        var headingSizes = kept.Select(l => l.Size).Where(s => bodySize > 0 && s >= bodySize * 1.2).Distinct().OrderByDescending(s => s).ToList();

        var blocks = new List<NoteBlock>();
        foreach (var line in kept)
        {
            if (line.Gap)
            {
                blocks.Add(new NoteBlock { Type = NoteBlockType.Paragraph, Runs = [] });
                continue;
            }
            if (line.Media is { } media)
            {
                blocks.Add(media.Block);
                continue;
            }
            var runs = line.Runs;
            var ordered = line.List;
            if (ordered is null && Marker(NoteReading.Text(runs)) is { } found)
            {
                ordered = found.Ordered;
                runs = Dropping(found.Length, runs);
            }
            if (ordered is { } isOrdered)
            {
                var item = new NoteListItem { Runs = runs };
                if (blocks.Count > 0 && blocks[^1].Type == NoteBlockType.List && blocks[^1].Ordered == isOrdered) blocks[^1].Items!.Add(item);
                else blocks.Add(new NoteBlock { Type = NoteBlockType.List, Ordered = isOrdered, Items = [item] });
            }
            else if ((line.Heading ?? (headingSizes.IndexOf(line.Size) is var at and >= 0 ? Math.Min(at + 1, 3) : null)) is { } level)
            {
                blocks.Add(new NoteBlock { Type = NoteBlockType.Heading, Level = level, Runs = runs });
            }
            else
            {
                blocks.Add(new NoteBlock { Type = NoteBlockType.Paragraph, Runs = runs });
            }
        }
        return new NoteBody { Blocks = blocks, PlainText = string.Join("\n", blocks.Select(NoteReading.Text)) };
    }

    private static readonly Regex Bullet = new(@"^[•◦▪▫‣⁃●○■□*-]\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Number = new(@"^\d{1,3}[.)]\s+", RegexOptions.CultureInvariant);

    /// <summary>A list marker at the start of a line ("• ", "- ", "2. ", "3) "): numbered or not, and its length.</summary>
    internal static (bool Ordered, int Length)? Marker(string text)
    {
        if (Bullet.Match(text) is { Success: true } bullet) return (false, bullet.Length);
        if (Number.Match(text) is { Success: true } number) return (true, number.Length);
        return null;
    }

    /// <summary>Runs with their spacing tidied: no space at the ends, one space between words, neighbours with the same marks joined.</summary>
    internal static List<NoteRun> Tidied(IReadOnlyList<NoteRun> runs)
    {
        var result = new List<NoteRun>();
        var pendingSpace = false;
        foreach (var run in runs)
        {
            foreach (var character in Lyrics.SwiftString.Characters(run.Text))
            {
                if (character.All(char.IsWhiteSpace))
                {
                    pendingSpace = result.Count > 0;
                    continue;
                }
                if (pendingSpace) result[^1] = result[^1].WithText(result[^1].Text + " ");
                pendingSpace = false;
                if (result.Count > 0 && result[^1].SameMarks(run)) result[^1] = result[^1].WithText(result[^1].Text + character);
                else result.Add(run.WithText(character));
            }
        }
        return result;
    }

    /// <summary>The runs without their first <paramref name="count"/> characters.</summary>
    internal static List<NoteRun> Dropping(int count, IReadOnlyList<NoteRun> runs)
    {
        var left = count;
        var result = new List<NoteRun>();
        foreach (var run in runs)
        {
            if (left <= 0)
            {
                result.Add(run);
                continue;
            }
            var cut = Math.Min(left, run.Text.Length);
            left -= cut;
            if (cut < run.Text.Length) result.Add(run.WithText(run.Text[cut..]));
        }
        return result;
    }
}
