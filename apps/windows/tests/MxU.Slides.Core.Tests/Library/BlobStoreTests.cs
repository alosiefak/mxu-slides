using MxU.Slides.Core.Library;

namespace MxU.Slides.Core.Tests.Library;

/// <summary>
/// Port of the BlobStore tests in MediaMissingTests.swift. Windows stamps a folder from a
/// clock that ticks every few milliseconds, so the store only believes a miss once the folder
/// has been quiet a moment (BlobStore.QuietWindow); <see cref="Settle"/> makes the folder's last
/// change look a while ago, as it would be in a running library.
/// </summary>
public sealed class BlobStoreTests : ScratchFolderTest
{
    private BlobStore NewStore() => new(PathFor("Blobs"));

    private void Settle(BlobStore blobs)
    {
        Directory.CreateDirectory(blobs.Folder);
        Directory.SetLastWriteTimeUtc(blobs.Folder, DateTime.UtcNow - TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void RemovingABlobFreesItAndReadsAsMissing()
    {
        var blobs = NewStore();
        var hash = blobs.Store(WriteFile("loop.txt", "loop"));
        Assert.True(blobs.Remove(hash));
        Assert.Null(blobs.Find(hash));
        Assert.False(blobs.Remove(hash), "nothing left to remove");
    }

    [Fact]
    public void BlobLookupSurvivesCachingAndDetectsDeletion()
    {
        var blobs = NewStore();
        var hash = blobs.Store(WriteFile("in.txt", "hello"));
        var resolved = blobs.Find(hash);
        Assert.NotNull(resolved);
        Assert.Equal(resolved, blobs.Find(hash));

        File.Delete(resolved);
        Assert.Null(blobs.Find(hash));

        // Another store (another part of the app) adds a file; this one finds it.
        var other = NewStore();
        var hash2 = other.Store(WriteFile("in2.txt", "world"));
        Assert.NotNull(blobs.Find(hash2));
    }

    [Fact]
    public void AMissIsRememberedUntilTheDirectoryChanges()
    {
        var blobs = NewStore();
        var held = blobs.Store(WriteFile("held.txt", "held"));
        Settle(blobs);
        Assert.Null(blobs.Find("not-on-this-pc"));
        var listed = blobs.DirectoryListings;
        for (var i = 0; i < 50; i++) Assert.Null(blobs.Find("not-on-this-pc"));
        Assert.NotNull(blobs.Find(held));
        Assert.Equal(listed, blobs.DirectoryListings); // absent and held answers come from the one listing
    }

    [Fact]
    public void AFileLandingBehindTheStoreMovesTheStampAndIsFound()
    {
        var blobs = NewStore();
        Settle(blobs);
        var hash = new string('a', 64);
        Assert.Null(blobs.Find(hash));
        var listed = blobs.DirectoryListings;
        File.WriteAllText(Path.Combine(blobs.Folder, hash + ".mov"), "behind");
        Assert.NotNull(blobs.Find(hash));
        Assert.Equal(listed + 1, blobs.DirectoryListings);
    }

    /// <summary>Windows-only: a file landing in the same clock tick as the listing (the folder's stamp doesn't move) is still found.</summary>
    [Fact]
    public void AFileLandingInsideTheStampsResolutionIsFoundOnceTheGapPasses()
    {
        var blobs = NewStore();
        Directory.CreateDirectory(blobs.Folder);
        var stamp = DateTime.UtcNow;
        Directory.SetLastWriteTimeUtc(blobs.Folder, stamp);
        var hash = new string('c', 64);
        Assert.Null(blobs.Find(hash));
        File.WriteAllText(Path.Combine(blobs.Folder, hash + ".png"), "same tick");
        Directory.SetLastWriteTimeUtc(blobs.Folder, stamp); // as if it landed in the same tick
        Thread.Sleep(60);
        Assert.NotNull(blobs.Find(hash));
    }

    [Fact]
    public void StoreAdoptAndRemoveAreSeenAtOnce()
    {
        var blobs = NewStore();
        var source = WriteFile("a.txt", "alpha");
        var expected = LibraryStore.HashFile(source);
        Assert.Null(blobs.Find(expected)); // remembered absent first

        var stored = blobs.Store(source);
        Assert.NotNull(blobs.Find(stored));

        var moved = WriteFile("b.txt", "beta");
        var movedHash = LibraryStore.HashFile(moved);
        Assert.Null(blobs.Find(movedHash));
        Assert.Equal(movedHash, blobs.Adopt(moved));
        Assert.NotNull(blobs.Find(movedHash));
        Assert.False(File.Exists(moved), "adopting moves the file in");

        Assert.True(blobs.Remove(stored));
        Assert.Null(blobs.Find(stored));
        Assert.NotNull(blobs.Find(movedHash));
    }

    [Fact]
    public void NoteDirectoryChangedListsAgain()
    {
        var blobs = NewStore();
        Settle(blobs);
        Assert.Null(blobs.Find("x"));
        var listed = blobs.DirectoryListings;
        Assert.Null(blobs.Find("x"));
        Assert.Equal(listed, blobs.DirectoryListings);
        blobs.NoteDirectoryChanged();
        Assert.Null(blobs.Find("x"));
        Assert.Equal(listed + 1, blobs.DirectoryListings);
    }

    [Fact]
    public void PresenceAnswersAWholePassFromOneListing()
    {
        var blobs = NewStore();
        var held = Enumerable.Range(0, 3).Select(i => blobs.Store(WriteFile($"f{i}.txt", $"file {i}"))).ToList();
        Settle(blobs);
        var asked = held.Concat(Enumerable.Range(0, 40).Select(i => $"missing-{i}")).ToList();
        Assert.Equal(held.ToHashSet(), blobs.Presence(asked));
        var listed = blobs.DirectoryListings;
        Assert.Equal(held.ToHashSet(), blobs.Presence(asked));
        Assert.Equal(listed, blobs.DirectoryListings); // an unchanged folder is not listed again
    }

    [Fact]
    public void PresenceIsThePerFileAnswer()
    {
        var perFile = NewStore();
        var whole = NewStore();
        var held = Enumerable.Range(0, 4).Select(i => perFile.Store(WriteFile($"p{i}.txt", $"presence {i}"))).ToList();
        var asked = held.Concat(["absent-1", "absent-2"]).ToList();
        void ExpectAgree(IEnumerable<string> hashes)
        {
            var list = hashes.ToList();
            Assert.Equal(list.Where(h => perFile.Find(h) is not null).ToHashSet(), whole.Presence(list));
        }
        ExpectAgree(asked);
        Assert.Equal(held.ToHashSet(), whole.Presence(asked));

        // Removed with the folder's stamp put back: the per-file check on a hit still notices.
        var stamp = Directory.GetLastWriteTimeUtc(perFile.Folder);
        File.Delete(perFile.Find(held[0])!);
        Directory.SetLastWriteTimeUtc(perFile.Folder, stamp);
        ExpectAgree(asked);
        Assert.Equal(held.Skip(1).ToHashSet(), whole.Presence(asked));

        var landed = new string('d', 64);
        File.WriteAllText(Path.Combine(perFile.Folder, landed + ".mov"), "behind");
        ExpectAgree(asked.Append(landed));
        Assert.Equal(new HashSet<string> { landed }, whole.Presence([landed]));
    }

    [Fact]
    public void HalfWrittenCopiesAndWaveformsAreNotMedia()
    {
        var blobs = NewStore();
        var hash = new string('e', 64);
        Directory.CreateDirectory(blobs.Folder);
        File.WriteAllText(Path.Combine(blobs.Folder, hash + ".mp4.tmp"), "copying");
        File.WriteAllText(Path.Combine(blobs.Folder, hash + ".peaks"), "waveform");
        File.WriteAllText(Path.Combine(blobs.Folder, "0f00.partial.mp4"), "transcoding");
        Assert.Null(blobs.Find(hash));
        Assert.Empty(blobs.AllHashes());
        Assert.Null(blobs.Find(""));
    }

    [Fact]
    public void ANameThatStartsWithTheHashCountsAsOnTheMac()
    {
        var blobs = NewStore();
        Directory.CreateDirectory(blobs.Folder);
        File.WriteAllText(Path.Combine(blobs.Folder, "abc123def.png"), "longer name");
        Assert.EndsWith("abc123def.png", blobs.Find("abc123"));
    }

    [Fact]
    public void TheLibraryFindsItsMediaThroughTheStore()
    {
        var library = NewLibrary();
        var item = library.ImportMedia(WriteFile("Sunrise.png", "a picture"));
        Assert.NotNull(item);
        var path = library.BlobPath(item);
        Assert.NotNull(path);
        File.Delete(path);
        Assert.Null(library.BlobPath(item));
        // Importing the same file again gives the item back and puts its file back.
        Assert.Equal(item.Id, library.ImportMedia(PathFor("Sunrise.png"))?.Id);
        Assert.NotNull(library.BlobPath(item));
    }
}
