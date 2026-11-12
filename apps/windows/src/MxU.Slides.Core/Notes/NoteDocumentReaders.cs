using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using MxU.Slides.Core.Model;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using Line = MxU.Slides.Core.Notes.NoteFileReader.Line;

namespace MxU.Slides.Core.Notes;

/// <summary>
/// Reads a Word file (.docx) into note lines. A .docx is a zip of XML: word/document.xml holds
/// the paragraphs (w:p) and their runs (w:r), word/styles.xml the paragraph styles (a "Heading 1"
/// style is a heading, its font size sets the heading sizes), word/numbering.xml which lists
/// are numbered, and word/_rels the links and pictures. Windows' stand-in for the Mac's
/// NSAttributedString(url:) plus DocxMedia.swift.
/// </summary>
public static class DocxNoteReader
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace Rels = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static List<Line>? Lines(Stream file, NoteFileReader.SaveMedia? save)
    {
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var document = Xml(zip, "word/document.xml");
        if (document?.Root?.Element(W + "body") is not { } body) return null;
        var styles = Styles(Xml(zip, "word/styles.xml"));
        var numbering = Numbering(Xml(zip, "word/numbering.xml"));
        var rels = Xml(zip, "word/_rels/document.xml.rels")?.Root?.Elements(Rels + "Relationship")
            .Where(r => r.Attribute("Id") is not null)
            .ToDictionary(r => (string)r.Attribute("Id")!, r => (Target: (string?)r.Attribute("Target") ?? "", External: (string?)r.Attribute("TargetMode") == "External"))
            ?? [];

        var lines = new List<Line>();
        foreach (var paragraph in Paragraphs(body))
        {
            var style = styles.GetValueOrDefault((string?)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? "Normal")
                ?? styles.GetValueOrDefault("Normal") ?? Style.Plain;
            var runs = new List<NoteRun>();
            var sizes = new Dictionary<double, int>();
            var media = new List<NoteFileReader.Media>();
            string? fieldLink = null;
            var inFieldResult = false;
            void Read(XElement container, string? href)
            {
                foreach (var node in container.Elements())
                {
                    if (node.Name == W + "hyperlink")
                    {
                        var link = (string?)node.Attribute(R + "id") is { } id && rels.TryGetValue(id, out var rel) ? rel.Target
                            : (string?)node.Attribute(W + "anchor") is { } anchor ? null : null;
                        Read(node, link ?? href);
                    }
                    else if (node.Name == W + "fldSimple")
                    {
                        Read(node, FieldLink((string?)node.Attribute(W + "instr")) ?? href);
                    }
                    else if (node.Name == W + "r")
                    {
                        if (node.Element(W + "fldChar") is { } fieldChar)
                        {
                            var kind = (string?)fieldChar.Attribute(W + "fldCharType");
                            if (kind == "begin") fieldLink = null;
                            inFieldResult = kind == "separate";
                            if (kind == "end") fieldLink = null;
                            continue;
                        }
                        if (node.Element(W + "instrText") is { } instruction)
                        {
                            fieldLink = FieldLink(instruction.Value) ?? fieldLink;
                            continue;
                        }
                        ReadRun(node, href ?? (inFieldResult ? fieldLink : null));
                    }
                    else if (node.Name == W + "ins" || node.Name == W + "smartTag" || node.Name == W + "sdt" || node.Name == W + "sdtContent")
                    {
                        Read(node, href);
                    }
                }
            }
            void ReadRun(XElement run, string? href)
            {
                var properties = run.Element(W + "rPr");
                var size = HalfPoints(properties?.Element(W + "sz")) ?? style.Size;
                var text = new StringBuilder();
                foreach (var part in run.Elements())
                {
                    if (part.Name == W + "t") text.Append(part.Value);
                    else if (part.Name == W + "tab") text.Append('\t');
                    else if (part.Name == W + "br" || part.Name == W + "cr") text.Append((char)0x2028);
                    else if (part.Name == W + "noBreakHyphen") text.Append('-');
                    else if (part.Name == W + "drawing" || part.Name == W + "pict" || part.Name == W + "object") media.AddRange(Pictures(part));
                }
                if (text.Length == 0) return;
                var noteRun = new NoteRun { Text = text.ToString(), Href = string.IsNullOrEmpty(href) ? null : href };
                if (On(properties?.Element(W + "b")) ?? style.Bold) noteRun.Bold = true;
                if (On(properties?.Element(W + "i")) ?? style.Italic) noteRun.Italic = true;
                if (properties?.Element(W + "u") is { } underline && (string?)underline.Attribute(W + "val") is not "none") noteRun.Underline = true;
                if (On(properties?.Element(W + "strike")) == true || On(properties?.Element(W + "dstrike")) == true) noteRun.Strike = true;
                runs.Add(noteRun);
                sizes[size] = sizes.GetValueOrDefault(size) + noteRun.Text.Count(c => !char.IsWhiteSpace(c));
            }
            IEnumerable<NoteFileReader.Media> Pictures(XElement holder)
            {
                var ids = holder.Descendants(A + "blip").Select(b => (string?)b.Attribute(R + "embed"))
                    .Concat(holder.Descendants(V + "imagedata").Select(i => (string?)i.Attribute(R + "id")));
                foreach (var id in ids)
                {
                    if (id is null || !rels.TryGetValue(id, out var rel) || rel.External || rel.Target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                    var path = Normalize("word/" + rel.Target);
                    if (zip.GetEntry(path) is not { } entry) continue;
                    if (NoteFileReader.Media.Saving(Bytes(entry), Path.GetFileName(path), save) is { } kept) yield return kept;
                }
            }
            Read(paragraph, null);
            bool? list = null;
            var numberRef = paragraph.Element(W + "pPr")?.Element(W + "numPr") ?? style.Numbering;
            if (numberRef is not null && (string?)numberRef.Element(W + "numId")?.Attribute(W + "val") is { } numId && numId != "0")
            {
                var level = (string?)numberRef.Element(W + "ilvl")?.Attribute(W + "val") ?? "0";
                list = numbering.TryGetValue((numId, level), out var ordered) ? ordered : false;
            }
            var line = new Line(runs, sizes.Count == 0 ? style.Size : sizes.MaxBy(p => p.Value).Key, style.Heading, list);
            if (runs.Count == 0 || runs.All(r => r.Text.Trim().Length == 0)) line = line with { Gap = true };
            lines.Add(list is null ? NoteFileReader.ReadingList(line, null) : line);
            lines.AddRange(media.Select(m => new Line([], Media: m)));
        }
        return lines;
    }

    /// <summary>The body's paragraphs in reading order, table cells included.</summary>
    private static IEnumerable<XElement> Paragraphs(XElement container)
    {
        foreach (var element in container.Elements())
        {
            if (element.Name == W + "p") yield return element;
            else if (element.Name == W + "tbl" || element.Name == W + "sdt" || element.Name == W + "sdtContent" || element.Name == W + "tr" || element.Name == W + "tc" || element.Name == W + "customXml")
            {
                foreach (var inner in Paragraphs(element)) yield return inner;
            }
        }
    }

    private static string? FieldLink(string? instruction)
    {
        if (instruction is null) return null;
        var text = instruction.Trim();
        if (!text.StartsWith("HYPERLINK", StringComparison.OrdinalIgnoreCase)) return null;
        var open = text.IndexOf('"');
        var close = open < 0 ? -1 : text.IndexOf('"', open + 1);
        return close > open ? text[(open + 1)..close] : null;
    }

    private sealed record Style(double Size, bool Bold, bool Italic, int? Heading, XElement? Numbering)
    {
        public static readonly Style Plain = new(11, false, false, null, null);
    }

    /// <summary>The paragraph styles, each with its size, weight and heading level, following its based-on chain.</summary>
    private static Dictionary<string, Style> Styles(XDocument? styles)
    {
        var result = new Dictionary<string, Style>();
        if (styles?.Root is not { } root) return result;
        var defaults = root.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr");
        var baseSize = HalfPoints(defaults?.Element(W + "sz")) ?? 10;
        var elements = root.Elements(W + "style").Where(s => (string?)s.Attribute(W + "type") == "paragraph" && s.Attribute(W + "styleId") is not null)
            .ToDictionary(s => (string)s.Attribute(W + "styleId")!);
        Style Resolve(string id, int depth)
        {
            if (result.TryGetValue(id, out var done)) return done;
            if (!elements.TryGetValue(id, out var element) || depth > 10) return Style.Plain with { Size = baseSize };
            var parent = (string?)element.Element(W + "basedOn")?.Attribute(W + "val") is { } basedOn ? Resolve(basedOn, depth + 1) : Style.Plain with { Size = baseSize };
            var properties = element.Element(W + "rPr");
            var name = ((string?)element.Element(W + "name")?.Attribute(W + "val") ?? id).ToLowerInvariant();
            int? heading = name == "title" ? 1
                : name.StartsWith("heading ", StringComparison.Ordinal) && int.TryParse(name[8..], out var level) ? Math.Min(level, 3)
                : parent.Heading;
            var style = new Style(
                HalfPoints(properties?.Element(W + "sz")) ?? parent.Size,
                On(properties?.Element(W + "b")) ?? parent.Bold,
                On(properties?.Element(W + "i")) ?? parent.Italic,
                heading,
                element.Element(W + "pPr")?.Element(W + "numPr") ?? parent.Numbering);
            result[id] = style;
            return style;
        }
        foreach (var id in elements.Keys) Resolve(id, 0);
        return result;
    }

    /// <summary>Which list levels are numbered, by (numId, level).</summary>
    private static Dictionary<(string, string), bool> Numbering(XDocument? numbering)
    {
        var result = new Dictionary<(string, string), bool>();
        if (numbering?.Root is not { } root) return result;
        var abstracts = root.Elements(W + "abstractNum").Where(a => a.Attribute(W + "abstractNumId") is not null)
            .ToDictionary(a => (string)a.Attribute(W + "abstractNumId")!);
        foreach (var num in root.Elements(W + "num"))
        {
            if ((string?)num.Attribute(W + "numId") is not { } numId
                || (string?)num.Element(W + "abstractNumId")?.Attribute(W + "val") is not { } abstractId
                || !abstracts.TryGetValue(abstractId, out var definition)) continue;
            foreach (var level in definition.Elements(W + "lvl"))
            {
                var format = (string?)level.Element(W + "numFmt")?.Attribute(W + "val") ?? "decimal";
                result[(numId, (string?)level.Attribute(W + "ilvl") ?? "0")] = format is not ("bullet" or "none");
            }
        }
        return result;
    }

    /// <summary>A toggle property: present means on, unless its value says off.</summary>
    private static bool? On(XElement? toggle) =>
        toggle is null ? null : (string?)toggle.Attribute(W + "val") is not ("0" or "false" or "off");

    private static double? HalfPoints(XElement? size) =>
        double.TryParse((string?)size?.Attribute(W + "val"), NumberStyles.Float, CultureInfo.InvariantCulture, out var half) ? half / 2 : null;

    internal static XDocument? Xml(ZipArchive zip, string path)
    {
        if (zip.GetEntry(path) is not { } entry) return null;
        using var stream = entry.Open();
        try
        {
            return XDocument.Load(stream);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    internal static byte[] Bytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>"word/../media/x.png" as "media/x.png": a zip path with its dots resolved, never outside the zip.</summary>
    internal static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return string.Join("/", parts);
    }
}

/// <summary>
/// Reads an OpenDocument text file (.odt, from LibreOffice or Google Docs) into note lines: a zip
/// whose content.xml holds headings (text:h), paragraphs (text:p), lists and spans, and whose
/// styles (in content.xml and styles.xml) say which are bold, italic, underlined or numbered.
/// Windows' stand-in for the Mac's NSAttributedString(url:).
/// </summary>
public static class OdtNoteReader
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace StyleNs = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";

    private sealed record Marks(bool? Bold, bool? Italic, bool? Underline, bool? Strike, double? Size, string? Parent);

    public static List<Line>? Lines(Stream file, NoteFileReader.SaveMedia? save)
    {
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var content = DocxNoteReader.Xml(zip, "content.xml");
        if (content?.Root?.Element(Office + "body")?.Element(Office + "text") is not { } body) return null;
        var styleSheets = new[] { DocxNoteReader.Xml(zip, "styles.xml"), content }.OfType<XDocument>().ToList();
        var styles = new Dictionary<string, Marks>();
        var numberedLists = new HashSet<string>();
        foreach (var sheet in styleSheets)
        {
            foreach (var style in sheet.Descendants(StyleNs + "style"))
            {
                if ((string?)style.Attribute(StyleNs + "name") is not { } name) continue;
                var p = style.Element(StyleNs + "text-properties");
                bool? Line(string attribute) => (string?)p?.Attribute(StyleNs + attribute) is { } value ? value != "none" : null;
                styles[name] = new Marks(
                    (string?)p?.Attribute(Fo + "font-weight") is { } weight ? weight is "bold" or "600" or "700" or "800" or "900" : null,
                    (string?)p?.Attribute(Fo + "font-style") is { } italic ? italic is "italic" or "oblique" : null,
                    Line("text-underline-style"), Line("text-line-through-style"),
                    Points((string?)p?.Attribute(Fo + "font-size")),
                    (string?)style.Attribute(StyleNs + "parent-style-name"));
            }
            foreach (var list in sheet.Descendants(Text + "list-style"))
            {
                if ((string?)list.Attribute(StyleNs + "name") is { } name && list.Elements(Text + "list-level-style-number").Any()) numberedLists.Add(name);
            }
        }
        Marks Resolved(string? name, int depth = 0)
        {
            if (name is null || depth > 10 || !styles.TryGetValue(name, out var marks)) return new Marks(null, null, null, null, null, null);
            var parent = Resolved(marks.Parent, depth + 1);
            return new Marks(marks.Bold ?? parent.Bold, marks.Italic ?? parent.Italic, marks.Underline ?? parent.Underline,
                marks.Strike ?? parent.Strike, marks.Size ?? parent.Size, null);
        }

        var lines = new List<Line>();
        void Paragraph(XElement paragraph, int? heading, bool? list)
        {
            var paragraphMarks = Resolved((string?)paragraph.Attribute(Text + "style-name"));
            var runs = new List<NoteRun>();
            var sizes = new Dictionary<double, int>();
            var media = new List<NoteFileReader.Media>();
            void Add(string text, Marks marks, string? href)
            {
                if (text.Length == 0) return;
                var run = new NoteRun { Text = text, Href = href };
                if (marks.Bold ?? paragraphMarks.Bold ?? false) run.Bold = true;
                if (marks.Italic ?? paragraphMarks.Italic ?? false) run.Italic = true;
                if (marks.Underline ?? paragraphMarks.Underline ?? false) run.Underline = true;
                if (marks.Strike ?? paragraphMarks.Strike ?? false) run.Strike = true;
                runs.Add(run);
                var size = marks.Size ?? paragraphMarks.Size ?? 12;
                sizes[size] = sizes.GetValueOrDefault(size) + text.Count(c => !char.IsWhiteSpace(c));
            }
            void Read(XElement container, Marks marks, string? href)
            {
                foreach (var node in container.Nodes())
                {
                    if (node is XText text)
                    {
                        Add(text.Value, marks, href);
                        continue;
                    }
                    if (node is not XElement element) continue;
                    if (element.Name == Text + "span") Read(element, Merge(Resolved((string?)element.Attribute(Text + "style-name")), marks), href);
                    else if (element.Name == Text + "a") Read(element, marks, (string?)element.Attribute(XLink + "href") ?? href);
                    else if (element.Name == Text + "s") Add(new string(' ', int.TryParse((string?)element.Attribute(Text + "c"), out var n) ? n : 1), marks, href);
                    else if (element.Name == Text + "tab") Add("\t", marks, href);
                    else if (element.Name == Text + "line-break") Add(((char)0x2028).ToString(), marks, href);
                    else if (element.Name == Draw + "frame")
                    {
                        foreach (var image in element.Descendants(Draw + "image"))
                        {
                            var path = DocxNoteReader.Normalize((string?)image.Attribute(XLink + "href") ?? "");
                            if (zip.GetEntry(path) is { } entry && NoteFileReader.Media.Saving(DocxNoteReader.Bytes(entry), Path.GetFileName(path), save) is { } kept)
                                media.Add(kept);
                        }
                    }
                }
            }
            Read(paragraph, new Marks(null, null, null, null, null, null), null);
            var line = new Line(runs, sizes.Count == 0 ? paragraphMarks.Size ?? 12 : sizes.MaxBy(p => p.Value).Key, heading, list);
            if (runs.All(r => r.Text.Trim().Length == 0)) line = line with { Gap = true, List = null };
            lines.Add(list is null ? NoteFileReader.ReadingList(line, null) : line);
            lines.AddRange(media.Select(m => new Line([], Media: m)));
        }
        void Walk(XElement container, bool? list)
        {
            foreach (var element in container.Elements())
            {
                if (element.Name == Text + "p") Paragraph(element, null, list);
                else if (element.Name == Text + "h")
                    Paragraph(element, Math.Min(3, int.TryParse((string?)element.Attribute(Text + "outline-level"), out var level) ? level : 1), null);
                else if (element.Name == Text + "list")
                {
                    var numbered = (string?)element.Attribute(Text + "style-name") is { } name ? numberedLists.Contains(name) : list ?? false;
                    foreach (var item in element.Elements().Where(e => e.Name == Text + "list-item" || e.Name == Text + "list-header")) Walk(item, numbered);
                }
                else if (element.Name == Text + "section" || element.Name == Table + "table" || element.Name == Table + "table-row"
                         || element.Name == Table + "table-cell" || element.Name == Table + "table-rows")
                    Walk(element, list);
            }
        }
        Walk(body, null);
        return lines;
    }

    private static Marks Merge(Marks inner, Marks outer) =>
        new(inner.Bold ?? outer.Bold, inner.Italic ?? outer.Italic, inner.Underline ?? outer.Underline, inner.Strike ?? outer.Strike, inner.Size ?? outer.Size, null);

    private static double? Points(string? size)
    {
        if (size is null || !size.EndsWith("pt", StringComparison.Ordinal)) return null;
        return double.TryParse(size[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var points) ? points : null;
    }
}

/// <summary>
/// Reads a PDF's printed glyphs, its thin lines (underlines) and its pictures with PdfPig, for
/// <see cref="NoteFileReader.Body(IReadOnlyList{NoteFileReader.PdfPageText}, IReadOnlyList{NoteFileReader.PdfPicture})"/>
/// to gather into the note. The Mac scans the page's content stream itself (PDFGlyphReader.swift);
/// PdfPig does that scan here. The Mac draws each picture's part of the page; Windows keeps the
/// picture's own image (as PNG, or the JPEG it was stored as).
/// </summary>
public static class PdfNoteReader
{
    public static NoteBody? Body(byte[] data, NoteFileReader.SaveMedia? save)
    {
        try
        {
            using var document = PdfDocument.Open(data);
            var pages = new List<NoteFileReader.PdfPageText>();
            var pictures = new List<NoteFileReader.PdfPicture>();
            var index = 0;
            foreach (var page in document.GetPages())
            {
                pages.Add(Read(page));
                if (save is not null) pictures.AddRange(Pictures(page, index, save));
                index++;
            }
            return NoteFileReader.Body(pages, pictures);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A damaged or encrypted PDF is no note.
            return null;
        }
    }

    private static readonly string[] BoldWords = ["bold", "black", "heavy", "semibold", "demi"];

    internal static NoteFileReader.PdfPageText Read(Page page)
    {
        var glyphs = page.Letters.Select(letter =>
        {
            var font = letter.FontName ?? "";
            var plus = font.IndexOf('+');
            if (plus >= 0) font = font[(plus + 1)..];
            var bold = letter.FontDetails?.IsBold == true || BoldWords.Any(w => font.Contains(w, StringComparison.OrdinalIgnoreCase));
            return new NoteFileReader.PdfGlyph(letter.Value, letter.StartBaseLine.X, letter.StartBaseLine.Y, letter.Width,
                letter.PointSize, bold, false, font);
        }).ToList();
        var rules = new List<NoteFileReader.PdfRect>();
        foreach (var path in page.Paths)
        {
            if (path.GetBoundingRectangle() is not { } box) continue;
            var bounds = new NoteFileReader.PdfRect(box.Left, box.Bottom, box.Width, box.Height);
            if (path.IsFilled) AddRule(bounds);
            else if (path.IsStroked && bounds.Height < 0.5)
            {
                var half = Math.Max(0.5, (double)path.LineWidth) / 2;
                AddRule(bounds with { Y = bounds.Y - half, Height = bounds.Height + half * 2 });
            }
        }
        void AddRule(NoteFileReader.PdfRect bounds)
        {
            if (bounds.Height > 0 && bounds.Height <= 3 && bounds.Width > bounds.Height * 4) rules.Add(bounds);
        }
        var images = page.GetImages().Select(i => new NoteFileReader.PdfRect(i.BoundingBox.Left, i.BoundingBox.Bottom, i.BoundingBox.Width, i.BoundingBox.Height)).ToList();
        return new NoteFileReader.PdfPageText(glyphs, rules, images);
    }

    private static IEnumerable<NoteFileReader.PdfPicture> Pictures(Page page, int index, NoteFileReader.SaveMedia save)
    {
        var number = 0;
        foreach (var image in page.GetImages())
        {
            if (image.BoundingBox.Width < 72 || image.BoundingBox.Height < 72) continue;
            number++;
            string name;
            byte[] bytes;
            if (image.TryGetPng(out var png))
            {
                bytes = png;
                name = $"pdf-page{index + 1}-{number}.png";
            }
            else if (image.RawMemory.Span is [0xFF, 0xD8, ..] raw)
            {
                bytes = raw.ToArray();
                name = $"pdf-page{index + 1}-{number}.jpg";
            }
            else
            {
                continue;
            }
            if (NoteFileReader.Media.Saving(bytes, name, save) is { } media)
                yield return new NoteFileReader.PdfPicture(index, image.BoundingBox.Top, media);
        }
    }
}

/// <summary>
/// The note file reader for every format Core can read on its own (text, Word, ODT, PDF). RTF and
/// PowerPoint are read by the importers that know them (ProImport's RtfNoteLines, PptxImport's
/// PptxNoteText); the app joins them (Infrastructure/NoteFiles.cs).
/// </summary>
public static class NoteFiles
{
    public static NoteBody? Read(string path, NoteFileReader.SaveMedia? save = null)
    {
        NoteBody? body;
        try
        {
            var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            body = extension switch
            {
                "pdf" => PdfNoteReader.Body(File.ReadAllBytes(path), save),
                "txt" or "text" or "md" => NoteFileReader.Body(File.ReadAllText(path, Encoding.UTF8)),
                "docx" => Lines(path, s => DocxNoteReader.Lines(s, save)),
                "odt" => Lines(path, s => OdtNoteReader.Lines(s, save)),
                _ => null,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
        {
            return null;
        }
        return body is { Blocks.Count: > 0 } ? body : null;
    }

    private static NoteBody? Lines(string path, Func<Stream, List<Line>?> read)
    {
        using var stream = File.OpenRead(path);
        return read(stream) is { } lines ? NoteFileReader.Body(lines) : null;
    }
}
