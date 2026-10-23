using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Lyrics;

public class ChordMathTests
{
    [Fact]
    public void DisplayKeyFromPlayedKeySpellsFromTheChartAndCrossesModesByRelative()
    {
        Assert.Equal("A", ChordMath.DisplayKey("A", musicKey: "G"));
        Assert.Equal("Bb", ChordMath.DisplayKey("A#", musicKey: "G"));
        Assert.Equal("G", ChordMath.DisplayKey("G", musicKey: "G"));
        Assert.Equal("G", ChordMath.DisplayKey("Em", musicKey: "G"));
        Assert.Equal("A", ChordMath.DisplayKey("F#m", musicKey: "G"));
        Assert.Equal("Am", ChordMath.DisplayKey("C", musicKey: "Am"));
        Assert.Equal("Bbm", ChordMath.DisplayKey("Bbm", musicKey: "F#m"));
        Assert.Null(ChordMath.DisplayKey("?", musicKey: "G"));
        Assert.Null(ChordMath.DisplayKey("A", musicKey: ""));
    }

    [Fact]
    public void ParsesSymbols()
    {
        Assert.Equal(new ChordMath.Symbol("C"), ChordMath.ParseSymbol("C"));
        Assert.Equal(new ChordMath.Symbol("F#", Suffix: "m7"), ChordMath.ParseSymbol("F#m7"));
        Assert.Equal(new ChordMath.Symbol("G", Bass: "B"), ChordMath.ParseSymbol("G/B"));
        Assert.Equal(new ChordMath.Symbol("Bb", Suffix: "sus4"), ChordMath.ParseSymbol("Bbsus4"));
        Assert.Equal(new ChordMath.Symbol("C", Suffix: "maj7", Bass: "E"), ChordMath.ParseSymbol("Cmaj7/E"));
        Assert.Equal(new ChordMath.Symbol("A", Suffix: "7b5"), ChordMath.ParseSymbol("A7b5"));
    }

    [Fact]
    public void RejectsNonChords()
    {
        foreach (var text in new[] { "x2", "Repeat", "Bridge", "Down", "Go", "Hm", "" })
        {
            Assert.False(ChordMath.IsChordToken(text), $"'{text}' should not parse");
        }
        foreach (var text in new[] { "E", "Am", "Db", "G7", "F#m7/A" })
        {
            Assert.True(ChordMath.IsChordToken(text), $"'{text}' should parse");
        }
    }

    [Fact]
    public void ParsesKeys()
    {
        Assert.Equal((0, false), ChordMath.ParseKey("C"));
        Assert.Equal((10, false), ChordMath.ParseKey("Bb"));
        Assert.Equal((6, true), ChordMath.ParseKey("F#m"));
        Assert.Equal((9, true), ChordMath.ParseKey("Am"));
        Assert.Null(ChordMath.ParseKey("nonsense"));
    }

    [Fact]
    public void TransposesWithKeySpelling()
    {
        Assert.Equal("D", ChordMath.Display("C", musicKey: "G", displayKey: "A", ChordNotation.Chords));
        Assert.Equal("G#m", ChordMath.Display("F#m", musicKey: "G", displayKey: "A", ChordNotation.Chords));

        Assert.Equal("C", ChordMath.Display("A", musicKey: "C", displayKey: "Eb", ChordNotation.Chords));
        Assert.Equal("D", ChordMath.Display("B", musicKey: "C", displayKey: "Eb", ChordNotation.Chords));
        Assert.Equal("G", ChordMath.Display("E", musicKey: "C", displayKey: "Eb", ChordNotation.Chords));
        Assert.Equal("Ab", ChordMath.Display("F", musicKey: "C", displayKey: "Eb", ChordNotation.Chords));

        Assert.Equal("C#", ChordMath.Display("C", musicKey: "C", displayKey: "C#", ChordNotation.Chords));

        Assert.Equal("A/C#", ChordMath.Display("G/B", musicKey: "G", displayKey: "A", ChordNotation.Chords));

        Assert.Equal("Dsus4", ChordMath.Display("Csus4", musicKey: "C", displayKey: "D", ChordNotation.Chords));
    }

    [Fact]
    public void NoKeysMeansNoTransposition()
    {
        Assert.Equal("F#m7", ChordMath.Display("F#m7", musicKey: null, displayKey: "A", ChordNotation.Chords));
        Assert.Equal("F#m7", ChordMath.Display("F#m7", musicKey: "A", displayKey: null, ChordNotation.Chords));
        Assert.Null(ChordMath.TransposeInterval(musicKey: "junk", displayKey: "A"));
    }

    [Fact]
    public void NashvilleNumbers()
    {
        Assert.Equal("1", ChordMath.Display("C", musicKey: "C", displayKey: null, ChordNotation.Numbers));
        Assert.Equal("6m7", ChordMath.Display("Am7", musicKey: "C", displayKey: null, ChordNotation.Numbers));
        Assert.Equal("4", ChordMath.Display("F", musicKey: "C", displayKey: null, ChordNotation.Numbers));
        Assert.Equal("b7", ChordMath.Display("Bb", musicKey: "C", displayKey: null, ChordNotation.Numbers));
        Assert.Equal("#4", ChordMath.Display("F#", musicKey: "C", displayKey: null, ChordNotation.Numbers));
        Assert.Equal("5/7", ChordMath.Display("G/B", musicKey: "C", displayKey: null, ChordNotation.Numbers));

        Assert.Equal("5", ChordMath.Display("D", musicKey: "G", displayKey: "Bb", ChordNotation.Numbers));

        Assert.Equal("1m", ChordMath.Display("Am", musicKey: "Am", displayKey: null, ChordNotation.Numbers));
    }

    [Fact]
    public void RomanNumerals()
    {
        Assert.Equal("I", ChordMath.Display("C", musicKey: "C", displayKey: null, ChordNotation.Numerals));
        Assert.Equal("vi", ChordMath.Display("Am", musicKey: "C", displayKey: null, ChordNotation.Numerals));
        Assert.Equal("vi7", ChordMath.Display("Am7", musicKey: "C", displayKey: null, ChordNotation.Numerals));
        Assert.Equal("Imaj7", ChordMath.Display("Cmaj7", musicKey: "C", displayKey: null, ChordNotation.Numerals));
        Assert.Equal("V/VII", ChordMath.Display("G/B", musicKey: "C", displayKey: null, ChordNotation.Numerals));
        Assert.Equal("bVII", ChordMath.Display("Bb", musicKey: "C", displayKey: null, ChordNotation.Numerals));
    }

    [Fact]
    public void DoReMi()
    {
        Assert.Equal("Do", ChordMath.Display("C", musicKey: "C", displayKey: null, ChordNotation.DoReMi));
        Assert.Equal("Sol", ChordMath.Display("G", musicKey: "C", displayKey: null, ChordNotation.DoReMi));
        Assert.Equal("Lam", ChordMath.Display("Am", musicKey: "C", displayKey: null, ChordNotation.DoReMi));
        Assert.Equal("Te", ChordMath.Display("Bb", musicKey: "C", displayKey: null, ChordNotation.DoReMi));
        Assert.Equal("Fi", ChordMath.Display("F#", musicKey: "C", displayKey: null, ChordNotation.DoReMi));
    }

    [Fact]
    public void DegreeNotationWithoutKeyFallsBackToLetters()
    {
        Assert.Equal("Am7", ChordMath.Display("Am7", musicKey: null, displayKey: null, ChordNotation.Numbers));
        Assert.Equal("Am7", ChordMath.Display("Am7", musicKey: null, displayKey: "D", ChordNotation.Numerals));
    }

    [Fact]
    public void KeyChoicesMatchMode()
    {
        Assert.Contains("Bb", ChordMath.KeyChoices("G"));
        Assert.DoesNotContain("Bbm", ChordMath.KeyChoices("G"));
        Assert.Equal(ChordMath.KeyChoices("G").Select(key => key + "m"), ChordMath.KeyChoices("Em"));
        Assert.Equal(12, ChordMath.KeyChoices("G").Count);
    }

    [Fact]
    public void DisplayPlacementsKeepAnchors()
    {
        ChordPlacement[] chords =
        [
            new() { Line = 0, Column = 0, Symbol = "G" },
            new() { Line = 0, Column = 8, Symbol = "C/E" },
            new() { Line = 2, Column = 4, Symbol = "Em7" },
        ];
        var result = ChordMath.DisplayPlacements(chords, musicKey: "G", displayKey: "A", ChordNotation.Chords);
        Assert.Equal(new[] { "A", "D/F#", "F#m7" }, result.Select(c => c.Symbol));
        Assert.Equal(new[] { 0, 0, 2 }, result.Select(c => c.Line));
        Assert.Equal(new[] { 0, 8, 4 }, result.Select(c => c.Column));
    }

    [Fact]
    public void HasChordsFindsPlacementsAnywhere()
    {
        var obj = new SlideObject { Id = "t", ObjectKind = SlideObjectKind.Text, Name = "", Text = "lyric" };
        var song = new Presentation
        {
            Id = "p", Name = "S", PresentationKind = PresentationKind.Deck, ThemeId = "",
            Slides =
            [
                new Slide { Id = "s1", Name = "", Objects = [] },
                new Slide { Id = "s2", Name = "", Objects = [obj] },
            ],
        };
        Assert.False(ChordMath.HasChords(song));
        obj.Chords = [new ChordPlacement { Line = 0, Column = 0, Symbol = "G" }];
        song.Slides[1].Objects = [obj];
        Assert.True(ChordMath.HasChords(song));
    }
}
