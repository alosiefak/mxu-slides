using System.Text.Json;
using System.Text.Json.Serialization;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Tests.Outputs.OutputFixtures;

namespace MxU.Slides.Core.Tests.Outputs;

// The saved setup (RenderContext.savePlaceholderScreens / restorePlaceholderScreens on the Mac).
public class ScreenSetupTests
{
    /// <summary>A setup that uses every saved setting at least once.</summary>
    private static OutputConfiguration Everything(out PlaceholderScreen wall, out PlaceholderScreen stage, out OutputSliceState lobby)
    {
        var outputs = new OutputConfiguration();
        wall = outputs.AddScreen("Main Wall", 3840, 1080, 50)!;
        stage = outputs.AddScreen("Stage", 1920, 1080)!;
        outputs.SetRole(stage.Id, ScreenRole.Confidence);
        outputs.SetScaleMode(wall.Id, OutputScaleMode.Fill);
        outputs.ApplyLayout(OutputLayoutKind.EdgeBlend, 2, 0.1, wall.Id);
        var tiles = outputs.ExtraSlices(wall.Id);
        outputs.AssignDevice(tiles[0].Id, OutputDevice.Display(@"\\.\DISPLAY2"), "Left projector");
        outputs.AssignDevice(tiles[1].Id, OutputDevice.DeckLink("1234"), "DeckLink Duo 2 (1)");
        outputs.SetVideoDelay(tiles[1].Id, 2);
        lobby = outputs.AddSlice(wall.Id, "Lobby")!;
        outputs.AssignDevice(lobby.Id, OutputDevice.Ndi("Lobby feed"));
        outputs.SetPlacement(lobby.Id, new OutputPlacement(1920, 1080, 0, 540, 1920, 540));
        outputs.SetAdjustments(wall.Id, new OutputAdjustments { TopLeft = new(12, -4), Gamma = 0.1 });
        outputs.SetMasks(wall.Id,
        [
            new OutputMask("Pillar", CenterBand, feather: 0.02),
            new OutputMask("Wings", LeftHalf, OutputMaskMode.In, alwaysOn: false, sliceIds: [lobby.Id]),
        ]);
        outputs.SetVideoDelay(stage.Id, 4);
        outputs.AssignDevice(stage.Id, OutputDevice.Display(@"\\.\DISPLAY3"), "Confidence");
        outputs.SetPlacement(stage.Id, new OutputPlacement(1920, 1080, 0, 0, 960, 1080));
        return outputs;
    }

    [Fact]
    public void SetupRoundTripsThroughJson()
    {
        var outputs = Everything(out var wall, out var stage, out var lobby);
        var json = ScreenSetup.ToJson(ScreenSetup.Capture(outputs));
        var restored = ScreenSetup.Load(ScreenSetup.FromJson(json));

        Assert.Equal(json, ScreenSetup.ToJson(ScreenSetup.Capture(restored)));
        Assert.Equal(outputs.Screens, restored.Screens);
        Assert.Equal(ScreenRole.Confidence, restored.Role(stage.Id));
        Assert.Equal(OutputScaleMode.Fill, restored.ScaleMode(wall.Id));
        Assert.Equal(OutputLayoutKind.Custom, restored.Layout(wall.Id)); // edge blend + a placed lobby feed
        Assert.Equal(outputs.ExtraSlices(wall.Id), restored.ExtraSlices(wall.Id));
        Assert.Equal(outputs.Devices.OrderBy(p => p.Key), restored.Devices.OrderBy(p => p.Key));
        Assert.Equal(outputs.DeviceNames.OrderBy(p => p.Key), restored.DeviceNames.OrderBy(p => p.Key));
        Assert.Equal(outputs.Masks(wall.Id), restored.Masks(wall.Id));
        Assert.Equal(outputs.Placement(lobby.Id), restored.Placement(lobby.Id));
        Assert.Equal(outputs.Adjustments(wall.Id), restored.Adjustments(wall.Id));
        Assert.Equal(4, restored.VideoDelay(stage.Id));
        Assert.False(restored.IsRunning(wall.Id)); // device-backed screens are paced by their devices again
    }

    [Fact]
    public void JsonUsesTheMacsFieldNamesAndEncodings()
    {
        var outputs = new OutputConfiguration();
        var id = Guid.Parse("e621e1f8-c36c-495a-93fc-0c247a3e6e5f");
        outputs.AddScreen("Main", 1920, 1080, id: id);
        outputs.SetRole(id, ScreenRole.Confidence);
        outputs.SetAdjustments(id, new OutputAdjustments { TopLeft = new(60, 40) });
        outputs.AssignDevice(id, "display:X", "Projector");
        var json = ScreenSetup.ToJson(ScreenSetup.Capture(outputs), indented: false);
        Assert.Equal(
            """[{"id":"E621E1F8-C36C-495A-93FC-0C247A3E6E5F","name":"Main","width":1920,"height":1080,"framesPerSecond":30,"deviceDisplayUUID":"display:X","deviceDisplayName":"Projector","role":"confidence","adjustments":{"topLeft":[60,40]}}]""",
            json);
    }

    [Fact]
    public void ReadsAMacWrittenSetup()
    {
        // As the Mac's JSONEncoder writes it: a display UUID with no prefix, CGPoints as arrays, keys in any order.
        const string mac = """
        [
          {
            "slices": [
              { "x": 0.5, "width": 0.5, "y": 0, "height": 1, "id": "11111111-2222-3333-4444-555555555555", "name": "Right",
                "adjustments": { "blendLeft": { "width": 0.12, "curve": 2.2 } } }
            ],
            "framesPerSecond": 60, "height": 1080, "width": 3840, "name": "Wall",
            "id": "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE",
            "deviceDisplayUUID": "37D8832A-2D66-02CA-B9F7-8F30A301B230", "deviceDisplayName": "LG UltraFine",
            "scaleMode": "fill", "videoDelayFrames": 3,
            "masks": [ { "id": "99999999-8888-7777-6666-555555555555", "name": "Pillar", "pathData": "M 0 0 L 1 0 L 1 1 Z",
                         "mode": "out", "feather": 0, "alwaysOn": true } ],
            "placement": { "frameWidth": 3840, "frameHeight": 2160, "x": 0, "y": 0, "width": 3840, "height": 1080 }
          }
        ]
        """;
        var outputs = ScreenSetup.Load(ScreenSetup.FromJson(mac));
        var wall = Assert.Single(outputs.Screens);
        Assert.Equal(new PlaceholderScreen(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "Wall", 3840, 1080, 60), wall);
        Assert.Equal("37D8832A-2D66-02CA-B9F7-8F30A301B230", outputs.Device(wall.Id));
        Assert.Equal(OutputDeviceKind.Display, OutputDevice.Kind(outputs.Device(wall.Id)!)); // a bare id is a display
        Assert.Equal(OutputScaleMode.Fill, outputs.ScaleMode(wall.Id));
        Assert.Equal(3, outputs.VideoDelay(wall.Id));
        Assert.Equal(new SceneRect(0.5, 0, 0.5, 1), outputs.ExtraSlices(wall.Id)[0].SourceRect);
        Assert.Equal(0.12, outputs.Adjustments(Guid.Parse("11111111-2222-3333-4444-555555555555")).BlendLeft?.Width);
        Assert.Equal(OutputMaskMode.Out, outputs.Masks(wall.Id)[0].Mode);
        Assert.Equal(3840, outputs.Placement(wall.Id)?.FrameWidth);
    }

    [Fact]
    public void PackedFramesSurviveARestart()
    {
        var outputs = new OutputConfiguration();
        var wall = outputs.AddScreen("Wall", 832, 416)!;
        var strip = outputs.AddScreen("Strip", 3840, 540)!;
        outputs.AssignDevice(wall.Id, "FRAME-4K");
        outputs.AssignDevice(strip.Id, "FRAME-4K", exclusive: false);
        var restored = ScreenSetup.Load(ScreenSetup.Capture(outputs));
        Assert.Equal("FRAME-4K", restored.Device(wall.Id));
        Assert.Equal("FRAME-4K", restored.Device(strip.Id));
    }

    [Fact]
    public void UnreadableOrIncompleteSetupsRestoreNothing()
    {
        Assert.Empty(ScreenSetup.FromJson("not json"));
        Assert.Empty(ScreenSetup.FromJson("""[{"name":"No id","width":1,"height":1,"framesPerSecond":30}]"""));
        Assert.Empty(ScreenSetup.FromJson("""[{"id":"not-a-uuid","name":"x","width":1,"height":1,"framesPerSecond":30}]"""));
        // A screen with an impossible size is skipped along with everything under it.
        var outputs = ScreenSetup.Load(ScreenSetup.FromJson(
            """[{"id":"E621E1F8-C36C-495A-93FC-0C247A3E6E5F","name":"Zero","width":0,"height":1080,"framesPerSecond":30,"role":"confidence"}]"""));
        Assert.Empty(outputs.Screens);
        Assert.Empty(outputs.Roles);
    }

    [Fact]
    public void UnknownRoleAndScaleStringsAreIgnored()
    {
        var outputs = ScreenSetup.Load(ScreenSetup.FromJson(
            """[{"id":"E621E1F8-C36C-495A-93FC-0C247A3E6E5F","name":"S","width":10,"height":10,"framesPerSecond":30,"role":"director","scaleMode":"stretch"}]"""));
        var screen = Assert.Single(outputs.Screens);
        Assert.Equal(ScreenRole.Audience, outputs.Role(screen.Id));
        Assert.Equal(OutputScaleMode.Fit, outputs.ScaleMode(screen.Id));
    }

    [Fact]
    public void LoadingDoesNotTriggerASave()
    {
        var saved = ScreenSetup.Capture(Everything(out _, out _, out _));
        var outputs = ScreenSetup.Load(saved);
        var saves = 0;
        outputs.ConfigurationChanged += () => saves++;
        Assert.Equal(0, saves);
        outputs.SetVideoDelay(outputs.Screens[0].Id, 1);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void EncodesTheSameInsideTheWindowsSettingsFile()
    {
        // AppSettings uses its own options (JsonStringEnumConverter, nulls written). The
        // attributes on the types keep the setup Mac-shaped there too.
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var setup = ScreenSetup.Capture(Everything(out _, out _, out _));
        var embedded = JsonSerializer.Serialize(setup, options);
        Assert.Equal(ScreenSetup.ToJson(setup, indented: false), embedded);
        Assert.Contains("\"mode\":\"in\"", embedded);
        Assert.DoesNotContain("null", embedded);
        Assert.Equal(ScreenSetup.ToJson(setup), ScreenSetup.ToJson(JsonSerializer.Deserialize<List<PlaceholderScreenConfig>>(embedded, options)!));
    }

    [Fact]
    public void FirstRunMakesAnAudienceAndAStageScreen()
    {
        var setup = ScreenSetup.FirstRun(OutputDevice.Display(@"\\.\DISPLAY2"), "Projector");
        var outputs = ScreenSetup.Load(setup);
        Assert.Equal(["Audience", "Stage"], outputs.Screens.Select(screen => screen.Name));
        var (audience, stage) = (outputs.Screens[0], outputs.Screens[1]);
        Assert.Equal(ScreenRole.Audience, outputs.Role(audience.Id));
        Assert.Equal(ScreenRole.Confidence, outputs.Role(stage.Id));
        Assert.Equal(@"display:\\.\DISPLAY2", outputs.Device(audience.Id));
        Assert.Equal("Projector", outputs.DeviceName(audience.Id));
        Assert.Null(outputs.Device(stage.Id));
        Assert.Equal(new SceneSize(1920, 1080), audience.Size);
    }
}

public class OutputDeviceTests
{
    [Fact]
    public void IdsCarryTheirKind()
    {
        Assert.Equal(@"display:\\.\DISPLAY1", OutputDevice.Display(@"\\.\DISPLAY1"));
        Assert.Equal(OutputDeviceKind.Ndi, OutputDevice.Kind(OutputDevice.Ndi("Stream")));
        Assert.Equal(OutputDeviceKind.DeckLink, OutputDevice.Kind(OutputDevice.DeckLink("42")));
        Assert.Equal(OutputDeviceKind.Display, OutputDevice.Kind(OutputDevice.Display("X")));
        Assert.Equal("Stream: Main", OutputDevice.Value(OutputDevice.Ndi("Stream: Main")));
        Assert.Equal("42", OutputDevice.Value("decklink:42"));
        Assert.Equal(@"\\.\DISPLAY1", OutputDevice.Value(@"display:\\.\DISPLAY1"));
        Assert.Equal("37D8832A", OutputDevice.Value("37D8832A")); // a Mac display UUID, unprefixed
    }

    [Fact]
    public void SwiftUuidsAreUppercaseAndReadInAnyCase()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id.ToString().ToUpperInvariant(), SwiftUuid.Format(id));
        Assert.Equal(id, SwiftUuid.Parse(id.ToString()));
        Assert.Null(SwiftUuid.Parse("display:X"));
    }
}
