using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Lyrics;

public class ChordChartTextTests
{
    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    private readonly string chart = Lf("""
        Amazing Grace
        Key: G

        Intro: G  C  G  D

        Verse 1
        G       C         G
        Amazing grace how sweet the sound
             G       D      G
        That saved a wretch like me

        Chorus
        | G  /  / | C  /  / |
        I once was lost
        """);

    [Fact]
    public void ChordLinesFoldIntoTheWordsBeneathThem()
    {
        var inlined = ChordChartText.Inlined(chart);

        Assert.Equal("G", inlined.MusicKey);
        Assert.Equal(12, inlined.ChordCount);
        Assert.Contains("[G]Amazing [C]grace how [G]sweet the sound", inlined.Text);
        Assert.Contains("That [G]saved a [D]wretch [G]like me", inlined.Text);

        Assert.Contains("Intro\n[G][C][G][D]", inlined.Text);
        Assert.Contains("I [G]once was l[C]ost", inlined.Text);
        Assert.DoesNotContain("Key:", inlined.Text);
    }

    [Fact]
    public void ImportedChartLandsCleanTextAndPlacements()
    {
        Assert.Equal(LyricTextFormat.ChordChart, LyricTextImporter.DetectFormat(chart));
        var presentation = LyricTextImporter.MakePresentation(chart, linesPerSlide: 2);

        Assert.Equal("Amazing Grace", presentation.Name);
        Assert.Equal("G", presentation.MusicKey);
        var verse = presentation.Slides.First(slide => slide.Objects.FirstOrDefault()?.Text.StartsWith("Amazing", StringComparison.Ordinal) == true);
        var lyrics = verse.Objects[0];
        Assert.Equal("Amazing grace how sweet the sound\nThat saved a wretch like me", lyrics.Text);
        Assert.Equal(new[]
        {
            (0, 0, "G"), (0, 8, "C"), (0, 18, "G"),
            (1, 5, "G"), (1, 13, "D"), (1, 20, "G"),
        }, Shape(lyrics.Chords));
    }

    [Fact]
    public void SongSelectChordSheetKeepsChordsAndItsFooter()
    {
        var sheet = Lf("""
            Amazing Grace (My Chains Are Gone)

            Verse 1
            G              C          G
            Amazing grace how sweet the sound

            CCLI Song # 4768151
            Chris Tomlin | John Newton | Louie Giglio
            """);
        Assert.Equal(LyricTextFormat.SongSelect, LyricTextImporter.DetectFormat(sheet));
        var presentation = LyricTextImporter.MakePresentation(sheet);
        var lyrics = presentation.Slides.First(slide => slide.Objects.Count > 0).Objects[0];

        Assert.Equal(4_768_151, presentation.Ccli?.SongNumber);
        Assert.Equal("Amazing grace how sweet the sound", lyrics.Text);
        Assert.Equal(new[] { "G", "C", "G" }, lyrics.Chords?.Select(c => c.Symbol));
    }

    [Fact]
    public void LyricsAndLabelsAreNeverChordLines()
    {
        foreach (var line in new[] { "Verse 1", "Chorus", "Amazing grace", "Be still", "A mighty fortress", "Oh oh oh" })
        {
            Assert.True(ChordChartText.ChordTokens(line) is null, $"'{line}'");
        }
        var plain = "Verse 1\nAmazing grace how sweet the sound\nThat saved a wretch like me";
        Assert.Equal(LyricTextFormat.PlainText, LyricTextImporter.DetectFormat(plain));
        Assert.Equal(plain, ChordChartText.Inlined(plain).Text);

        Assert.Equal(LyricTextFormat.ChordPro, LyricTextImporter.DetectFormat("[G]Amazing grace\n[C]How sweet"));
    }

    [Fact]
    public void KeyLinesReadWithOrWithoutASeparator()
    {
        var cases = new Dictionary<string, string>
        {
            ["Key G"] = "G", ["Key: G"] = "G", ["Key - Bb"] = "Bb", ["Key of Em"] = "Em", ["key g"] = "G", ["KEY = F#m"] = "F#m",
        };
        foreach (var (line, key) in cases)
        {
            var inlined = ChordChartText.Inlined($"{line}\nG       C\nAmazing grace");
            Assert.True(key == inlined.MusicKey, $"'{line}'");
            Assert.False(inlined.Text.Contains(line), $"'{line}' leaves the body");
        }
    }

    [Fact]
    public void ChordsPastTheWordsLandAtTheEndInOrder()
    {
        Assert.Equal("[G]Short[C][D]", ChordChartText.Merging([(0, "G"), (12, "C"), (16, "D")], "Short"));
    }

    private static (int Line, int Column, string Symbol)[] Shape(IEnumerable<ChordPlacement>? chords) =>
        (chords ?? []).Select(c => (c.Line, c.Column, c.Symbol)).ToArray();
}
