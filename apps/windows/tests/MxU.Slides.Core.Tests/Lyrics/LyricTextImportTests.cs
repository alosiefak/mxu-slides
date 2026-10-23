using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Lyrics;

public class LyricTextImportTests
{
    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    private readonly string songSelectText = Lf("""
        Amazing Grace (My Chains Are Gone)

        Verse 1
        Amazing grace how sweet the sound
        That saved a wretch like me
        I once was lost but now am found
        Was blind but now I see

        Chorus
        My chains are gone I've been set free
        My God my Savior has ransomed me

        Verse 2
        The Lord has promised good to me
        His word my hope secures

        Chorus
        My chains are gone I've been set free
        My God my Savior has ransomed me

        CCLI Song # 4768151
        Chris Tomlin | John Newton | Louie Giglio
        © 2006 sixsteps Music | Vamos Publishing | worshiptogether.com songs
        For use solely with the SongSelect® Terms of Use. All rights reserved. www.ccli.com
        CCLI Licence # 123456
        """);

    private readonly string chordProText = Lf("""
        {title: Living Hope}
        {artist: Phil Wickham}
        {ccli: 7106807}
        {copyright: © 2018 Phil Wickham Music}
        {key: G}

        {c: Verse 1}
        How great the [G]chasm that [C]lay between us
        How high the [Em]mountain I could not [D]climb

        {soc}
        Hallelujah [G]praise the One who [C]set me free
        {eoc}
        """);

    private readonly string plainText = Lf("""
        On the mountain we will worship
        In the valley we will sing

        You are faithful in each season
        Every sunrise every evening
        """);

    [Fact]
    public void DetectsSongSelect()
    {
        Assert.Equal(LyricTextFormat.SongSelect, LyricTextImporter.DetectFormat(songSelectText));
    }

    [Fact]
    public void DetectsChordProByDirective()
    {
        Assert.Equal(LyricTextFormat.ChordPro, LyricTextImporter.DetectFormat(chordProText));
    }

    [Fact]
    public void DetectsChordProByChordDensityWithoutDirectives()
    {
        var bare = Lf("""
            How great the [G]chasm that [C]lay between us
            How high the [Em]mountain I could not [D]climb
            """);
        Assert.Equal(LyricTextFormat.ChordPro, LyricTextImporter.DetectFormat(bare));
    }

    [Fact]
    public void PlainLyricsStayPlain()
    {
        Assert.Equal(LyricTextFormat.PlainText, LyricTextImporter.DetectFormat(plainText));

        Assert.Equal(LyricTextFormat.PlainText, LyricTextImporter.DetectFormat("Sing it loud [oh oh]\nLift it high [yeah]"));
    }

    [Fact]
    public void SongSelectExtractsCCLIAndTitle()
    {
        var normalized = LyricTextImporter.Normalize(songSelectText);
        Assert.Equal("Amazing Grace (My Chains Are Gone)", normalized.Title);
        Assert.Equal(4768151, normalized.Ccli?.SongNumber);
        Assert.Equal("Chris Tomlin | John Newton | Louie Giglio", normalized.Ccli?.Author);
        Assert.Equal("© 2006 sixsteps Music | Vamos Publishing | worshiptogether.com songs", normalized.Ccli?.Copyright);
        Assert.Equal(2006, normalized.Ccli?.CopyrightYear);

        Assert.DoesNotContain("CCLI", normalized.Body);
        Assert.DoesNotContain("SongSelect", normalized.Body);
    }

    [Fact]
    public void ImportBuildsEverySlideOnThePickedDesign()
    {
        var presentation = LyricTextImporter.MakePresentation(
            songSelectText, themeId: "sunday", themeSlideName: "Lyrics (Lower Third)", linesPerSlide: 2);
        Assert.Equal("sunday", presentation.ThemeId);
        Assert.All(presentation.Slides, slide => Assert.Equal("Lyrics (Lower Third)", slide.ThemeSlideName));
        Assert.All(LyricTextImporter.MakePresentation(songSelectText).Slides, slide => Assert.Equal("Lyrics", slide.ThemeSlideName));
    }

    [Fact]
    public void SongSelectImportBuildsSectionedPresentation()
    {
        var presentation = LyricTextImporter.MakePresentation(songSelectText, linesPerSlide: 2);
        Assert.Equal("Amazing Grace (My Chains Are Gone)", presentation.Name);
        Assert.Null(presentation.Folder);
        Assert.Equal(4768151, presentation.Ccli?.SongNumber);

        var sections = Assert.IsType<List<PresentationSection>>(presentation.Sections);

        Assert.Equal(new[] { "Intro", "Verse 1", "Chorus", "Verse 2" }, sections.Select(s => s.Name));

        Assert.Equal(5, presentation.Slides.Count);
        Assert.Empty(presentation.Slides[0].Objects);
        var arrangement = Assert.Single(Assert.IsType<List<Arrangement>>(presentation.Arrangements));
        var names = arrangement.SectionIds.Select(id => sections.First(s => s.Id == id).Name);
        Assert.Equal(new[] { "Intro", "Verse 1", "Chorus", "Verse 2", "Chorus" }, names);
        Assert.Equal(arrangement.Id, presentation.DefaultArrangementId);

        foreach (var slide in presentation.Slides)
        {
            Assert.Equal("Lyrics", slide.ThemeSlideName);
        }
        foreach (var slide in presentation.Slides.Skip(1))
        {
            Assert.Single(slide.Objects);
            Assert.Null(slide.Objects[0].TextStyle);
        }
    }

    [Fact]
    public void ImportOpensOnABlankNamedForWhatTheTextHas()
    {
        Assert.Equal("Intro", LyricTextImporter.MakePresentation(plainText).Sections?.FirstOrDefault()?.Name);

        var worded = LyricTextImporter.MakePresentation("Intro\nOh oh oh\n\nVerse 1\nAmazing grace");
        Assert.Equal(new[] { "Blank", "Intro", "Verse 1" }, worded.Sections?.Select(s => s.Name));
        Assert.Empty(worded.Slides[0].Objects);
        Assert.Equal("Oh oh oh", worded.Slides[1].Objects[0].Text);

        var cue = LyricTextImporter.MakePresentation("Intro\n\nVerse 1\nAmazing grace");
        Assert.Equal(new[] { "Intro", "Verse 1" }, cue.Sections?.Select(s => s.Name));
        Assert.Equal(2, cue.Slides.Count);
        Assert.Empty(cue.Slides[0].Objects);
    }

    [Fact]
    public void ChordProKeepsCleanGlassAndKeepsSource()
    {
        var presentation = LyricTextImporter.MakePresentation(chordProText);
        Assert.Equal("Living Hope", presentation.Name);
        Assert.Equal(7106807, presentation.Ccli?.SongNumber);
        Assert.Equal("Phil Wickham", presentation.Ccli?.Author);
        Assert.Equal(2018, presentation.Ccli?.CopyrightYear);
        Assert.Equal(chordProText.Replace("\r\n", "\n"), presentation.ChordProSource);
        Assert.Equal("G", presentation.MusicKey);

        var allText = string.Join("\n", presentation.Slides.SelectMany(s => s.Objects).Select(o => o.Text));
        Assert.Contains("How great the chasm that lay between us", allText);
        Assert.DoesNotContain("[", allText);

        var sections = Assert.IsType<List<PresentationSection>>(presentation.Sections);
        Assert.Equal(new[] { "Intro", "Verse 1", "Chorus" }, sections.Select(s => s.Name));
    }

    [Fact]
    public void ChordProImportsChordsAsPlacements()
    {
        var presentation = LyricTextImporter.MakePresentation(chordProText);

        var verse = presentation.Slides[1].Objects[0];
        var verseChords = Assert.IsType<List<ChordPlacement>>(verse.Chords);
        Assert.Equal(new[] { "G", "C", "Em", "D" }, verseChords.Select(c => c.Symbol));
        Assert.Equal(new[] { 0, 0, 1, 1 }, verseChords.Select(c => c.Line));
        var line0 = verse.Text.Split('\n')[0];
        var line1 = verse.Text.Split('\n')[1];
        Assert.Equal(line0.IndexOf("chasm", StringComparison.Ordinal), verseChords[0].Column);
        Assert.Equal(line0.IndexOf("lay", StringComparison.Ordinal), verseChords[1].Column);
        Assert.Equal(line1.IndexOf("climb", StringComparison.Ordinal), verseChords[3].Column);

        var chorus = presentation.Slides[2].Objects[0];
        Assert.Equal(new[] { "G", "C" }, chorus.Chords?.Select(c => c.Symbol));

        var source = Assert.IsType<string>(presentation.ReflowSource);
        Assert.Contains("[G]", source);
        var rebuilt = Reflow.Build(source, linesPerSlide: 2);
        Assert.Equal(Shape(verseChords), Shape(rebuilt.Slides[0].Objects[0].Chords));
    }

    [Fact]
    public void BracketExtractionLeavesLyricPunctuationAndNonChords()
    {
        var extracted = ChordMath.ExtractLine("How [G]great is our [C/E]God");
        Assert.Equal("How great is our God", extracted.Text);
        Assert.Equal(new[] { "G", "C/E" }, extracted.Chords.Select(c => c.Symbol));

        Assert.Equal("", ChordMath.ExtractLine("[D]").Text);

        var stage = ChordMath.ExtractLine("Sing it loud [oh oh]");
        Assert.Equal("Sing it loud [oh oh]", stage.Text);
        Assert.Empty(stage.Chords);
    }

    [Fact]
    public void BracketedSkipsEmptyChords()
    {
        ChordPlacement[] chords =
        [
            new() { Line = 0, Column = 3, Symbol = "" },
            new() { Line = 0, Column = 5, Symbol = "G" },
            new() { Line = 0, Column = 7, Symbol = " " },
        ];
        Assert.Equal("Step [G]two", ChordMath.Bracketed("Step two", chords));
    }

    [Fact]
    public void BracketedSerializationRoundTrips()
    {
        var source = "How great the chasm that lay between us";
        ChordPlacement[] chords =
        [
            new() { Line = 0, Column = 14, Symbol = "G" },
            new() { Line = 0, Column = 25, Symbol = "C" },
        ];
        var bracketed = ChordMath.Bracketed(source, chords);
        Assert.Equal("How great the [G]chasm that [C]lay between us", bracketed);
        var back = ChordMath.Extract(bracketed);
        Assert.Equal(source, back.Text);
        Assert.Equal(Shape(chords), Shape(back.Chords));
    }

    [Fact]
    public void PlainTextChunksAtBlankLines()
    {
        var presentation = LyricTextImporter.MakePresentation(plainText, fallbackTitle: "New Song", linesPerSlide: 2);
        Assert.Equal("New Song", presentation.Name);
        Assert.Null(presentation.Ccli);

        Assert.Equal(new[] { "Intro", "Verse 1", "Verse 2" }, presentation.Sections?.Select(s => s.Name));
        Assert.Equal(3, presentation.Slides.Count);
        Assert.Equal("On the mountain we will worship\nIn the valley we will sing", presentation.Slides[1].Objects[0].Text);
    }

    [Fact]
    public void EmptyPasteStillMakesAValidPresentation()
    {
        var presentation = LyricTextImporter.MakePresentation("   \n\n  ");
        Assert.Equal("Imported Lyrics", presentation.Name);
        Assert.Single(presentation.Slides);
        Assert.Null(presentation.ReflowSource);
    }

    [Fact]
    public void SongSelectMiscLabelIsASection()
    {
        Assert.Equal("Misc 1", Reflow.LabelName("Misc 1"));
        Assert.Equal("Instrumental", Reflow.LabelName("Instrumental"));
    }

    private static (int Line, int Column, string Symbol)[] Shape(IEnumerable<ChordPlacement>? chords) =>
        (chords ?? []).Select(c => (c.Line, c.Column, c.Symbol)).ToArray();
}
