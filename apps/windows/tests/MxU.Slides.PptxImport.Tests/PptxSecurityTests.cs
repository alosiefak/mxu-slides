using System.IO.Compression;
using System.Text;

namespace MxU.Slides.PptxImport.Tests;

public class PptxSecurityTests
{
    [Fact]
    public void RejectsExternalEntities()
    {
        using var root = new TempFolder();
        var secret = root.File("private.txt");
        File.WriteAllText(secret, "PRIVATE_SENTINEL");
        var xml = $"<!DOCTYPE root [<!ENTITY secret SYSTEM '{new Uri(secret).AbsoluteUri}'>]><root>&secret;</root>";
        File.WriteAllText(root.File("slide.xml"), xml);
        Assert.ThrowsAny<Exception>(() => new PptxPackage(root.Path).Document("slide.xml"));
    }

    [Fact]
    public void RejectsTraversalParts()
    {
        using var root = new TempFolder();
        var package = new PptxPackage(root.Path);
        Assert.ThrowsAny<Exception>(() => package.FilePath("../private.xml"));
        var traversal = package.ResolveTarget("../../../private.xml", "ppt/slides/slide1.xml");
        Assert.ThrowsAny<Exception>(() => package.FilePath(traversal));
        var absoluteTraversal = package.ResolveTarget("/../private.xml", "ppt/slides/slide1.xml");
        Assert.ThrowsAny<Exception>(() => package.FilePath(absoluteTraversal));
        Assert.Equal("ppt/media/image.png", package.ResolveTarget("../media/image.png", "ppt/slides/slide1.xml"));
        Assert.Equal("slide1.xml", Path.GetFileName(package.FilePath("ppt/slides/slide1.xml")));

        Assert.ThrowsAny<Exception>(() => package.FilePath("C:/Windows/win.ini"));
        Assert.ThrowsAny<Exception>(() => package.FilePath("ppt/slides/slide1.xml:hidden"));
        Assert.ThrowsAny<Exception>(() => package.FilePath("ppt/CON"));
        Assert.ThrowsAny<Exception>(() => package.FilePath("ppt/nul.xml"));
    }

    [Fact]
    public void RejectsSymlinkParts()
    {
        using var root = new TempFolder();
        var package = new PptxPackage(root.Path);
        File.WriteAllText(root.File("safe.xml"), "<root/>");
        try
        {
            Directory.CreateSymbolicLink(root.File("link"), Path.GetDirectoryName(root.Path)!);
            File.CreateSymbolicLink(root.File("internal.xml"), "safe.xml");
            File.CreateSymbolicLink(root.File("dangling.xml"), "missing.xml");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("creating symbolic links needs Developer Mode or administrator rights");
        }
        Assert.ThrowsAny<Exception>(() => package.FilePath("link/private.xml"));
        Assert.ThrowsAny<Exception>(() => package.Document("internal.xml"));
        Assert.ThrowsAny<Exception>(() => package.FilePath("dangling.xml"));
    }

    [Theory]
    [InlineData("<!DOCTYPE root>")]
    [InlineData("<!DOCTYPE root [<!ENTITY text 'internal entity'>]>")]
    public void RejectsDtdDeclarations(string declaration)
    {
        using var root = new TempFolder();
        var xml = $"<?xml version='1.0' encoding='UTF-16'?>{declaration}<root/>";
        File.WriteAllBytes(root.File("slide.xml"), [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(xml)]);
        Assert.Throws<PptxImportException>(() => new PptxPackage(root.Path).Document("slide.xml"));
    }

    [Fact]
    public void AcceptsOrdinaryXmlAndBuiltInEntities()
    {
        using var root = new TempFolder();
        File.WriteAllText(root.File("slide.xml"), "<root>A &amp; B</root>");
        Assert.Equal("A & B", new PptxPackage(root.Path).Document("slide.xml").Root?.Value);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("missing")]
    [InlineData("safe.xml")]
    public void RejectsArchiveContainingASymbolicLink(string target)
    {
        using var root = new TempFolder();
        var archive = root.File("unsafe.pptx");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var link = zip.CreateEntry("link");
            link.ExternalAttributes = unchecked((int)0xA1FF_0000);
            using (var stream = link.Open()) stream.Write(Encoding.UTF8.GetBytes(target));
            using (var stream = zip.CreateEntry("safe.xml").Open()) stream.Write("<root/>"u8);
        }
        Assert.ThrowsAny<Exception>(() => PptxArchive.Extract(archive, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../outside.xml")]
    [InlineData("/rooted.xml")]
    [InlineData("C:/rooted.xml")]
    [InlineData(@"ppt\..\..\outside.xml")]
    [InlineData("ppt/CON")]
    [InlineData("ppt/aux.xml")]
    [InlineData("ppt/slide1.xml:stream")]
    [InlineData("ppt//slide1.xml")]
    public void RejectsArchiveEntriesThatLeaveTheFolder(string name)
    {
        using var root = new TempFolder();
        var archive = root.File("unsafe.pptx");
        TestPackages.Zip(archive, new Dictionary<string, byte[]> { ["safe.xml"] = "<root/>"u8.ToArray(), [name] = "<root/>"u8.ToArray() });
        var error = Assert.Throws<PptxImportException>(() => PptxArchive.Extract(archive, TestContext.Current.CancellationToken));
        Assert.Contains("unsafe file", error.Message);
        Assert.False(File.Exists(Path.Combine(root.Path, "outside.xml")));
    }

    [Fact]
    public void RejectsEntriesThatCollideOnACaseInsensitiveDisk()
    {
        using var root = new TempFolder();
        var archive = root.File("twins.pptx");
        TestPackages.Zip(archive, new Dictionary<string, byte[]> { ["ppt/a.xml"] = "<a/>"u8.ToArray(), ["PPT/A.xml"] = "<b/>"u8.ToArray() });
        Assert.Throws<PptxImportException>(() => PptxArchive.Extract(archive, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RejectsZipBombs()
    {
        using var root = new TempFolder();
        var archive = root.File("bomb.pptx");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("ppt/media/zeros.bin", CompressionLevel.SmallestSize).Open();
            var block = new byte[1 << 20];
            for (var i = 0; i < 300; i++) stream.Write(block);
        }
        Assert.True(new FileInfo(archive).Length < 1 << 20, $"the bomb should be small, was {new FileInfo(archive).Length:N0} bytes");
        var error = Assert.Throws<PptxImportException>(() => PptxArchive.Extract(archive, TestContext.Current.CancellationToken));
        Assert.Contains("zip bomb", error.Message);
    }

    [Fact]
    public void ExtractsAnOrdinaryPackage()
    {
        using var root = new TempFolder();
        var archive = root.File("deck.pptx");
        TestPackages.Zip(archive, new Dictionary<string, byte[]>
        {
            ["[Content_Types].xml"] = "<Types/>"u8.ToArray(),
            ["ppt/slides/slide1.xml"] = "<sld/>"u8.ToArray(),
        });
        var extracted = PptxArchive.Extract(archive, TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal("<sld/>", File.ReadAllText(Path.Combine(extracted, "ppt", "slides", "slide1.xml")));
            Assert.True(File.Exists(Path.Combine(extracted, "[Content_Types].xml")));
        }
        finally
        {
            PptxArchive.TryDelete(extracted);
        }
    }

    [Fact]
    public void NonZipFilesFailWithAReadableReason()
    {
        using var root = new TempFolder();
        var archive = root.File("garbage.pptx");
        File.WriteAllText(archive, "not a zip at all");
        var error = Assert.Throws<PptxImportException>(() => PptxArchive.Extract(archive, TestContext.Current.CancellationToken));
        Assert.Contains("not a PowerPoint package", error.Message);
    }
}
