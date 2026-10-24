namespace MxU.Slides.PptxImport.Tests;

// Ported from PresenterCore/Tests/FontActivatorTests.swift: how a deck's embedded fonts are saved into the library.
public class FontActivatorTests
{
    /// <summary>A minimal sfnt with one OS/2 table carrying an fsType (embedding licence).</summary>
    private static byte[] SfntData(ushort fsType) =>
    [
        0x00, 0x01, 0x00, 0x00, // sfnt version
        0x00, 0x01, // one table
        0, 0, 0, 0, 0, 0,
        .. "OS/2"u8.ToArray(),
        0, 0, 0, 0, // checksum
        0, 0, 0, 28, // offset
        0, 0, 0, 10, // length
        0, 4, 0, 0, 0, 0, 0, 0,
        (byte)(fsType >> 8), (byte)(fsType & 0xFF),
    ];

    [Fact]
    public void StoresValidFontContentAddressedAndDedupes()
    {
        using var root = new TempFolder();
        var source = root.File("font1.fntdata");
        File.WriteAllBytes(source, SfntData(0));

        var stored = FontActivator.Store(source, root.Path);
        Assert.NotNull(stored);
        Assert.Equal(".ttf", Path.GetExtension(stored));
        Assert.Equal("fonts", Path.GetFileName(Path.GetDirectoryName(stored)));
        Assert.True(File.Exists(stored));

        Assert.Equal(stored, FontActivator.Store(source, root.Path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(stored)!));
    }

    [Fact]
    public void RestrictedLicenseEmbeddingIsRefused()
    {
        using var root = new TempFolder();
        var restricted = root.File("restricted.fntdata");
        File.WriteAllBytes(restricted, SfntData(0x0002));
        Assert.Null(FontActivator.Store(restricted, root.Path));

        var preview = root.File("preview.fntdata");
        File.WriteAllBytes(preview, SfntData(0x0004));
        Assert.NotNull(FontActivator.Store(preview, root.Path));
    }

    [Fact]
    public void OdttfObfuscationUnwindsFromTheGuidName()
    {
        using var root = new TempFolder();
        var clear = SfntData(0);
        const string guid = "B14E9DBF-345A-4B32-9C09-2EA65B7A9A02";
        var key = FontActivator.GuidBytes($"{guid}.fntdata");
        Assert.NotNull(key);
        var obfuscated = clear.ToArray();
        var reversedKey = key.Reverse().ToArray();
        for (var index = 0; index < 32; index++) obfuscated[index] ^= reversedKey[index % 16];
        var source = root.File($"{guid}.fntdata");
        File.WriteAllBytes(source, obfuscated);

        var stored = FontActivator.Store(source, root.Path);
        Assert.NotNull(stored);
        Assert.Equal(clear, File.ReadAllBytes(stored));
    }

    [Fact]
    public void GarbageDataIsRefused()
    {
        using var root = new TempFolder();
        var garbage = root.File("font1.fntdata");
        File.WriteAllText(garbage, "definitely not a font");
        Assert.Null(FontActivator.Store(garbage, root.Path));
    }
}
