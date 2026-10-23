using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Lyrics;

public class SongEditingTests
{
    private const string Lyrics = "Verse 1\nLine one\nLine two\n\nChorus\nSing it\nSing it loud\n\nVerse 2\nLine three\nLine four";

    private static Presentation Build(string lyrics = Lyrics, string? order = null, Presentation? existing = null) =>
        SongEditing.Build(existing, lyrics, 2, new SongEditing.Details("Test Song"), "theme", order);

    [Fact]
    public void BuildsSectionsAndAnOpeningBlank()
    {
        var song = Build();
        Assert.Equal(PresentationKind.Song, song.PresentationKind);
        Assert.Equal(["Intro", "Verse 1", "Chorus", "Verse 2"], song.Sections!.Select(s => s.Name));
        Assert.Equal(4, song.Slides.Count);
        Assert.Empty(song.Slides[0].Objects);
        Assert.Equal("Line one\nLine two", song.Slides[1].Objects.Single().Text);
        Assert.Equal("theme", song.ThemeId);
    }

    [Fact]
    public void WindowsLineEndingsAreNormalised()
    {
        var song = Build(Lyrics.Replace("\n", "\r\n"));
        Assert.Equal(4, song.Slides.Count);
        Assert.DoesNotContain(song.Slides, s => s.Objects.Any(o => o.Text.Contains('\r')));
    }

    [Fact]
    public void ParsesAbbreviatedOrdersWithRepeats()
    {
        var song = Build();
        var ids = SongEditing.ParseOrder("V1, C, V2, C x2, Nonsense", song.Sections!);
        Assert.Equal(["Verse 1", "Chorus", "Verse 2", "Chorus", "Chorus"],
            ids.Select(id => song.Sections!.Single(s => s.Id == id).Name));
    }

    [Fact]
    public void ACustomOrderBecomesTheDefaultArrangement()
    {
        var song = Build(order: "Verse 1, Chorus, Verse 2, Chorus");
        var arranged = SlideSceneBuilder.ArrangedSlides(song, song.DefaultArrangementId);
        Assert.Equal(4, arranged.Count);
        Assert.Equal("Custom", song.Arrangements!.Single(a => a.Id == song.DefaultArrangementId).Name);
        Assert.Equal("Verse 1, Chorus, Verse 2, Chorus", SongEditing.OrderText(song, song.DefaultArrangementId));
    }

    [Fact]
    public void ReEditingKeepsIdentityAndPerSlideDetails()
    {
        var original = Build();
        original.Slides[2].Notes = "Band builds here";
        original.Slides[2].Background = new CueMedia { MediaId = "m1" };
        original.Background = new CueMedia { MediaId = "song-bg" };

        var edited = Build(Lyrics.Replace("Sing it loud", "Sing it louder"), existing: original);

        Assert.Equal(original.Id, edited.Id);
        Assert.Equal("song-bg", edited.Background!.MediaId);
        var chorus = edited.Slides.Single(s => s.Objects.Any(o => o.Text.Contains("louder")));
        Assert.Equal("Band builds here", chorus.Notes);
        Assert.Equal("m1", chorus.Background!.MediaId);
    }

    [Fact]
    public void LyricsTextRoundTrips()
    {
        var song = Build();
        var again = Build(SongEditing.LyricsText(song), existing: song);
        Assert.Equal(song.Slides.Select(s => s.Objects.FirstOrDefault()?.Text), again.Slides.Select(s => s.Objects.FirstOrDefault()?.Text));
        Assert.Equal(song.Sections!.Select(s => s.Name), again.Sections!.Select(s => s.Name));
    }

    [Fact]
    public void LinesPerSlideReflectsTheLongestSlide()
    {
        const string fourLineVerse = "Verse 1\nOne\nTwo\nThree\nFour";
        Assert.Equal(2, SongEditing.LinesPerSlide(Build(fourLineVerse)));
        Assert.Equal(4, SongEditing.LinesPerSlide(SongEditing.Build(null, fourLineVerse, 4, new("x"), "t")));
    }

    [Fact]
    public void SlidesNeverSpanAStanzaBreak()
    {
        // Two-line stanzas stay two-line slides even when four lines would fit, as on the Mac.
        Assert.Equal(2, SongEditing.LinesPerSlide(SongEditing.Build(null, Lyrics, 4, new("x"), "t")));
    }
}
