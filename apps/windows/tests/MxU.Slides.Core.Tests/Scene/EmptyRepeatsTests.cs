using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Scene;

// Port of SlideSceneBuilderTests.swift's emptySectionPlayedTwiceInARowShowsOnceOnlyWhenChosen:
// "Show a repeated chord-only section once". Each call names the setting, so these tests never
// depend on the app-wide SlideSceneBuilder.EmptyRepeatsOnce (other tests run alongside).
public class EmptyRepeatsTests
{
    private static Slide Lyric(string id, string section, string text, List<ChordPlacement>? chords = null) => new()
    {
        Id = id, Name = "", SectionId = section,
        Objects = [new SlideObject { Id = $"{id}-t", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = text, Chords = chords }],
    };

    private static Presentation Song() => new()
    {
        Id = "p", Name = "The Blood", PresentationKind = PresentationKind.Deck, ThemeId = "",
        Slides =
        [
            Lyric("c1", "c", "Hallelujah"),
            Lyric("i1", "i", "", [new ChordPlacement { Line = 0, Column = 0, Symbol = "D" }]),
            Lyric("i2", "i2", "", [new ChordPlacement { Line = 0, Column = 0, Symbol = "Bm" }]),
            Lyric("b1", "b", "It's never been about"),
        ],
        Sections =
        [
            new PresentationSection { Id = "c", Name = "Chorus" },
            new PresentationSection { Id = "i", Name = "Interlude" },
            new PresentationSection { Id = "i2", Name = "Interlude 2" },
            new PresentationSection { Id = "b", Name = "Bridge" },
        ],
        Arrangements = [new Arrangement { Id = "live", Name = "Bethel Music", SectionIds = ["c", "c", "i", "i", "i2", "b", "i"] }],
    };

    [Fact]
    public void EmptySectionPlayedTwiceInARowShowsOnceOnlyWhenChosen()
    {
        var song = Song();
        Assert.Equal(["c1", "c1", "i1", "i1", "i2", "b1", "i1"], SlideSceneBuilder.ArrangedSlides(song, "live", emptyRepeatsOnce: false).Select(s => s.Id));
        Assert.Equal([1, 1], SlideSceneBuilder.PlayedBlocks(["i", "i"], song, emptyRepeatsOnce: false).Select(b => b.Count));

        Assert.Equal(["c1", "c1", "i1", "i2", "b1", "i1"], SlideSceneBuilder.ArrangedSlides(song, "live", emptyRepeatsOnce: true).Select(s => s.Id));
        Assert.Equal([0, 1, 2, 3, 3, 4, 5], SlideSceneBuilder.ArrangementBlockStarts(song, "live", emptyRepeatsOnce: true));
        Assert.Equal([0, 1, 2, 3, 3, 4, 5], HotKeyJump.BlockSectionNames(song, "live", emptyRepeatsOnce: true).Select(b => b.Start));
        Assert.Equal([1, 1, 0], SlideSceneBuilder.PlayedBlocks(["c", "i", "i"], song, emptyRepeatsOnce: true).Select(b => b.Count));

        // A repeat that brings a background still plays.
        song.Slides[1].Background = new CueMedia { MediaId = "loop" };
        Assert.Equal(["c1", "c1", "i1", "i1", "i2", "b1", "i1"], SlideSceneBuilder.ArrangedSlides(song, "live", emptyRepeatsOnce: true).Select(s => s.Id));
    }
}
