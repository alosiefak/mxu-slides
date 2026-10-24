using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using Choice = MxU.Slides.Core.Outputs.PreviewTargets.Choice;

namespace MxU.Slides.Core.Tests.Outputs;

// Port of the PreviewTargetLogic cases of PresenterCoreTests/MultiViewTests.swift, the screen
// list and aspect of LivePanel.swift's PreviewTargets, and the OutputPresetMenu / OutputPresetChip rules.
public class PreviewTargetsTests
{
    private static readonly List<Choice> Screens = [new("screen-1", "Projector"), new("screen-2", "Stream")];
    private static readonly List<Choice> Layouts = [new(PreviewTargets.LayoutTarget("doc-1"), "Booth")];

    [Fact]
    public void LayoutTargetsRoundTripAndScreensAreNotLayouts()
    {
        Assert.Equal("doc-1", PreviewTargets.LayoutId(PreviewTargets.LayoutTarget("doc-1")));
        Assert.Null(PreviewTargets.LayoutId("screen-1"));
        Assert.Null(PreviewTargets.LayoutId(PreviewTargets.LayoutPrefix));
    }

    [Fact]
    public void AStoredLayoutPickResolvesAndADeletedOneFallsBackToTheFirstScreen()
    {
        var stored = PreviewTargets.LayoutTarget("doc-1");
        Assert.Equal("Booth", PreviewTargets.Resolve(stored, Screens, Layouts)?.Name);
        Assert.Equal("screen-1", PreviewTargets.Resolve(stored, Screens, [])?.Id);
        Assert.Equal("Booth", PreviewTargets.Resolve("", [], Layouts)?.Name);
        Assert.Null(PreviewTargets.Resolve("", [], []));
        Assert.Equal("screen-1", PreviewTargets.Resolve(null, Screens, Layouts)?.Id);
    }

    [Fact]
    public void ALayoutPreviewsAtItsOwnCanvasAspect()
    {
        Assert.Equal(1080.0 / 1920.0, PreviewTargets.Aspect(1080, 1920));
        Assert.Equal(16.0 / 9.0, PreviewTargets.Aspect(null, null));
        Assert.Equal(16.0 / 9.0, PreviewTargets.Aspect(1080, 0));
    }

    [Fact]
    public void ScreensListAndPreviewAtTheirOwnSize()
    {
        var config = new OutputConfiguration();
        var wall = config.AddScreen("LED wall", 1920, 640)!;
        var stage = config.AddScreen("Stage", 1280, 720)!;
        var choices = PreviewTargets.Screens(config);
        Assert.Equal([SwiftUuid.Format(wall.Id), SwiftUuid.Format(stage.Id)], choices.Select(c => c.Id));
        Assert.Equal(["LED wall", "Stage"], choices.Select(c => c.Name));
        Assert.Equal(3.0, PreviewTargets.Aspect(SwiftUuid.Format(wall.Id), config));
        Assert.Equal(0.5, PreviewTargets.Aspect(PreviewTargets.LayoutTarget("rail"), config, new Dictionary<string, double> { ["rail"] = 0.5 }));
        Assert.Equal(16.0 / 9.0, PreviewTargets.Aspect(PreviewTargets.LayoutTarget("unknown"), config));
        Assert.Equal(16.0 / 9.0, PreviewTargets.Aspect(null, config));
    }

    // MARK: The output preset ("look") switcher

    private static readonly List<OutputPreset> Presets =
    [
        new() { Id = "p1", Name = "Stream lower thirds", Assignments = [] },
        new() { Id = "p2", Name = "Easter", Assignments = [] },
    ];

    [Fact]
    public void ThePresetMenuStartsWithNoPreset()
    {
        var choices = OutputPresetMenu.Choices(Presets);
        Assert.Equal(["", "p1", "p2"], choices.Select(c => c.Id));
        Assert.Equal(OutputPresetMenu.DefaultName, choices[0].Name);
        Assert.True(OutputPresetMenu.IsActive(choices[0], null)); // nothing active: the default is ticked
        Assert.True(OutputPresetMenu.IsActive(choices[2], "p2"));
        Assert.False(OutputPresetMenu.IsActive(choices[0], "p2"));
    }

    [Fact]
    public void TheChipNamesTheActivePresetShortly()
    {
        Assert.Equal("Default — All Layers", OutputPresetMenu.ActiveName(Presets, null));
        Assert.Equal("Default — All Layers", OutputPresetMenu.ActiveName(Presets, "deleted"));
        Assert.Equal("Easter", OutputPresetMenu.ActiveName(Presets, "p2"));
        Assert.Equal("Easter", OutputPresetMenu.ChipName("Easter"));
        Assert.Equal("Thirteen char", OutputPresetMenu.ChipName("Thirteen char"));
        Assert.Equal("Stream lower…", OutputPresetMenu.ChipName("Stream lower thirds")); // 13 characters, then …
        Assert.Equal("Default — All…", OutputPresetMenu.ChipName(OutputPresetMenu.DefaultName)); // the space before … is trimmed
    }
}
