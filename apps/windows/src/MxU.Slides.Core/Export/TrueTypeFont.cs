using System.Buffers.Binary;
using System.Text;

namespace MxU.Slides.Core.Export;

/// <summary>
/// Just enough of a TrueType font file to put text in a PDF: which glyph draws each character
/// (the <c>cmap</c> table), how wide each glyph is (<c>hmtx</c>), the font's size and shape
/// (<c>head</c>, <c>hhea</c>, <c>OS/2</c>, <c>post</c>), its PostScript name (<c>name</c>), and
/// whether its licence lets it be embedded (<c>OS/2</c> fsType). <see cref="Subset"/> makes a
/// smaller copy that keeps only the glyphs a document uses, so a one-page lyric sheet carries a
/// few kilobytes of font rather than a megabyte.
/// <para>
/// Only fonts with TrueType outlines (a <c>glyf</c> table) are taken: that's what a PDF's
/// "FontFile2" holds. OpenType fonts with CFF outlines, and font collections (.ttc), are refused
/// (<see cref="TryLoad"/> returns null) and the caller picks another font. Windows only: the Mac
/// draws its PDFs with Core Graphics, which embeds fonts itself.
/// </para>
/// </summary>
public sealed class TrueTypeFont
{
    private readonly byte[] data;
    private readonly Dictionary<string, (int Offset, int Length)> tables;
    private readonly Dictionary<int, ushort> cmap;
    private readonly ushort[] advances;

    private TrueTypeFont(byte[] data, Dictionary<string, (int Offset, int Length)> directory)
    {
        this.data = data;
        tables = directory;
        var head = Table("head");
        UnitsPerEm = U16(head, 18);
        Bounds = (I16(head, 36), I16(head, 38), I16(head, 40), I16(head, 42));
        var hhea = Table("hhea");
        Ascent = I16(hhea, 4);
        Descent = I16(hhea, 6);
        LineGap = I16(hhea, 8);
        GlyphCount = U16(Table("maxp"), 4);
        advances = ReadAdvances(U16(hhea, 34));
        cmap = ReadCmap();
        PostScriptName = ReadPostScriptName() ?? "Font";
        if (tables.ContainsKey("OS/2"))
        {
            var os2 = Table("OS/2");
            Weight = U16(os2, 4);
            EmbeddingFlags = U16(os2, 8);
            CapHeight = U16(os2, 0) >= 2 && tables["OS/2"].Length >= 90 ? I16(os2, 88) : (int)(Ascent * 0.7);
        }
        else
        {
            CapHeight = (int)(Ascent * 0.7);
        }
        if (tables.ContainsKey("post"))
        {
            var post = Table("post");
            ItalicAngle = I32(post, 4) / 65536.0;
            IsFixedPitch = U32(post, 12) != 0;
        }
    }

    public int UnitsPerEm { get; }
    public (int XMin, int YMin, int XMax, int YMax) Bounds { get; }
    public int Ascent { get; }
    public int Descent { get; }
    public int LineGap { get; }
    public int CapHeight { get; }
    public int GlyphCount { get; }
    public int Weight { get; } = 400;
    public double ItalicAngle { get; }
    public bool IsFixedPitch { get; }
    public string PostScriptName { get; }

    /// <summary>The OS/2 table's fsType: what the font's licence allows when it's embedded.</summary>
    public int EmbeddingFlags { get; }

    /// <summary>
    /// False when the licence forbids embedding (fsType's "restricted licence" bit, 0x0002, alone).
    /// "Preview and print" (0x0004) and "editable" (0x0008) both allow a PDF, which is only looked at
    /// and printed.
    /// </summary>
    public bool CanEmbed => (EmbeddingFlags & 0x000F) != 0x0002;

    /// <summary>Reads a font file; null when it isn't a TrueType font this can embed (see the class notes).</summary>
    public static TrueTypeFont? TryLoad(string path)
    {
        try
        {
            return TryParse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static TrueTypeFont? TryParse(byte[] bytes)
    {
        try
        {
            if (bytes.Length < 12) return null;
            var version = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            // 1.0 or 'true' is TrueType; 'OTTO' (CFF outlines) and 'ttcf' (a collection) aren't for FontFile2.
            if (version != 0x00010000 && version != 0x74727565) return null;
            var count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));
            var tables = new Dictionary<string, (int Offset, int Length)>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var record = 12 + i * 16;
                var tag = Encoding.ASCII.GetString(bytes, record, 4);
                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(record + 8));
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(record + 12));
                if (offset < 0 || length < 0 || (long)offset + length > bytes.Length) return null;
                tables[tag] = (offset, length);
            }
            foreach (var required in new[] { "head", "hhea", "hmtx", "maxp", "cmap", "loca", "glyf" })
                if (!tables.ContainsKey(required)) return null;
            return new TrueTypeFont(bytes, tables);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // A damaged table that points past the end of the file.
            return null;
        }
    }

    /// <summary>The glyph that draws <paramref name="codePoint"/>, or 0 (the font's "missing" box) when it has none.</summary>
    public ushort GlyphFor(int codePoint) => cmap.GetValueOrDefault(codePoint);

    public bool Has(int codePoint) => cmap.ContainsKey(codePoint);

    /// <summary>A glyph's advance width, in font units (<see cref="UnitsPerEm"/> to the em).</summary>
    public int Advance(ushort glyph) => advances.Length == 0 ? 0 : advances[Math.Min(glyph, advances.Length - 1)];

    /// <summary>How wide <paramref name="text"/> is at <paramref name="size"/> points (no kerning).</summary>
    public double Measure(string text, double size)
    {
        var units = 0L;
        foreach (var rune in (text ?? "").EnumerateRunes()) units += Advance(GlyphFor(rune.Value));
        return units * size / UnitsPerEm;
    }

    /// <summary>
    /// A copy of the font with every glyph not in <paramref name="glyphs"/> emptied (glyph 0, and the
    /// parts composite glyphs are built from, are always kept). Glyph numbers don't change, so text
    /// drawn with the full font draws the same with the subset. Tables a PDF doesn't use for
    /// drawing (kerning and layout: GSUB, GPOS, kern…) are left out.
    /// </summary>
    public byte[] Subset(IEnumerable<ushort> glyphs)
    {
        var (locaOffsets, _) = ReadLoca();
        var keep = new HashSet<ushort>(glyphs) { 0 };
        var glyf = Table("glyf");
        // Composite glyphs ("é" built from "e" and "´") need their parts.
        var pending = new Stack<ushort>(keep);
        while (pending.Count > 0)
        {
            var glyph = pending.Pop();
            foreach (var part in Components(glyf, locaOffsets, glyph))
                if (keep.Add(part)) pending.Push(part);
        }

        var newGlyf = new MemoryStream();
        var newLoca = new byte[(GlyphCount + 1) * 4];
        for (var g = 0; g < GlyphCount; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(g * 4), (uint)newGlyf.Length);
            if (!keep.Contains((ushort)g)) continue;
            var start = locaOffsets[g];
            var end = locaOffsets[g + 1];
            if (end > start) newGlyf.Write(glyf.Slice(start, end - start));
            while (newGlyf.Length % 4 != 0) newGlyf.WriteByte(0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(GlyphCount * 4), (uint)newGlyf.Length);

        var head = Table("head").ToArray();
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1); // the new loca is the long kind
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 0); // checkSumAdjustment, worked out below

        var kept = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in new[] { "cmap", "cvt ", "fpgm", "gasp", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "prep" })
            if (tables.ContainsKey(tag)) kept[tag] = Table(tag).ToArray();
        kept["glyf"] = newGlyf.ToArray();
        kept["loca"] = newLoca;
        kept["head"] = head;
        var font = Assemble(kept);
        var adjustment = unchecked(0xB1B0AFBA - Checksum(font));
        var headAt = HeadOffset(font);
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(headAt + 8), adjustment);
        return font;
    }

    // MARK: Building a font file

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        var count = tables.Count;
        var entrySelector = (int)Math.Floor(Math.Log2(count));
        var searchRange = (1 << entrySelector) * 16;
        var header = new byte[12 + count * 16];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)(count * 16 - searchRange));
        var body = new MemoryStream();
        var index = 0;
        foreach (var (tag, bytes) in tables)
        {
            var record = 12 + index++ * 16;
            Encoding.ASCII.GetBytes(tag, header.AsSpan(record, 4));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(record + 4), Checksum(bytes));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(record + 8), (uint)(header.Length + body.Length));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(record + 12), (uint)bytes.Length);
            body.Write(bytes);
            while (body.Length % 4 != 0) body.WriteByte(0);
        }
        return [.. header, .. body.ToArray()];
    }

    private static int HeadOffset(byte[] font)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (var i = 0; i < count; i++)
            if (Encoding.ASCII.GetString(font, 12 + i * 16, 4) == "head")
                return (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(12 + i * 16 + 8));
        throw new InvalidDataException("no head table");
    }

    /// <summary>A TrueType checksum: the sum of the data as big-endian 32-bit words (zero-padded).</summary>
    internal static uint Checksum(ReadOnlySpan<byte> bytes)
    {
        uint sum = 0;
        for (var i = 0; i < bytes.Length; i += 4)
        {
            uint word = 0;
            for (var j = 0; j < 4; j++) word = (word << 8) | (i + j < bytes.Length ? bytes[i + j] : (byte)0);
            sum = unchecked(sum + word);
        }
        return sum;
    }

    // MARK: Reading

    private ReadOnlySpan<byte> Table(string tag)
    {
        var (offset, length) = tables[tag];
        return data.AsSpan(offset, length);
    }

    private ushort[] ReadAdvances(int metrics)
    {
        var hmtx = Table("hmtx");
        metrics = Math.Min(metrics, hmtx.Length / 4);
        var result = new ushort[Math.Max(GlyphCount, metrics)];
        ushort last = 0;
        for (var i = 0; i < result.Length; i++)
        {
            // Glyphs past the last metric share its width (monospaced runs at the end of the table).
            if (i < metrics) last = BinaryPrimitives.ReadUInt16BigEndian(hmtx[(i * 4)..]);
            result[i] = last;
        }
        return result;
    }

    private (int[] Offsets, bool Long) ReadLoca()
    {
        var loca = Table("loca");
        var isLong = I16(Table("head"), 50) == 1;
        var offsets = new int[GlyphCount + 1];
        for (var i = 0; i <= GlyphCount; i++)
            offsets[i] = isLong
                ? (int)BinaryPrimitives.ReadUInt32BigEndian(loca[(i * 4)..])
                : BinaryPrimitives.ReadUInt16BigEndian(loca[(i * 2)..]) * 2;
        return (offsets, isLong);
    }

    private static IEnumerable<ushort> Components(ReadOnlySpan<byte> glyf, int[] offsets, ushort glyph)
    {
        var parts = new List<ushort>();
        if (glyph + 1 >= offsets.Length) return parts;
        var start = offsets[glyph];
        var end = offsets[glyph + 1];
        if (end - start < 10 || BinaryPrimitives.ReadInt16BigEndian(glyf[start..]) >= 0) return parts; // a simple glyph
        var at = start + 10;
        while (at + 4 <= end)
        {
            var flags = BinaryPrimitives.ReadUInt16BigEndian(glyf[at..]);
            parts.Add(BinaryPrimitives.ReadUInt16BigEndian(glyf[(at + 2)..]));
            at += 4 + ((flags & 0x0001) != 0 ? 4 : 2); // the offsets: two words, or two bytes
            if ((flags & 0x0008) != 0) at += 2; // one scale
            else if ((flags & 0x0040) != 0) at += 4; // x and y scales
            else if ((flags & 0x0080) != 0) at += 8; // a 2 × 2 matrix
            if ((flags & 0x0020) == 0) break; // no more components
        }
        return parts;
    }

    private Dictionary<int, ushort> ReadCmap()
    {
        var table = Table("cmap");
        var count = U16(table, 2);
        int best = -1, bestRank = -1;
        for (var i = 0; i < count; i++)
        {
            var platform = U16(table, 4 + i * 8);
            var encoding = U16(table, 6 + i * 8);
            var offset = (int)U32(table, 8 + i * 8);
            if (offset + 2 > table.Length) continue;
            var format = U16(table, offset);
            // Full Unicode (format 12) beats the basic plane (format 4); Windows' tables beat Unicode-platform ones.
            var rank = (platform, encoding, format) switch
            {
                (3, 10, 12) => 5,
                (0, _, 12) => 4,
                (3, 1, 4) => 3,
                (0, _, 4) => 2,
                _ => -1,
            };
            if (rank > bestRank) (best, bestRank) = (offset, rank);
        }
        var map = new Dictionary<int, ushort>();
        if (best < 0) return map;
        if (U16(table, best) == 12)
        {
            var groups = U32(table, best + 12);
            for (var g = 0; g < groups; g++)
            {
                var at = best + 16 + g * 12;
                var first = U32(table, at);
                var last = U32(table, at + 4);
                var glyph = U32(table, at + 8);
                for (var c = first; c <= last && c - first < 0x110000; c++) map.TryAdd((int)c, (ushort)(glyph + c - first));
            }
            return map;
        }
        var segments = U16(table, best + 6) / 2;
        var ends = best + 14;
        var starts = ends + segments * 2 + 2;
        var deltas = starts + segments * 2;
        var ranges = deltas + segments * 2;
        for (var s = 0; s < segments; s++)
        {
            var end = U16(table, ends + s * 2);
            var start = U16(table, starts + s * 2);
            var delta = I16(table, deltas + s * 2);
            var rangeOffset = U16(table, ranges + s * 2);
            for (var c = start; c <= end && c != 0xFFFF; c++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (c + delta) & 0xFFFF;
                }
                else
                {
                    var at = ranges + s * 2 + rangeOffset + (c - start) * 2;
                    if (at + 2 > table.Length) continue;
                    glyph = U16(table, at);
                    if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                }
                if (glyph != 0) map.TryAdd(c, (ushort)glyph);
            }
        }
        return map;
    }

    private string? ReadPostScriptName()
    {
        if (!tables.ContainsKey("name")) return null;
        var table = Table("name");
        var count = U16(table, 2);
        var storage = U16(table, 4);
        string? found = null;
        for (var i = 0; i < count; i++)
        {
            var record = 6 + i * 12;
            var platform = U16(table, record);
            var nameId = U16(table, record + 6);
            var length = U16(table, record + 8);
            var offset = U16(table, record + 10);
            if (nameId != 6 || storage + offset + length > table.Length) continue;
            var bytes = table.Slice(storage + offset, length);
            var name = platform is 0 or 3 ? Encoding.BigEndianUnicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
            // A PDF name can't hold spaces or brackets; PostScript names shouldn't have them anyway.
            name = new string(name.Where(c => c > ' ' && c < 127 && "[](){}<>/%#".IndexOf(c) < 0).ToArray());
            if (name.Length > 0) found = name;
            if (platform == 3 && found is not null) break;
        }
        return found;
    }

    private static int U16(ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadUInt16BigEndian(span[at..]);

    private static int I16(ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadInt16BigEndian(span[at..]);

    private static uint U32(ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadUInt32BigEndian(span[at..]);

    private static int I32(ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadInt32BigEndian(span[at..]);
}
