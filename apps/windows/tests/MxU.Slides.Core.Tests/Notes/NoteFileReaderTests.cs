using System.IO.Compression;
using System.Security;
using System.Text;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace MxU.Slides.Core.Tests.Notes;

/// <summary>
/// Port of the Mac's NoteFileReaderTests.swift and NoteFileMediaTests.swift. The Mac writes its
/// test files with AppKit (NSAttributedString to .docx, the layout manager to PDF); these tests
/// write the same notes as Word XML, ODT XML and a PDF made with PdfPig's writer.
/// </summary>
public class NoteFileReaderTests
{
    /// <summary>A paragraph of the test note: its runs (words, bold, underline), size, and whether it's a bullet.</summary>
    private sealed record Para((string Text, bool Bold, bool Underline)[] Runs, double Size = 12, bool Bullet = false);

    private static readonly Para[] Notes =
    [
        new([("Unshakable", true, false)], 24),
        new([("When everything shakes", false, false)]),
        new([("God is our refuge", true, false)], Bullet: true),
        new([("God is our strength", true, false)], Bullet: true),
        new([("Psalm 46:1-2", false, false)]),
        new([("1 God is our refuge and strength, an ever-present help in trouble. 2 Therefore we will not fear, though the earth give way and the mountains fall into the heart of the sea.", false, false)]),
        new([("“Faith is not the absence of fear, but trusting God in the middle of it.” — Corrie ten Boom", false, false)]),
        new([("Next step: ", true, false), ("Trust", false, true), (" him with one fear this week.", false, false)]),
    ];

    internal static string Temporary(string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "note-files-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, name);
    }

    internal static void Zip(string path, IReadOnlyDictionary<string, byte[]> parts)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in parts)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(data);
        }
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private const string WordNs = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";

    internal static void Docx(string path, string bodyXml, string? numbering = null, IReadOnlyDictionary<string, byte[]>? media = null, string rels = "")
    {
        var parts = new Dictionary<string, byte[]>
        {
            ["[Content_Types].xml"] = Utf8("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>"),
            ["word/document.xml"] = Utf8($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document {WordNs}><w:body>{bodyXml}</w:body></w:document>"),
            ["word/_rels/document.xml.rels"] = Utf8($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{rels}</Relationships>"),
        };
        if (numbering is not null) parts["word/numbering.xml"] = Utf8($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:numbering {WordNs}>{numbering}</w:numbering>");
        foreach (var (name, data) in media ?? new Dictionary<string, byte[]>()) parts[name] = data;
        Zip(path, parts);
    }

    private static string WordRun(string text, bool bold, bool underline, double size) =>
        $"<w:r><w:rPr>{(bold ? "<w:b/>" : "")}{(underline ? "<w:u w:val=\"single\"/>" : "")}<w:sz w:val=\"{size * 2}\"/></w:rPr><w:t xml:space=\"preserve\">{SecurityElement.Escape(text)}</w:t></w:r>";

    private static string NotesDocx()
    {
        var path = Temporary("Unshakable.docx");
        var body = string.Concat(Notes.Select(p =>
            "<w:p>" + (p.Bullet ? "<w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr>" : "")
            + string.Concat(p.Runs.Select(r => WordRun(r.Text, r.Bold, r.Underline, p.Size))) + "</w:p>"));
        Docx(path, body, "<w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"bullet\"/></w:lvl></w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num>");
        return path;
    }

    /// <summary>The note laid out in a PDF as a word processor would: wrapped at the width, a bullet before each list item, a thin line under underlined words.</summary>
    private static byte[] NotesPdf(IEnumerable<Para> paragraphs, double width)
    {
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var page = builder.AddPage(612, 792);
        var y = 720.0;
        foreach (var paragraph in paragraphs)
        {
            var left = paragraph.Bullet ? 72 + 36 : 72.0;
            if (paragraph.Bullet) page.AddText("•", paragraph.Size, new PdfPoint(72 + 12, y), regular);
            // Words with their run's font, wrapped at the width.
            var words = paragraph.Runs.SelectMany(r => Pieces(r.Text).Select(w => (Word: w, r.Bold, r.Underline))).ToList();
            var x = left;
            foreach (var (word, isBold, underline) in words)
            {
                var font = isBold ? bold : regular;
                var measured = page.MeasureText(word, paragraph.Size, new PdfPoint(x, y), font);
                var advance = measured.Count == 0 ? 0 : measured[^1].EndBaseLine.X - x;
                if (word != " " && x > left && x + advance > left + width - (left - 72))
                {
                    y -= paragraph.Size * 1.2;
                    x = left;
                }
                if (x == left && word == " ") continue;
                page.AddText(word, paragraph.Size, new PdfPoint(x, y), font);
                if (underline && word != " ") page.DrawRectangle(new PdfPoint(x, y - 2), advance, 0.6, 0.6, true);
                x += advance;
            }
            y -= paragraph.Size * 1.2 + 8;
        }
        return builder.Build();
    }

    /// <summary>Words and the spaces between them, as separate pieces.</summary>
    private static IEnumerable<string> Pieces(string text)
    {
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (c == ' ')
            {
                if (current.Length > 0) yield return current.ToString();
                current.Clear();
                yield return " ";
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static List<string> Marks(NoteBody body) =>
        NoteSlideMarks.Groups(NoteSlideMarks.Suggesting(body)).Select(g => $"{g.Type ?? "body"}: {g.Text}").ToList();

    [Fact]
    public void AWordFileAndItsPdfImportToTheSameMarks()
    {
        var word = NoteFiles.Read(NotesDocx())!;
        var pdf = PdfNoteReader.Body(NotesPdf(Notes, 300), null)!;

        Assert.Equal([NoteBlockType.Heading, NoteBlockType.Paragraph, NoteBlockType.List, NoteBlockType.Paragraph, NoteBlockType.Paragraph, NoteBlockType.Paragraph, NoteBlockType.Paragraph],
            word.Blocks.Select(b => b.Type));
        Assert.Equal(["God is our refuge", "God is our strength"], word.Blocks[2].Items!.Select(i => NoteReading.Text(i.Runs)));
        Assert.Equal([new NoteRun { Text = "Next step: ", Bold = true }, new NoteRun { Text = "Trust ", Underline = true }, new NoteRun { Text = "him with one fear this week." }],
            word.Blocks[^1].Runs!);
        Assert.Equal(word, pdf);
        Assert.Equal(Marks(word), Marks(pdf));
        Assert.Contains(Marks(word), m => m.StartsWith("scripture: Psalm 46:1-2\n1 God is our refuge", StringComparison.Ordinal));
    }

    [Fact]
    public void AListWrittenAsTabbedMarkersIsAList()
    {
        NoteFileReader.Line Line(string text, bool bold) => NoteFileReader.ReadingList(new NoteFileReader.Line([new NoteRun { Text = text, Bold = bold ? true : null }], 12), null);
        var body = NoteFileReader.Body([
            Line("Intro: the earthquake", false),
            Line("\t1\tEverything that can be shaken will be shaken", true),
            Line("\t2\tWhat God builds can’t be shaken", true),
            Line("\t3\tSo we worship with reverence and awe", true),
        ]);
        Assert.Equal([NoteBlockType.Paragraph, NoteBlockType.List], body.Blocks.Select(b => b.Type));
        Assert.True(body.Blocks[1].Ordered);
        Assert.Equal(["Everything that can be shaken will be shaken", "What God builds can’t be shaken", "So we worship with reverence and awe"],
            body.Blocks[1].Items!.Select(i => NoteReading.Text(i.Runs)));
        Assert.Equal(["points: Everything that can be shaken will be shaken\nWhat God builds can’t be shaken\nSo we worship with reverence and awe"], Marks(body));
    }

    [Fact]
    public void WrappedPdfLinesJoinIntoTheirParagraph()
    {
        var verse = new Para([("1 God is our refuge and strength, an ever-present help in trouble. 2 Therefore we will not fear.", false, false)]);
        var body = PdfNoteReader.Body(NotesPdf([verse, new Para([("Selah", false, false)])], 160), null)!;
        Assert.Equal("1 God is our refuge and strength, an ever-present help in trouble. 2 Therefore we will not fear.\nSelah", body.PlainText);
    }

    [Fact]
    public void RichTextFilesKeepBoldAndUnderline()
    {
        var expected = new List<NoteRun> { new() { Text = "Big idea ", Bold = true }, new() { Text = "trust", Underline = true } };

        var docx = Temporary("notes.docx");
        Docx(docx, "<w:p>" + WordRun("Big idea ", true, false, 12) + WordRun("trust", false, true, 12) + "</w:p>");
        Assert.Equal(expected, NoteFiles.Read(docx)!.Blocks[0].Runs!);

        var odt = Temporary("notes.odt");
        Zip(odt, new Dictionary<string, byte[]>
        {
            ["mimetype"] = Utf8("application/vnd.oasis.opendocument.text"),
            ["content.xml"] = Utf8("""
                <?xml version="1.0" encoding="UTF-8"?>
                <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0"
                    xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0">
                  <office:automatic-styles>
                    <style:style style:name="T1" style:family="text"><style:text-properties fo:font-weight="bold"/></style:style>
                    <style:style style:name="T2" style:family="text"><style:text-properties style:text-underline-style="solid"/></style:style>
                  </office:automatic-styles>
                  <office:body><office:text><text:p><text:span text:style-name="T1">Big idea </text:span><text:span text:style-name="T2">trust</text:span></text:p></office:text></office:body>
                </office:document-content>
                """),
        });
        Assert.Equal(expected, NoteFiles.Read(odt)!.Blocks[0].Runs!);
    }

    [Fact]
    public void OdtHeadingsAndNumberedListsAreRead()
    {
        var odt = Temporary("message.odt");
        Zip(odt, new Dictionary<string, byte[]>
        {
            ["content.xml"] = Utf8("""
                <?xml version="1.0" encoding="UTF-8"?>
                <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0"
                    xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0">
                  <office:automatic-styles><text:list-style style:name="L1"><text:list-level-style-number text:level="1"/></text:list-style></office:automatic-styles>
                  <office:body><office:text>
                    <text:h text:outline-level="1">Unshakable</text:h>
                    <text:list text:style-name="L1"><text:list-item><text:p>Trust</text:p></text:list-item><text:list-item><text:p>Rest</text:p></text:list-item></text:list>
                  </office:text></office:body>
                </office:document-content>
                """),
        });
        var body = NoteFiles.Read(odt)!;
        Assert.Equal([NoteBlockType.Heading, NoteBlockType.List], body.Blocks.Select(b => b.Type));
        Assert.True(body.Blocks[1].Ordered);
        Assert.Equal("Unshakable\nTrust\nRest", body.PlainText);
    }

    [Fact]
    public void PlainTextLinesBecomeParagraphsAndTypedLists()
    {
        var body = NoteFileReader.Body("Unshakable\n\n- God is our refuge\n- God is\tour strength\n1. Trust\n2) Rest\n");
        Assert.Equal([NoteBlockType.Paragraph, NoteBlockType.List, NoteBlockType.List], body.Blocks.Select(b => b.Type));
        Assert.False(body.Blocks[1].Ordered);
        Assert.True(body.Blocks[2].Ordered);
        Assert.Equal("Unshakable\nGod is our refuge\nGod is our strength\nTrust\nRest", body.PlainText);
    }

    [Fact]
    public void FilesWithNoWordsOrOfAnotherKindAreNotRead()
    {
        var empty = Temporary("empty.txt");
        File.WriteAllBytes(empty, []);
        var pages = Temporary("notes.pages");
        File.WriteAllText(pages, "x");
        Assert.Null(NoteFiles.Read(empty));
        Assert.Null(NoteFiles.Read(pages));
        var broken = Temporary("broken.docx");
        File.WriteAllText(broken, "not a zip");
        Assert.Null(NoteFiles.Read(broken));
    }

    // NoteFileMediaTests.swift

    internal static NoteFileReader.SaveMedia Saver()
    {
        var folder = Temporary("media");
        Directory.CreateDirectory(folder);
        return (data, name) =>
        {
            var path = Path.Combine(folder, $"{Guid.NewGuid()}-{name}");
            File.WriteAllBytes(path, data);
            return new Uri(path).AbsoluteUri;
        };
    }

    [Fact]
    public void DroppedFilesBecomeTheirBlocks()
    {
        var save = Saver();
        var picture = Temporary("Stage.png");
        File.WriteAllBytes(picture, TestPng.Make(80, 60));
        var video = Temporary("Walkthrough.mp4");
        File.WriteAllText(video, "mp4");
        var pdf = Temporary("Bulletin.pdf");
        File.WriteAllText(pdf, "%PDF");
        Assert.Equal(NoteBlockType.Image, NoteFileReader.MediaBlock(picture, save)?.Type);
        var clip = NoteFileReader.MediaBlock(video, save)!;
        Assert.True(clip.AttachmentKind == "video" && clip.Filename == "Walkthrough.mp4");
        Assert.Null(NoteFileReader.MediaBlock(pdf, save));
        var icon = Temporary("icon.png");
        File.WriteAllBytes(icon, TestPng.Make(16, 16));
        Assert.Null(NoteFileReader.MediaBlock(icon, save)); // a file-type chip's icon is no picture
    }

    [Fact]
    public void AMarkerNotesEmptyLinesEndItsSlides()
    {
        var save = Saver();
        NoteFileReader.Line Text(string text) => new([new NoteRun { Text = text }], Gap: text.Trim().Length == 0);
        var lines = new List<NoteFileReader.Line>
        {
            Text("Slide 2: Baptism recap"), new([], Media: NoteFileReader.Media.Saving("mp4"u8.ToArray(), "Recap.mp4", save)),
            Text(""), Text(""), Text("I'll tell Sarah's story here."), Text(""), Text("Slide 3: Go and do likewise."), Text(""),
        };
        var body = NoteFileReader.Body(lines);
        Assert.Equal([NoteBlockType.Paragraph, NoteBlockType.Attachment, NoteBlockType.Paragraph, NoteBlockType.Paragraph, NoteBlockType.Paragraph, NoteBlockType.Paragraph],
            body.Blocks.Select(b => b.Type));
        Assert.True(body.Blocks[2].Runs!.Count == 0 && body.Blocks[4].Runs!.Count == 0);
        Assert.Equal(["Baptism recap", "", "Go and do likewise."], NoteSlideMarks.Groups(NoteSlideMarks.Suggesting(body)).Select(g => g.Text));
        Assert.Single(NoteFileReader.Body("1. Grace\n\n2. Truth\n").Blocks); // no markers: empty lines go, the points stay one list
    }

    [Fact]
    public void AWordFilesPicturesStayAfterTheirParagraph()
    {
        var docx = Temporary("Unshakable.docx");
        var png = TestPng.Make(80, 60);
        Docx(docx,
            "<w:p><w:r><w:t>Unshakable</w:t></w:r></w:p>"
            + "<w:p><w:r><w:drawing><a:graphic><a:graphicData><a:blip r:embed=\"rId5\"/></a:graphicData></a:graphic></w:drawing></w:r></w:p>"
            + "<w:p><w:r><w:t>God is our refuge</w:t></w:r></w:p>",
            media: new Dictionary<string, byte[]> { ["word/media/image1.png"] = png },
            rels: "<Relationship Id=\"rId5\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/image1.png\"/>");
        var body = NoteFiles.Read(docx, Saver())!;
        Assert.Contains(body.Blocks.Select(b => b.Type).ToList(), new[] { new List<NoteBlockType> { NoteBlockType.Heading, NoteBlockType.Image, NoteBlockType.Paragraph }, [NoteBlockType.Paragraph, NoteBlockType.Image, NoteBlockType.Paragraph] }, new ListComparer());
        Assert.Equal(png, File.ReadAllBytes(new Uri(body.Blocks[1].Url!).LocalPath));
        Assert.DoesNotContain(NoteFiles.Read(docx)!.Blocks, b => b.Type == NoteBlockType.Image);
    }

    private sealed class ListComparer : IEqualityComparer<List<NoteBlockType>>
    {
        public bool Equals(List<NoteBlockType>? x, List<NoteBlockType>? y) => x is not null && y is not null && x.SequenceEqual(y);

        public int GetHashCode(List<NoteBlockType> obj) => obj.Count;
    }

    [Fact]
    public void APdfsPictureStaysWhereThePageDrawsIt()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(612, 792);
        page.AddText("Above the picture", 12, new PdfPoint(72, 700), font);
        page.AddText("Below the picture", 12, new PdfPoint(72, 300), font);
        var png = TestPng.Make(80, 60);
        page.AddPng(png, new PdfRectangle(72, 400, 272, 550));
        page.AddPng(png, new PdfRectangle(72, 250, 82, 260));
        var data = builder.Build();

        var body = PdfNoteReader.Body(data, Saver())!;
        Assert.Equal([NoteBlockType.Paragraph, NoteBlockType.Image, NoteBlockType.Paragraph], body.Blocks.Select(b => b.Type));
        Assert.Equal("Below the picture", NoteReading.Text(body.Blocks[2]));
        Assert.Equal([NoteBlockType.Paragraph, NoteBlockType.Paragraph], PdfNoteReader.Body(data, null)!.Blocks.Select(b => b.Type));
    }
}

/// <summary>A plain PNG of one colour, made by hand (zlib-compressed rows and CRC-checked chunks), for tests.</summary>
internal static class TestPng
{
    public static byte[] Make(int width, int height)
    {
        var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++) raw.Write([0x30, 0xB0, 0xC0, 0xFF]);
        }
        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw.ToArray());
        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(body);
        var crc = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(body));
        png.Write(crc);
    }

    private static uint Crc(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
