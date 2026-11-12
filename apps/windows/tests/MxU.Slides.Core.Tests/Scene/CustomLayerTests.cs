using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Scene;

// Ports of the custom-layer cases in SlideSceneTests/ShowStateTests.swift and
// MediaTransportTargetTests.swift, and RenderEngineTests/CustomLayerTests.swift's ordering.
public class CustomLayerTests
{
    private static readonly CustomLayer Lower = new() { Id = "lower", Name = "Lower Wall", Above = "slide" };

    private static CueMedia Video(string id, MediaClassification classification = MediaClassification.Foreground) =>
        new() { MediaId = id, Layer = CueMediaLayer.Videos, Classification = classification };

    [Fact]
    public void MediaOnACustomLayerDrawsAboveTheLayerItNames()
    {
        var state = new ShowState();
        state.SetCustomLayers([Lower]);
        state.Fire(Video("m"), "lower");
        var scene = state.AnimatedScene(SceneSize.Hd);
        var ids = scene.Layers.Select(l => l.Id).ToList();
        Assert.Equal(ids.IndexOf("slide") + 1, ids.IndexOf("lower"));
        Assert.Equal(["live-media-lower"], scene.Layer("lower")!.Items.Select(i => i.Id));
        Assert.Equal("lower", state.LayerStackTopFirst()[ids.Count - 1 - ids.IndexOf("lower")].Id);
        Assert.Contains(state.TransportSeeds(), seed => seed.MediaId == "m" && seed.LayerId == "lower");
    }

    [Fact]
    public void FiringOntoALayerThePresetHasntGotDoesNothing()
    {
        var state = new ShowState();
        state.Fire(Video("m"), "lower");
        Assert.Empty(state.CustomLayerMedia);
    }

    [Fact]
    public void ALayerThatGoesTakesItsMediaAndClearsReachIt()
    {
        var state = new ShowState();
        state.SetCustomLayers([Lower]);
        state.Fire(Video("m"), "lower");
        state.Clear(ShowFunction.Media);
        Assert.Empty(state.CustomLayerMedia);

        state.Fire(Video("m"), "lower");
        state.Clear("lower");
        Assert.Empty(state.CustomLayerMedia);

        state.Fire(Video("m"), "lower");
        state.SetCustomLayers([]);
        Assert.Empty(state.CustomLayerMedia);
    }

    [Fact]
    public void ANewSlideSweepsForegroundMediaButNotABackground()
    {
        var state = new ShowState();
        state.SetCustomLayers([Lower]);
        state.Fire(Video("fg"), "lower");
        var slide = new Slide { Id = "s", Name = "", Objects = [] };
        state.Fire(slide);
        Assert.Empty(state.CustomLayerMedia);

        state.Fire(Video("bg", MediaClassification.Background), "lower");
        state.Fire(slide);
        Assert.Equal("bg", state.CustomLayerMedia["lower"].MediaId);

        // A slide whose background names the layer plays it there.
        var withBackground = new Slide { Id = "t", Name = "", Objects = [], Background = new CueMedia { MediaId = "own", Layer = CueMediaLayer.Videos, LayerId = "lower" } };
        state.Fire(withBackground);
        Assert.Equal("own", state.CustomLayerMedia["lower"].MediaId);
        Assert.False(state.LiveMedia.ContainsKey(LayerKind.Videos));
    }
}
