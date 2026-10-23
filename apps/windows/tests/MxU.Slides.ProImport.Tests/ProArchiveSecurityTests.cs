using System.IO.Compression;
using System.Text;

namespace MxU.Slides.ProImport.Tests;

public class ProArchiveSecurityTests
{
    private static string Archive(TempFolder folder, Action<ZipArchive> fill)
    {
        var path = folder.Combine("unsafe.probundle");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create)) fill(archive);
        return path;
    }

    private static void AddFile(ZipArchive archive, string name, string content = "", int? unixMode = null)
    {
        var entry = archive.CreateEntry(name);
        if (unixMode is { } mode) entry.ExternalAttributes = mode << 16;
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("missing")]
    [InlineData("safe.pro")]
    public void RejectsArchiveContainingASymbolicLink(string target)
    {
        using var folder = new TempFolder();
        var archive = Archive(folder, zip =>
        {
            AddFile(zip, "safe.pro");
            AddFile(zip, "link", target, unixMode: 0xA1FF);
        });
        Assert.ThrowsAny<IOException>(() => ImportArchive.Extract(archive));
    }

    [Theory]
    [InlineData("../escaped.pro")]
    [InlineData("nested/../../escaped.pro")]
    public void RejectsEntriesThatClimbOutOfTheFolder(string name)
    {
        using var folder = new TempFolder();
        var archive = Archive(folder, zip => AddFile(zip, name));
        Assert.ThrowsAny<IOException>(() => ImportArchive.Extract(archive));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "escaped.pro")));
    }

    [Fact]
    public void ExtractsPlainFilesAndLeavesOutResourceForks()
    {
        using var folder = new TempFolder();
        var archive = Archive(folder, zip =>
        {
            AddFile(zip, "song.pro", "x", unixMode: 0x81A4);
            AddFile(zip, "Media/bg.png", "y");
            AddFile(zip, "__MACOSX/._song.pro", "fork");
            AddFile(zip, "._song.pro", "fork");
        });
        var extracted = ImportArchive.Extract(archive);
        try
        {
            Assert.True(File.Exists(Path.Combine(extracted, "song.pro")));
            Assert.True(File.Exists(Path.Combine(extracted, "Media", "bg.png")));
            Assert.False(Directory.Exists(Path.Combine(extracted, "__MACOSX")));
            Assert.False(File.Exists(Path.Combine(extracted, "._song.pro")));
        }
        finally
        {
            ImportArchive.TryDelete(extracted);
        }
    }
}
