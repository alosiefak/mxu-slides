using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

/// <summary>Ports of the file-handling tests in MediaImportTests.swift (the decoding ones are in the Broadcast tests).</summary>
public sealed class MediaImportingTests : ScratchFolderTest
{
    [Fact]
    public void AFolderOpensUpSortedWithoutHiddenFiles()
    {
        WriteFile("drop/b.png", "b");
        WriteFile("drop/a.png", "a");
        WriteFile("drop/.DS_Store", "mac junk");
        WriteFile("drop/inner/c.mp4", "c");
        var hidden = WriteFile("drop/hidden.png", "h");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var single = WriteFile("single.jpg", "s");
        var files = MediaImporting.Files([PathFor("drop"), single]);
        Assert.Equal(["a.png", "b.png", "c.mp4", "single.jpg"], files.Select(Path.GetFileName));
    }

    [Fact]
    public void DuplicateImportSharesOneBlob()
    {
        var library = NewLibrary();
        var a = MediaImporting.AddMedia(library, WriteFile("a.png", "same picture"))!;
        var b = MediaImporting.AddMedia(library, WriteFile("b.png", "same picture"))!;
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(a.FileHash, b.FileHash);
        Assert.Single(library.Blobs.AllHashes());
        Assert.Equal(2, library.Count<MediaItem>());
    }

    [Fact]
    public void AnImportTakesTheGivenNameAndRecordsWhenItCameIn()
    {
        var library = NewLibrary();
        var before = DateTimeOffset.Now.ToUnixTimeSeconds();
        var item = MediaImporting.AddMedia(library, WriteFile("Background.png", "pixels"), name: "Background 2")!;
        Assert.Equal("Background 2", item.Name);
        Assert.Equal("Background.png", item.FileName);
        Assert.True(item.ImportedAt >= before);
    }

    [Fact]
    public void TheProbeFillsInTheItemAndAFlaggedVideoIsStillKept()
    {
        var prober = new FakeProber(MediaFileStatus.NeedsTranscode, "unsupported codec: MJPG");
        var library = new LibraryStore(PathFor("library")) { Prober = prober };
        library.Load();
        var item = MediaImporting.AddMedia(library, WriteFile("camera.avi", "frames"))!;
        Assert.Equal((MediaFileStatus.NeedsTranscode, "unsupported codec: MJPG"), (item.FileStatus, item.StatusDetail));
        Assert.Equal((12.5, 1920, 1080), (item.DurationSeconds, item.PixelWidth, item.PixelHeight));
        Assert.Single(prober.Probed);
    }

    [Fact]
    public void APictureTheProbeCantReadIsSkipped()
    {
        var library = new LibraryStore(PathFor("library")) { Prober = new FakeProber(skip: "Windows can't read this HEIC photo") };
        library.Load();
        Assert.Null(MediaImporting.AddMedia(library, WriteFile("photo.heic", "heic")));
        Assert.Null(library.ImportMedia(PathFor("photo.heic")));
        Assert.Equal(0, library.Count<MediaItem>());
        Assert.Empty(library.Blobs.AllHashes()); // nothing copied in for a file that can't be used
    }

    [Fact]
    public void AGivenProbeIsUsedInsteadOfLookingAgain()
    {
        var prober = new FakeProber();
        var library = new LibraryStore(PathFor("library")) { Prober = prober };
        library.Load();
        var path = WriteFile("loop.mp4", "video");
        var item = MediaImporting.AddMedia(library, path, probe: new MediaProbe(MediaKind.Video, MediaFileStatus.Ready, "", 30, 1280, 720))!;
        Assert.Equal(1280, item.PixelWidth);
        Assert.Empty(prober.Probed);
    }

    [Fact]
    public void TheImportersStillReuseTheItemForTheSameFile()
    {
        var library = NewLibrary();
        var first = library.ImportMedia(WriteFile("logo.png", "logo"))!;
        var again = library.ImportMedia(WriteFile("logo copy.png", "logo"))!;
        Assert.Equal(first.Id, again.Id);
        Assert.Null(library.ImportMedia(WriteFile("readme.txt", "text")));
    }
}
