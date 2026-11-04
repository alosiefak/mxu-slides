using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

/// <summary>
/// The library's media files as files (the Mac's update of 2026-10): an edited file takes its new
/// bytes (port of aChangedFileKeepsItsNameAndSettingsAndTakesTheNewBytes, plus how Windows notices
/// the edit), a delete takes a file nothing else uses, and an import nobody watches reuses only
/// the very same file (MediaImportDedupeTests.swift).
/// </summary>
public sealed class MediaFileEditsTests : ScratchFolderTest
{
    private static readonly string OldHash = new('a', 64);

    [Fact]
    public void AChangedFileKeepsItsNameAndSettingsAndTakesTheNewBytes()
    {
        var item = Media("m1", "old", "One-Off Capture");
        item.FileName = "clip.mp4";
        item.InPoint = 2;
        item.DurationSeconds = 1492.9;
        var probe = new MediaProbe(MediaKind.Video, MediaFileStatus.Ready, "", 47.8, 1920, 1080);

        var readded = MediaRelink.Refingerprinted(item, "new", probe);
        Assert.Equal("new", readded.FileHash);
        Assert.Equal(47.8, readded.DurationSeconds);
        Assert.Equal(1920, readded.PixelWidth);
        Assert.Equal(("m1", "One-Off Capture", "clip.mp4"), (readded.Id, readded.Name, readded.FileName));
        Assert.Equal(2, readded.InPoint);
        Assert.Equal("old", item.FileHash); // the item passed in is left as it was

        var unprobed = MediaRelink.Refingerprinted(item, "new", probe: null);
        Assert.Equal("new", unprobed.FileHash);
        Assert.Equal(1492.9, unprobed.DurationSeconds); // an unreadable file keeps the facts it had
    }

    [Fact]
    public void AnImportNobodyWatchesReusesOnlyTheSameFile()
    {
        Assert.True(MediaImportDedupe.UnattendedUsesExisting(new MediaImportDedupe.Match.SameFile("m1")));
        Assert.False(MediaImportDedupe.UnattendedUsesExisting(new MediaImportDedupe.Match.SameName("m1", "Announce")));
        Assert.False(MediaImportDedupe.UnattendedUsesExisting(new MediaImportDedupe.Match.New()));
    }

    [Fact]
    public void AFileSeenForTheFirstTimeIsNotedAsItIs()
    {
        var library = NewLibrary();
        var file = Blob(library, OldHash, DateTime.UtcNow.AddHours(-1));
        File.WriteAllText(Path.Combine(library.BlobDirectory, "not-a-hash.png"), "x"); // a file the library didn't name: never looked at

        var edits = new BlobEdits(library.BlobDirectory, library.Root);
        var (settled, settling) = edits.EditedFiles(DateTime.UtcNow);
        Assert.Empty(settled); // the first launch of this version: no time noted, so nothing to compare
        Assert.False(settling);
        Assert.Empty(edits.EditedFiles(DateTime.UtcNow).Settled);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void AnEditIsLookedAtOnlyOnceTheFileHasBeenStill()
    {
        var library = NewLibrary();
        var file = Blob(library, OldHash, DateTime.UtcNow.AddHours(-1));
        var edits = new BlobEdits(library.BlobDirectory, library.Root);
        edits.StampKnown(file);
        Assert.Empty(edits.EditedFiles(DateTime.UtcNow).Settled);

        // Saved from a photo editor just now: still settling.
        var saved = DateTime.UtcNow;
        File.WriteAllText(file, "touched up");
        File.SetLastWriteTimeUtc(file, saved);
        var (now, settling) = edits.EditedFiles(saved.AddSeconds(2));
        Assert.Empty(now);
        Assert.True(settling);

        // Still for longer than the settle time: ready, under its old hash.
        var (later, stillSettling) = edits.EditedFiles(saved + BlobEdits.Settle + TimeSpan.FromSeconds(1));
        Assert.Equal([(OldHash, file)], later);
        Assert.False(stillSettling);

        // Once adopted (or found to be the same bytes) it's noted again, and not offered twice.
        edits.StampKnown(file);
        Assert.Empty(edits.EditedFiles(saved.AddMinutes(5)).Settled);
    }

    [Fact]
    public void TheNotedTimesSurviveARestart()
    {
        var library = NewLibrary();
        var file = Blob(library, OldHash, DateTime.UtcNow.AddHours(-1));
        var first = new BlobEdits(library.BlobDirectory, library.Root);
        first.StampKnown(file);
        first.Save();
        Assert.True(File.Exists(Path.Combine(library.Root, BlobEdits.FileName)));

        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-10)); // edited while the app was closed
        var again = new BlobEdits(library.BlobDirectory, library.Root);
        Assert.Equal([(OldHash, file)], again.EditedFiles(DateTime.UtcNow).Settled);

        again.Forget(file); // taken in under a new name: the old one is forgotten
        again.Save();
        Assert.Empty(new BlobEdits(library.BlobDirectory, library.Root).EditedFiles(DateTime.UtcNow).Settled);
    }

    [Fact]
    public void ADeleteTakesOnlyFilesNothingElseUses()
    {
        var library = NewLibrary();
        library.Save(Media("kept", "shared"));
        library.Save(Audio("song", "music", "Song"));
        var unused = BlobEdits.UnusedAfterDelete(library, ["shared", "gone", "GONE", "music", ""]);
        Assert.Equal(["gone"], unused); // another item still names "shared"; the music file stays; case doesn't matter
    }

    private string Blob(LibraryStore library, string hash, DateTime writtenUtc)
    {
        Directory.CreateDirectory(library.BlobDirectory);
        var path = Path.Combine(library.BlobDirectory, hash + ".png");
        File.WriteAllText(path, "original");
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }
}
