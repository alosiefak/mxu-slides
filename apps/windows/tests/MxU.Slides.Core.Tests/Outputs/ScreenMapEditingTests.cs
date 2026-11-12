using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Outputs;

// The Screen Map window's edits (ScreenMapEditing.cs).
public class ScreenMapEditingTests
{
    private static OutputPreset Preset() => new() { Id = "p", Name = "Room", Assignments = [] };

    [Fact]
    public void StartingPointsWriteThePresetsPlacementsAndLeaveOtherLayersAlone()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var map = ScreenMap.SideBySide([(a, 1920, 1080), (b, 1920, 1080)]);
        var preset = Preset();
        ScreenMapEditing.Place(preset, "slide", ScreenMapEditing.Start.OnePerScreen, map);
        ScreenMapEditing.Place(preset, "videos", ScreenMapEditing.Start.AcrossScreens, map, fill: true);
        Assert.Equal(2, preset.Placements!.Count(p => p.LayerId == "slide"));
        var across = preset.Placements!.Single(p => p.LayerId == "videos");
        Assert.Equal((0.0, 3840.0, MediaScaleMode.Fill), (across.X, across.Width, across.Scale!.Value));
        ScreenMapEditing.Place(preset, "slide", ScreenMapEditing.Start.NotPlaced, map);
        Assert.Equal(["videos"], preset.Placements!.Select(p => p.LayerId));
        Assert.Equal("Placed once on the map", ScreenMapEditing.Summary(preset, "videos"));
    }

    [Fact]
    public void FitOrFillIsOneChoiceForAllOfALayersFrames()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var map = ScreenMap.SideBySide([(a, 1920, 1080), (b, 1920, 1080)]);
        var preset = Preset();
        ScreenMapEditing.Place(preset, "slide", ScreenMapEditing.Start.OnePerScreen, map);
        ScreenMapEditing.Place(preset, "videos", ScreenMapEditing.Start.AcrossScreens, map);
        Assert.False(ScreenMapEditing.Fills(preset, "slide"));
        ScreenMapEditing.SetFill(preset, "slide", true);
        Assert.True(ScreenMapEditing.Fills(preset, "slide"));
        Assert.All(preset.Placements!.Where(p => p.LayerId == "slide"), p => Assert.Equal(MediaScaleMode.Fill, p.Scale));
        Assert.False(ScreenMapEditing.Fills(preset, "videos")); // the other layer keeps its own choice
        ScreenMapEditing.SetFill(preset, "slide", false);
        Assert.All(preset.Placements!, p => Assert.Null(p.Scale)); // Fit is stored as absent, as the Mac writes it
        Assert.False(ScreenMapEditing.Fills(null, "slide"));
    }

    [Fact]
    public void CustomLayersAddAboveSlideAndRemovingOneTakesItsPlacements()
    {
        var preset = Preset();
        var first = ScreenMapEditing.AddCustomLayer(preset, "", "slide");
        var second = ScreenMapEditing.AddCustomLayer(preset, "Bug", first);
        Assert.Equal("Layer 1", preset.Layers![0].Name);
        var order = ScreenMapEditing.Layers(preset).Select(l => l.Id).ToList();
        Assert.True(order.IndexOf(second) < order.IndexOf(first) && order.IndexOf(first) < order.IndexOf("slide")); // top first
        ScreenMapEditing.SetPlacements(preset, first, [new MapPlacement(new SceneRect(0, 0, 10, 10))]);
        ScreenMapEditing.RemoveCustomLayer(preset, first);
        Assert.Equal("slide", preset.Layers!.Single().Above); // the layer above it drops into its place
        Assert.Null(preset.Placements);
    }

    [Fact]
    public void NewLedWallSeedsItsFrameAndSeatsItOnTheMap()
    {
        var outputs = new OutputConfiguration();
        var main = outputs.AddScreen("Main", 1920, 1080)!;
        var wall = ScreenMapEditing.NewLedWall(outputs, "LED Wall", 1536, 864)!.Value;
        Assert.Equal(new OutputPlacement(1920, 1080, 0, 0, 1536, 864), outputs.Placement(wall));
        Assert.Equal(new SceneRect(0, 0, 1920, 1080), outputs.ScreenMap!.RectFor(main.Id));
        Assert.Equal(new SceneRect(1920, 0, 1536, 864), outputs.ScreenMap.RectFor(wall));
        var big = ScreenMapEditing.NewLedWall(outputs, "Big", 2500, 900)!.Value;
        Assert.Equal(3840, outputs.Placement(big)!.FrameWidth);
    }
}
