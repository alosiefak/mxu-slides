using MxU.Slides.Core.Model;
using MxU.Slides.OpenLpImport.Testing;

namespace MxU.Slides.OpenLpImport.Tests;

/// <summary>One OpenLP row → one of the app's documents.</summary>
public class OpenLpSongMapperTests
{
    private static OpenLpSong Song(string name, OpenLpShape shape = OpenLpShape.V3)
    {
        var sample = OpenLpSampleData.Songs(shape).Single(song => song.Title == name);
        return new OpenLpSong(1, sample.Title, sample.AlternateTitle, sample.Lyrics, sample.VerseOrder, sample.Copyright, sample.Ccli, sample.Authors ?? []);
    }

    private static OpenLpSong Song(string lyrics, string verseOrder) => new(1, "A Test Song", null, lyrics, verseOrder, null, null, []);

    private static List<string> SectionNames(Presentation song) => (song.Sections ?? []).Select(section => section.Name).ToList();

    /// <summary>The sections in the order the default arrangement plays them.</summary>
    private static List<string> SungOrder(Presentation song)
    {
        var arrangement = Assert.Single(song.Arrangements ?? []);
        Assert.Equal(arrangement.Id, song.DefaultArrangementId);
        return arrangement.SectionIds.Select(id => song.Sections!.Single(section => section.Id == id).Name).ToList();
    }

    private static string AllText(Presentation song) =>
        string.Join("\n", song.Slides.SelectMany(slide => slide.Objects).Select(o => o.Text));

    private static List<string> SlideTexts(Presentation song, string sectionName)
    {
        var section = song.Sections!.Last(candidate => candidate.Name == sectionName);
        return song.Slides.Where(slide => slide.SectionId == section.Id).Select(slide => string.Join("\n", slide.Objects.Select(o => o.Text))).ToList();
    }

    [Fact]
    public void AnOrdinarySongKeepsItsTitleCreditsAndSections()
    {
        var mapped = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.MorningLight));
        var song = Assert.IsType<Presentation>(mapped.Presentation);
        Assert.Empty(mapped.Warnings);
        Assert.Equal(OpenLpSampleData.MorningLight, song.Name);
        Assert.Equal(PresentationKind.Song, song.PresentationKind);
        Assert.Equal(PresentationOriginSource.LyricsFile, song.Origin?.Source);
        Assert.Equal("OpenLP", song.Origin?.Detail);
        // The lyric importer opens every song on a blank slide, as it does for SongSelect.
        Assert.Equal(["Intro", "Verse 1", "Chorus", "Verse 2", "Bridge"], SectionNames(song));
        Assert.Equal(7654321, song.Ccli?.SongNumber);
        Assert.Equal("Ada Testwell, Bayo Sample", song.Ccli?.Author);
        Assert.Equal("© 2019 Made-Up Hymns Ltd\nUsed for testing only", song.Ccli?.Copyright);
        Assert.Equal(2019, song.Ccli?.CopyrightYear);
        Assert.Equal(OpenLpSampleData.MorningLight, song.Ccli?.SongTitle);
        Assert.Contains("Morning light has come again", AllText(song));
        Assert.All(song.Slides, slide => Assert.Equal("Lyrics", slide.ThemeSlideName));
    }

    [Fact]
    public void TheVerseOrderBecomesTheArrangement()
    {
        var song = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.MorningLight)).Presentation!;
        Assert.Equal(["Intro", "Verse 1", "Chorus", "Verse 2", "Chorus", "Bridge", "Chorus"], SungOrder(song));
        Assert.Equal(OpenLpSongMapper.ArrangementName, song.Arrangements![0].Name);
    }

    [Fact]
    public void AVerseOrderNamingAMissingVerseLeavesThatStepOutAndSaysSo()
    {
        var mapped = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.MissingVerse));
        Assert.Equal(["Intro", "Verse 1", "Chorus", "Chorus"], SungOrder(mapped.Presentation!));
        Assert.Contains(mapped.Warnings, warning => warning.Contains("\"v3\""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1 v2")]
    [InlineData("V1, V2")]
    public void NoVerseOrderOrOneThatChangesNothingMakesNoArrangement(string verseOrder)
    {
        var mapped = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.Xml(("v", "1", "One line here"), ("v", "2", "Two lines there")), verseOrder));
        Assert.Null(mapped.Presentation!.Arrangements);
        Assert.Null(mapped.Presentation.DefaultArrangementId);
        Assert.Empty(mapped.Warnings);
    }

    [Fact]
    public void AVerseOrderThatLeavesAVerseOutKeepsTheVerseButDoesNotSingIt()
    {
        var song = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.Xml(("v", "1", "One line here"), ("v", "2", "Two lines there"), ("c", "1", "Sing")), "c v1 c")).Presentation!;
        Assert.Equal(["Intro", "Verse 1", "Verse 2", "Chorus"], SectionNames(song));
        Assert.Equal(["Intro", "Chorus", "Verse 1", "Chorus"], SungOrder(song));
    }

    [Fact]
    public void EveryVerseTypeGetsItsName()
    {
        var mapped = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.EveryKind));
        var song = mapped.Presentation!;
        Assert.Empty(mapped.Warnings);
        // The song's own Intro has words, so the blank opening slide is called "Blank".
        Assert.Equal(["Blank", "Intro", "Verse 1", "Pre-Chorus", "Chorus 1", "Chorus 2", "Other", "Ending"], SectionNames(song));
        Assert.Equal(["Blank", "Intro", "Verse 1", "Pre-Chorus", "Chorus 1", "Chorus 2", "Other", "Ending", "Chorus 2"], SungOrder(song));
    }

    [Fact]
    public void TwoVersesStoredUnderOneNameAreBothKeptAndBothSung()
    {
        var song = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.Xml(("v", "1", "First words"), ("v", "1", "Other words"), ("c", "1", "Sing")), "v1 c1")).Presentation!;
        Assert.Equal(["Intro", "Verse 1", "Verse 1 (2)", "Chorus"], SectionNames(song));
        Assert.Equal(["Intro", "Verse 1", "Verse 1 (2)", "Chorus"], SectionNames(song));
        Assert.Null(song.Arrangements); // v1 plays both, then the chorus: the stored order.
    }

    [Fact]
    public void SplitMarksStartNewSlides()
    {
        var song = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.ForcedSplit), linesPerSlide: 4).Presentation!;
        Assert.Equal(["The first half stands alone", "The second half replies"], SlideTexts(song, "Verse 1"));

        var rivers = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.Rivers), linesPerSlide: 4).Presentation!;
        var slides = SlideTexts(rivers, "Verse 1");
        Assert.Equal(2, slides.Count);
        Assert.StartsWith("Rivers of kindness", slides[0]);
        Assert.StartsWith("Fields by the water", slides[1]);
        Assert.DoesNotContain("[---]", AllText(rivers));
    }

    [Fact]
    public void FormattingTagsNeverReachTheSlides()
    {
        var mapped = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.Painted), linesPerSlide: 4);
        var text = AllText(mapped.Presentation!);
        Assert.Contains("Red is the first of the colours", text);
        Assert.Contains("Bold and leaning the next", text);
        Assert.Contains("Then yellow arrives", text);
        Assert.Contains("Gold is our own little colour", text);
        Assert.Contains("A {mystery} sits in the line", text);
        Assert.DoesNotContain("{r}", text);
        Assert.DoesNotContain("{/", text);
        Assert.DoesNotContain("{br}", text);
        Assert.Contains("{mystery}", Assert.Single(mapped.Warnings));
    }

    [Fact]
    public void ChordsAreKeptAsChordsAndNeverShownAsWords()
    {
        var song = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.Chords), linesPerSlide: 4).Presentation!;
        var text = AllText(song);
        Assert.DoesNotContain("[", text);
        Assert.Contains("Strings and keys together", text);
        var chords = song.Slides.SelectMany(slide => slide.Objects).SelectMany(o => o.Chords ?? []).Select(chord => chord.Symbol).ToList();
        Assert.Equal(["C", "G/B", "Am", "F", "C", "F", "G", "C"], chords);
    }

    [Fact]
    public void ALineWhollyInBracesIsNotLost()
    {
        var mapped = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.Xml(("v", "1", "{whisper this}\nand sing that")), ""));
        Assert.Contains("(whisper this)", AllText(mapped.Presentation!));
        Assert.Contains(mapped.Warnings, warning => warning.Contains("round brackets"));
    }

    [Fact]
    public void ALyricLineThatReadsLikeASectionNameIsWarnedAbout()
    {
        var mapped = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.Xml(("v", "1", "Sing this\nChorus\nand this")), ""));
        Assert.Contains(mapped.Warnings, warning => warning.Contains("section name"));
    }

    [Fact]
    public void PlainTextLyricsBecomeNumberedVerses()
    {
        var mapped = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.PlainText));
        Assert.Equal(["Intro", "Verse 1", "Verse 2"], SectionNames(mapped.Presentation!));
        Assert.Contains("The second verse comes after", AllText(mapped.Presentation!));
    }

    [Fact]
    public void DamagedXmlAndEmptyLyricsMakeNoSongAndSayWhy()
    {
        var broken = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.Broken));
        Assert.Null(broken.Presentation);
        Assert.Contains("damaged XML", Assert.Single(broken.Warnings));

        var empty = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.Empty));
        Assert.Null(empty.Presentation);
        Assert.Contains("no words", Assert.Single(empty.Warnings));
    }

    [Fact]
    public void ASongWithoutCreditsHasNoCreditsBlock()
    {
        var song = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.NoCredits)).Presentation!;
        Assert.Null(song.Ccli);
        Assert.Null(song.Arrangements);
    }

    [Theory]
    [InlineData("CCLI 1234567", 1234567)]
    [InlineData(" 42 ", 42)]
    [InlineData("none", null)]
    [InlineData("0", null)]
    [InlineData("99999999999999999999", null)]
    public void TheCcliNumberIsReadFromWhateverWasTyped(string typed, int? expected)
    {
        var song = OpenLpSongMapper.MapSong(new OpenLpSong(1, "T", null, OpenLpSampleData.Xml(("v", "1", "Words here")), null, "© Someone", typed, [])).Presentation!;
        Assert.Equal(expected, song.Ccli?.SongNumber);
    }

    [Fact]
    public void YorubaTitleAndWordsSurviveExactly()
    {
        var song = OpenLpSongMapper.MapSong(Song(OpenLpSampleData.Yoruba)).Presentation!;
        Assert.Equal(OpenLpSampleData.Yoruba, song.Name, StringComparer.Ordinal);
        Assert.Equal("Fọlákẹ́ Àpẹẹrẹ", song.Ccli?.Author, StringComparer.Ordinal);
        Assert.Contains(OpenLpSampleData.YorubaLine, AllText(song), StringComparison.Ordinal);
        Assert.Contains("Ọpẹ́ ni fún Ọ lónìí", AllText(song), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUntitledSongGetsANameThatSaysWhereItCameFrom()
    {
        var song = OpenLpSongMapper.MapSong(new OpenLpSong(17, "  ", null, OpenLpSampleData.Xml(("v", "1", "Words here")), null, null, null, [])).Presentation!;
        Assert.Equal("Untitled song 17 (OpenLP)", song.Name);
    }

    [Fact]
    public void ACustomSlideBecomesAPresentationInItsOwnFolder()
    {
        var sample = OpenLpSampleData.CustomSlides.Single(slide => slide.Title == OpenLpSampleData.Liturgy);
        var mapped = OpenLpSongMapper.MapCustom(new OpenLpCustomSlide(1, sample.Title, sample.Text, sample.Credits));
        var deck = mapped.Presentation!;
        Assert.Empty(mapped.Warnings);
        Assert.Equal(PresentationKind.Deck, deck.PresentationKind);
        Assert.Equal("OpenLP custom slides", deck.Folder);
        Assert.Equal(OpenLpSampleData.Liturgy, deck.Name);
        // One slide per OpenLP slide, words exactly as typed (no lyric phrasing).
        Assert.Equal(
            ["Leader: This is the day we gather.\nAll: We are glad to be here.", "Leader: Let every voice be heard.\nAll: Let every heart be glad."],
            deck.Slides.Select(slide => slide.Objects.Single().Text));
        Assert.Null(deck.Sections);
    }

    [Fact]
    public void ACustomSlideSplitsAtASplitMarkAndLosesItsTags()
    {
        var sample = OpenLpSampleData.CustomSlides.Single(slide => slide.Title == OpenLpSampleData.Notices);
        var deck = OpenLpSongMapper.MapCustom(new OpenLpCustomSlide(1, sample.Title, sample.Text, sample.Credits)).Presentation!;
        Assert.Equal(["This week\nChoir practice on Thursday at 6", "Bring a friend next Sunday"], deck.Slides.Select(slide => slide.Objects.Single().Text));
    }
}
