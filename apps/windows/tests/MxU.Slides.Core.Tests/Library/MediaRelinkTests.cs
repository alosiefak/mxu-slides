using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

/// <summary>
/// Ports of the relinking tests in MediaMissingTests.swift, plus the Windows parts: tombstones
/// come from the library's Trash folder, and a folder scan offers same-name files.
/// </summary>
public sealed class MediaRelinkTests : ScratchFolderTest
{
    private static Presentation Opener(string mediaId) => new()
    {
        Id = "p1", Name = "Opener", PresentationKind = PresentationKind.Deck, ThemeId = "",
        Slides = [new Slide { Id = "s1", Name = "", Objects = [], Background = new CueMedia { MediaId = mediaId } }],
    };

    [Fact]
    public void MediaCueFieldsAreOptionalAndRoundTrip()
    {
        const string legacy = """
            {"id":"m1","name":"Clip","mediaKind":"video","classification":"background",
            "fileHash":"h","fileName":"c.mp4","fileStatus":"ready","statusDetail":"",
            "tags":[],"favorite":false,"collections":[],"loops":false}
            """;
        var decoded = MxuJson.Deserialize<MediaItem>(legacy);
        Assert.Null(decoded.Actions);
        Assert.Null(decoded.AutoAdvance);
        Assert.Empty(MediaRelink.CarriedSettings(decoded));

        decoded.Actions = [new SlideAction { Id = "a", Kind = SlideActionKind.FireCombo, ComboId = "c1" }];
        decoded.AutoAdvance = new AutoAdvance { DelaySeconds = 5, AfterPlayback = true };
        var round = MxuJson.Clone(decoded);
        Assert.True(MxuJson.StructurallyEqual(decoded, round));
        Assert.Equal(["Actions", "Auto Advance"], MediaRelink.CarriedSettings(decoded));
    }

    [Fact]
    public void RestoredItemAdoptsIncomingNameAndCarriesSettings()
    {
        var file = WriteFile("Totally Renamed Copy.png", "png");
        var probe = new FakeProber().Probe(file)!;
        Assert.Equal(MediaKind.Image, probe.MediaKind);

        var tombstone = Media("ghost", "oldhash", "Sermon Bumper");
        tombstone.InPoint = 2;
        tombstone.PlayRate = 1.5;
        tombstone.Favorite = true;
        tombstone.Tags = ["bumpers"];
        var restored = MediaRelink.Restored(tombstone, "newhash", file, probe);

        Assert.Equal("ghost", restored.Id);
        Assert.Equal("Totally Renamed Copy", restored.Name);
        Assert.Equal("Totally Renamed Copy.png", restored.FileName);
        Assert.Equal("newhash", restored.FileHash);
        Assert.Equal(MediaKind.Image, restored.MediaKind);
        Assert.Equal(1920, restored.PixelWidth);
        Assert.True(restored.Favorite);
        Assert.Equal(["bumpers"], restored.Tags);
        Assert.Equal(2, restored.InPoint);
        Assert.Equal(["Trim", "Play Rate"], MediaRelink.CarriedSettings(tombstone));
        Assert.Empty(MediaRelink.CarriedSettings(Media("m", "h")));
    }

    [Fact]
    public void TombstoneRestoreHealsReferencesUnderOriginalId()
    {
        var library = NewLibrary();
        var original = WriteFile("bumper.png", "bumper picture");
        var item = library.ImportMedia(original)!;
        library.Save(Opener(item.Id));
        library.Delete<MediaItem>(item.Id);
        Assert.Null(library.Get<MediaItem>(item.Id));

        var targets = MissingMedia.Find(library);
        var tombstone = Assert.Single(targets.Tombstones);
        Assert.Equal((item.Id, "bumper"), (tombstone.Id, tombstone.Name)); // the Trash kept the whole item

        var returned = WriteFile("from-backup (1).png", "bumper picture");
        Assert.NotNull(MediaRelink.RestoreTombstone(library, tombstone, returned));
        var reopened = library.Get<MediaItem>(item.Id)!;
        Assert.Equal("from-backup (1)", reopened.Name);
        Assert.Equal(item.FileHash, reopened.FileHash);
        Assert.Equal(new HashSet<string> { item.Id }, MediaReferences.Ids(library.Get<Presentation>("p1")!));
        Assert.NotNull(library.BlobPath(reopened));
        Assert.Empty(MissingMedia.Find(library).Tombstones);
    }

    [Fact]
    public void TombstonesAreTheNewestTrashCopyAndOnlyWhatsStillUsed()
    {
        var library = NewLibrary();
        var used = library.ImportMedia(WriteFile("used.png", "one"))!;
        var unused = library.ImportMedia(WriteFile("unused.png", "two"))!;
        library.Save(Opener(used.Id));
        used.Name = "First name";
        library.Save(used);
        library.Delete<MediaItem>(used.Id);
        library.Delete<MediaItem>(unused.Id);
        // An id nobody has any record of still gets a stand-in, so its slide can be given a file.
        var presentation = library.Get<Presentation>("p1")!;
        presentation.Slides.Add(new Slide { Id = "s2", Name = "", Objects = [], Background = new CueMedia { MediaId = "never-seen" } });
        presentation.Slides.Add(new Slide { Id = "s3", Name = "", Objects = [], Background = new CueMedia { MediaId = "input::item::camera" } });
        library.Save(presentation);

        var tombstones = MissingMedia.Find(library).Tombstones;
        Assert.Equal(2, tombstones.Count);
        Assert.Equal("First name", tombstones.First(t => t.Id == used.Id).Name);
        Assert.Equal("Deleted media", tombstones.First(t => t.Id == "never-seen").Name);
        Assert.DoesNotContain(tombstones, t => t.Id == unused.Id); // nothing uses it
    }

    /// <summary>A timeline's music clip keeps its song's id where a video's would be: it isn't missing media.</summary>
    [Fact]
    public void MusicOnATimelineIsNotMissingMedia()
    {
        var library = NewLibrary();
        library.Save(Audio("song", "h", "Walk-in"));
        var presentation = Opener("gone-video");
        presentation.Timelines = [new PresentationTimeline { Id = "t", Cues = [new TimelineCue { Id = "c", Lane = "audio", MediaId = "song" }] }];
        library.Save(presentation);
        Assert.Equal(["gone-video"], MissingMedia.Find(library).Tombstones.Select(t => t.Id));
        Assert.Equal(1, MissingMedia.MissingCount(library, MediaReferences.Ids(presentation)));
        Assert.Equal(["gone-video"], MissingMedia.UnknownIds(library, ["song", "gone-video", "input::item::cam"]));
    }

    [Fact]
    public void MissingFilesAreFoundAndAFolderScanRelinksThemByContent()
    {
        var library = NewLibrary();
        var item = library.ImportMedia(WriteFile("Deep Blue.png", "deep blue"))!;
        var other = library.ImportMedia(WriteFile("Sunrise.png", "sunrise"))!;
        File.Delete(library.BlobPath(item)!);
        File.Delete(library.BlobPath(other)!);
        Assert.True(MissingMedia.IsFileMissing(library, item));
        Assert.Equal(new HashSet<string> { item.Id, other.Id }, MissingMedia.MissingIds(library, library.All<MediaItem>()));

        // A renamed copy of one, and a different file under the other's name.
        var folder = Directory.CreateDirectory(PathFor("USB stick/Media")).FullName;
        File.WriteAllText(Path.Combine(folder, "renamed on the stick.png"), "deep blue");
        File.WriteAllText(Path.Combine(folder, "Sunrise.png"), "sunrise, re-exported");
        var (relinked, restored, byName) = MissingMedia.Locate(library, PathFor("USB stick"), TestContext.Current.CancellationToken);
        Assert.Equal((1, 0), (relinked, restored));
        Assert.False(MissingMedia.IsFileMissing(library, item));
        Assert.Equal("Deep Blue", library.Get<MediaItem>(item.Id)!.Name); // a relink changes nothing about the item
        var suggestion = Assert.Single(byName);
        Assert.Equal((other.Id, false), (suggestion.Item.Id, suggestion.IsTombstone));
        Assert.True(MissingMedia.IsFileMissing(library, other), "a same-name file is only offered, never used by itself");
    }

    [Fact]
    public void ReplacingAFileKeepsTheItemAndSaysWhatCarriedOver()
    {
        var library = new LibraryStore(PathFor("library")) { Prober = new FakeProber() };
        library.Load();
        var item = library.ImportMedia(WriteFile("Countdown.mp4", "old video"))!;
        item.InPoint = 3;
        item.Transition = new Transition { TransitionKind = TransitionKind.Dissolve, DurationSeconds = 1 };
        library.Save(item);
        library.Save(Opener(item.Id));

        var carried = MediaRelink.ReplaceFile(library, item.Id, WriteFile("New Countdown.mp4", "new video"));
        Assert.Equal(["Trim", "Transition"], carried);
        var swapped = library.Get<MediaItem>(item.Id)!;
        Assert.Equal(("Countdown", "New Countdown.mp4", 12.5), (swapped.Name, swapped.FileName, swapped.DurationSeconds));
        Assert.NotEqual(item.FileHash, swapped.FileHash);
        Assert.NotNull(library.BlobPath(swapped));
        Assert.Equal(new HashSet<string> { item.Id }, MediaReferences.Ids(library.Get<Presentation>("p1")!)); // every use follows

        Assert.Empty(MediaRelink.ReplaceFile(library, item.Id, PathFor("New Countdown.mp4"))!); // the same file again: nothing carried
        Assert.Null(MediaRelink.ReplaceFile(library, item.Id, WriteFile("notes.txt", "not media")));
        Assert.Null(MediaRelink.ReplaceFile(library, "gone", PathFor("New Countdown.mp4")));
    }

    [Fact]
    public void AnotherItemHoldingTheFileIsFound()
    {
        var library = NewLibrary();
        library.Save(Media("b", "same", "Zebra"));
        library.Save(Media("a", "same", "Apple"));
        library.Save(Media("me", "mine", "Mine"));
        Assert.Equal("a", MediaRelink.HoldingFile(library, "same", "me")?.Id);
        Assert.Null(MediaRelink.HoldingFile(library, "mine", "me"));
    }
}
