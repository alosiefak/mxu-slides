using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// Fonts that travel with a presentation. A .mxuslides file carries the font files its
/// slides and themes name, unless they come with the computer, so the words look the same at
/// the other end; importing one puts its fonts in the library's fonts folder. Port of the Mac's
/// FontSharing (and the parts of FontActivator it uses). The Mac asks Core Text about fonts;
/// here the font files' own name tables are read (<see cref="FontNames"/>), which works the
/// same on any PC and in tests.
/// </summary>
public static class FontSharing
{
    /// <summary>The library's fonts folder, the same name the Mac and the PowerPoint importer use.</summary>
    public const string LibraryFolderName = "fonts";

    /// <summary>One face of a font file: its PostScript name ("Montserrat-Bold"), its family ("Montserrat") and its file.</summary>
    public sealed record Face(string Name, string Family, string Path);

    /// <summary>Every font a document names (any "fontName" or "fontFamily" in it, however deep).</summary>
    public static HashSet<string> Names<T>(T value)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        Collect(JsonSerializer.SerializeToNode(value, MxuJson.Compact), found);
        return found;
    }

    private static void Collect(JsonNode? node, HashSet<string> found)
    {
        switch (node)
        {
            case JsonObject map:
                foreach (var (key, child) in map)
                {
                    if (key is "fontName" or "fontFamily" && child is JsonValue v && v.TryGetValue<string>(out var name))
                    {
                        if (name.Length > 0) found.Add(name);
                    }
                    else
                    {
                        Collect(child, found);
                    }
                }
                break;
            case JsonArray list:
                foreach (var child in list) Collect(child, found);
                break;
        }
    }

    /// <summary>Fonts Windows itself installs (C:\Windows\Fonts), which every PC has and which aren't ours to hand out.</summary>
    public static string SystemFontsFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    /// <summary>Fonts installed for this Windows user only.</summary>
    public static string UserFontsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");

    public static bool IsSystem(string path) =>
        SystemFontsFolder.Length > 0 && Path.GetFullPath(path).StartsWith(Path.GetFullPath(SystemFontsFolder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public static bool IsShareable(string path, byte[] data) =>
        !IsSystem(path) && SniffExtension(data) is not null && IsEmbeddingAllowed(data);

    /// <summary>
    /// The files that hold the named fonts, with the faces wanted from each: names already
    /// shared are skipped, and so are fonts that come with Windows.
    /// </summary>
    public static Dictionary<string, List<Face>> FilesToShare(IEnumerable<string> names, IReadOnlySet<string> shared, Func<string, IReadOnlyList<Face>> resolve)
    {
        var files = new Dictionary<string, List<Face>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.Where(n => !shared.Contains(n)).Order(StringComparer.Ordinal))
        {
            foreach (var face in resolve(name).Where(f => !IsSystem(f.Path) && !shared.Contains(f.Name)))
            {
                if (!files.TryGetValue(face.Path, out var faces)) files[face.Path] = faces = [];
                if (!faces.Contains(face)) faces.Add(face);
            }
        }
        return files;
    }

    /// <summary>A font file is worth installing unless every face in it is here already (or it names none).</summary>
    public static bool NeedsInstall(IReadOnlyList<string> faces, Func<string, bool> available) =>
        faces.Count == 0 || faces.Any(f => !available(f));

    /// <summary>The family and the faces (PostScript names) in a font file; null when it isn't a font.</summary>
    public static (string Family, List<string> Faces)? FacesIn(string path)
    {
        var faces = FontNames.Read(path);
        return faces.Count == 0 ? null : (faces[0].Family, faces.Select(f => f.PostScriptName).ToList());
    }

    /// <summary>What a shareable font file is, its id being its SHA-256 (the name it travels under); null when it can't be shared.</summary>
    public static FontFile? Document(string path)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (!IsShareable(path, data) || SniffExtension(data) is not { } extension || FacesIn(path) is not { } inside) return null;
        return new FontFile
        {
            Id = Convert.ToHexStringLower(SHA256.HashData(data)),
            Family = inside.Family,
            Faces = inside.Faces,
            Ext = extension,
            ByteSize = data.Length,
        };
    }

    /// <summary>"ttf", "otf" or "ttc" from the file's first bytes; null for anything else.</summary>
    public static string? SniffExtension(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return null;
        var magic = data[..4];
        if (magic.SequenceEqual(new byte[] { 0x00, 0x01, 0x00, 0x00 }) || magic.SequenceEqual("true"u8)) return "ttf";
        if (magic.SequenceEqual("OTTO"u8)) return "otf";
        if (magic.SequenceEqual("ttcf"u8)) return "ttc";
        return null;
    }

    /// <summary>False for a font whose licence forbids embedding it in documents (OS/2 fsType "restricted").</summary>
    public static bool IsEmbeddingAllowed(byte[] data) => FontNames.FsType(data) is not { } fsType || (fsType & 0x000F) != 0x0002;

    /// <summary>
    /// Copies a presentation's font files into the library's fonts folder (as
    /// &lt;hash&gt;.&lt;ext&gt;, the name they travelled under), skipping ones whose faces this PC
    /// already has. Returns how many were added.
    /// </summary>
    public static int Install(IEnumerable<string> files, string libraryRoot, Func<string, bool> available)
    {
        var installed = 0;
        var folder = Path.Combine(libraryRoot, LibraryFolderName);
        foreach (var file in files)
        {
            if (FacesIn(file) is not { } inside || !NeedsInstall(inside.Faces, available)) continue;
            var target = Path.Combine(folder, Path.GetFileName(file));
            try
            {
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(folder);
                    File.Copy(file, target + ".tmp", overwrite: true);
                    File.Move(target + ".tmp", target, overwrite: true);
                }
                installed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A font that can't be copied leaves its words in a stand-in font; the import goes on.
            }
        }
        return installed;
    }
}

/// <summary>
/// The fonts in some folders, by face and by family: what this PC can draw with, or which
/// file to send for a name. Reading a font's names only reads its name table, not the whole
/// file, so even Windows' own fonts folder takes a moment, not minutes.
/// </summary>
public sealed class FontIndex
{
    private readonly List<FontSharing.Face> faces = [];
    private readonly HashSet<string> postScriptNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> families = new(StringComparer.OrdinalIgnoreCase);

    public static FontIndex Scan(IEnumerable<string> folders)
    {
        var index = new FontIndex();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                foreach (var face in FontNames.Read(file)) index.Add(new FontSharing.Face(face.PostScriptName, face.Family, file), face.FullName);
            }
        }
        return index;
    }

    private void Add(FontSharing.Face face, string fullName)
    {
        faces.Add(face);
        postScriptNames.Add(face.Name);
        if (fullName.Length > 0) postScriptNames.Add(fullName);
        families.Add(face.Family);
    }

    public IReadOnlyList<FontSharing.Face> Faces => faces;

    /// <summary>The face with exactly this name, or failing that every face of the family with this name (Core Text's lookup on the Mac).</summary>
    public IReadOnlyList<FontSharing.Face> Resolve(string name)
    {
        var exact = faces.Where(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(1).ToList();
        return exact.Count > 0 ? exact : [.. faces.Where(f => f.Family.Equals(name, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>True when a face of this name (PostScript or full name) or a family of this name is here.</summary>
    public bool IsAvailable(string name) => postScriptNames.Contains(name) || families.Contains(name);
}

/// <summary>
/// Reads the names out of a font file's 'name' table: the family (the typographic family,
/// name 16, when there is one, else name 1), the full name (4) and the PostScript name (6), for
/// each face (a .ttc holds several).
/// </summary>
public static class FontNames
{
    public sealed record Names(string Family, string FullName, string PostScriptName);

    public static List<Names> Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static List<Names> Read(Stream stream)
    {
        var found = new List<Names>();
        try
        {
            var head = ReadAt(stream, 0, 12);
            if (head is null || FontSharing.SniffExtension(head) is not { } kind) return found;
            var starts = new List<long>();
            if (kind == "ttc")
            {
                var count = U32(head, 8);
                if (count is 0 or > 256 || ReadAt(stream, 12, (int)count * 4) is not { } offsets) return found;
                for (var i = 0; i < count; i++) starts.Add(U32(offsets, i * 4));
            }
            else
            {
                starts.Add(0);
            }
            foreach (var start in starts)
            {
                if (Face(stream, start) is { } names) found.Add(names);
            }
        }
        catch (Exception e) when (e is IOException or ArgumentException or OverflowException)
        {
            found.Clear();
        }
        return found;
    }

    private static Names? Face(Stream stream, long start)
    {
        if (ReadAt(stream, start, 12) is not { } header) return null;
        var tables = U16(header, 4);
        if (ReadAt(stream, start + 12, tables * 16) is not { } directory) return null;
        for (var i = 0; i < tables; i++)
        {
            if (Encoding.ASCII.GetString(directory, i * 16, 4) != "name") continue;
            var offset = U32(directory, i * 16 + 8);
            var length = U32(directory, i * 16 + 12);
            if (length is < 6 or > 4 << 20 || ReadAt(stream, offset, (int)length) is not { } table) return null;
            return Parse(table);
        }
        return null;
    }

    private static Names? Parse(byte[] table)
    {
        var count = U16(table, 2);
        var stringsAt = U16(table, 4);
        // Per name id, the best string found so far and how good its platform/language is.
        var best = new Dictionary<int, (int Score, string Text)>();
        for (var i = 0; i < count; i++)
        {
            var record = 6 + i * 12;
            if (record + 12 > table.Length) break;
            int platform = U16(table, record), encoding = U16(table, record + 2), language = U16(table, record + 4);
            int nameId = U16(table, record + 6), length = U16(table, record + 8), offset = U16(table, record + 10);
            if (nameId is not (1 or 4 or 6 or 16)) continue;
            var at = stringsAt + offset;
            if (at + length > table.Length) continue;
            var (score, text) = platform switch
            {
                3 when encoding is 0 or 1 or 10 => (language == 0x0409 ? 4 : 3, Encoding.BigEndianUnicode.GetString(table, at, length)),
                0 => (2, Encoding.BigEndianUnicode.GetString(table, at, length)),
                1 when encoding == 0 => (1, Encoding.Latin1.GetString(table, at, length)),
                _ => (0, ""),
            };
            if (score == 0 || text.Length == 0) continue;
            if (!best.TryGetValue(nameId, out var held) || score > held.Score) best[nameId] = (score, text.Trim('\0', ' '));
        }
        string Name(int id) => best.TryGetValue(id, out var v) ? v.Text : "";
        var family = Name(16).Length > 0 ? Name(16) : Name(1);
        var postScript = Name(6).Length > 0 ? Name(6) : Name(4).Replace(" ", "", StringComparison.Ordinal);
        return family.Length == 0 || postScript.Length == 0 ? null : new Names(family, Name(4), postScript);
    }

    /// <summary>The OS/2 table's embedding licence bits; null when the font has none.</summary>
    public static ushort? FsType(byte[] data)
    {
        ushort? U16At(long offset) => offset >= 0 && offset + 2 <= data.Length ? (ushort)(data[offset] << 8 | data[offset + 1]) : null;
        uint? U32At(long offset) => offset >= 0 && offset + 4 <= data.Length
            ? (uint)data[offset] << 24 | (uint)data[offset + 1] << 16 | (uint)data[offset + 2] << 8 | data[offset + 3]
            : null;
        long start = 0;
        if (data.AsSpan().StartsWith("ttcf"u8))
        {
            if (U32At(12) is not { } first) return null;
            start = first;
        }
        if (U16At(start + 4) is not { } numTables) return null;
        for (var index = 0; index < numTables; index++)
        {
            var record = start + 12 + index * 16;
            if (record + 16 > data.Length) return null;
            if (!data.AsSpan((int)record, 4).SequenceEqual("OS/2"u8)) continue;
            if (U32At(record + 8) is not { } offset) return null;
            return U16At(offset + 8);
        }
        return null;
    }

    private static byte[]? ReadAt(Stream stream, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > stream.Length) return null;
        var buffer = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static int U16(byte[] data, int offset) => data[offset] << 8 | data[offset + 1];

    private static long U32(byte[] data, int offset) => (long)data[offset] << 24 | (long)data[offset + 1] << 16 | (long)data[offset + 2] << 8 | data[offset + 3];
}
