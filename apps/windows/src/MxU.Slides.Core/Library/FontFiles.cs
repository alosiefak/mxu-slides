using System.Text;

namespace MxU.Slides.Core.Library;

/// <summary>
/// The fonts a library carries: a PowerPoint deck's embedded fonts are saved into its
/// <c>fonts</c> folder (as <c>&lt;sha256&gt;.ttf</c>, by PptxImport's FontActivator), and the app
/// loads them for drawing without installing them into Windows (the Mac's
/// FontActivator.activateStored registers them for the app's process only).
/// <para>
/// This half reads what's in a font file, without Windows: each face's family, style and
/// PostScript name (the names slides store, like "Gotham-Bold") from its <c>name</c> table, and
/// its weight, width and slant from its <c>OS/2</c> table. That's what Core Text resolves a
/// font name by on the Mac (FontSharing.faces(inFileAt:)).
/// </para>
/// </summary>
public static class FontFiles
{
    public const string FolderName = "fonts";

    /// <summary>One face in a font file.</summary>
    /// <param name="Weight">OpenType weight, 100 (thin) to 900 (black); 400 is regular.</param>
    /// <param name="Width">OpenType width class, 1 (ultra-condensed) to 9 (ultra-expanded); 5 is normal.</param>
    public sealed record Face(string Family, string Subfamily, string PostScriptName, string FullName, int Weight, int Width, bool Italic);

    /// <summary>The library's stored font files, in name order (as the Mac activates them).</summary>
    public static IReadOnlyList<string> Stored(string libraryRoot)
    {
        var folder = Path.Combine(libraryRoot, FolderName);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Every face in the file (several in a .ttc collection); empty when it isn't a font this can read.</summary>
    public static IReadOnlyList<Face> Faces(byte[] data)
    {
        try
        {
            if (data.Length < 12) return [];
            if (data.AsSpan(0, 4).SequenceEqual("ttcf"u8))
            {
                var count = (int)Math.Min(U32(data, 8), 64);
                var faces = new List<Face>(count);
                for (var i = 0; i < count; i++)
                    if (FaceAt(data, U32(data, 12 + i * 4)) is { } face) faces.Add(face);
                return faces;
            }
            return FaceAt(data, 0) is { } single ? [single] : [];
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return []; // a truncated or damaged file: nothing to load
        }
    }

    /// <summary>
    /// Every string in the first face's <c>name</c> table, by name id: 0 the copyright notice, 1 the
    /// family, 4 the full name, 5 the version, 13 the licence and 14 its web address, and so on.
    /// Empty when the file can't be read. The font credits are made from these (BundledFonts).
    /// </summary>
    public static IReadOnlyDictionary<int, string> NameStrings(byte[] data)
    {
        try
        {
            if (data.Length < 12) return new Dictionary<int, string>();
            var start = data.AsSpan(0, 4).SequenceEqual("ttcf"u8) ? U32(data, 12) : 0;
            var tables = Tables(data, start);
            return tables.TryGetValue("name", out var name)
                ? Names(data, name.Offset, name.Length).ToDictionary(p => p.Key, p => p.Value.Trim('\0', ' '))
                : new Dictionary<int, string>();
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return new Dictionary<int, string>();
        }
    }

    /// <summary>The family names a library's fonts add (for the font pickers and the PowerPoint importer).</summary>
    public static IReadOnlyList<string> Families(string libraryRoot)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Stored(libraryRoot))
        {
            try
            {
                foreach (var face in Faces(File.ReadAllBytes(file))) names.Add(face.Family);
            }
            catch (IOException)
            {
            }
        }
        return [.. names];
    }

    private static Face? FaceAt(byte[] data, long start)
    {
        var tables = Tables(data, start);
        if (!tables.TryGetValue("name", out var name)) return null;
        var names = Names(data, name.Offset, name.Length);
        var family = names.GetValueOrDefault(16) ?? names.GetValueOrDefault(1);
        if (string.IsNullOrWhiteSpace(family)) return null;
        var subfamily = names.GetValueOrDefault(17) ?? names.GetValueOrDefault(2) ?? "Regular";
        var postScript = names.GetValueOrDefault(6) ?? (family + "-" + subfamily).Replace(" ", "");
        var full = names.GetValueOrDefault(4) ?? $"{family} {subfamily}";

        int weight = 400, width = 5;
        var italic = subfamily.Contains("Italic", StringComparison.OrdinalIgnoreCase) || subfamily.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        if (tables.TryGetValue("OS/2", out var os2) && os2.Length >= 64)
        {
            weight = Math.Clamp((int)U16(data, os2.Offset + 4), 1, 1000);
            width = Math.Clamp((int)U16(data, os2.Offset + 6), 1, 9);
            var selection = U16(data, os2.Offset + 62);
            italic = (selection & 0x0001) != 0 || (selection & 0x0200) != 0;
        }
        else if (tables.TryGetValue("head", out var head) && head.Length >= 46)
        {
            italic = (U16(data, head.Offset + 44) & 0x0002) != 0;
        }
        // Some fonts store a weight class of 1–9 (old Windows style); scale those up.
        if (weight < 10) weight *= 100;
        return new Face(family.Trim(), subfamily.Trim(), postScript.Trim(), full.Trim(), weight, width, italic);
    }

    private static Dictionary<string, (long Offset, long Length)> Tables(byte[] data, long start)
    {
        var tables = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        var count = U16(data, start + 4);
        for (var i = 0; i < count; i++)
        {
            var record = start + 12 + i * 16;
            if (record + 16 > data.Length) break;
            var tag = Encoding.ASCII.GetString(data, (int)record, 4);
            tables[tag] = (U32(data, record + 8), U32(data, record + 12));
        }
        return tables;
    }

    /// <summary>
    /// The name table's strings by name id, preferring Windows English, then any Windows
    /// (UTF-16), then Unicode, then Macintosh Roman, as font tools do.
    /// </summary>
    private static Dictionary<int, string> Names(byte[] data, long offset, long length)
    {
        var count = U16(data, offset + 2);
        var storage = offset + U16(data, offset + 4);
        var best = new Dictionary<int, (int Rank, string Text)>();
        for (var i = 0; i < count; i++)
        {
            var record = offset + 6 + i * 12;
            if (record + 12 > offset + length || record + 12 > data.Length) break;
            var platform = U16(data, record);
            var encoding = U16(data, record + 2);
            var language = U16(data, record + 4);
            var nameId = U16(data, record + 6);
            var size = U16(data, record + 8);
            var at = storage + U16(data, record + 10);
            if (at + size > data.Length || size == 0) continue;
            (int Rank, string Text)? candidate = (platform, encoding) switch
            {
                (3, 1 or 10) => (language == 0x0409 ? 0 : 1, Encoding.BigEndianUnicode.GetString(data, (int)at, size)),
                (0, _) => (2, Encoding.BigEndianUnicode.GetString(data, (int)at, size)),
                (1, 0) => (3, Encoding.Latin1.GetString(data, (int)at, size)),
                _ => null,
            };
            if (candidate is not { } c) continue;
            if (!best.TryGetValue(nameId, out var current) || c.Rank < current.Rank) best[nameId] = c;
        }
        return best.ToDictionary(p => p.Key, p => p.Value.Text);
    }

    private static ushort U16(byte[] data, long offset) => (ushort)(data[offset] << 8 | data[offset + 1]);

    private static uint U32(byte[] data, long offset) =>
        (uint)data[offset] << 24 | (uint)data[offset + 1] << 16 | (uint)data[offset + 2] << 8 | data[offset + 3];
}
