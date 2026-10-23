using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Lyrics;

public class LyricPhrasingTests
{
    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    [Fact]
    public void WrappedChartLinesRejoinTheLineAbove()
    {
        string[] lines =
        [
            "I cannot describe it",
            "But nothing is brighter",
            "[G]Than this",
            "Out in the open sky",
            "And my heart found the",
            "Most High",
            "It has never been about",
            "Perf[D]ormance only",
            "Everything for the glory",
            "Of Your name",
        ];
        Assert.Equal(new[]
        {
            "I cannot describe it",
            "But nothing is brighter [G]than this",
            "Out in the open sky",
            "And my heart found the Most High",
            "It has never been about perf[D]ormance only",
            "Everything for the glory of Your name",
        }, LyricPhrasing.Rejoined(lines, new LyricPhrasing.Casing(string.Join("\n", lines))));
    }

    [Fact]
    public void LinesThatStandAloneDoNotRejoin()
    {
        string[] lines =
        [
            "The person I was",
            "Before I found the way",
            "Sing it out",
            "Sing it out",
            "Oh",
            "We are going higher than the mountains ever were",
            "Of every nation",
            "This is the end.",
            "Of it",
        ];
        Assert.Equal(lines, LyricPhrasing.Rejoined(lines, new LyricPhrasing.Casing("")));
    }

    [Fact]
    public void ShortChartLinesThatStartAPhraseKeepTheChartsBreaks()
    {
        string[] verse1 =
        [
            "[G]Who else would rocks", "Cry out to [G]worship", "[G]Whose glory [C6]taught",
            "The stars to [G]shine", "[C2]Perhaps creation [D]longs", "To [Em]have the [D]words to [C2]sing",
            "[C2]But this [D]joy is [G]mine",
        ];
        Assert.Equal(verse1, LyricPhrasing.Rejoined(verse1, new LyricPhrasing.Casing(string.Join("\n", verse1))));
        Assert.Equal(new[] { 2, 2, 2, 1 }, LyricPhrasing.Slides(verse1, perSlide: 2).Select(s => s.Count));
        string[] verse2 = ["Who else would die", "For our redemption", "Whose resurrection", "Means I’ll rise"];
        Assert.Equal(verse2, LyricPhrasing.Rejoined(verse2, new LyricPhrasing.Casing("")));
        string[] jesus = ["I don’t want anything but You", "You’re more than every dream", "Come true", "All of the things I thought I wanted"];
        Assert.Equal("You’re more than every dream come true", LyricPhrasing.Rejoined(jesus, new LyricPhrasing.Casing(""))[1]);
    }

    [Fact]
    public void ALineEndingOnIsRunsOnSoTheChorusKeepsItsPairs()
    {
        string[] chorus =
        [
            "[D2]With a thousand [A]hallelujahs", "[Eadd4]We magni[F#m7]fy Your [Eadd4]name",
            "[D2]You alone de[A]serve the glory", "[Eadd4]The honor [F#m7]and the [Eadd4]praise",
            "[D2]Lord Jesu[Eadd4]s", "This song is", "For[A]e[A/C#]ver [F#m7]Yours[Eadd4]",
            "A [D2]thousand halle[A/C#]lujahs", "And a [Eadd4]thousand [A]more",
        ];
        var lines = LyricPhrasing.Rejoined(chorus, new LyricPhrasing.Casing(string.Join("\n", chorus)));
        Assert.Equal(new[]
        {
            "[D2]Lord Jesu[Eadd4]s", "This song is for[A]e[A/C#]ver [F#m7]Yours[Eadd4]",
            "A [D2]thousand halle[A/C#]lujahs", "And a [Eadd4]thousand [A]more",
        }, lines.Skip(4));
        Assert.Equal(new[] { 2, 2, 2, 2 }, LyricPhrasing.Slides(lines, perSlide: 2).Select(s => s.Count));
    }

    [Fact]
    public void SlidesBreakWherePhrasesEndNotEveryTwoLines()
    {
        string[] lines =
        [
            "We lift our eyes",
            "We see the morning come",
            "Only by grace",
            "We never earned the light",
            "Only by grace",
        ];
        Assert.Equal(new[]
        {
            new[] { "We lift our eyes", "We see the morning come" },
            ["Only by grace"],
            ["We never earned the light", "Only by grace"],
        }, Slides(lines, perSlide: 2));
        Assert.Equal(
            new[] { new[] { "Walk into the", "Light of day" }, ["Run"] },
            Slides(["Walk into the", "Light of day", "Run"], perSlide: 2));
        Assert.Equal(new[] { new[] { "One", "Two" } }, Slides(["One", "Two"], perSlide: 2));
        Assert.Empty(LyricPhrasing.Slides([], perSlide: 2));
    }

    private static string[][] Slides(string[] lines, int perSlide) =>
        LyricPhrasing.Slides(lines, perSlide).Select(slide => slide.ToArray()).ToArray();

    [Fact]
    public void PhrasedReflowRejoinsAndSplitsByPhrase()
    {
        var text = Lf("""
            Verse 1
            [D]I cannot describe it
            But nothing is [A]brighter
            Than [G]this
            Out in the open sky
            """);
        var phrased = Reflow.Parse(text, linesPerSlide: 2, phrased: true);
        Assert.Equal(new[]
        {
            "[D]I cannot describe it\nBut nothing is [A]brighter than [G]this",
            "Out in the open sky",
        }, phrased.Sections[0].Slides);
        Assert.Equal(2, Reflow.Parse(text, linesPerSlide: 2).Sections[0].Slides.Count);
        Assert.Equal("Than [G]this\nOut in the open sky", Reflow.Parse(text, linesPerSlide: 2).Sections[0].Slides[1]);
        Assert.Equal(Lf("""
            Verse 1
            [D]I cannot describe it
            But nothing is [A]brighter than [G]this

            Out in the open sky
            """), Reflow.Materialize(text, linesPerSlide: 2, phrased: true));
    }

    [Fact]
    public void TypedLyricsKeepTheirBreaksChartsSplitByPhrase()
    {
        var words = "I cannot describe it\nBut nothing is brighter\nThan this\nOut in the open sky";
        var typed = LyricTextImporter.MakePresentation(words, linesPerSlide: 2);
        Assert.Equal(new[]
        {
            "I cannot describe it\nBut nothing is brighter", "Than this\nOut in the open sky",
        }, typed.Slides.Select(s => s.Objects.FirstOrDefault()?.Text).OfType<string>());

        var chart = LyricTextImporter.MakePresentation("{title: Bright}\n[D]" + words, linesPerSlide: 2);
        Assert.Equal(new[]
        {
            "I cannot describe it\nBut nothing is brighter than this", "Out in the open sky",
        }, chart.Slides.Select(s => s.Objects.FirstOrDefault()?.Text).OfType<string>());
        Assert.Equal("Verse 1\n[D]I cannot describe it\nBut nothing is brighter than this\n\nOut in the open sky", chart.ReflowSource);
        Assert.Equal(chart.ReflowSource, ChordProExport.ReflowSeed(chart));
        Assert.Equal(words, typed.ReflowSource);
    }

    [Fact]
    public void JoinLinesPullsTheNextLineUpAcrossASlideBreak()
    {
        var text = "Verse 1\nBut nothing is [A]brighter\n\n[G]Than this\nOut in the open sky";
        var caret = "Verse 1\nBut noth".Length;
        var edit = LyricPhrasing.JoinEdit(caret, text);
        Assert.Equal("Verse 1\n".Length, edit?.Location);
        Assert.Equal("But nothing is [A]brighter\n\n[G]Than this".Length, edit?.Length);
        Assert.Equal("But nothing is [A]brighter [G]than this", edit?.Replacement);
        Assert.Null(LyricPhrasing.JoinEdit(0, text));
        Assert.Null(LyricPhrasing.JoinEdit(text.Length, text));
        Assert.Null(LyricPhrasing.JoinEdit(9, "Verse 1\nWords here\nChorus\nMore"));
    }

    [Fact]
    public void ReflowSplitsImportedDecksByPhraseButNotTypedOnes()
    {
        static PresentationOrigin Origin(PresentationOriginSource source) => new() { Source = source };
        Assert.True(LyricPhrasing.Applies(Origin(PresentationOriginSource.SongSelect), "Words"));
        Assert.True(LyricPhrasing.Applies(Origin(PresentationOriginSource.ChartFile), "Words"));
        Assert.True(LyricPhrasing.Applies(Origin(PresentationOriginSource.PastedLyrics), "[G]Words\n[C]More"));
        Assert.False(LyricPhrasing.Applies(Origin(PresentationOriginSource.PastedLyrics), "Words\nMore"));
        Assert.False(LyricPhrasing.Applies(Origin(PresentationOriginSource.ProPresenter), "Words"));
        Assert.False(LyricPhrasing.Applies(null, "Words"));
    }
}
