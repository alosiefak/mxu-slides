using System.Text.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Outputs;

// Which look each screen shows: its own (the Screens window's Look picker, saved with the
// screen) unless the active output preset gives it a slide theme, as on the Mac.
public class ScreenLookTests
{
    [Fact]
    public void AScreensOwnLookIsItsFeedsLook()
    {
        var outputs = new OutputConfiguration();
        var stream = outputs.AddScreen("Stream", 1920, 1080)!;
        var projector = outputs.AddScreen("Projector", 1920, 1080)!;
        var saves = 0;
        outputs.ConfigurationChanged += () => saves++;
        outputs.SetScreenLook(stream.Id, new SlideThemeLook("digital", "Lower Thirds", null));
        Assert.Equal(1, saves); // saved with the setup
        Assert.Equal("digital#Lower Thirds", outputs.Feed(SwiftUuid.Format(stream.Id)).SlideThemeKey);
        Assert.Null(outputs.Feed(SwiftUuid.Format(projector.Id)).SlideThemeKey); // the slide as designed
        Assert.Equal(["digital#Lower Thirds"], outputs.ActiveLooks.Keys);

        outputs.SetScreenLook(stream.Id, new SlideThemeLook("digital", "Lower Thirds", ""));
        Assert.Equal(1, saves); // no change, no save
        outputs.SetScreenLook(stream.Id, new SlideThemeLook("", null, null));
        Assert.Null(outputs.ScreenLook(stream.Id)); // a look without a theme is none
        Assert.Empty(outputs.ActiveLooks);
    }

    [Fact]
    public void SlicesShowTheirScreensLook()
    {
        var outputs = new OutputConfiguration();
        var wall = outputs.AddScreen("Wall", 3840, 1080)!;
        var right = outputs.AddSlice(wall.Id, "Right", new SceneRect(0.5, 0, 0.5, 1))!;
        outputs.SetScreenLook(wall.Id, new SlideThemeLook("digital", null, null));
        Assert.Equal("digital", outputs.Feed(SwiftUuid.Format(right.Id)).SlideThemeKey);
        Assert.Equal("digital", outputs.LookKey(right.Id));
    }

    [Fact]
    public void ThePresetsSlideThemeWinsAndOtherScreensKeepTheirOwn()
    {
        var outputs = new OutputConfiguration();
        var stream = outputs.AddScreen("Stream", 1920, 1080)!;
        var lobby = outputs.AddScreen("Lobby", 1920, 1080)!;
        outputs.SetScreenLook(stream.Id, new SlideThemeLook("digital", "Lower Thirds", null));
        outputs.SetScreenLook(lobby.Id, new SlideThemeLook("classic", null, null));
        var preset = new OutputPreset { Id = "p", Name = "Sermon", Assignments = [] };
        OutputPresets.SetSlideTheme(preset, OutputTargetKind.PlaceholderScreen, SwiftUuid.Format(stream.Id), "digital", "Side Thirds");
        var routing = OutputPresets.Apply(outputs, preset);

        Assert.Equal("digital#Side Thirds", outputs.Feed(SwiftUuid.Format(stream.Id)).SlideThemeKey); // the preset's look
        Assert.Equal("classic", outputs.Feed(SwiftUuid.Format(lobby.Id)).SlideThemeKey); // not routed: the screen's own
        Assert.Equal(["classic", "digital#Side Thirds"], outputs.ActiveLooks.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(outputs.ActiveLooks.Keys.Order(), outputs.LooksUnder(routing).Keys.Order());

        // What a slide switching to no preset would bring: each screen's own look again.
        Assert.Equal(["classic", "digital#Lower Thirds"], outputs.LooksUnder(PresetRouting.None).Keys.Order(StringComparer.Ordinal));

        OutputPresets.Apply(outputs, null);
        Assert.Equal("digital#Lower Thirds", outputs.Feed(SwiftUuid.Format(stream.Id)).SlideThemeKey);
        Assert.Empty(outputs.PresetLooks);
    }

    [Fact]
    public void StageScreensNeverNeedALook()
    {
        var outputs = new OutputConfiguration();
        var stage = outputs.AddScreen("Stage", 1920, 1080)!;
        outputs.SetRole(stage.Id, ScreenRole.Confidence);
        outputs.SetScreenLook(stage.Id, new SlideThemeLook("digital", null, null));
        Assert.Empty(outputs.ActiveLooks); // it shows the stage layout, not the slide
    }

    [Fact]
    public void TheLookIsSavedUnderTheMacsOutputPresetFieldNames()
    {
        var outputs = new OutputConfiguration();
        var stream = outputs.AddScreen("Stream", 1920, 1080)!;
        outputs.SetScreenLook(stream.Id, new SlideThemeLook("digital", "Lower Thirds", "lower-1"));
        var json = ScreenSetup.ToJson(ScreenSetup.Capture(outputs));
        using var document = JsonDocument.Parse(json);
        var saved = document.RootElement[0];
        Assert.Equal("digital", saved.GetProperty("slideThemeId").GetString());
        Assert.Equal("Lower Thirds", saved.GetProperty("slideThemeFolder").GetString());
        Assert.Equal("lower-1", saved.GetProperty("slideThemeSlideId").GetString());

        var restored = ScreenSetup.Load(ScreenSetup.FromJson(json));
        Assert.Equal(new SlideThemeLook("digital", "Lower Thirds", "lower-1"), restored.ScreenLook(stream.Id));

        var plain = new OutputConfiguration();
        plain.AddScreen("Projector", 1920, 1080);
        Assert.DoesNotContain("slideTheme", ScreenSetup.ToJson(ScreenSetup.Capture(plain)), StringComparison.Ordinal); // nothing written without a look
    }

    [Fact]
    public void RemovingAScreenForgetsItsLook()
    {
        var outputs = new OutputConfiguration();
        var stream = outputs.AddScreen("Stream", 1920, 1080)!;
        outputs.SetScreenLook(stream.Id, new SlideThemeLook("digital", null, null));
        outputs.RemoveScreen(stream.Id);
        Assert.Empty(outputs.ScreenLooks);
    }
}
