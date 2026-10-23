using MxU.Slides.Core.Lyrics;

namespace MxU.Slides.Core.Tests.Lyrics;

public class ReflowTests
{
    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    [Fact]
    public void LabelLinesAreDetectedInEveryPastedShape()
    {
        Assert.Equal("Verse 1", Reflow.LabelName("Verse 1"));
        Assert.Equal("Chorus", Reflow.LabelName("CHORUS"));
        Assert.Equal("Bridge", Reflow.LabelName("[Bridge]"));
        Assert.Equal("Chorus", Reflow.LabelName("chorus:"));
        Assert.Equal("Pre-Chorus 2", Reflow.LabelName("  Pre-Chorus 2  "));
        Assert.Equal("Point 3", Reflow.LabelName("Point 3"));

        Assert.Null(Reflow.LabelName("The chorus of creation sings"));
        Assert.Null(Reflow.LabelName("Verse 1 tells the story"));
        Assert.Null(Reflow.LabelName(""));
    }

    [Fact]
    public void LabeledLyricsParseIntoSectionsAndChunks()
    {
        var text = Lf("""
            Verse 1
            Amazing grace how sweet the sound
            That saved a wretch like me
            I once was lost but now am found
            Was blind but now I see

            Chorus
            My chains are gone
            I've been set free
            """);
        var result = Reflow.Parse(text, linesPerSlide: 2);
        Assert.Equal(new[] { "Verse 1", "Chorus" }, result.Sections.Select(s => s.Name));
        Assert.Equal(new[]
        {
            "Amazing grace how sweet the sound\nThat saved a wretch like me",
            "I once was lost but now am found\nWas blind but now I see",
        }, result.Sections[0].Slides);
        Assert.Single(result.Sections[1].Slides);
        Assert.Equal(new[] { 0, 1 }, result.Order);
    }

    [Fact]
    public void RepeatedBareLabelRepeatsTheSectionWithoutDuplicating()
    {
        var text = Lf("""
            Verse 1
            Line one
            Line two

            Chorus
            Sing it out

            Verse 2
            Line three

            Chorus
            """);
        var result = Reflow.Parse(text, linesPerSlide: 2);
        Assert.Equal(new[] { "Verse 1", "Chorus", "Verse 2" }, result.Sections.Select(s => s.Name));
        Assert.Equal(new[] { 0, 1, 2, 1 }, result.Order);

        var built = Reflow.Build(text, linesPerSlide: 2);
        Assert.NotNull(built.Arrangement);
        Assert.Equal(4, built.Arrangement.SectionIds.Count);
        Assert.Equal(3, built.Slides.Count);
    }

    [Fact]
    public void RepeatedLabelWithIdenticalContentMerges()
    {
        var text = Lf("""
            Chorus
            Sing it out

            Verse 1
            A line

            Chorus
            Sing it out
            """);
        var result = Reflow.Parse(text, linesPerSlide: 2);
        Assert.Equal(new[] { "Chorus", "Verse 1" }, result.Sections.Select(s => s.Name));
        Assert.Equal(new[] { 0, 1, 0 }, result.Order);
    }

    [Fact]
    public void UnlabeledStanzasBecomeVerses()
    {
        var text = Lf("""
            First stanza line one
            First stanza line two

            Second stanza line one
            """);
        var result = Reflow.Parse(text, linesPerSlide: 2);
        Assert.Equal(new[] { "Verse 1", "Verse 2" }, result.Sections.Select(s => s.Name));
        Assert.Equal(new[] { 0, 1 }, result.Order);
    }

    [Fact]
    public void StanzasStayInTheirLabeledSectionWhenLabelsExist()
    {
        var text = Lf("""
            Chorus
            Stanza one line

            Stanza two line
            """);
        var result = Reflow.Parse(text, linesPerSlide: 4);
        Assert.Equal(new[] { "Chorus" }, result.Sections.Select(s => s.Name));
        Assert.Equal(new[] { "Stanza one line", "Stanza two line" }, result.Sections[0].Slides);
    }

    [Fact]
    public void ChunkingRespectsLinesPerSlide()
    {
        var five = "Verse 1\nl1\nl2\nl3\nl4\nl5";
        Assert.Equal(new[] { "l1\nl2", "l3\nl4", "l5" }, Reflow.Parse(five, linesPerSlide: 2).Sections[0].Slides);
        Assert.Equal(new[] { "l1\nl2\nl3\nl4", "l5" }, Reflow.Parse(five, linesPerSlide: 4).Sections[0].Slides);
        Assert.Equal(5, Reflow.Parse(five, linesPerSlide: 0).Sections[0].Slides.Count);
    }

    [Fact]
    public void BuiltSlidesWireSectionsAndThemeCategory()
    {
        var built = Reflow.Build("Verse 1\nA\nB\n\nChorus\nC", linesPerSlide: 2);
        Assert.Equal(2, built.Sections.Count);
        Assert.Equal(2, built.Slides.Count);
        Assert.Equal(built.Sections[0].Id, built.Slides[0].SectionId);
        Assert.Equal(built.Sections[1].Id, built.Slides[1].SectionId);
        Assert.All(built.Slides, slide => Assert.Equal("Lyrics", slide.ThemeSlideName));
        Assert.Single(built.Slides[0].Objects);
        Assert.Null(built.Slides[0].Objects[0].TextStyle);
        Assert.Null(built.Arrangement);
    }

    [Fact]
    public void AReflowKeepsTheDeckLyricDesign()
    {
        var built = Reflow.Build("Verse 1\nA\nB", linesPerSlide: 2, themeSlideName: "Lower Third");
        Assert.All(built.Slides, slide => Assert.Equal("Lower Third", slide.ThemeSlideName));
        Assert.Equal("Lower Third", Reflow.LyricDesign(built.Slides));
        Assert.Equal("Lyrics", Reflow.LyricDesign([]));
    }

    [Fact]
    public void ExtraLabelsReadAsSectionsAndTheWiderVocabularyLands()
    {
        var text = Lf("""
            Vamp Out
            Oh oh oh

            [Half Chorus]
            Praise Him
            """);
        var parsed = Reflow.Parse(text, linesPerSlide: 2, extraLabels: ["Vamp Out"]);
        Assert.Equal(new[] { "Vamp Out", "Half-Chorus" }, parsed.Sections.Select(s => s.Name));

        Assert.Equal(new[] { "Verse 1", "Half-Chorus" }, Reflow.Parse(text, linesPerSlide: 2).Sections.Select(s => s.Name));
    }

    [Fact]
    public void MaterializeWritesTheSlideBreaksIntoTheText()
    {
        var text = Lf("""
            Chorus
            a
            b
            c

            d
            e

            Turnaround

            Bridge
            f
            g
            h
            i
            """);
        var split = Reflow.Materialize(text, linesPerSlide: 2);
        Assert.Equal("Chorus\na\nb\n\nc\n\nd\ne\n\nTurnaround\n\nBridge\nf\ng\n\nh\ni", split);

        var parsed = Reflow.Parse(split, linesPerSlide: Reflow.StanzaOnly);
        Assert.Equal(
            new[] { new[] { "a\nb", "c", "d\ne" }, [], ["f\ng", "h\ni"] },
            parsed.Sections.Select(s => s.Slides));

        Assert.Equal(
            "Chorus\na\nb\nc\n\nd\ne\n\nTurnaround\n\nBridge\nf\ng\nh\n\ni",
            Reflow.Materialize(split, linesPerSlide: 3, mergeStanzas: true));
    }

    [Fact]
    public void RepeatLinesAreTheLabelAlone()
    {
        Assert.Equal("Bridge", Reflow.LabelName("REPEAT BRIDGE"));
        var parsed = Reflow.Parse("Bridge\na\n\nTag\nb\n\nREPEAT BRIDGE", linesPerSlide: 2);
        Assert.Equal(new[] { "Bridge", "Tag" }, parsed.Sections.Select(s => s.Name));
        Assert.Equal(new[] { 0, 1, 0 }, parsed.Order);
    }

    [Fact]
    public void OpeningOnBlankLeadsWithAnIntroWhenTheTextHasNone()
    {
        var parsed = Reflow.Parse("Verse 1\nAmazing grace\n\nChorus\nMy chains\n\nChorus\n", linesPerSlide: 2);
        var opened = parsed.OpeningOnBlank();
        Assert.Equal(new[] { "Intro", "Verse 1", "Chorus" }, opened.Sections.Select(s => s.Name));
        Assert.Equal(new[] { "" }, opened.Sections[0].Slides);

        Assert.Equal(new[] { 0, 1, 2, 2 }, opened.Order);
    }

    [Fact]
    public void OpeningOnBlankIsNamedBlankWhenTheTextHasAWordedIntro()
    {
        var parsed = Reflow.Parse("Intro\nOh oh oh\n\nVerse 1\nAmazing grace", linesPerSlide: 2);
        var opened = parsed.OpeningOnBlank();
        Assert.Equal(new[] { "Blank", "Intro", "Verse 1" }, opened.Sections.Select(s => s.Name));
        Assert.Equal(new[] { "Oh oh oh" }, opened.Sections[1].Slides);
        Assert.True(Reflow.IsIntro("Intro 2"));
        Assert.False(Reflow.IsIntro("Introduction"));
    }

    [Fact]
    public void OpeningOnBlankFillsAnEmptyLeadingIntro()
    {
        var parsed = Reflow.Parse("Intro\n\nVerse 1\nAmazing grace", linesPerSlide: 2);
        var opened = parsed.OpeningOnBlank();

        Assert.Equal(new[] { "Intro", "Verse 1" }, opened.Sections.Select(s => s.Name));
        Assert.Equal(new[] { "" }, opened.Sections[0].Slides);
        Assert.Equal(new[] { 0, 1 }, opened.Order);
    }

    [Fact]
    public void AChordOnlyIntroIsTheOpeningBlank()
    {
        var parsed = Reflow.Parse("Intro\n[G] [D] [Em] [C]\n\nVerse 1\n[G]Amazing grace", linesPerSlide: 2);
        var opened = parsed.OpeningOnBlank();
        Assert.Equal(new[] { "Intro", "Verse 1" }, opened.Sections.Select(s => s.Name));
        Assert.Equal(parsed.Sections[0].Slides, opened.Sections[0].Slides);
        Assert.Equal(parsed.Order, opened.Order);
    }

    [Fact]
    public void OpeningOnBlankLeavesEmptyTextEmpty()
    {
        Assert.Equal(new Reflow.ParseResult(), Reflow.Parse("", linesPerSlide: 2).OpeningOnBlank());
    }

    [Fact]
    public void BuildTurnsAnEmptySlideEntryIntoAnObjectlessSlide()
    {
        var built = Reflow.Build(Reflow.Parse("Verse 1\nAmazing grace", linesPerSlide: 2).OpeningOnBlank());
        Assert.Equal(new[] { "Intro", "Verse 1" }, built.Sections.Select(s => s.Name));
        Assert.Equal(2, built.Slides.Count);
        Assert.Empty(built.Slides[0].Objects);
        Assert.Equal(built.Sections[0].Id, built.Slides[0].SectionId);
        Assert.Equal("Lyrics", built.Slides[0].ThemeSlideName);
        Assert.Equal(new[] { "Amazing grace" }, built.Slides[1].Objects.Select(o => o.Text));
        Assert.Null(built.Arrangement);
    }
}
