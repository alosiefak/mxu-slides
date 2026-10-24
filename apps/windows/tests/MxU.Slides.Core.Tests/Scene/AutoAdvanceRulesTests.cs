using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Scene;

// Ports of effectiveAutoAdvancePrefersTheSlide and autoAdvanceTargetWalksWrapsAndEnds
// (SlideSceneTests/SlideSceneBuilderTests.swift) and the decoding cases of
// PresenterCoreTests/MediaAutoAdvanceTests.swift (its lead case is in SongTransitionsTests).
public class AutoAdvanceRulesTests
{
    [Fact]
    public void EffectiveAutoAdvancePrefersTheSlide()
    {
        var docLevel = new AutoAdvance { DelaySeconds = 5, LoopToStart = true };
        var own = new AutoAdvance { DelaySeconds = 12 };
        var presentation = new Presentation
        {
            Id = "p", Name = "Deck", PresentationKind = PresentationKind.Deck, ThemeId = "",
            Slides = [new Slide { Id = "s", Name = "", Objects = [] }], AutoAdvance = docLevel,
        };
        var slide = presentation.Slides[0];
        Assert.Same(docLevel, SlideSceneBuilder.EffectiveAutoAdvance(slide, presentation));
        slide.AutoAdvance = own;
        Assert.Same(own, SlideSceneBuilder.EffectiveAutoAdvance(slide, presentation));
        presentation.AutoAdvance = null;
        slide.AutoAdvance = null;
        Assert.Null(SlideSceneBuilder.EffectiveAutoAdvance(slide, presentation));
    }

    [Fact]
    public void AutoAdvanceTargetWalksWrapsAndEnds()
    {
        Assert.Equal(1, SlideSceneBuilder.AutoAdvanceTarget(0, 3, loopToStart: false));
        Assert.Null(SlideSceneBuilder.AutoAdvanceTarget(2, 3, loopToStart: false));
        Assert.Equal(0, SlideSceneBuilder.AutoAdvanceTarget(2, 3, loopToStart: true));

        Assert.Equal(0, SlideSceneBuilder.AutoAdvanceTarget(0, 1, loopToStart: true));
        Assert.Null(SlideSceneBuilder.AutoAdvanceTarget(0, 1, loopToStart: false));

        Assert.Null(SlideSceneBuilder.AutoAdvanceTarget(5, 3, loopToStart: true));
        Assert.Null(SlideSceneBuilder.AutoAdvanceTarget(-1, 3, loopToStart: true));
        Assert.Null(SlideSceneBuilder.AutoAdvanceTarget(0, 0, loopToStart: true));
    }

    [Fact]
    public void StoredMediaPlaylistWithRetiredWalkFieldsStillDecodes()
    {
        const string json = """
            {"id":"p","name":"Loop","playlistKind":"media","autoAdvance":true,"autoAdvanceDelaySeconds":7,
            "entries":[{"id":"e","refKind":"media","refId":"m","autoAdvanceDelaySeconds":2}],
            "playbackMode":"playAll","crossfadeSeconds":0}
            """;
        var decoded = MxuJson.Deserialize<Playlist>(json);
        Assert.Equal(PlaylistKind.Media, decoded.PlaylistKind);
        Assert.Equal(["m"], decoded.Entries.Select(e => e.RefId));
    }

    [Fact]
    public void SlideDecodesWithAutoAdvanceAbsent()
    {
        var decoded = MxuJson.Deserialize<Slide>("""{"id":"s","name":"Verse","objects":[]}""");
        Assert.Null(decoded.AutoAdvance);
    }

    [Fact]
    public void SlideAutoAdvanceRoundTrips()
    {
        var slide = new Slide { Id = "s", Name = "Last", Objects = [], AutoAdvance = new AutoAdvance { DelaySeconds = 7, LoopToStart = true } };
        var decoded = MxuJson.Deserialize<Slide>(MxuJson.SerializeToUtf8(slide));
        Assert.Equal((7.0, (bool?)true, (bool?)null), (decoded.AutoAdvance!.DelaySeconds, decoded.AutoAdvance.LoopToStart, decoded.AutoAdvance.AfterPlayback));
    }

    [Fact]
    public void FillMediaIdIsLibraryMediaOnly()
    {
        var video = new SlideObject { Id = "v", ObjectKind = SlideObjectKind.Shape, Name = "V", Text = "", Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = "clip" } };
        Assert.Equal("clip", SlideSceneBuilder.FillMediaId(video));
        var camera = new SlideObject { Id = "c", ObjectKind = SlideObjectKind.Shape, Name = "C", Text = "", Fill = new ObjectFill { FillKind = FillKind.Media, LiveInputId = "cam" } };
        Assert.Null(SlideSceneBuilder.FillMediaId(camera)); // a live input isn't library media
        var text = new SlideObject { Id = "t", ObjectKind = SlideObjectKind.Text, Name = "T", Text = "hi" };
        Assert.Null(SlideSceneBuilder.FillMediaId(text));
    }
}
