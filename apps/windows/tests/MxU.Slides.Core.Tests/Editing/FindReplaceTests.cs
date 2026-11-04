using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Editing;

public sealed class FindReplaceTests
{
    private static Presentation Song(string id, params string[] texts) => new()
    {
        Id = id, Name = id, PresentationKind = PresentationKind.Song, ThemeId = "",
        Slides = [.. texts.Select((t, i) => new Slide
        {
            Id = $"{id}-s{i}", Name = "",
            Objects = [new SlideObject { Id = $"{id}-o{i}", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = t }],
        })],
    };

    [Fact]
    public void FindsEveryMatchInSlideOrderIgnoringCase()
    {
        var song = Song("grace", "Amazing grace how sweet\nthe sound", "Grace hath brought me safe");
        var matches = FindReplace.Find(song, new FindQuery("grace"));
        Assert.Equal([1, 2], matches.Select(m => m.SlideNumber));
        Assert.Equal(new TextStyleRuns.Range(8, 13), matches[0].Range);
        Assert.Equal("Amazing [grace] how sweet the sound", matches[0].Context);
        Assert.Single(FindReplace.Find(song, new FindQuery("Grace", MatchCase: true)));
    }

    [Fact]
    public void WholeWordsSkipsPartsOfLongerWords()
    {
        var song = Song("s", "the theme of thee, the end");
        Assert.Equal(4, FindReplace.Find(song, new FindQuery("the")).Count);
        Assert.Equal(2, FindReplace.Find(song, new FindQuery("the", WholeWords: true)).Count);
    }

    [Fact]
    public void ReplaceAllFollowsEachMatchsCapitalAndKeepsChords()
    {
        var song = Song("s", "Thee I adore\nI worship thee", "THEE alone");
        song.Slides[0].Objects[0].Chords = [new ChordPlacement { Line = 1, Column = 2, Symbol = "G" }];
        Assert.Equal(3, FindReplace.ReplaceAll(song, new FindQuery("thee", WholeWords: true), "you"));
        Assert.Equal("You I adore\nI worship you", song.Slides[0].Objects[0].Text);
        Assert.Equal("YOU alone", song.Slides[1].Objects[0].Text);
        Assert.Equal(2, song.Slides[0].Objects[0].Chords![0].Column); // "worship" didn't move
    }

    [Fact]
    public void ReplacingOneMatchChecksItIsStillThere()
    {
        var song = Song("s", "grace upon grace");
        var query = new FindQuery("grace");
        var matches = FindReplace.Find(song, query);
        Assert.True(FindReplace.Replace(song, matches[1], query, "mercy"));
        Assert.Equal("grace upon mercy", song.Slides[0].Objects[0].Text);
        Assert.False(FindReplace.Replace(song, matches[1], query, "mercy"), "the text has changed since");
    }

    [Fact]
    public void LinkedTextAndEmptyQueriesAreLeftAlone()
    {
        var song = Song("s", "grace");
        song.Slides[0].Objects.Add(new SlideObject { Id = "clock", ObjectKind = SlideObjectKind.Text, Name = "Clock", Text = "grace", TextLink = new TextLink() });
        Assert.Single(FindReplace.Find(song, new FindQuery("grace")));
        Assert.Empty(FindReplace.Find(song, new FindQuery("")));
        Assert.Equal(0, FindReplace.ReplaceAll(song, new FindQuery(""), "x"));
    }

    [Fact]
    public void SearchesManySongs()
    {
        var matches = FindReplace.Find([Song("a", "grace"), Song("b", "none"), Song("c", "more grace")], new FindQuery("grace"));
        Assert.Equal(["a", "c"], matches.Select(m => m.PresentationId));
    }
}
