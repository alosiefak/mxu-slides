using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Outputs;

// The non-UI logic of the Mac's ScreenConfigurationView.swift and OutputsSection.swift.
public class ScreenEditingTests
{
    [Fact]
    public void NewScreensAreNumberedAndOfferedInThreeSizes()
    {
        var outputs = new OutputConfiguration();
        Assert.Equal("Screen 1", ScreenEditing.AddScreen(outputs)!.Name);
        var vertical = ScreenEditing.AddScreenSizes[2];
        var second = ScreenEditing.AddScreen(outputs, vertical.Width, vertical.Height)!;
        Assert.Equal(("Screen 2", 1080, 1920), (second.Name, second.Width, second.Height));
        Assert.Equal([24, 25, 30, 50, 60], ScreenEditing.FrameRates);
    }

    [Fact]
    public void IdentityEditsAreValidatedAndKeepTheRate()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Main", 1920, 1080, 50)!;
        var saves = 0;
        outputs.ConfigurationChanged += () => saves++;
        Assert.False(ScreenEditing.CommitIdentity(outputs, screen.Id, "  ", "1920", "1080"));
        Assert.False(ScreenEditing.CommitIdentity(outputs, screen.Id, "Main", "wide", "1080"));
        Assert.False(ScreenEditing.CommitIdentity(outputs, screen.Id, "Main", "0", "1080"));
        Assert.True(ScreenEditing.CommitIdentity(outputs, screen.Id, " Main ", "1920", "1080"));
        Assert.Equal(0, saves); // nothing changed
        Assert.True(ScreenEditing.CommitIdentity(outputs, screen.Id, "Wall", "3840", "1080"));
        Assert.Equal(new PlaceholderScreen(screen.Id, "Wall", 3840, 1080, 50), outputs.Screen(screen.Id));
        ScreenEditing.SetFrameRate(outputs, screen.Id, 25);
        Assert.Equal(25, outputs.Screen(screen.Id)!.FramesPerSecond);
    }

    [Fact]
    public void StatusLinesSayWhatCarriesTheScreen()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Main", 1920, 1080)!;
        Assert.Equal(new ScreenStatusLine("1920×1080 · placeholder", null), ScreenEditing.Status(outputs, screen));
        outputs.SetRole(screen.Id, ScreenRole.Confidence);
        outputs.AssignDevice(screen.Id, "display:A", "Epson");
        Assert.Equal(new ScreenStatusLine("1920×1080 · confidence · disconnected: Epson", false), ScreenEditing.Status(outputs, screen));
        outputs.UpdateDisplays([new DisplaySnapshot("display:A", "EPSON PJ", new SceneRect(0, 0, 1920, 1080))]);
        Assert.Equal(new ScreenStatusLine("1920×1080 · confidence · EPSON PJ", true), ScreenEditing.Status(outputs, screen));
        outputs.AssignDevice(screen.Id, OutputDevice.Ndi("Stage feed"));
        Assert.Equal("1920×1080 · confidence · NDI “Stage feed”", ScreenEditing.Status(outputs, screen).Text);
        Assert.Equal("NDI", ScreenEditing.BackingSummary(outputs, screen.Id));
        Assert.Equal("Unconfigured", ScreenEditing.BackingSummary(outputs, Guid.NewGuid()));
    }

    [Fact]
    public void LayoutPickerOffersCustomOnlyWhileCustomAndExplainsEachShape()
    {
        Assert.DoesNotContain(OutputLayoutKind.Custom, ScreenEditing.LayoutChoices(OutputLayoutKind.Single));
        Assert.Contains(OutputLayoutKind.Custom, ScreenEditing.LayoutChoices(OutputLayoutKind.Custom));
        Assert.All(OutputLayoutKinds.All, kind => Assert.NotEmpty(ScreenEditing.LayoutHelp(kind)));
    }

    [Fact]
    public void ChoosingEdgeBlendBuildsTwoTilesWithTheDefaultOverlap()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Wall", 3840, 1080)!;
        ScreenEditing.ChooseLayout(outputs, screen.Id, OutputLayoutKind.EdgeBlend);
        var seam = Assert.Single(ScreenEditing.Seams(outputs, screen.Id));
        Assert.Equal(0.06, seam.Overlap, 1e-9);
        Assert.Equal(230, ScreenEditing.OverlapPixels(screen, seam)); // 0.06 × 3840 = 230.4
        Assert.Equal(2, ScreenEditing.OutputCount(outputs, screen.Id));
    }

    [Fact]
    public void TheStepperGrowsAndShrinksAMirror()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Main", 1920, 1080)!;
        ScreenEditing.ChooseLayout(outputs, screen.Id, OutputLayoutKind.Mirror);
        Assert.Equal(2, ScreenEditing.OutputCount(outputs, screen.Id));
        ScreenEditing.SetOutputCount(outputs, screen.Id, 4);
        Assert.Equal(["Mirror 2", "Mirror 3", "Mirror 4"], outputs.ExtraSlices(screen.Id).Select(slice => slice.Name));
        var released = new List<Guid>();
        var last = outputs.ExtraSlices(screen.Id)[^1].Id;
        ScreenEditing.SetOutputCount(outputs, screen.Id, 3, released.Add);
        Assert.Equal([last], released);
        Assert.Equal(3, ScreenEditing.OutputCount(outputs, screen.Id));
    }

    [Fact]
    public void TheStepperRebuildsAWallKeepingItsOverlap()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Wall", 3840, 1080)!;
        outputs.ApplyLayout(OutputLayoutKind.EdgeBlend, 2, 0.1, screen.Id);
        ScreenEditing.SetOutputCount(outputs, screen.Id, 3);
        var seams = ScreenEditing.Seams(outputs, screen.Id);
        Assert.Equal(2, seams.Count);
        Assert.All(seams, seam => Assert.Equal(0.1, seam.Overlap, 1e-9));
        Assert.Equal(["Left", "Center", "Right"], ScreenEditing.TilesByPosition(outputs, screen.Id).Select(tile => tile.Name));
    }

    [Fact]
    public void ChangingTheOverlapRetilesAndResizesRampsKeepingTheirShape()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Wall", 3840, 1080)!;
        outputs.ApplyLayout(OutputLayoutKind.EdgeBlend, 2, 0.06, screen.Id);
        var left = ScreenEditing.TilesByPosition(outputs, screen.Id)[0];
        outputs.SetAdjustments(left.Id, outputs.Adjustments(left.Id) with { BlendRight = new OutputEdgeBlend(0.1, curve: 1.5, blackLift: 0.05) });

        ScreenEditing.SetOverlap(outputs, screen.Id, 0.2);
        var tiles = ScreenEditing.TilesByPosition(outputs, screen.Id);
        Assert.Equal(0.6, tiles[0].SourceRect.Width, 1e-9);
        var blend = outputs.Adjustments(tiles[0].Id).BlendRight!;
        Assert.Equal(0.2 / 0.6, blend.Width, 1e-9);
        Assert.Equal((1.5, 0.05), (blend.Curve, blend.BlackLift)); // the seam's tuning survives
        Assert.Null(outputs.Adjustments(tiles[0].Id).BlendLeft); // the outer edge still never ramps
        Assert.Equal(0.2 / 0.6, outputs.Adjustments(tiles[1].Id).BlendLeft!.Width, 1e-9);
    }

    [Fact]
    public void TitlesPlacementsAndSteps()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Wall", 832, 416)!;
        var whole = outputs.Slices(screen.Id)[0];
        Assert.Equal("Main", ScreenEditing.SliceTitle(screen.Id, whole, OutputLayoutKind.Mirror));
        Assert.Equal("Full Canvas", ScreenEditing.SliceTitle(screen.Id, whole, OutputLayoutKind.Custom));
        var added = ScreenEditing.AddOutput(outputs, screen.Id)!;
        Assert.Equal("Output 2", added.Name);
        Assert.Equal("Output", ScreenEditing.SliceTitle(screen.Id, added with { Name = null }, OutputLayoutKind.Custom));
        Assert.Equal("Wall — Output 2", ScreenEditing.PackedLabel(outputs, added.Id));

        Assert.Equal(new OutputPlacement(1920, 1080, 0, 0, 832, 416), ScreenEditing.DefaultPlacement(outputs, screen.Id));
        Assert.Equal(new OutputPlacement(1920, 1080, 0, 0, 1920, 1080), ScreenEditing.DefaultPlacement(outputs, Guid.NewGuid()));
        Assert.Equal(16, ScreenEditing.StepUp(13));
        Assert.Equal(24, ScreenEditing.StepUp(16));
        Assert.Equal(8, ScreenEditing.StepDown(13));
        Assert.Equal(0, ScreenEditing.StepDown(3));
        Assert.Equal(8, ScreenEditing.FrameDimension(2.9));
        Assert.Equal(1919, ScreenEditing.FrameDimension(1919.9));
        Assert.Equal(new SceneRect(50, 0, 50, 10), ScreenEditing.DiagramRect(new OutputSliceState(Guid.Empty, null, new SceneRect(0.5, 0, 0.5, 0.01)), new SceneSize(100, 50)));
    }

    [Fact]
    public void CarrySiblingsArePackedOutputsOnTheSameDevice()
    {
        var outputs = new OutputConfiguration();
        var wall = outputs.AddScreen("Wall", 832, 416)!;
        var strip = outputs.AddScreen("Strip", 3840, 540)!;
        outputs.AssignDevice(wall.Id, "FRAME");
        outputs.AssignDevice(strip.Id, "FRAME", exclusive: false);
        Assert.Equal([strip.Id], ScreenEditing.CarrySiblings(outputs, wall.Id));
        Assert.Empty(ScreenEditing.CarrySiblings(outputs, Guid.NewGuid()));
    }
}
