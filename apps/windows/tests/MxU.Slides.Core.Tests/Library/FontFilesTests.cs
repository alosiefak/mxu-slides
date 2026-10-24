using System.Text;
using MxU.Slides.Core.Library;

namespace MxU.Slides.Core.Tests.Library;

// What the app reads from a library font file before loading it (the names Core Text resolves on the
// Mac: FontSharing.faces(inFileAt:)), on a hand-built font and on one Windows ships.
public class FontFilesTests
{
    /// <summary>A minimal sfnt: a name table (Windows, English) and an OS/2 table with a weight and italic bit.</summary>
    private static byte[] Font(string family, string subfamily, string postScript, ushort weight, bool italic, string? typographicFamily = null)
    {
        var names = new List<(ushort Id, string Text)> { (1, family), (2, subfamily), (4, $"{family} {subfamily}"), (6, postScript) };
        if (typographicFamily is not null) names.Add((16, typographicFamily));
        var storage = new MemoryStream();
        var records = new List<byte>();
        foreach (var (id, text) in names)
        {
            // platform 3 (Windows), encoding 1 (UTF-16), language 0x0409 (English), name id, length, offset.
            var bytes = Encoding.BigEndianUnicode.GetBytes(text);
            foreach (var field in new ushort[] { 3, 1, 0x0409, id, (ushort)bytes.Length, (ushort)storage.Length }) records.AddRange(U16(field));
            storage.Write(bytes);
        }
        var nameTable = new List<byte>();
        nameTable.AddRange(U16(0));
        nameTable.AddRange(U16((ushort)names.Count));
        nameTable.AddRange(U16((ushort)(6 + names.Count * 12)));
        nameTable.AddRange(records);
        nameTable.AddRange(storage.ToArray());

        var os2 = new byte[96];
        os2[4] = (byte)(weight >> 8);
        os2[5] = (byte)weight;
        os2[7] = 5; // normal width
        if (italic) os2[63] = 1;

        var tables = new (string Tag, byte[] Data)[] { ("OS/2", os2), ("name", nameTable.ToArray()) };
        var font = new List<byte>();
        font.AddRange([0, 1, 0, 0]);
        font.AddRange(U16((ushort)tables.Length));
        font.AddRange(new byte[6]);
        var offset = 12 + tables.Length * 16;
        var bodies = new List<byte>();
        foreach (var (tag, data) in tables)
        {
            font.AddRange(Encoding.ASCII.GetBytes(tag));
            font.AddRange(new byte[4]);
            font.AddRange(U32((uint)(offset + bodies.Count)));
            font.AddRange(U32((uint)data.Length));
            bodies.AddRange(data);
        }
        font.AddRange(bodies);
        return [.. font];
    }

    private static byte[] U16(ushort v) => [(byte)(v >> 8), (byte)v];
    private static byte[] U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    [Fact]
    public void AFaceIsReadByItsNamesWeightAndSlant()
    {
        var face = Assert.Single(FontFiles.Faces(Font("Gotham Bold", "Italic", "Gotham-BoldItalic", 700, italic: true, typographicFamily: "Gotham")));
        Assert.Equal("Gotham", face.Family); // the typographic family groups the weights, as Core Text does
        Assert.Equal("Gotham-BoldItalic", face.PostScriptName);
        Assert.Equal(700, face.Weight);
        Assert.True(face.Italic);

        var book = Assert.Single(FontFiles.Faces(Font("Gotham Book", "Regular", "Gotham-Book", 400, italic: false)));
        Assert.Equal("Gotham Book", book.Family); // no typographic family: the legacy one
        Assert.False(book.Italic);
        Assert.Equal("Gotham Book Regular", book.FullName);
    }

    [Fact]
    public void SomethingThatIsntAFontHasNoFaces()
    {
        Assert.Empty(FontFiles.Faces(Encoding.UTF8.GetBytes("definitely not a font")));
        Assert.Empty(FontFiles.Faces([0, 1, 0, 0, 0, 9, 0, 0, 0, 0, 0, 0])); // tables promised but missing
    }

    [Fact]
    public void TheLibrarysStoredFontsAreListedInNameOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mxu-fonts-{Guid.NewGuid():N}");
        var folder = Directory.CreateDirectory(Path.Combine(root, FontFiles.FolderName)).FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "bbb.otf"), Font("Beta", "Regular", "Beta-Regular", 400, false));
            File.WriteAllBytes(Path.Combine(folder, "aaa.ttf"), Font("Alpha", "Bold", "Alpha-Bold", 700, false));
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a font");
            Assert.Equal(["aaa.ttf", "bbb.otf"], FontFiles.Stored(root).Select(Path.GetFileName));
            Assert.Equal(["Alpha", "Beta"], FontFiles.Families(root));
            Assert.Empty(FontFiles.Stored(Path.Combine(root, "nowhere")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AFontWindowsShipsReadsAsCoreTextNamesIt()
    {
        var arial = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        if (!File.Exists(arial)) Assert.Skip("No Arial on this computer");
        var face = Assert.Single(FontFiles.Faces(File.ReadAllBytes(arial)));
        Assert.Equal("Arial", face.Family);
        Assert.Equal("ArialMT", face.PostScriptName);
        Assert.Equal(400, face.Weight);
        Assert.False(face.Italic);
    }
}
