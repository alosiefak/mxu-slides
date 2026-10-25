using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

/// <summary>
/// Favourites and tags, last-used times and file dates: the things the library browser sorts and
/// filters by that aren't in the documents' own words.
/// </summary>
public sealed class LibraryMarksTests : ScratchFolderTest
{
    private static Presentation Song(string id) => new() { Id = id, Name = id, PresentationKind = PresentationKind.Song, ThemeId = "", Slides = [] };

    [Fact]
    public void MediaAndMusicKeepTheirMarksInTheSharedFields()
    {
        var library = NewLibrary();
        library.Save(Media("m", "h1"));
        library.Save(Audio("a", "h2", "Walk-in"));
        var marks = new LibraryMarks(library);
        marks.SetFavorite(LibraryItemKind.Media, "m", true);
        marks.SetTags(LibraryItemKind.Audio, "a", ["Offering", " offering ", "Walk  in"]);

        // The fields a Mac already reads and writes, so the marks travel with the item.
        Assert.True(library.Get<MediaItem>("m")!.Favorite);
        Assert.Equal(["Offering", "Walk in"], library.Get<AudioItem>("a")!.Tags);
        Assert.Empty(library.Extras.Owners(LibraryMarks.ExtraKind));
    }

    [Fact]
    public void ASongsMarksAreAWindowsExtraAndTheSongItselfIsUntouched()
    {
        var library = NewLibrary();
        var song = Song("s");
        library.Save(song);
        var before = File.ReadAllText(library.DocumentPath(typeof(Presentation), "s"));
        var marks = new LibraryMarks(library);
        marks.SetFavorite(LibraryItemKind.Song, "s", true);
        marks.SetTags(LibraryItemKind.Song, "s", ["Easter"]);

        Assert.Equal(before, File.ReadAllText(library.DocumentPath(typeof(Presentation), "s"))); // a Mac reads exactly what it did
        Assert.Equal(["s"], library.Extras.Owners(LibraryMarks.ExtraKind));
        // A fresh reader (the next launch) sees them.
        var again = new LibraryMarks(library);
        Assert.True(again.IsFavorite(LibraryItemKind.Song, "s"));
        Assert.Equal(["Easter"], again.Tags(LibraryItemKind.Song, "s"));

        // Unmarked again, the extra goes rather than lingering empty.
        again.SetFavorite(LibraryItemKind.Song, "s", false);
        again.SetTags(LibraryItemKind.Song, "s", []);
        Assert.Empty(library.Extras.Owners(LibraryMarks.ExtraKind));
    }

    [Fact]
    public void TaggingABatchAddsOrTakesOffOneTagAndListsEveryTag()
    {
        var library = NewLibrary();
        library.Save(Song("s1"));
        library.Save(Song("s2"));
        var item = Media("m", "h");
        item.Tags = ["Water"];
        library.Save(item);
        var marks = new LibraryMarks(library);
        marks.Tag([(LibraryItemKind.Song, "s1"), (LibraryItemKind.Song, "s2"), (LibraryItemKind.Media, "m")], "Easter", add: true);
        Assert.Equal(["Easter", "Water"], marks.AllTags());
        marks.Tag([(LibraryItemKind.Media, "m")], "EASTER", add: false);
        Assert.Equal(["Water"], library.Get<MediaItem>("m")!.Tags);
        Assert.Equal(["Easter"], marks.Tags(LibraryItemKind.Song, "s2"));
    }

    [Fact]
    public void TagsAreTidied()
    {
        // The first spelling of a tag wins; later ones with other capitals are the same tag.
        Assert.Equal(["Easter", "good friday"], LibraryMarks.Parse(" Easter, good friday ,Good  Friday,, easter"));
        Assert.Equal(["Good Friday"], LibraryMarks.Parse("Good  Friday"));
        Assert.Equal(LibraryMarks.MaxTagLength, LibraryMarks.Clean([new string('x', 100)]).Single().Length);
    }

    [Fact]
    public void ADuplicatedSongKeepsItsMarksAndADeletedOneLosesThem()
    {
        var library = NewLibrary();
        var marks = new LibraryMarks(library);
        marks.SetTags(LibraryItemKind.Song, "s", ["Easter"]);
        marks.Copy("s", "copy");
        Assert.Equal(["Easter"], marks.Tags(LibraryItemKind.Song, "copy"));
        marks.Forget("s");
        Assert.Empty(marks.Tags(LibraryItemKind.Song, "s"));
        Assert.Equal(["copy"], library.Extras.Owners(LibraryMarks.ExtraKind));
    }

    // MARK: Usage

    [Fact]
    public void UsageKeepsTheLatestTimeAndSavesTheMacsBackupShape()
    {
        var library = NewLibrary();
        var usage = new LibraryUsage(library.Root);
        usage.Touch("a", DateTimeOffset.FromUnixTimeSeconds(200));
        usage.Touch("a", DateTimeOffset.FromUnixTimeSeconds(100)); // an older stamp never wins
        usage.Merge(new Dictionary<string, double> { ["b"] = 50, ["a"] = 150 });
        usage.SaveNow();

        var saved = LibraryUsage.ReadFile(Path.Combine(library.Root, LibraryUsage.FileName));
        Assert.Equal(200, saved["a"]); // id → seconds since 1970, like the Mac's usage.json
        Assert.Equal(50, saved["b"]);
        var again = new LibraryUsage(library.Root);
        again.Load();
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(200), again.LastUsed("a"));
        Assert.Null(again.LastUsed("never"));
    }

    [Fact]
    public void ADamagedUsageFileReadsAsNothingUsed()
    {
        var file = WriteFile("usage.json", "{ not json");
        Assert.Empty(LibraryUsage.ReadFile(file));
        Assert.Empty(LibraryUsage.ReadFile(PathFor("missing.json")));
    }

    // MARK: Dates

    [Fact]
    public void DatesComeFromTheFilesAndASaveMovesOnlyTheModifiedOne()
    {
        var library = NewLibrary();
        var song = Song("s");
        library.Save(song);
        var path = library.DocumentPath(typeof(Presentation), "s");
        var added = DateTime.UtcNow.AddDays(-30);
        var modified = DateTime.UtcNow.AddDays(-2);
        File.SetCreationTimeUtc(path, added);
        File.SetLastWriteTimeUtc(path, modified);

        var dates = new LibraryDates(library);
        dates.Load();
        Assert.True(dates.IsLoaded);
        Assert.Equal(added, dates.AddedAt(typeof(Presentation), "s")!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
        Assert.Equal(modified, dates.UpdatedAt(typeof(Presentation), "s")!.Value.UtcDateTime, TimeSpan.FromSeconds(1));

        library.Save(song);
        Assert.Equal(added, dates.AddedAt(typeof(Presentation), "s")!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
        Assert.True(dates.UpdatedAt(typeof(Presentation), "s") > DateTimeOffset.UtcNow.AddMinutes(-1));
        // File.Replace (every save) keeps the file's creation date, which is what Date Added reads.
        Assert.Equal(added, File.GetCreationTimeUtc(path), TimeSpan.FromSeconds(1));

        library.Delete<Presentation>("s");
        Assert.Null(dates.UpdatedAt(typeof(Presentation), "s"));
    }

    [Fact]
    public void AMediaItemWasAddedWhenItsImportSaysSo()
    {
        var library = NewLibrary();
        var item = Media("m", "h");
        item.ImportedAt = 1_700_000_000;
        library.Save(item);
        var dates = new LibraryDates(library);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), dates.AddedAt(item));
    }

    // MARK: Batch rules

    [Fact]
    public void DuplicatesAreNamedAsTheMacNamesThem()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var song = Song("s");
        song.Name = "Amazing Grace";
        var copy = LibraryBatch.Duplicate(song, now);
        Assert.Equal("Amazing Grace Copy", copy.Name);
        Assert.NotEqual(song.Id, copy.Id);
        Assert.Equal(PresentationOriginSource.Duplicate, copy.Origin!.Source);
        Assert.Equal("of Amazing Grace", copy.Origin.Detail);
        Assert.Equal("2027-01-15T08:00:00Z", copy.Origin.CreatedAt);

        var media = LibraryBatch.Duplicate(Media("m", "h", "Ocean"), now);
        Assert.Equal("Ocean Copy", media.Name);
        Assert.Equal(1_800_000_000, media.ImportedAt);
        Assert.Equal("h", media.FileHash); // the same file, not a second one
    }

    [Fact]
    public void FilingClearsTheTeamFolderAndReportsNoChangeWhenAlreadyThere()
    {
        var song = Song("s");
        song.FolderId = "team-folder";
        Assert.True(LibraryBatch.File(song, "Hymns"));
        Assert.Equal("Hymns", song.Folder);
        Assert.Null(song.FolderId);
        Assert.False(LibraryBatch.File(song, "Hymns"));
        Assert.True(LibraryBatch.File(song, ""));
        Assert.Null(song.Folder);
        Assert.False(LibraryBatch.File(song, null));
    }

    [Fact]
    public void ApplyingAThemeClearsTheSlidesOwnThemesAndTheSameThemeResetsOverrides()
    {
        var song = Song("s");
        song.ThemeId = "old";
        song.Slides =
        [
            new Slide { Id = "1", Name = "", ThemeId = "other", Objects = [] },
            new Slide { Id = "2", Name = "", Objects = [new SlideObject { Id = "o", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = "x", X = 10, Y = 10, Width = 100, Height = 100 }] },
        ];
        Assert.True(LibraryBatch.ApplyTheme(song, "new"));
        Assert.Equal("new", song.ThemeId);
        Assert.Null(song.Slides[0].ThemeId);
        Assert.Equal(10, song.Slides[1].Objects[0].X); // a new theme leaves the slides' own positions alone

        Assert.True(LibraryBatch.ApplyTheme(song, "new")); // the same theme again: back to the theme's positions
        Assert.Null(song.Slides[1].Objects[0].X);
        Assert.False(LibraryBatch.ApplyTheme(song, "new")); // nothing left to reset
    }

    [Fact]
    public void DeletingWarnsAboutPresentationsStillInAService()
    {
        var library = NewLibrary();
        var song = Song("s");
        song.Name = "Amazing Grace";
        library.Save(song);
        library.Save(Song("free"));
        library.Save(new Service
        {
            Id = "sv", Name = "Sunday", ServiceDate = "2026-10-25",
            Items = [new ServiceItem { Id = "i", ItemKind = ServiceItemKind.Presentation, Name = "Amazing Grace", RefId = "s" }],
        });
        var used = LibraryBatch.ServicesUsing(library, ["s", "free"]);
        Assert.Equal("Amazing Grace", used.Single().PresentationName);
        Assert.Equal(["Sunday"], used.Single().Services);
    }

    [Fact]
    public void BatchExportNamesNeverClashOrOverwrite()
    {
        Assert.Equal(["Amazing Grace.pdf", "Amazing Grace 2.pdf", "Sunday- 9-14.pdf", "Untitled.pdf", "Oceans 2.pdf"],
            LibraryBatch.UniqueFileNames(["Amazing Grace", "Amazing Grace", "Sunday: 9/14", " ... ", "Oceans"], ".pdf", ["oceans.pdf"]));
        Assert.Equal("A-B", LibraryBatch.SafeFileStem("A?B."));
    }

    [Fact]
    public void ACloneLeavesTheOriginalAlone()
    {
        var song = Song("s");
        var copy = LibraryBatch.Duplicate(song, DateTimeOffset.UtcNow);
        copy.Slides.Add(new Slide { Id = "x", Name = "", Objects = [] });
        Assert.Empty(song.Slides);
        Assert.True(MxuJson.StructurallyEqual(song.Slides, new List<Slide>()));
    }
}
