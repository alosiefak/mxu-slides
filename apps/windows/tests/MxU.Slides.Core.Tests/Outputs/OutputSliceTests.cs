using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Tests.Outputs.OutputFixtures;

namespace MxU.Slides.Core.Tests.Outputs;

// Ported from OutputEngineTests/OutputSliceTests.swift (OutputSliceTests,
// AlignmentGridTests, PackedFrameTests, OutputPlacementTests). The packed
// OutputMirror pixel test is ported as the placement geometry it checks.
public class OutputSliceTests
{
    private static (OutputConfiguration Outputs, PlaceholderScreen Screen) MakeScreen()
    {
        var outputs = new OutputConfiguration();
        return (outputs, outputs.AddScreen("Wide", 384, 108)!);
    }

    [Fact]
    public void DefaultSliceIsTheScreenItself()
    {
        var (outputs, screen) = MakeScreen();
        var slices = outputs.Slices(screen.Id);
        Assert.Equal([screen.Id], slices.Select(slice => slice.Id)); // the implicit default carries the screen id
        Assert.Equal(OutputPass.FullSourceRect, slices[0].SourceRect);
        var info = outputs.SliceInfo(screen.Id);
        Assert.NotNull(info);
        Assert.Equal(screen.Id, info.ScreenId);
        Assert.Equal(384, info.Width);
        Assert.Equal(108, info.Height);
    }

    [Fact]
    public void ExtraSlicesResolveRegionAndPixelSize()
    {
        var (outputs, screen) = MakeScreen();
        var left = outputs.AddSlice(screen.Id, "Left", new SceneRect(0, 0, 0.5, 1))!;
        Assert.Equal(2, outputs.Slices(screen.Id).Count);
        var info = outputs.SliceInfo(left.Id)!;
        Assert.Equal(screen.Id, info.ScreenId);
        Assert.Equal(192, info.Width); // half a 384-wide canvas
        Assert.Equal(108, info.Height);
        Assert.Equal("Left", info.Name);

        outputs.SetSliceSourceRect(left.Id, new SceneRect(0.9, -1, 5, 0));
        var clamped = outputs.SliceInfo(left.Id)!.SourceRect;
        Assert.Equal(0.9, clamped.X, 0.001);
        Assert.Equal(0, clamped.Y, 0.001);
        Assert.True(clamped.Right <= 1.0001);
        Assert.True(clamped.Height > 0);
    }

    [Fact]
    public void DisplayExclusivityIsPerDisplayNotPerScreen()
    {
        var (outputs, screen) = MakeScreen();
        var left = outputs.AddSlice(screen.Id, sourceRect: new SceneRect(0, 0, 0.5, 1))!;
        var right = outputs.AddSlice(screen.Id, sourceRect: new SceneRect(0.5, 0, 0.5, 1))!;

        outputs.AssignDevice(left.Id, "DISPLAY-A");
        outputs.AssignDevice(right.Id, "DISPLAY-B");
        Assert.Equal("DISPLAY-A", outputs.Device(left.Id));
        Assert.Equal("DISPLAY-B", outputs.Device(right.Id)); // two slices of ONE screen hold two displays

        outputs.AssignDevice(right.Id, "DISPLAY-A");
        Assert.Null(outputs.Device(left.Id)); // DISPLAY-A stolen from the left slice
        Assert.Equal("DISPLAY-A", outputs.Device(right.Id));
    }

    [Fact]
    public void ClaimedSlicePausesTheOffscreenTimer()
    {
        var (outputs, screen) = MakeScreen();
        Assert.True(outputs.IsRunning(screen.Id)); // unbacked screens pace themselves
        var slice = outputs.AddSlice(screen.Id, sourceRect: new SceneRect(0, 0, 0.5, 1))!;
        outputs.AssignDevice(slice.Id, "DISPLAY-A");
        Assert.False(outputs.IsRunning(screen.Id)); // any claimed slice means glass paces the scene
        outputs.AssignDevice(slice.Id, null);
        Assert.True(outputs.IsRunning(screen.Id)); // unclaimed again = self-paced again
    }

    [Fact]
    public void RemoveSliceClearsEveryStore()
    {
        var (outputs, screen) = MakeScreen();
        var slice = outputs.AddSlice(screen.Id, sourceRect: new SceneRect(0.5, 0, 0.5, 1))!;
        outputs.AssignDevice(slice.Id, "DISPLAY-A");
        outputs.SetAdjustments(slice.Id, new OutputAdjustments { Brightness = -0.2 });
        outputs.SetVideoDelay(slice.Id, 3);

        outputs.RemoveSlice(slice.Id);
        Assert.Single(outputs.Slices(screen.Id));
        Assert.Null(outputs.SliceInfo(slice.Id));
        Assert.Null(outputs.Device(slice.Id));
        Assert.False(outputs.StoredAdjustments.ContainsKey(slice.Id));
        Assert.Equal(0, outputs.VideoDelay(slice.Id));
    }

    [Fact]
    public void RemoveScreenClearsItsSlices()
    {
        var (outputs, screen) = MakeScreen();
        var slice = outputs.AddSlice(screen.Id)!;
        outputs.AssignDevice(slice.Id, "DISPLAY-A");
        outputs.RemoveScreen(screen.Id);
        Assert.Null(outputs.SliceInfo(slice.Id));
        Assert.Null(outputs.Device(slice.Id));
        Assert.Empty(outputs.Slices(screen.Id));
    }

    [Fact]
    public void SliceCrudFiresPersistence()
    {
        var (outputs, screen) = MakeScreen();
        var saves = 0;
        outputs.ConfigurationChanged += () => saves++;
        var slice = outputs.AddSlice(screen.Id)!;
        Assert.Equal(1, saves);
        outputs.SetSliceSourceRect(slice.Id, new SceneRect(0, 0, 0.5, 1));
        Assert.Equal(2, saves);
        outputs.SetSliceSourceRect(slice.Id, new SceneRect(0, 0, 0.5, 1));
        Assert.Equal(2, saves); // no-op region writes don't churn the save path
        outputs.SetSliceName(slice.Id, "Left");
        Assert.Equal(3, saves);
        outputs.RemoveSlice(slice.Id);
        Assert.Equal(4, saves);
    }

    [Fact]
    public void ProviderNormalizesSliceIdsToTheOwningScreen()
    {
        var (outputs, screen) = MakeScreen();
        outputs.SetRole(screen.Id, ScreenRole.Confidence);
        var marker = new RenderScene(new SceneSize(384, 108));
        marker.AddItem(Solid("marker", new SceneRect(0, 0, 10, 10), SceneColor.White), LayerKind.Slide);
        outputs.ConfidenceSceneProvider = _ => marker;
        var slice = outputs.AddSlice(screen.Id, sourceRect: new SceneRect(0.5, 0, 0.5, 1))!;
        var scene = outputs.PreviewProvider(SwiftUuid.Format(slice.Id))();
        Assert.Same(marker, scene); // the slice composites its screen's scene
    }

    [Fact]
    public void MaskSliceScoping()
    {
        var (outputs, screen) = MakeScreen();
        outputs.MaskPathFill = FillPolygon;
        var mirror = outputs.AddSlice(screen.Id, "Lobby")!;
        outputs.SetMasks(screen.Id, [new OutputMask("Column", CenterBand, sliceIds: [mirror.Id])]);
        Assert.Null(outputs.MaskProvider(SwiftUuid.Format(screen.Id))()); // scoped mask leaves the default output alone
        Assert.NotNull(outputs.MaskProvider(SwiftUuid.Format(mirror.Id))()); // scoped mask covers its slice

        outputs.SetMasks(screen.Id, [new OutputMask("Column", CenterBand)]);
        var defaultRaster = outputs.MaskProvider(SwiftUuid.Format(screen.Id))();
        var mirrorRaster = outputs.MaskProvider(SwiftUuid.Format(mirror.Id))();
        Assert.NotNull(defaultRaster);
        Assert.Same(defaultRaster, mirrorRaster); // unscoped masks share one raster across a mirror's slices
    }

    // ── AlignmentGridTests ──────────────────────────────────────────────────

    [Fact]
    public void TestPatternReplacesProgramOnEveryOutput()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Wall", 384, 108)!;
        var program = new RenderScene(new SceneSize(384, 108));
        outputs.SceneProvider = () => program;
        var slice = outputs.AddSlice(screen.Id, sourceRect: new SceneRect(0, 0, 0.5, 1))!;

        Assert.Same(program, outputs.PreviewProvider(SwiftUuid.Format(screen.Id))());
        outputs.SetTestPattern(screen.Id, true);
        var onDefault = outputs.PreviewProvider(SwiftUuid.Format(screen.Id))();
        var onSlice = outputs.PreviewProvider(SwiftUuid.Format(slice.Id))();
        Assert.NotSame(program, onDefault); // the grid replaces program
        SameScene(onDefault, onSlice); // every output of the screen shows the same grid
        Assert.True(onDefault.AllItems.Count() > 20, "the grid actually carries lines");
        outputs.SetTestPattern(screen.Id, false);
        Assert.Same(program, outputs.PreviewProvider(SwiftUuid.Format(screen.Id))());
    }

    [Fact]
    public void SyncTestReplacesProgramAndDrivesOnePlayerAcrossScreens()
    {
        var outputs = new OutputConfiguration();
        var wall = outputs.AddScreen("Wall", 384, 108)!;
        var stream = outputs.AddScreen("Stream", 192, 108)!;
        var program = new RenderScene(new SceneSize(384, 108));
        outputs.SceneProvider = () => program;
        var slice = outputs.AddSlice(wall.Id, sourceRect: new SceneRect(0, 0, 0.5, 1))!;
        var playback = new List<bool>();
        outputs.SyncTestPlaybackChanged += playback.Add;

        outputs.SetSyncTest(wall.Id, true);
        var onWall = outputs.PreviewProvider(SwiftUuid.Format(wall.Id))();
        SameScene(onWall, outputs.PreviewProvider(SwiftUuid.Format(slice.Id))());
        Assert.Equal(
            new ItemContent[] { new ItemContent.Media(OutputConfiguration.SyncTestMediaId, SceneMediaScaleMode.Fit) },
            onWall.AllItems.Select(item => item.Content));
        Assert.Equal(new SceneSize(384, 108), onWall.CanvasSize);

        SameScene(
            program.Scaled(new SceneSize(192, 108), OutputScaleMode.Fit),
            outputs.PreviewProvider(SwiftUuid.Format(stream.Id))());
        outputs.SetSyncTest(stream.Id, true);
        Assert.Equal(new SceneSize(192, 108), outputs.PreviewProvider(SwiftUuid.Format(stream.Id))().CanvasSize);
        outputs.SetSyncTest(wall.Id, false);
        Assert.Same(program, outputs.PreviewProvider(SwiftUuid.Format(wall.Id))());
        outputs.SetSyncTest(stream.Id, false);
        Assert.Equal([true, false], playback); // one player, from the first screen on to the last off
        outputs.SetTestPattern(wall.Id, true);
        outputs.SetSyncTest(wall.Id, true);
        Assert.True(outputs.PreviewProvider(SwiftUuid.Format(wall.Id))().AllItems.Count() > 20, "the rigging grid outranks the sync test");
    }

    // ── PackedFrameTests ────────────────────────────────────────────────────

    [Fact]
    public void NonExclusiveClaimsShareADisplay()
    {
        var outputs = new OutputConfiguration();
        var wall = outputs.AddScreen("Wall", 832, 416)!;
        var strip = outputs.AddScreen("Strip", 3840, 540)!;

        outputs.AssignDevice(wall.Id, "FRAME-4K");
        outputs.AssignDevice(strip.Id, "FRAME-4K", exclusive: false);
        Assert.Equal("FRAME-4K", outputs.Device(wall.Id)); // alongside-claims keep the incumbent
        Assert.Equal("FRAME-4K", outputs.Device(strip.Id));
        Assert.Equal(2, outputs.CarriedSlices("FRAME-4K").Count);

        var projector = outputs.AddScreen("Projector", 1920, 1080)!;
        outputs.AssignDevice(projector.Id, "FRAME-4K");
        Assert.Null(outputs.Device(wall.Id));
        Assert.Null(outputs.Device(strip.Id));
        Assert.Equal("FRAME-4K", outputs.Device(projector.Id));
    }

    [Fact]
    public void CompositeMirrorPacksTwoScreensIntoOneFeed()
    {
        // The Mac renders this through OutputMirror and reads pixels back; here, the
        // placement geometry that decides which layer covers each pixel of the 320×180 frame.
        var none = new OutputAdjustments();
        var wall = OutputPass.Geometry(none, OutputPass.FullSourceRect, new SceneRect(0, 0, 0.5, 0.5), 320, 180);
        var strip = OutputPass.Geometry(none, OutputPass.FullSourceRect, new SceneRect(0, 0.75, 1, 0.25), 320, 180);
        bool Covers(OutputPassGeometry layer, int x, int y) =>
            layer.SourceUv((x + 0.5) / 320, (y + 0.5) / 180) is { } uv && uv.U is >= 0 and <= 1 && uv.V is >= 0 and <= 1;
        Assert.True(Covers(wall, 80, 40), "wall lands top-left");
        Assert.True(Covers(strip, 160, 160), "strip lands along the bottom");
        Assert.False(Covers(wall, 240, 40) || Covers(strip, 240, 40), "unclaimed frame area is black");
    }

    // ── OutputPlacementTests ────────────────────────────────────────────────

    [Fact]
    public void PlacementStoreRoundTripsAndCleansUp()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Wall", 832, 416)!;
        var saves = 0;
        outputs.ConfigurationChanged += () => saves++;

        var placement = new OutputPlacement(1920, 1080, 0, 0, 832, 416);
        outputs.SetPlacement(screen.Id, placement);
        Assert.Equal(1, saves); // placements persist
        Assert.Equal(placement, outputs.Placement(screen.Id));
        var unit = outputs.Placement(screen.Id)!.UnitRect;
        Assert.Equal(0, unit.X, 0.0001);
        Assert.Equal(832.0 / 1920.0, unit.Width, 0.0001);

        outputs.SetPlacement(screen.Id, placement);
        Assert.Equal(1, saves); // no-op writes don't churn the save path

        outputs.SetPlacement(screen.Id, new OutputPlacement(1920, 1080, 0, 0, 1920, 1080));
        Assert.Null(outputs.Placement(screen.Id)); // a fill placement is no placement

        outputs.SetPlacement(screen.Id, placement);
        outputs.RemoveScreen(screen.Id);
        Assert.Null(outputs.Placement(screen.Id)); // removal clears the store
    }

    [Fact]
    public void SlicePlacementClearsWithTheSlice()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Wall", 832, 416)!;
        var slice = outputs.AddSlice(screen.Id, "Banner")!;
        outputs.SetPlacement(slice.Id, new OutputPlacement(1920, 1080, 1920 - 384, 0, 384, 768));
        Assert.NotNull(outputs.Placement(slice.Id));
        outputs.RemoveSlice(slice.Id);
        Assert.Null(outputs.Placement(slice.Id));
    }

    // ── Beyond the Mac tests ────────────────────────────────────────────────

    [Fact]
    public void ClampingKeepsTheRegionOnTheCanvasAndAtLeastOnePercent()
    {
        Assert.Equal(new SceneRect(0, 0, 0.01, 0.01), OutputConfiguration.ClampedUnit(new SceneRect(-1, -1, 0, 0)));
        Assert.Equal(new SceneRect(0.5, 0.5, 0.5, 0.5), OutputConfiguration.ClampedUnit(new SceneRect(0.5, 0.5, 2, 2)));
        // A negative width counts back from x, as a CGRect's would.
        var flipped = OutputConfiguration.ClampedUnit(new SceneRect(0.6, 0, -0.2, 1));
        Assert.Equal(0.4, flipped.X, 1e-9);
        Assert.Equal(0.2, flipped.Width, 1e-9);
    }

    [Fact]
    public void DefaultSliceCannotBeRemovedRenamedOrMoved()
    {
        var (outputs, screen) = MakeScreen();
        var saves = 0;
        outputs.ConfigurationChanged += () => saves++;
        outputs.RemoveSlice(screen.Id);
        outputs.SetSliceName(screen.Id, "Nope");
        outputs.SetSliceSourceRect(screen.Id, new SceneRect(0, 0, 0.5, 1));
        Assert.Equal(0, saves);
        Assert.Single(outputs.Slices(screen.Id));
        Assert.Null(outputs.AddSlice(Guid.NewGuid())); // slices need a screen
    }

    [Fact]
    public void EmptySliceNameClearsIt()
    {
        var (outputs, screen) = MakeScreen();
        var slice = outputs.AddSlice(screen.Id, "Lobby")!;
        outputs.SetSliceName(slice.Id, "");
        Assert.Null(outputs.SliceInfo(slice.Id)!.Name);
    }

    [Fact]
    public void SliceSizeRoundsHalvesAwayFromZero()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Odd", 101, 3)!;
        var half = outputs.AddSlice(screen.Id, sourceRect: new SceneRect(0, 0, 0.5, 0.5))!;
        var info = outputs.SliceInfo(half.Id)!;
        Assert.Equal(51, info.Width); // 50.5 rounds up, as Swift's rounded()
        Assert.Equal(2, info.Height); // 1.5 rounds up
    }

    [Fact]
    public void ReconfiguringKeepsSlicesAndPacing()
    {
        var (outputs, screen) = MakeScreen();
        var slice = outputs.AddSlice(screen.Id, "Lobby")!;
        Assert.Null(outputs.ReconfigureScreen(screen.Id, "Bad", 0, 108));
        var resized = outputs.ReconfigureScreen(screen.Id, "Wider", 768, 108, 60)!;
        Assert.Equal(new PlaceholderScreen(screen.Id, "Wider", 768, 108, 60), resized);
        Assert.Equal(2, outputs.Slices(screen.Id).Count);
        Assert.Equal(768, outputs.SliceInfo(slice.Id)!.Width); // a mirror slice follows the new size
        Assert.True(outputs.IsRunning(screen.Id));

        outputs.AssignDevice(screen.Id, "DISPLAY-A");
        outputs.ReconfigureScreen(screen.Id, "Wider", 768, 108, 30);
        Assert.False(outputs.IsRunning(screen.Id)); // a device-backed screen is paced by its device
    }

    [Fact]
    public void CanvasWakeKeepsABackedScreenRenderingWhileItsSettingsAreOpen()
    {
        var (outputs, screen) = MakeScreen();
        outputs.AssignDevice(screen.Id, "DISPLAY-A");
        Assert.False(outputs.IsRunning(screen.Id));
        outputs.SetCanvasWake(screen.Id, true);
        Assert.True(outputs.IsRunning(screen.Id));
        outputs.SetCanvasWake(screen.Id, false);
        Assert.False(outputs.IsRunning(screen.Id));
    }

    [Fact]
    public void ZeroSizeOrRateIsRejected()
    {
        // From PlaceholderScreenTests.testZeroSizeIsRejected.
        var outputs = new OutputConfiguration();
        Assert.Null(outputs.AddScreen("bad", 0, 1080));
        Assert.Null(outputs.AddScreen("bad", 1080, 1080, framesPerSecond: 0));
        Assert.Empty(outputs.Screens);
    }

    [Fact]
    public void GoingLiveTakesTheDisplayFromEveryScreenThatHadIt()
    {
        var outputs = new OutputConfiguration();
        var a = outputs.AddScreen("A", 960, 540)!;
        var b = outputs.AddScreen("B", 960, 540)!;
        outputs.AssignDevice(a.Id, "DISPLAY-A", "Projector");
        outputs.AssignDevice(b.Id, "DISPLAY-A", exclusive: false);
        var saves = 0;
        outputs.ConfigurationChanged += () => saves++;
        outputs.SetLive("DISPLAY-A", true);
        Assert.True(outputs.IsLive("DISPLAY-A"));
        Assert.Null(outputs.Device(a.Id));
        Assert.Null(outputs.Device(b.Id));
        Assert.Null(outputs.DeviceName(a.Id));
        Assert.Equal(1, saves);

        // Assigning it to a screen again ends the direct output.
        outputs.AssignDevice(a.Id, "DISPLAY-A");
        Assert.False(outputs.IsLive("DISPLAY-A"));
    }

    [Fact]
    public void AssignedNamesComeFromTheCallerOrTheConnectedDisplay()
    {
        var (outputs, screen) = MakeScreen();
        outputs.UpdateDisplays([new DisplaySnapshot(OutputDevice.Display(@"\\.\DISPLAY2"), "Epson", new SceneRect(1920, 0, 1920, 1080))]);
        outputs.AssignDevice(screen.Id, OutputDevice.Display(@"\\.\DISPLAY2"));
        Assert.Equal("Epson", outputs.DeviceName(screen.Id));
        outputs.AssignDevice(screen.Id, OutputDevice.Ndi("Stream"), "Stream feed");
        Assert.Equal("Stream feed", outputs.DeviceName(screen.Id));
        outputs.AssignDevice(screen.Id, null);
        Assert.Null(outputs.DeviceName(screen.Id));
    }

    [Fact]
    public void CarriesListEachDevicesSlicesAndPackOrderIsById()
    {
        var outputs = new OutputConfiguration();
        var wall = outputs.AddScreen("Wall", 832, 416)!;
        var banner = outputs.AddSlice(wall.Id, "Banner")!;
        outputs.AssignDevice(banner.Id, "FRAME");
        outputs.AssignDevice(wall.Id, "FRAME", exclusive: false);
        var carries = outputs.Carries();
        Assert.Equal([new OutputCarry(wall.Id, wall.Id), new OutputCarry(banner.Id, wall.Id)], carries["FRAME"]);
        var packed = outputs.CarriedSlices("FRAME").Select(carry => carry.SliceId).ToList();
        Assert.Equal(new[] { wall.Id, banner.Id }.OrderBy(SwiftUuid.Format, StringComparer.Ordinal), packed);
        Assert.Empty(outputs.CarriedSlices("NOTHING"));
    }

    [Fact]
    public void StateChangedFiresForRuntimeChangesThatAreNotSaved()
    {
        var (outputs, screen) = MakeScreen();
        int saves = 0, redraws = 0;
        outputs.ConfigurationChanged += () => saves++;
        outputs.StateChanged += () => redraws++;
        outputs.SetTestPattern(screen.Id, true);
        outputs.SetActiveMaskIds(screen.Id, [Guid.NewGuid()]);
        Assert.Equal(0, saves);
        Assert.Equal(2, redraws);
        outputs.SetRole(screen.Id, ScreenRole.Confidence);
        Assert.Equal(1, saves);
        Assert.Equal(3, redraws);
    }
}
