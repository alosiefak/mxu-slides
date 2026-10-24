using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Tests.Outputs.OutputFixtures;

namespace MxU.Slides.Core.Tests.Outputs;

// The non-UI logic of the Mac's OutputPresetsController.swift and the routing matrix in ScreenConfigurationView.swift.
public class OutputPresetsTests
{
    private static readonly string[] AllLayersSorted = LayerKinds.All.Select(layer => layer.Raw()).Order(StringComparer.Ordinal).ToArray();

    private static (OutputConfiguration Outputs, PlaceholderScreen Main, PlaceholderScreen Stage) Setup()
    {
        var outputs = new OutputConfiguration();
        outputs.UpdateDisplays([new DisplaySnapshot("display:A", "Projector", new SceneRect(0, 0, 1920, 1080))]);
        var main = outputs.AddScreen("Main", 1920, 1080)!;
        var stage = outputs.AddScreen("Stage", 1920, 1080)!;
        outputs.SetRole(stage.Id, ScreenRole.Confidence);
        return (outputs, main, stage);
    }

    [Fact]
    public void NewPresetsAreEmptyOrStartFromTheBroadcastFeedTemplate()
    {
        var (outputs, main, stage) = Setup();
        var empty = OutputPresets.Create(outputs, fromBroadcastTemplate: false);
        Assert.Equal("Untitled Preset", empty.Name);
        Assert.Empty(empty.Assignments);
        Assert.NotNull(SwiftUuid.Parse(empty.Id));

        var broadcast = OutputPresets.Create(outputs, fromBroadcastTemplate: true, id: "P1");
        Assert.Equal(("P1", "Broadcast Feed"), (broadcast.Id, broadcast.Name));
        Assert.Equal(["display:A", SwiftUuid.Format(main.Id), SwiftUuid.Format(stage.Id)], broadcast.Assignments.Select(a => a.TargetId));
        Assert.Equal([OutputTargetKind.Display, OutputTargetKind.PlaceholderScreen, OutputTargetKind.PlaceholderScreen], broadcast.Assignments.Select(a => a.TargetKind));
        Assert.All(broadcast.Assignments, a => Assert.Equal(["slide", "overlays"], a.EnabledLayers));
    }

    [Fact]
    public void DuplicateRenameAndClear()
    {
        var (outputs, _, _) = Setup();
        var original = OutputPresets.Create(outputs, fromBroadcastTemplate: true);
        var copy = OutputPresets.Duplicate(original);
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal("Broadcast Feed Copy", copy.Name);
        copy.Assignments[0].EnabledLayers.Add("alerts");
        Assert.Equal(2, original.Assignments[0].EnabledLayers.Count); // a deep copy

        Assert.False(OutputPresets.Rename(copy, "   \n"));
        Assert.Equal("Broadcast Feed Copy", copy.Name);
        Assert.True(OutputPresets.Rename(copy, "  Stream  "));
        Assert.Equal("Stream", copy.Name);

        OutputPresets.ClearAssignment(copy, "display:A");
        Assert.Null(OutputPresets.EnabledLayers(copy, "display:A"));
        Assert.True(OutputPresets.IsRouted(copy, "display:A", LayerKind.Videos)); // "All Layers On"
    }

    [Fact]
    public void TogglingALayerStartsFromEverything()
    {
        var (outputs, main, _) = Setup();
        var preset = OutputPresets.Create(outputs, false);
        var target = SwiftUuid.Format(main.Id);
        Assert.True(OutputPresets.IsRouted(preset, target, LayerKind.Videos));
        OutputPresets.ToggleLayer(preset, OutputTargetKind.PlaceholderScreen, target, "videos");
        Assert.False(OutputPresets.IsRouted(preset, target, LayerKind.Videos));
        Assert.True(OutputPresets.IsRouted(preset, target, LayerKind.Slide));
        Assert.Equal(AllLayersSorted.Where(layer => layer != "videos"), preset.Assignments.Single().EnabledLayers); // sorted
        OutputPresets.ToggleLayer(preset, OutputTargetKind.PlaceholderScreen, target, "videos");
        Assert.Equal(AllLayersSorted, preset.Assignments.Single().EnabledLayers);
        Assert.True(OutputPresets.IsRouted(null, target, LayerKind.Videos)); // no preset = every layer everywhere
    }

    [Fact]
    public void SlideThemesScopeToAFolderOrASlideOnlyWithATheme()
    {
        var (outputs, main, _) = Setup();
        var preset = OutputPresets.Create(outputs, false);
        var target = SwiftUuid.Format(main.Id);
        OutputPresets.SetSlideTheme(preset, OutputTargetKind.PlaceholderScreen, target, "lower-thirds", "Sermon", "");
        Assert.Equal("lower-thirds", OutputPresets.SlideThemeId(preset, target));
        Assert.Equal("Sermon", OutputPresets.SlideThemeFolder(preset, target));
        Assert.Null(OutputPresets.SlideThemeSlideId(preset, target));
        Assert.Equal(AllLayersSorted, preset.Assignments.Single().EnabledLayers); // a new row starts with every layer

        OutputPresets.SetSlideTheme(preset, OutputTargetKind.PlaceholderScreen, target, "", "Sermon", "s1");
        Assert.Null(OutputPresets.SlideThemeId(preset, target));
        Assert.Null(OutputPresets.SlideThemeFolder(preset, target)); // folder and slide only count with a theme
        Assert.Null(OutputPresets.SlideThemeSlideId(preset, target));
    }

    [Fact]
    public void LooksAreDistinctAndKeyed()
    {
        Assert.Equal("t", OutputPresets.LookKey("t", null, ""));
        Assert.Equal("t#Sermon", OutputPresets.LookKey("t", "Sermon", null));
        Assert.Equal("t##s1", OutputPresets.LookKey("t", null, "s1"));
        Assert.Equal("t#Sermon##s1", OutputPresets.LookKey("t", "Sermon", "s1"));

        var (outputs, main, _) = Setup();
        var preset = OutputPresets.Create(outputs, false);
        OutputPresets.SetSlideTheme(preset, OutputTargetKind.Display, "display:A", "t", "Sermon");
        OutputPresets.SetSlideTheme(preset, OutputTargetKind.PlaceholderScreen, SwiftUuid.Format(main.Id), "t", "Sermon");
        Assert.Equal([new SlideThemeLook("t", "Sermon", null)], OutputPresets.SlideThemeLooks(preset));
    }

    [Fact]
    public void MaskTogglesKeepSortedUppercaseIds()
    {
        var (outputs, main, _) = Setup();
        var preset = OutputPresets.Create(outputs, false);
        var target = SwiftUuid.Format(main.Id);
        var a = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");
        var b = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
        OutputPresets.ToggleMask(preset, OutputTargetKind.PlaceholderScreen, target, a);
        OutputPresets.ToggleMask(preset, OutputTargetKind.PlaceholderScreen, target, b);
        Assert.Equal([SwiftUuid.Format(b), SwiftUuid.Format(a)], preset.Assignments.Single().MaskIds!);
        Assert.Equal(new HashSet<Guid> { a, b }, OutputPresets.MaskIds(preset, target));
        OutputPresets.ToggleMask(preset, OutputTargetKind.PlaceholderScreen, target, a);
        OutputPresets.ToggleMask(preset, OutputTargetKind.PlaceholderScreen, target, b);
        Assert.Null(preset.Assignments.Single().MaskIds); // none left = no list
    }

    [Fact]
    public void ApplyingAPresetRoutesLayersThemesAndMasks()
    {
        var (outputs, main, _) = Setup();
        var program = new RenderScene(new SceneSize(1920, 1080));
        program.AddItem(Solid("lyric", new SceneRect(0, 0, 10, 10), SceneColor.White), LayerKind.Slide);
        program.AddItem(Solid("loop", new SceneRect(0, 0, 10, 10), SceneColor.White), LayerKind.LoopingVideos);
        outputs.SceneProvider = () => program;
        var lowerThird = new RenderScene(new SceneSize(1920, 1080));
        outputs.VariantSceneProvider = key => key == "lt" ? lowerThird : null;
        var wings = new OutputMask("Wings", LeftHalf, alwaysOn: false);
        outputs.SetMasks(main.Id, [wings]);

        var preset = OutputPresets.Create(outputs, fromBroadcastTemplate: true);
        var target = SwiftUuid.Format(main.Id).ToLowerInvariant(); // ids are matched however they are cased
        preset.Assignments.RemoveAll(a => a.TargetId != "display:A");
        OutputPresets.ToggleMask(preset, OutputTargetKind.PlaceholderScreen, target, wings.Id);
        OutputPresets.ToggleLayer(preset, OutputTargetKind.PlaceholderScreen, target, "loopingVideos");

        var routing = OutputPresets.Apply(outputs, preset);
        Assert.Equal([wings.Id], outputs.ActiveMasks(main.Id).Select(mask => mask.Id));
        var onMain = outputs.PreviewProvider(SwiftUuid.Format(main.Id))();
        Assert.Single(onMain.AllItems); // the loop layer isn't routed to Main
        Assert.Equal(["lyric"], outputs.PreviewProvider("display:A")().AllItems.Select(item => item.Id)); // the broadcast feed keeps only the slide
        Assert.Empty(routing.Looks);

        OutputPresets.SetSlideTheme(preset, OutputTargetKind.Display, "display:A", "lt");
        routing = OutputPresets.Apply(outputs, preset);
        Assert.Equal("lt", routing.SlideThemes["display:A"]);
        Assert.Equal(new SlideThemeLook("lt", null, null), routing.Looks["lt"]);
        Assert.Empty(outputs.PreviewProvider("display:A")().AllItems); // the (empty) lower-third variant replaces the program there

        OutputPresets.Apply(outputs, null);
        Assert.Empty(outputs.LayerRouting);
        Assert.Empty(outputs.SlideThemeRouting);
        Assert.Empty(outputs.ActiveMasks(main.Id));
        Assert.Same(program, outputs.PreviewProvider(SwiftUuid.Format(main.Id))());
    }

    [Fact]
    public void AMissingThemeLeavesTheTargetAsDesigned()
    {
        var (outputs, main, _) = Setup();
        var preset = OutputPresets.Create(outputs, false);
        OutputPresets.SetSlideTheme(preset, OutputTargetKind.PlaceholderScreen, SwiftUuid.Format(main.Id), "gone");
        var routing = OutputPresets.Routing(preset, themeExists: id => id != "gone");
        Assert.Empty(routing.SlideThemes);
        Assert.Empty(routing.Looks);
    }

    [Fact]
    public void RoutingMatrixRowsAndSelectableMasks()
    {
        var (outputs, main, stage) = Setup();
        Assert.Equal(
            [new OutputRoutingTarget("display:A", "Projector", OutputTargetKind.Display), new OutputRoutingTarget(SwiftUuid.Format(main.Id), "Main", OutputTargetKind.PlaceholderScreen)],
            OutputPresets.Targets(outputs)); // confidence screens aren't routed
        var always = new OutputMask("Pillar", CenterBand);
        var optional = new OutputMask("Wings", LeftHalf, alwaysOn: false);
        outputs.SetMasks(main.Id, [always, optional]);
        var targets = OutputPresets.Targets(outputs);
        Assert.Equal([optional], OutputPresets.SelectableMasks(outputs, targets[1]));
        Assert.Empty(OutputPresets.SelectableMasks(outputs, targets[0])); // masks live on screens
        Assert.DoesNotContain(targets, target => target.Name == stage.Name);
    }
}
