using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Lyrics;

public class ChordProExportTests
{
    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    private static Presentation Song()
    {
        var chart = Lf("""
            Amazing Grace
            Key: G

            Verse 1
            G       C         G
            Amazing grace how sweet the sound
                 G       D      G
            That saved a wretch like me
            I once was lost but now am found
            Was blind but now I see

            Tag
            G  D

            Chorus
            G
            My chains are gone
            """);
        var presentation = LyricTextImporter.MakePresentation(chart, linesPerSlide: 2, lyricLines: []);

        if (presentation.Sections?.FirstOrDefault(section => section.Name == "Tag") is { } tag)
        {
            tag.Name = "Free Worship";
        }
        presentation.Ccli = new CCLIInfo { SongNumber = 4_768_151, Author = "Chris Tomlin", Copyright = "© 2006 sixsteps Music" };
        return presentation;
    }

    [Fact]
    public void WritesMetadataSectionsSlidesAndChordsAsTheSongStands()
    {
        var presentation = Song();

        var verseIndex = presentation.Slides.FindIndex(slide => slide.Objects.FirstOrDefault()?.Text.StartsWith("Amazing", StringComparison.Ordinal) == true);
        Assert.True(verseIndex >= 0);
        presentation.Slides[verseIndex].Objects[0].Chords![1].Symbol = "Cmaj7";

        var text = ChordProExport.Text(presentation);

        Assert.StartsWith("{title: Amazing Grace}\n{artist: Chris Tomlin}\n{key: G}\n{copyright: © 2006 sixsteps Music}\n{ccli: 4768151}\n", text);
        Assert.Contains("{start_of_verse: Verse 1}\n[G]Amazing [Cmaj7]grace how [G]sweet the sound\nThat [G]saved a [D]wretch [G]like me\n\nI once was lost but now am found\nWas blind but now I see\n{end_of_verse}", text);
        Assert.Contains("{start_of_part: Free Worship}\n[G][D]\n{end_of_part}", text);
        Assert.Contains("{start_of_chorus: Chorus}\n[G]My chains are gone\n{end_of_chorus}", text);
        Assert.Equal("Amazing Grace.txt", ChordProExport.FileName(presentation));
        Assert.Equal("Amazing Grace.cho", ChordProExport.FileName(presentation, ChordProExport.FileFormat.ChordPro));
    }

    [Fact]
    public void ReimportingTheExportRebuildsTheSameSong()
    {
        var original = Song();
        var reimported = LyricTextImporter.MakePresentation(ChordProExport.Text(original), linesPerSlide: 2);

        static List<string[]> Shape(Presentation presentation)
        {
            var names = (presentation.Sections ?? []).ToDictionary(section => section.Id, section => section.Name);
            return presentation.Slides
                .Where(slide => slide.Objects.Count > 0)
                .Select(slide => new[]
                {
                    names.GetValueOrDefault(slide.SectionId ?? "") ?? "",
                    ChordMath.Bracketed(slide.Objects[0].Text, slide.Objects[0].Chords ?? []),
                })
                .ToList();
        }
        Assert.Equal(LyricTextFormat.ChordPro, LyricTextImporter.DetectFormat(ChordProExport.Text(original)));
        Assert.Equal(Shape(original), Shape(reimported));
        Assert.Equal("G", reimported.MusicKey);
        Assert.Equal(4_768_151, reimported.Ccli?.SongNumber);
        Assert.Equal("Amazing Grace", reimported.Name);
    }

    [Fact]
    public void FlippingAChordLineToLyricsKeepsItAsWords()
    {
        var chart = Lf("""
            G       C
            Amazing grace
            A
            song of praise
            """);
        Assert.Equal(new[] { "G       C", "A" }, LyricTextImporter.ChordChartLines(chart));

        var read = LyricTextImporter.MakePresentation(chart, linesPerSlide: 4);
        var flipped = LyricTextImporter.MakePresentation(chart, linesPerSlide: 4, lyricLines: ["A"]);

        var readLyrics = read.Slides.First(slide => slide.Objects.Count > 0).Objects[0];
        var flippedLyrics = flipped.Slides.First(slide => slide.Objects.Count > 0).Objects[0];

        Assert.Equal("Amazing grace\nsong of praise", readLyrics.Text);
        Assert.Equal("Amazing grace\nA song of praise", flippedLyrics.Text);
        Assert.Equal(new[] { "G", "C" }, flippedLyrics.Chords?.Select(c => c.Symbol));
    }

    [Fact]
    public void ReflowStartsFromTheSourceUntilTheSlidesMoveOn()
    {
        var presentation = LyricTextImporter.MakePresentation(
            "Verse 1\n[G]Amazing grace\nHow sweet the sound\n\nChorus\n[C]My chains", linesPerSlide: 1);
        var source = Assert.IsType<string>(presentation.ReflowSource);
        Assert.Equal(source, ChordProExport.ReflowSeed(presentation));

        var index = presentation.Slides.FindIndex(slide => slide.Objects.FirstOrDefault()?.Text == "Amazing grace");
        Assert.True(index >= 0);
        presentation.Slides[index].Objects[0].Chords = [new ChordPlacement { Line = 0, Column = 8, Symbol = "D" }];
        var seed = ChordProExport.ReflowSeed(presentation);
        Assert.Equal("Verse 1\nAmazing [D]grace\n\nHow sweet the sound\n\nChorus\n[C]My chains", seed);
        Assert.Contains(Reflow.Build(seed, linesPerSlide: 1).Slides, slide => slide.Objects.FirstOrDefault()?.Chords?.FirstOrDefault()?.Symbol == "D");
    }

    [Fact]
    public void OnlySongsAreSongs()
    {
        Assert.True(ChordProExport.IsSong(Song()));
        Assert.True(ChordProExport.IsSong(LyricTextImporter.MakePresentation("Verse 1\nAmazing grace\n\nChorus\nMy chains")),
            "lyrics without chords still export");

        var sermon = new Presentation
        {
            Id = "s", Name = "Sunday Message", PresentationKind = PresentationKind.Deck, ThemeId = "",
            Slides =
            [
                new Slide
                {
                    Id = "1", Name = "", SectionId = "p1",
                    Objects = [new SlideObject { Id = "t", ObjectKind = SlideObjectKind.Text, Name = "Body", Text = "Psalm 23 [A]" }],
                },
            ],
            Sections = [new PresentationSection { Id = "p1", Name = "Point 1" }, new PresentationSection { Id = "r", Name = "Reading" }],
        };
        Assert.False(ChordProExport.IsSong(sermon));
        Assert.False(ChordProExport.IsSong(new Presentation { Id = "m", Name = "Welcome Loop", PresentationKind = PresentationKind.Deck, ThemeId = "", Slides = [] }));

        var keyed = new Presentation { Id = "k", Name = "Hymn", PresentationKind = PresentationKind.Deck, ThemeId = "", Slides = [] };
        keyed.MusicKey = "G";
        Assert.True(ChordProExport.IsSong(keyed));
    }
}
