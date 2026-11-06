using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Stage;

/// <summary>The stage display's chords follow a song's capo (Windows: ConfidenceSceneBuilder.Capo.cs).</summary>
[Collection(nameof(StageCapoTests))] // sets a static hook: never beside the other stage tests
public sealed class StageCapoTests : IDisposable
{
    private static Presentation Song(string? displayKey = null) => new()
    {
        Id = "song", Name = "Song", PresentationKind = PresentationKind.Song, ThemeId = "", MusicKey = "A", DisplayKey = displayKey,
        Slides =
        [
            new Slide
            {
                Id = "s", Name = "",
                Objects = [new SlideObject { Id = "t", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = "Amazing grace", Chords = [new ChordPlacement { Line = 0, Column = 0, Symbol = "A" }] }],
            },
        ],
    };

    public void Dispose() => ConfidenceSceneBuilder.CapoFor = null;

    [Fact]
    public void WithACapoTheStageShowsTheShapesPlayed()
    {
        var song = Song();
        Assert.Null(ConfidenceSceneBuilder.SlideText(song.Slides[0], song).DisplayKey); // no capo: the song's own key
        ConfidenceSceneBuilder.CapoFor = id => id == "song" ? 2 : 0;
        Assert.Equal("G", ConfidenceSceneBuilder.SlideText(song.Slides[0], song).DisplayKey);
    }

    [Fact]
    public void ACapoWorksFromTheKeyTheSongIsShownIn()
    {
        var song = Song(displayKey: "B");
        ConfidenceSceneBuilder.CapoFor = _ => 2;
        Assert.Equal("A", ConfidenceSceneBuilder.SlideText(song.Slides[0], song).DisplayKey);
    }
}

/// <summary>Runs on its own, as it sets <see cref="ConfidenceSceneBuilder.CapoFor"/> for the whole process.</summary>
[CollectionDefinition(nameof(StageCapoTests), DisableParallelization = true)]
public sealed class StageCapoCollection;
