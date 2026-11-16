namespace MxU.Slides.OpenLpImport.Tests;

/// <summary>OpenLP's lyrics text → verses and slides (no database, no library: just strings).</summary>
public class OpenLpLyricsTests
{
    private static string Xml(params (string Type, string Label, string Text)[] verses) => Testing.OpenLpSampleData.Xml(verses);

    [Fact]
    public void XmlBecomesVersesWithTheirTypeAndLabel()
    {
        var parsed = OpenLpLyrics.Parse(Xml(("v", "1", "One\nTwo"), ("c", "1", "Three"), ("b", "2", "Four")));
        Assert.False(parsed.Unreadable);
        Assert.Empty(parsed.Warnings);
        Assert.Equal(['v', 'c', 'b'], parsed.Verses.Select(verse => verse.Type));
        Assert.Equal(["1", "1", "2"], parsed.Verses.Select(verse => verse.Label));
        Assert.Equal(["One\nTwo"], parsed.Verses[0].Slides);
    }

    [Theory]
    [InlineData("v", 'v')]
    [InlineData("V", 'v')]
    [InlineData("Chorus", 'c')]
    [InlineData("Pre-Chorus", 'p')]
    [InlineData("e", 'e')]
    [InlineData("custom", 'c')]
    [InlineData("zzz", 'o')]
    [InlineData("", 'o')]
    [InlineData(null, 'o')]
    public void OnlyTheFirstLetterOfAVerseTypeCounts(string? raw, char expected) => Assert.Equal(expected, OpenLpLyrics.VerseType(raw));

    [Fact]
    public void TheOptionalSplitStartsANewSlide()
    {
        var parsed = OpenLpLyrics.Parse(Xml(("v", "1", "One\nTwo\n[---]\nThree\nFour")));
        Assert.Equal(["One\nTwo", "Three\nFour"], parsed.Verses[0].Slides);
    }

    [Fact]
    public void TheForcedSplitStartsANewSlide()
    {
        var parsed = OpenLpLyrics.Parse(Xml(("v", "1", "One\n[--}{--]\nTwo\n[---]\nThree")));
        Assert.Equal(["One", "Two", "Three"], parsed.Verses[0].Slides);
        Assert.Empty(parsed.Warnings);
    }

    [Fact]
    public void ASplitInTheMiddleOfALineStillSplits()
    {
        var parsed = OpenLpLyrics.Parse(Xml(("v", "1", "One [---] Two")));
        Assert.Equal(["One", "Two"], parsed.Verses[0].Slides);
    }

    [Fact]
    public void BuiltInFormattingTagsAreDroppedAndTheirWordsKept()
    {
        var parsed = OpenLpLyrics.Parse(Xml(("v", "1", "{r}Red{/r} and {st}bold{/st} and {it}leaning{/it} {y}yellow{/y} {b}black{/b}{u}{/u}")));
        Assert.Equal(["Red and bold and leaning yellow black"], parsed.Verses[0].Slides);
        Assert.Empty(parsed.Warnings);
    }

    [Fact]
    public void BrIsALineBreak()
    {
        var parsed = OpenLpLyrics.Parse(Xml(("v", "1", "One{br}Two")));
        Assert.Equal(["One\nTwo"], parsed.Verses[0].Slides);
    }

    [Fact]
    public void AChurchsOwnTagIsDroppedWhenItIsOpenedAndClosed()
    {
        // The closing tag is in another verse: tags are paired across the whole song.
        var parsed = OpenLpLyrics.Parse(Xml(("v", "1", "{gold}Gold{/gold} here"), ("v", "2", "{sky}Blue"), ("v", "3", "ends{/sky}")));
        Assert.Equal(["Gold here", "Blue", "ends"], parsed.Verses.SelectMany(verse => verse.Slides));
        Assert.Empty(parsed.Warnings);
    }

    [Fact]
    public void ABraceWordThatIsNoTagIsKeptAndWarnedAboutOnce()
    {
        var parsed = OpenLpLyrics.Parse(Xml(("v", "1", "A {mystery} here\nand {mystery} there")));
        Assert.Equal(["A {mystery} here\nand {mystery} there"], parsed.Verses[0].Slides);
        var warning = Assert.Single(parsed.Warnings);
        Assert.Contains("{mystery}", warning);
    }

    [Fact]
    public void ChordsAreLeftInTheWordsForTheLyricImporter()
    {
        var parsed = OpenLpLyrics.Parse(Xml(("v", "1", "[C]Sing [G/B]out")));
        Assert.Equal(["[C]Sing [G/B]out"], parsed.Verses[0].Slides);
    }

    [Fact]
    public void PlainTextIsOneVersePerBlock()
    {
        var parsed = OpenLpLyrics.Parse("First line\r\nSecond line\r\n\r\nThird line\n\n\n");
        Assert.False(parsed.Unreadable);
        Assert.Equal(["v1", "v2"], parsed.Verses.Select(verse => verse.Type + verse.Label));
        Assert.Equal(["First line\nSecond line", "Third line"], parsed.Verses.SelectMany(verse => verse.Slides));
    }

    [Theory]
    [InlineData("<song version=\"1.0\"><lyrics><verse type=\"v\" label=\"1\"><![CDATA[Half a page")]
    [InlineData("<song><lyrics><verse>one</lyrics></song>")]
    [InlineData("<!DOCTYPE song [<!ENTITY x \"y\">]><song><lyrics><verse type=\"v\" label=\"1\">&x;</verse></lyrics></song>")]
    public void DamagedXmlIsReportedNotThrown(string raw)
    {
        var parsed = OpenLpLyrics.Parse(raw);
        Assert.True(parsed.Unreadable);
        Assert.Empty(parsed.Verses);
        Assert.Single(parsed.Warnings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void NoTextIsNoVerses(string? raw)
    {
        var parsed = OpenLpLyrics.Parse(raw);
        Assert.False(parsed.Unreadable);
        Assert.Empty(parsed.Verses);
    }

    [Fact]
    public void AVerseWithNoLabelIsNumberOne()
    {
        var parsed = OpenLpLyrics.Parse("<song><lyrics><verse type=\"c\">Words</verse></lyrics></song>");
        Assert.Equal("1", Assert.Single(parsed.Verses).Label);
    }

    [Fact]
    public void YorubaLettersComeThroughExactly()
    {
        const string line = "Ẹ̀mí mi, yin Ọlọ́run ṣé";
        var parsed = OpenLpLyrics.Parse(Xml(("v", "1", line)));
        Assert.Equal(line, parsed.Verses[0].Slides[0], StringComparer.Ordinal);
    }
}
