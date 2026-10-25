using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace MxU.Slides.Core.Export;

/// <summary>
/// A small PDF writer: pages with pictures (JPEG), text in an embedded TrueType font, lines and
/// filled shapes. That's all an exported presentation needs (a sheet of slides, a page per slide, a
/// lyric sheet, a chord chart), so the app needs no PDF library. The Mac draws its PDFs with Core
/// Graphics (SlideImageExport.swift, drawPDF); Windows has no PDF drawing of its own, so this
/// writes the file format directly (ISO 32000, PDF 1.7).
/// <para>
/// <b>How a PDF is put together.</b> It's a list of numbered objects (a page, a picture, a font,
/// the drawing instructions for a page) followed by a table of where each one starts in the file
/// (the "xref"), so a reader can jump straight to any of them. Each page's drawing is a little
/// program: "<c>q 300 0 0 169 36 600 cm /Im1 Do Q</c>" draws picture 1 at (36, 600), 300 × 169
/// points; "<c>BT /F1 12 Tf 36 700 Td &lt;0024&gt; Tj ET</c>" writes glyph 0x24 in font 1 at 12 points.
/// Coordinates are points (1/72 inch) from the bottom-left corner of the page.
/// </para>
/// <para>
/// <b>Text</b> is written as glyph numbers ("Identity-H"), so any character the font has can be
/// used, not just the 200 or so of the old 8-bit encodings: Yoruba's ẹ and ṣ, accents, curly
/// quotes. A "ToUnicode" table goes with the font, so the words can be searched and copied out of
/// the PDF. The font is embedded with only the glyphs used (<see cref="TrueTypeFont.Subset"/>).
/// </para>
/// </summary>
public sealed class PdfDocument
{
    private readonly List<PdfPage> pages = [];
    private readonly List<PdfImage> images = [];
    private readonly List<PdfFont> fonts = [];

    /// <summary>The document's title (shown in a PDF reader's window).</summary>
    public string? Title { get; set; }

    public string Producer { get; set; } = "MxU Slides for Windows";

    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;

    public IReadOnlyList<PdfPage> Pages => pages;

    public PdfPage AddPage(double width, double height)
    {
        var page = new PdfPage(width, height);
        pages.Add(page);
        return page;
    }

    /// <summary>A JPEG picture to draw on pages (it's stored as it is: PDF readers decode JPEG themselves).</summary>
    public PdfImage AddJpeg(byte[] jpeg)
    {
        var (width, height, components) = JpegSize(jpeg) ?? throw new InvalidDataException("That isn't a JPEG picture");
        var image = new PdfImage($"Im{images.Count + 1}", jpeg, width, height, components);
        images.Add(image);
        return image;
    }

    /// <summary>
    /// A font to write text with, and fonts to borrow from for characters it hasn't got (each
    /// becomes a font of its own in the file, used only if some text needs it). Refused
    /// (<see cref="InvalidOperationException"/>) when a licence forbids embedding.
    /// </summary>
    public PdfFont AddFont(TrueTypeFont font, params TrueTypeFont[] fallbacks)
    {
        if (!font.CanEmbed) throw new InvalidOperationException($"{font.PostScriptName}'s licence doesn't allow it to be embedded");
        var added = new PdfFont($"F{fonts.Count + 1}", font, SubsetTag(fonts.Count));
        fonts.Add(added);
        foreach (var fallback in fallbacks.Where(f => f.CanEmbed))
        {
            var extra = new PdfFont($"F{fonts.Count + 1}", fallback, SubsetTag(fonts.Count));
            fonts.Add(extra);
            added.Fallbacks.Add(extra);
        }
        return added;
    }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        Save(stream);
        return stream.ToArray();
    }

    public void Save(Stream output)
    {
        var writer = new ObjectWriter(output);
        writer.Header();
        // Object numbers: 1 catalog, 2 page tree, 3 info, then fonts, pictures, pages.
        const int catalog = 1, pageTree = 2, info = 3;
        var next = 4;
        // Only the fonts some page draws with (a fallback no text needed isn't written at all).
        var fontObjects = fonts.Where(f => pages.Any(p => p.Fonts.Contains(f))).ToDictionary(f => f, _ => { var first = next; next += 5; return first; });
        var imageObjects = images.ToDictionary(i => i, _ => next++);
        var pageObjects = pages.Select(_ => { var first = next; next += 2; return first; }).ToList();

        writer.Object(catalog, $"<< /Type /Catalog /Pages {pageTree} 0 R >>");
        writer.Object(pageTree, $"<< /Type /Pages /Kids [{string.Join(' ', pageObjects.Select(p => $"{p} 0 R"))}] /Count {pages.Count} >>");
        var infoEntries = new StringBuilder($"<< /Producer {PdfText(Producer)} /CreationDate {PdfText(PdfDate(Created))}");
        if (!string.IsNullOrEmpty(Title)) infoEntries.Append($" /Title {PdfText(Title)}");
        writer.Object(info, infoEntries.Append(" >>").ToString());

        foreach (var (font, first) in fontObjects) WriteFont(writer, font, first);
        foreach (var (image, number) in imageObjects)
        {
            var space = image.Components switch { 1 => "/DeviceGray", 4 => "/DeviceCMYK", _ => "/DeviceRGB" };
            // Adobe's CMYK JPEGs are stored inverted; the Decode array turns them back.
            var decode = image.Components == 4 ? " /Decode [1 0 1 0 1 0 1 0]" : "";
            writer.Stream(number, $"/Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace {space} /BitsPerComponent 8 /Filter /DCTDecode{decode}", image.Data);
        }
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var number = pageObjects[i];
            var resources = new StringBuilder("<<");
            var usedFonts = fonts.Where(page.Fonts.Contains).ToList();
            if (usedFonts.Count > 0) resources.Append(" /Font << ").Append(string.Join(' ', usedFonts.Select(f => $"/{f.Name} {fontObjects[f]} 0 R"))).Append(" >>");
            var usedImages = images.Where(page.Images.Contains).ToList();
            if (usedImages.Count > 0) resources.Append(" /XObject << ").Append(string.Join(' ', usedImages.Select(m => $"/{m.Name} {imageObjects[m]} 0 R"))).Append(" >>");
            resources.Append(" >>");
            writer.Object(number, $"<< /Type /Page /Parent {pageTree} 0 R /MediaBox [0 0 {Num(page.Width)} {Num(page.Height)}] /Resources {resources} /Contents {number + 1} 0 R >>");
            writer.Stream(number + 1, "/Filter /FlateDecode", Deflate(Encoding.ASCII.GetBytes(page.Content)));
        }
        writer.Finish(catalog, info, next);
    }

    // MARK: Fonts

    /// <summary>
    /// A font is five objects: the font itself (Type0, which can address 65 536 glyphs), the
    /// descendant CID font (the glyph widths), its descriptor (size and shape), the font file, and
    /// the ToUnicode table that maps glyphs back to the text for searching and copying.
    /// </summary>
    private static void WriteFont(ObjectWriter writer, PdfFont font, int first)
    {
        var (type0, cidFont, descriptor, file, toUnicode) = (first, first + 1, first + 2, first + 3, first + 4);
        var ttf = font.Font;
        var name = $"{font.Tag}+{ttf.PostScriptName}";
        double Scale(int units) => units * 1000.0 / ttf.UnitsPerEm;
        var glyphs = font.Used.Keys.Order().ToList();

        writer.Object(type0, $"<< /Type /Font /Subtype /Type0 /BaseFont /{name} /Encoding /Identity-H /DescendantFonts [{cidFont} 0 R] /ToUnicode {toUnicode} 0 R >>");
        var widths = new StringBuilder();
        foreach (var glyph in glyphs) widths.Append($"{glyph} [{Num(Scale(ttf.Advance(glyph)))}] ");
        writer.Object(cidFont, $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{name} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
            + $"/FontDescriptor {descriptor} 0 R /DW 1000 /W [{widths}] /CIDToGIDMap /Identity >>");
        // Flags: 4 = symbolic (the glyphs aren't the standard Latin set), plus 1 for a fixed-pitch font and 64 for italic.
        var flags = 4 | (ttf.IsFixedPitch ? 1 : 0) | (ttf.ItalicAngle != 0 ? 64 : 0);
        var (xMin, yMin, xMax, yMax) = ttf.Bounds;
        writer.Object(descriptor, $"<< /Type /FontDescriptor /FontName /{name} /Flags {flags} "
            + $"/FontBBox [{Num(Scale(xMin))} {Num(Scale(yMin))} {Num(Scale(xMax))} {Num(Scale(yMax))}] /ItalicAngle {Num(ttf.ItalicAngle)} "
            + $"/Ascent {Num(Scale(ttf.Ascent))} /Descent {Num(Scale(ttf.Descent))} /CapHeight {Num(Scale(ttf.CapHeight))} "
            + $"/StemV {(ttf.Weight >= 600 ? 120 : 80)} /FontFile2 {file} 0 R >>");
        var subset = ttf.Subset(glyphs);
        writer.Stream(file, $"/Length1 {subset.Length} /Filter /FlateDecode", Deflate(subset));
        writer.Stream(toUnicode, "/Filter /FlateDecode", Deflate(Encoding.ASCII.GetBytes(ToUnicodeMap(font.Used))));
    }

    /// <summary>The CMap that says which characters each glyph stands for (in blocks of 100, the most a block may hold).</summary>
    internal static string ToUnicodeMap(IReadOnlyDictionary<ushort, string> used)
    {
        var text = new StringBuilder();
        text.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        text.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        text.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var entries = used.OrderBy(pair => pair.Key).ToList();
        for (var start = 0; start < entries.Count; start += 100)
        {
            var block = entries.Skip(start).Take(100).ToList();
            text.Append($"{block.Count} beginbfchar\n");
            foreach (var (glyph, chars) in block)
                text.Append($"<{glyph:X4}> <{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(chars))}>\n");
            text.Append("endbfchar\n");
        }
        text.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return text.ToString();
    }

    /// <summary>A subset font's name starts with six capital letters and "+", different for each font in the file.</summary>
    private static string SubsetTag(int index)
    {
        var letters = new char[6];
        var n = index + 1;
        for (var i = 5; i >= 0; i--)
        {
            letters[i] = (char)('A' + n % 26);
            n /= 26;
        }
        return "MXU" + new string(letters, 3, 3);
    }

    // MARK: Pieces

    internal static byte[] Deflate(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(bytes);
        return output.ToArray();
    }

    /// <summary>A number as PDF writes it: invariant digits, at most three decimals, no exponent.</summary>
    internal static string Num(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "0";
        var rounded = Math.Round(value, 3);
        return (rounded == 0 ? 0 : rounded).ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>A text string for the document's information: UTF-16 with its byte-order mark, in hex, which suits any language.</summary>
    internal static string PdfText(string text) => "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";

    private static string PdfDate(DateTimeOffset when)
    {
        var offset = when.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var zone = offset == TimeSpan.Zero ? "Z" : $"{sign}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'";
        return "D:" + when.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + zone;
    }

    /// <summary>A JPEG's size and number of colour channels, from its frame header (SOF), or null when it isn't a JPEG.</summary>
    internal static (int Width, int Height, int Components)? JpegSize(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
        var at = 2;
        while (at + 9 < jpeg.Length)
        {
            if (jpeg[at] != 0xFF)
            {
                at++;
                continue;
            }
            var marker = jpeg[at + 1];
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                at += 2;
                continue;
            }
            var length = (jpeg[at + 2] << 8) | jpeg[at + 3];
            // The start-of-frame markers (baseline, progressive…), but not DHT (C4), JPG (C8) or DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                return ((jpeg[at + 7] << 8) | jpeg[at + 8], (jpeg[at + 5] << 8) | jpeg[at + 6], jpeg[at + 9]);
            at += 2 + length;
        }
        return null;
    }

    /// <summary>Writes the objects one by one, noting where each starts, then the table of those places.</summary>
    private sealed class ObjectWriter(Stream output)
    {
        private readonly Dictionary<int, long> offsets = [];
        private long position;

        public void Header()
        {
            // The second line's four high bytes tell file tools this is binary, not text.
            Raw(Encoding.ASCII.GetBytes("%PDF-1.7\n"));
            Raw([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
        }

        public void Object(int number, string body)
        {
            offsets[number] = position;
            Raw(Encoding.ASCII.GetBytes($"{number} 0 obj\n{body}\nendobj\n"));
        }

        public void Stream(int number, string dictionary, byte[] data)
        {
            offsets[number] = position;
            Raw(Encoding.ASCII.GetBytes($"{number} 0 obj\n<< {dictionary} /Length {data.Length} >>\nstream\n"));
            Raw(data);
            Raw(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
        }

        public void Finish(int root, int info, int count)
        {
            var xref = position;
            var table = new StringBuilder($"xref\n0 {count}\n0000000000 65535 f \n");
            for (var i = 1; i < count; i++) table.Append($"{offsets.GetValueOrDefault(i):0000000000} 00000 n \n");
            table.Append($"trailer\n<< /Size {count} /Root {root} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            Raw(Encoding.ASCII.GetBytes(table.ToString()));
        }

        private void Raw(byte[] bytes)
        {
            output.Write(bytes);
            position += bytes.Length;
        }
    }
}

/// <summary>A picture added to a PDF, drawn on a page with <see cref="PdfPage.DrawImage"/>.</summary>
public sealed record PdfImage(string Name, byte[] Data, int Width, int Height, int Components);

/// <summary>
/// A font added to a PDF. It remembers which glyphs the pages used, and the characters each
/// stands for, to embed just those and to write the ToUnicode table.
/// </summary>
public sealed class PdfFont
{
    private readonly Dictionary<ushort, string> used = [];

    internal PdfFont(string name, TrueTypeFont font, string tag)
    {
        Name = name;
        Font = font;
        Tag = tag;
    }

    public string Name { get; }
    public TrueTypeFont Font { get; }
    internal string Tag { get; }
    internal IReadOnlyDictionary<ushort, string> Used => used;

    /// <summary>Fonts that draw the characters this one hasn't got (see <see cref="PdfDocument.AddFont"/>).</summary>
    public List<PdfFont> Fallbacks { get; } = [];

    /// <summary>How wide <paramref name="text"/> is at <paramref name="size"/> points, borrowed characters included.</summary>
    public double Measure(string text, double size) => Pieces(text).Sum(piece => piece.Font.Font.Measure(piece.Text, size));

    /// <summary>
    /// The text cut into runs, each in the first font that has its characters: this one, or a
    /// fallback. A character none of them has stays in this font (and shows its "missing" box).
    /// </summary>
    internal IReadOnlyList<(PdfFont Font, string Text)> Pieces(string text)
    {
        var pieces = new List<(PdfFont, string)>();
        PdfFont? current = null;
        var run = new StringBuilder();
        foreach (var rune in Prepare(text).EnumerateRunes())
        {
            var font = Font.Has(rune.Value) || Rune.IsWhiteSpace(rune) ? this : Fallbacks.FirstOrDefault(f => f.Font.Has(rune.Value)) ?? this;
            if (current is not null && font != current)
            {
                pieces.Add((current, run.ToString()));
                run.Clear();
            }
            current = font;
            run.Append(rune.ToString());
        }
        if (current is not null && run.Length > 0) pieces.Add((current, run.ToString()));
        return pieces;
    }

    /// <summary>The height from the baseline to the top of the tallest letters, at <paramref name="size"/>.</summary>
    public double Ascent(double size) => Font.Ascent * size / Font.UnitsPerEm;

    /// <summary>
    /// Text as it's drawn: composed (NFC), so "e" plus a combining dot becomes the one letter "ẹ"
    /// where the font has it (there's no text shaping here to place a separate mark), and tabs as spaces.
    /// </summary>
    internal static string Prepare(string text) => (text ?? "").Replace('\t', ' ').Normalize(NormalizationForm.FormC);

    /// <summary>The glyph numbers for <paramref name="text"/>, in hex as a PDF string ("&lt;00240025&gt;"), noting each as used.</summary>
    internal string Encode(string text)
    {
        var hex = new StringBuilder("<");
        foreach (var rune in Prepare(text).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control) continue;
            var glyph = Font.GlyphFor(rune.Value);
            // The "missing" glyph stands for no character: leaving it out of ToUnicode keeps searches honest.
            if (glyph != 0) used.TryAdd(glyph, rune.ToString());
            hex.Append(glyph.ToString("X4", CultureInfo.InvariantCulture));
        }
        return hex.Append('>').ToString();
    }
}

/// <summary>One page of a <see cref="PdfDocument"/>: draw on it, in points from its bottom-left corner.</summary>
public sealed class PdfPage
{
    private readonly StringBuilder content = new();

    internal PdfPage(double width, double height)
    {
        Width = width;
        Height = height;
    }

    public double Width { get; }
    public double Height { get; }
    internal HashSet<PdfFont> Fonts { get; } = [];
    internal HashSet<PdfImage> Images { get; } = [];
    internal string Content => content.ToString();

    private static string N(double value) => PdfDocument.Num(value);

    /// <summary>Draws a picture filling <paramref name="rect"/>, clipped to rounded corners when <paramref name="cornerRadius"/> is above 0.</summary>
    public void DrawImage(PdfImage image, PdfRect rect, double cornerRadius = 0)
    {
        Images.Add(image);
        content.Append("q\n");
        if (cornerRadius > 0) content.Append(RoundedRect(rect, cornerRadius)).Append("W n\n");
        content.Append($"{N(rect.Width)} 0 0 {N(rect.Height)} {N(rect.X)} {N(rect.Y)} cm /{image.Name} Do\nQ\n");
    }

    /// <summary>A rectangle's outline, <paramref name="gray"/> from 0 (black) to 1 (white).</summary>
    public void StrokeRect(PdfRect rect, double cornerRadius, double gray, double lineWidth = 0.5)
    {
        content.Append($"q {N(lineWidth)} w {N(gray)} G\n").Append(RoundedRect(rect, cornerRadius)).Append("S Q\n");
    }

    /// <summary>A filled rectangle in an RGB colour (0–1 each).</summary>
    public void FillRect(PdfRect rect, double red, double green, double blue, double cornerRadius = 0)
    {
        content.Append($"q {N(red)} {N(green)} {N(blue)} rg\n").Append(RoundedRect(rect, cornerRadius)).Append("f Q\n");
    }

    /// <summary>A straight line.</summary>
    public void Line(double x1, double y1, double x2, double y2, double gray, double lineWidth = 0.5) =>
        content.Append($"q {N(lineWidth)} w {N(gray)} G {N(x1)} {N(y1)} m {N(x2)} {N(y2)} l S Q\n");

    /// <summary>
    /// Writes <paramref name="text"/> with its baseline starting at (<paramref name="x"/>, <paramref name="y"/>).
    /// <paramref name="gray"/> is its colour from 0 (black) to 1 (white). Returns the text's width.
    /// </summary>
    public double Text(PdfFont font, double size, double x, double y, string text, double gray = 0)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var at = x;
        foreach (var (piece, run) in font.Pieces(text))
        {
            Fonts.Add(piece);
            content.Append($"BT /{piece.Name} {N(size)} Tf {N(gray)} g {N(at)} {N(y)} Td {piece.Encode(run)} Tj ET\n");
            at += piece.Font.Measure(run, size);
        }
        return at - x;
    }

    /// <summary>
    /// Writes <paramref name="text"/>, cut with "…" so it fits <paramref name="width"/> (the Mac's
    /// captions are truncated at the end the same way).
    /// </summary>
    public void TextFitting(PdfFont font, double size, double x, double y, string text, double width, double gray = 0) =>
        Text(font, size, x, y, Fit(font, size, text, width), gray);

    public static string Fit(PdfFont font, double size, string text, double width)
    {
        if (font.Measure(text, size) <= width) return text;
        var runes = text.EnumerateRunes().ToList();
        for (var count = runes.Count - 1; count > 0; count--)
        {
            var candidate = string.Concat(runes.Take(count).Select(r => r.ToString())).TrimEnd() + "…";
            if (font.Measure(candidate, size) <= width) return candidate;
        }
        return "…";
    }

    /// <summary>A rectangle's path, its corners rounded with quarter-circle curves (the usual 0.5523 handle length).</summary>
    private static string RoundedRect(PdfRect r, double radius)
    {
        radius = Math.Clamp(radius, 0, Math.Min(r.Width, r.Height) / 2);
        if (radius <= 0) return $"{N(r.X)} {N(r.Y)} {N(r.Width)} {N(r.Height)} re\n";
        var k = radius * 0.5523;
        var (x0, y0, x1, y1) = (r.X, r.Y, r.MaxX, r.MaxY);
        return $"{N(x0 + radius)} {N(y0)} m {N(x1 - radius)} {N(y0)} l "
            + $"{N(x1 - radius + k)} {N(y0)} {N(x1)} {N(y0 + radius - k)} {N(x1)} {N(y0 + radius)} c "
            + $"{N(x1)} {N(y1 - radius)} l {N(x1)} {N(y1 - radius + k)} {N(x1 - radius + k)} {N(y1)} {N(x1 - radius)} {N(y1)} c "
            + $"{N(x0 + radius)} {N(y1)} l {N(x0 + radius - k)} {N(y1)} {N(x0)} {N(y1 - radius + k)} {N(x0)} {N(y1 - radius)} c "
            + $"{N(x0)} {N(y0 + radius)} l {N(x0)} {N(y0 + radius - k)} {N(x0 + radius - k)} {N(y0)} {N(x0 + radius)} {N(y0)} c h\n";
    }
}
