using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace MxU.Slides.Core.Tests.Export;

/// <summary>
/// A tiny PDF reader, for tests only: it checks the file's structure the way a PDF reader relies
/// on it (the cross-reference table points at each object, the trailer names the catalog) and reads
/// back what was written: the pages, their pictures, and their text, through each font's
/// ToUnicode table, as a reader does when you search or copy. "Parse what you write."
/// </summary>
internal sealed partial class MiniPdf
{
    private readonly byte[] bytes;
    private readonly string latin;
    private readonly Dictionary<int, long> offsets = [];

    public MiniPdf(byte[] bytes)
    {
        this.bytes = bytes;
        // Latin-1 keeps one character per byte, so string offsets are byte offsets.
        latin = Encoding.Latin1.GetString(bytes);
        if (!latin.StartsWith("%PDF-1.7", StringComparison.Ordinal)) throw new InvalidDataException("no PDF header");
        var startxref = latin.LastIndexOf("startxref", StringComparison.Ordinal);
        var xrefAt = long.Parse(latin[(startxref + 10)..].Split('\n')[0].Trim());
        if (!latin[(int)xrefAt..].StartsWith("xref", StringComparison.Ordinal)) throw new InvalidDataException("startxref doesn't point at the xref");
        var lines = latin[(int)xrefAt..].Split('\n');
        var count = int.Parse(lines[1].Split(' ')[1]);
        for (var i = 1; i < count; i++)
        {
            var entry = lines[2 + i];
            offsets[i] = long.Parse(entry[..10]);
            if (!latin[(int)offsets[i]..].StartsWith($"{i} 0 obj", StringComparison.Ordinal)) throw new InvalidDataException($"xref entry {i} doesn't point at object {i}");
        }
        var trailer = latin[latin.LastIndexOf("trailer", StringComparison.Ordinal)..];
        Root = int.Parse(Ref().Match(trailer[trailer.IndexOf("/Root", StringComparison.Ordinal)..]).Groups[1].Value);
    }

    public int Root { get; }

    public int ObjectCount => offsets.Count;

    /// <summary>An object's dictionary text (everything before "stream" or "endobj").</summary>
    public string Dictionary(int number)
    {
        var start = (int)offsets[number];
        var end = latin.IndexOf("endobj", start, StringComparison.Ordinal);
        var body = latin[start..end];
        var stream = body.IndexOf("\nstream\n", StringComparison.Ordinal);
        return stream >= 0 ? body[..stream] : body;
    }

    /// <summary>An object's stream, inflated when it's FlateDecode.</summary>
    public byte[] Stream(int number)
    {
        var dictionary = Dictionary(number);
        var length = int.Parse(LengthPattern().Match(dictionary).Groups[1].Value);
        var start = (int)offsets[number] + dictionary.Length + "\nstream\n".Length;
        var raw = bytes.AsSpan(start, length).ToArray();
        if (!dictionary.Contains("/FlateDecode", StringComparison.Ordinal)) return raw;
        using var zlib = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    public IReadOnlyList<int> Pages()
    {
        var pagesRef = int.Parse(Ref().Match(Dictionary(Root)[Dictionary(Root).IndexOf("/Pages", StringComparison.Ordinal)..]).Groups[1].Value);
        var kids = Dictionary(pagesRef);
        kids = kids[kids.IndexOf("/Kids", StringComparison.Ordinal)..kids.IndexOf(']')];
        return [.. Ref().Matches(kids).Select(m => int.Parse(m.Groups[1].Value))];
    }

    public (double Width, double Height) PageSize(int page)
    {
        var box = MediaBox().Match(Dictionary(page));
        return (double.Parse(box.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), double.Parse(box.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    public string Content(int page)
    {
        var contents = int.Parse(Ref().Match(Dictionary(page)[Dictionary(page).IndexOf("/Contents", StringComparison.Ordinal)..]).Groups[1].Value);
        return Encoding.ASCII.GetString(Stream(contents));
    }

    /// <summary>The pictures a page draws (their resource names).</summary>
    public IReadOnlyList<string> ImagesDrawn(int page) => [.. DoPattern().Matches(Content(page)).Select(m => m.Groups[1].Value)];

    /// <summary>Every piece of text on the page, decoded through its font's ToUnicode table, in drawing order.</summary>
    public IReadOnlyList<string> Text(int page)
    {
        var fonts = FontMaps(page);
        var text = new List<string>();
        foreach (Match show in TextPattern().Matches(Content(page)))
        {
            var map = fonts[show.Groups[1].Value];
            var hex = show.Groups[2].Value;
            var line = new StringBuilder();
            for (var i = 0; i + 4 <= hex.Length; i += 4) line.Append(map.GetValueOrDefault(Convert.ToUInt16(hex.Substring(i, 4), 16), "�"));
            text.Add(line.ToString());
        }
        return text;
    }

    /// <summary>The font file embedded for a page's font, uncompressed.</summary>
    public byte[] FontFile(int page, string fontName)
    {
        var type0 = FontObject(page, fontName);
        var cid = int.Parse(Ref().Match(Dictionary(type0)[Dictionary(type0).IndexOf("/DescendantFonts", StringComparison.Ordinal)..]).Groups[1].Value);
        var descriptor = int.Parse(Ref().Match(Dictionary(cid)[Dictionary(cid).IndexOf("/FontDescriptor", StringComparison.Ordinal)..]).Groups[1].Value);
        var file = int.Parse(Ref().Match(Dictionary(descriptor)[Dictionary(descriptor).IndexOf("/FontFile2", StringComparison.Ordinal)..]).Groups[1].Value);
        return Stream(file);
    }

    private int FontObject(int page, string fontName)
    {
        var dictionary = Dictionary(page);
        var match = Regex.Match(dictionary, $@"/{fontName} (\d+) 0 R");
        return int.Parse(match.Groups[1].Value);
    }

    private Dictionary<string, Dictionary<ushort, string>> FontMaps(int page)
    {
        var maps = new Dictionary<string, Dictionary<ushort, string>>();
        foreach (Match font in FontRef().Matches(Dictionary(page)))
        {
            var type0 = int.Parse(font.Groups[2].Value);
            var unicode = int.Parse(Ref().Match(Dictionary(type0)[Dictionary(type0).IndexOf("/ToUnicode", StringComparison.Ordinal)..]).Groups[1].Value);
            var cmap = Encoding.ASCII.GetString(Stream(unicode));
            var map = new Dictionary<ushort, string>();
            foreach (Match entry in BfChar().Matches(cmap))
                map[Convert.ToUInt16(entry.Groups[1].Value, 16)] = Encoding.BigEndianUnicode.GetString(Convert.FromHexString(entry.Groups[2].Value));
            maps[font.Groups[1].Value] = map;
        }
        return maps;
    }

    [GeneratedRegex(@"(\d+) 0 R")]
    private static partial Regex Ref();

    [GeneratedRegex(@"/Length (\d+)")]
    private static partial Regex LengthPattern();

    [GeneratedRegex(@"/MediaBox \[0 0 ([\d.]+) ([\d.]+)\]")]
    private static partial Regex MediaBox();

    [GeneratedRegex(@"/(Im\d+) Do")]
    private static partial Regex DoPattern();

    [GeneratedRegex(@"BT /(F\d+) [\d.]+ Tf [\d.]+ g [\d.-]+ [\d.-]+ Td <([0-9A-F]*)> Tj ET")]
    private static partial Regex TextPattern();

    [GeneratedRegex(@"/(F\d+) (\d+) 0 R")]
    private static partial Regex FontRef();

    [GeneratedRegex(@"<([0-9A-F]{4})> <([0-9A-F]+)>")]
    private static partial Regex BfChar();
}
