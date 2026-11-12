using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

/// <summary>
/// Searching across the library: what matches (every word typed begins a word in the item, as the
/// Mac's full-text search does) and in which order (the Mac's tiers, then recent use).
/// </summary>
public sealed class LibrarySearchTests : ScratchFolderTest
{
    private static Presentation Song(string id, string name, string lyrics = "", string? author = null, int? ccli = null) => new()
    {
        Id = id, Name = name, PresentationKind = PresentationKind.Song, ThemeId = "",
        Ccli = author is null && ccli is null ? null : new CCLIInfo { Author = author, SongNumber = ccli },
        Slides = [.. lyrics.Split("\n\n").Select((stanza, i) => new Slide
        {
            Id = $"{id}-{i}", Name = "", Objects = [new SlideObject { Id = $"{id}-{i}-o", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = stanza }],
        })],
    };

    private LibraryCatalog Catalog(params Presentation[] songs)
    {
        var library = NewLibrary();
        foreach (var song in songs) library.Save(song);
        return new LibraryCatalog(library);
    }

    private static string[] Ids(IEnumerable<LibrarySearchHit> hits) => [.. hits.Select(h => h.Entry.Id)];

    [Fact]
    public void EveryWordTypedMustBeginAWordInTheItem()
    {
        var catalog = Catalog(Song("ag", "Amazing Grace"), Song("ga", "Great Are You Lord"), Song("ok", "Oceans"));
        Assert.Equal(["ag"], Ids(catalog.Search("amaz gr")));
        Assert.Empty(catalog.Search("mazing")); // the middle of a word doesn't count, as on the Mac
        Assert.Empty(catalog.Search("   "));
    }

    [Fact]
    public void CapitalsAccentsAndApostrophesDontMatter()
    {
        var catalog = Catalog(Song("t", "'Twas Grace", "'Twas grace that taught my heart to fear"), Song("j", "Jésus, Tu Es Là"));
        Assert.Equal(["t"], Ids(catalog.Search("twas")));
        Assert.Equal(["j"], Ids(catalog.Search("jesus tu")));
        Assert.Equal(["t"], Ids(catalog.Search("TAUGHT MY")));
    }

    [Fact]
    public void TitlesRankExactThenStartingWithThenAnywhere()
    {
        var catalog = Catalog(
            Song("lyric", "Holy Spirit", "Grace upon grace"),
            Song("inside", "His Amazing Grace"),
            Song("starts", "Grace Alone"),
            Song("exact", "Grace"));
        Assert.Equal(["exact", "starts", "inside", "lyric"], Ids(catalog.Search("grace")));
    }

    [Fact]
    public void ACcliNumberOrAnAuthorFindsTheSong()
    {
        var catalog = Catalog(Song("ag", "Amazing Grace", author: "John Newton", ccli: 22025), Song("o", "Oceans", author: "Hillsong United", ccli: 6428767));
        var byNumber = catalog.Search("22025");
        Assert.Equal(["ag"], Ids(byNumber));
        Assert.Equal(SearchField.Number, byNumber[0].Field);
        var byAuthor = catalog.Search("newton");
        Assert.Equal(["ag"], Ids(byAuthor));
        Assert.Equal(SearchField.Author, byAuthor[0].Field);
        Assert.Empty(catalog.Search("2202")); // a CCLI number matches whole, not as the start of another
    }

    [Fact]
    public void LyricHitsCarryTheLineTheyMatchedAndAPhraseBeatsScatteredWords()
    {
        var catalog = Catalog(
            Song("scattered", "Song A", "I once was blind\n\nbut lost in wonder"),
            Song("phrase", "Song B", "Amazing grace how sweet the sound\nThat saved a wretch like me\n\nI once was lost but now am found"));
        var hits = catalog.Search("once was lost");
        Assert.Equal(["phrase", "scattered"], Ids(hits));
        Assert.Equal(SearchField.Lyrics, hits[0].Field);
        Assert.Equal("I once was lost but now am found", hits[0].Snippet);
    }

    [Fact]
    public void ALongLyricLineIsCutAroundTheMatch()
    {
        var line = "This is a very long line of words that goes on and on before it finally reaches the word hallelujah and then keeps going for a while longer";
        var hit = Catalog(Song("s", "Long", line)).Search("hallelujah").Single();
        Assert.NotNull(hit.Snippet);
        Assert.StartsWith("…", hit.Snippet);
        Assert.Contains("hallelujah", hit.Snippet);
        Assert.True(hit.Snippet!.Length <= 75);
    }

    [Fact]
    public void WithinATierTheMostRecentlyUsedComesFirst()
    {
        var catalog = Catalog(Song("old", "Grace Old"), Song("new", "Grace New"), Song("never", "Grace Never"));
        catalog.Usage.Touch("old", DateTimeOffset.FromUnixTimeSeconds(100));
        catalog.Usage.Touch("new", DateTimeOffset.FromUnixTimeSeconds(200));
        Assert.Equal(["new", "old", "never"], Ids(catalog.Search("grace")));
    }

    [Fact]
    public void MediaIsFoundByNameFileNameAndTag()
    {
        var library = NewLibrary();
        var item = Media("m1", "hash1", "Ocean Waves");
        item.FileName = "IMG_2041 drone.mp4";
        item.Tags = ["Water", "Summer"];
        library.Save(item);
        library.Save(Song("s", "Oceans", "You call me out upon the waters"));
        var catalog = new LibraryCatalog(library);
        Assert.Equal(["m1", "s"], Ids(catalog.Search("ocean")));
        Assert.Equal(SearchField.FileName, catalog.Search("drone").Single().Field);
        Assert.Equal(SearchField.Tag, catalog.Search("summer").Single().Field);
    }

    [Fact]
    public void FiltersNarrowByKindFavouriteTagAndFolder()
    {
        var library = NewLibrary();
        var hymn = Song("h", "Grace Hymn");
        hymn.Folder = "Hymns/Classic";
        library.Save(hymn);
        library.Save(Song("m", "Grace Modern"));
        library.Save(Media("v", "hash", "Grace Loop"));
        var catalog = new LibraryCatalog(library);
        catalog.Marks.SetFavorite(LibraryItemKind.Song, "m", true);
        catalog.Marks.SetTags(LibraryItemKind.Song, "h", ["Easter"]);

        Assert.Equal(["v"], Ids(catalog.Search("grace", new LibrarySearchFilters(Kinds: new HashSet<LibraryItemKind> { LibraryItemKind.Media }))));
        Assert.Equal(["m"], Ids(catalog.Search("grace", new LibrarySearchFilters(FavoritesOnly: true))));
        Assert.Equal(["h"], Ids(catalog.Search("grace", new LibrarySearchFilters(Tag: "easter"))));
        Assert.Equal(["h"], Ids(catalog.Search("grace", new LibrarySearchFilters(Folder: "Hymns"))));
    }

    [Fact]
    public void ASavedEditIsSearchedAtOnce()
    {
        var library = NewLibrary();
        var song = Song("s", "Before");
        library.Save(song);
        var catalog = new LibraryCatalog(library);
        Assert.Single(catalog.Search("before"));
        song.Name = "After";
        library.Save(song);
        Assert.Empty(catalog.Search("before"));
        Assert.Single(catalog.Search("after"));
    }

    [Theory]
    [InlineData("'Twas Grace!", "twas grace")]
    [InlineData("  Él  Shaddai  ", "el shaddai")]
    [InlineData("10,000 Reasons (Bless the Lord)", "10 000 reasons bless the lord")]
    [InlineData("", "")]
    public void TextIsNormalisedForComparing(string text, string expected) => Assert.Equal(expected, LibrarySearch.Normalize(text));
}
