using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Media;

// A picture or video slide's media: its layer (the Mac's SlideLayerFacts and MediaCueMenu's Layer
// picker), a picture's scale, a video's end (SlidePlanner.showMedia), the custom layers a Clear step
// can name (ActionEditors' layer stack), and the theme design drag (NewSlide.swift's ThemeDesignDrag).
public class CueMediaEditingTests
{
    private static CueMedia Cue(CueMediaLayer? layer = null, MediaClassification? classification = null) =>
        new() { MediaId = "m", Layer = layer, Classification = classification };

    [Fact]
    public void ABuiltInLayerIsStoredAsTheLayerAnythingElseAsItsId()
    {
        var cue = Cue(CueMediaLayer.Videos);
        CueMediaEditing.SetRoutingLayer(cue, "stillGraphics");
        Assert.Equal((CueMediaLayer?)CueMediaLayer.StillGraphics, cue.Layer);
        Assert.Null(cue.LayerId);
        CueMediaEditing.SetRoutingLayer(cue, "overlays");
        Assert.Null(cue.Layer);
        Assert.Equal("overlays", cue.LayerId);
        Assert.Equal("overlays", CueMediaEditing.RoutingLayerId(cue));
        CueMediaEditing.SetRoutingLayer(cue, "custom-1");
        Assert.Equal("custom-1", CueMediaEditing.RoutingLayerId(cue));
        CueMediaEditing.SetRoutingLayer(cue, null);
        Assert.Null(cue.Layer);
        Assert.Null(cue.LayerId);
        Assert.Null(CueMediaEditing.RoutingLayerId(cue));
    }

    [Fact]
    public void ThePickerShowsTheDefaultUnlessAnotherOfferedLayerIsChosen()
    {
        List<CustomLayer> custom = [new() { Id = "c1", Name = "Side screens" }];
        var cue = Cue(CueMediaLayer.StillGraphics, MediaClassification.Foreground);
        // A foreground picture plays on Still Graphics anyway, so the picker reads Default.
        Assert.Null(CueMediaEditing.ChosenLayer(cue, MediaKind.Image, custom));
        Assert.Equal("Default (Still Graphics)", CueMediaEditing.LayerChoices(cue, MediaKind.Image, custom)[0].Name);
        CueMediaEditing.SetRoutingLayer(cue, "c1");
        Assert.Equal("c1", CueMediaEditing.ChosenLayer(cue, MediaKind.Image, custom));
        Assert.Equal(["", "loopingVideos", "stillGraphics", "videos", "overlays", "c1"],
            CueMediaEditing.LayerChoices(cue, MediaKind.Image, custom).Select(c => c.Id ?? ""));
        // A layer that is gone (its preset deleted) reads as the default.
        Assert.Null(CueMediaEditing.ChosenLayer(cue, MediaKind.Image, []));
        // A background video plays on Background Media by default.
        Assert.Equal(CueMediaLayer.LoopingVideos, CueMediaEditing.DefaultLayer(Cue(classification: MediaClassification.Background), MediaKind.Video));
    }

    [Fact]
    public void CustomLayersAreEveryPresetsOnceEachByName()
    {
        var a = new OutputPreset { Id = "a", Name = "A", Layers = [new() { Id = "z", Name = "Zone" }, new() { Id = "l", Name = "Lobby" }] };
        var b = new OutputPreset { Id = "b", Name = "B", Layers = [new() { Id = "l", Name = "Lobby" }] };
        Assert.Equal(["l", "z"], CueMediaEditing.CustomLayers([a, b, new OutputPreset { Id = "c", Name = "C" }]).Select(l => l.Id));
    }

    [Fact]
    public void FillIsStoredAsNothingAndScaleAndBlurFillsAPhotoSpot()
    {
        var cue = Cue();
        Assert.Equal(MediaScaleMode.Fill, CueMediaEditing.Scale(cue));
        CueMediaEditing.SetScale(cue, MediaScaleMode.ScaleAndBlur);
        Assert.Equal((MediaScaleMode?)MediaScaleMode.ScaleAndBlur, cue.ScaleMode);
        CueMediaEditing.SetScale(cue, MediaScaleMode.Fill);
        Assert.Null(cue.ScaleMode);
        Assert.Equal(MediaScaleMode.Fill, CueMediaEditing.InPhotoSpot(MediaScaleMode.ScaleAndBlur));
        Assert.Equal(MediaScaleMode.Fit, CueMediaEditing.InPhotoSpot(MediaScaleMode.Fit));
    }

    [Fact]
    public void AVideosEndIsReadAndWrittenAsTheMacsPlannerWritesIt()
    {
        var slide = new Slide { Id = "s", Name = "", Background = Cue(CueMediaLayer.Videos) };
        Assert.Equal(CueVideoEnd.Stop, CueMediaEditing.VideoEnd(slide));

        CueMediaEditing.SetVideoEnd(slide, CueVideoEnd.Next);
        Assert.True(slide.AutoAdvance is { AfterPlayback: true, DelaySeconds: 0 });
        Assert.Equal(CueVideoEnd.Next, CueMediaEditing.VideoEnd(slide));

        CueMediaEditing.SetVideoEnd(slide, CueVideoEnd.FadeToBlack);
        Assert.Equal((MediaEndFade?)MediaEndFade.Black, slide.Background!.EndFade);
        Assert.Null(slide.AutoAdvance); // the next slide no longer follows the video
        Assert.Equal(CueVideoEnd.FadeToBlack, CueMediaEditing.VideoEnd(slide));

        CueMediaEditing.SetVideoEnd(slide, CueVideoEnd.Loop);
        Assert.True(slide.Background.Loops);
        Assert.Null(slide.Background.EndFade);
        Assert.Equal(CueVideoEnd.Loop, CueMediaEditing.VideoEnd(slide));

        CueMediaEditing.SetVideoEnd(slide, CueVideoEnd.FadeToClear);
        Assert.False(slide.Background.Loops);
        Assert.Equal(CueVideoEnd.FadeToClear, CueMediaEditing.VideoEnd(slide));

        // A timed Auto Advance of the slide's own is kept.
        slide.AutoAdvance = new AutoAdvance { DelaySeconds = 8 };
        CueMediaEditing.SetVideoEnd(slide, CueVideoEnd.Stop);
        Assert.Equal(8, slide.AutoAdvance?.DelaySeconds);
        Assert.False(CueMediaEditing.SetVideoEnd(new Slide { Id = "plain", Name = "" }, CueVideoEnd.Loop));
    }

    [Fact]
    public void ClearStepsListCustomLayersAboveTheLayerTheyName()
    {
        var choices = new ActionChoices
        {
            CustomLayers = [new() { Id = "c1", Name = "Lobby", Above = "videos" }, new() { Id = "c2", Name = "Side", Above = null }],
        };
        Assert.Equal(["alerts", "overlays", "c2", "slide", "c1", "videos", "stillGraphics", "loopingVideos", "videoInput"],
            ActionEditing.LayerStack(choices).Select(c => c.Id));
        Assert.Equal(LayerKinds.All.Reverse().Select(l => l.Raw()), ActionEditing.LayerStack(new ActionChoices()).Select(c => c.Id));
        var clearCustom = new SlideAction { Id = "x", Kind = SlideActionKind.ClearLayer, Layer = "c1" };
        Assert.Equal("Clear Lobby", SlideActions.Label(clearCustom, new SlideActions.LabelNames(_ => null, Layers: [("c1", "Lobby")])));
        var clearVideos = new SlideAction { Id = "y", Kind = SlideActionKind.ClearLayer, Layer = "videos" };
        Assert.Equal("Clear Foreground Videos", SlideActions.Label(clearVideos, new SlideActions.LabelNames(_ => null)));
    }

    [Fact]
    public void AThemeDesignDragNamesItsThemeAndDesign()
    {
        var payload = ThemeDesignDrag.Payload("theme-1", "design-2");
        Assert.Equal("mxuthemedesign::theme-1::design-2", payload);
        Assert.Equal(("theme-1", "design-2"), ThemeDesignDrag.Parse(payload));
        Assert.Null(ThemeDesignDrag.Parse("mxuslide::abc"));
        Assert.Null(ThemeDesignDrag.Parse("mxuthemedesign::only"));
        Assert.Null(ThemeDesignDrag.Parse("mxuthemedesign::::x"));
        Assert.Null(ThemeDesignDrag.Parse(null));
        var theme = new Theme { Id = "theme-1", Name = "T", Slides = [new Slide { Id = "design-2", Name = "Lyrics" }] };
        Assert.Equal("Lyrics", ThemeDesignDrag.Resolve(payload, id => id == theme.Id ? theme : null)?.Design.Name);
        Assert.Null(ThemeDesignDrag.Resolve(ThemeDesignDrag.Payload("theme-1", "gone"), id => id == theme.Id ? theme : null));
    }
}
