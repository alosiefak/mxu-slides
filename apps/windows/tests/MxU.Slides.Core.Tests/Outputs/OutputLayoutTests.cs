using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Outputs;

// Ported from OutputEngineTests/OutputLayoutTests.swift.
public class OutputLayoutTests
{
    [Fact]
    public void LedWallRoundTripsAndReleasesItsPlacement()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Wall", 832, 416)!;
        outputs.ApplyLayout(OutputLayoutKind.LedWall, 1, 0, screen.Id);
        var placement = outputs.Placement(screen.Id);
        Assert.NotNull(placement);
        Assert.Equal(1920, placement.FrameWidth); // the wall fits HD, so the seed frame is 1080p
        Assert.Equal(832, placement.Width); // seeded 1:1 at wall-native pixels
        Assert.Equal(OutputLayoutKind.LedWall, outputs.Layout(screen.Id));

        outputs.SetPlacement(screen.Id, placement with { X = 512 });
        outputs.ApplyLayout(OutputLayoutKind.LedWall, 1, 0, screen.Id);
        Assert.Equal(512, outputs.Placement(screen.Id)?.X); // re-picking LED Wall keeps a moved placement

        outputs.ApplyLayout(OutputLayoutKind.Single, 1, 0, screen.Id);
        Assert.Null(outputs.Placement(screen.Id));
        Assert.Equal(OutputLayoutKind.Single, outputs.Layout(screen.Id));
    }

    [Fact]
    public void PlacedExtraSliceReadsBackAsCustom()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Wall", 832, 416)!;
        var banner = outputs.AddSlice(screen.Id, "Banner")!;
        outputs.SetPlacement(banner.Id, new OutputPlacement(1920, 1080, 0, 0, 384, 768));
        Assert.Equal(OutputLayoutKind.Custom, outputs.Layout(screen.Id)); // multi-region frames are Custom
    }

    [Fact]
    public void EveryLayoutRoundTripsThroughClassify()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Wall", 384, 108)!;

        Assert.Equal(OutputLayoutKind.Single, outputs.Layout(screen.Id));

        outputs.ApplyLayout(OutputLayoutKind.Mirror, 3, 0, screen.Id);
        Assert.Equal(3, outputs.Slices(screen.Id).Count);
        Assert.Equal(OutputLayoutKind.Mirror, outputs.Layout(screen.Id));

        outputs.ApplyLayout(OutputLayoutKind.Grouped, 2, 0, screen.Id);
        Assert.Equal(OutputLayoutKind.Grouped, outputs.Layout(screen.Id));
        var grouped = outputs.Slices(screen.Id).Skip(1).ToList();
        Assert.Equal(2, grouped.Count);
        Assert.Equal(0.5, grouped[0].SourceRect.Width, 0.001);

        outputs.ApplyLayout(OutputLayoutKind.EdgeBlend, 3, 0.06, screen.Id);
        Assert.Equal(OutputLayoutKind.EdgeBlend, outputs.Layout(screen.Id));
        var tiles = outputs.Slices(screen.Id).Skip(1).ToList();
        Assert.Equal(3, tiles.Count);
        Assert.Equal("Left", tiles[0].Name);
        Assert.Equal("Center", tiles[1].Name);

        var middle = outputs.Adjustments(tiles[1].Id);
        Assert.NotNull(middle.BlendLeft);
        Assert.NotNull(middle.BlendRight);
        var left = outputs.Adjustments(tiles[0].Id);
        Assert.Null(left.BlendLeft); // the wall's outer edge never ramps
        Assert.NotNull(left.BlendRight);

        outputs.ApplyLayout(OutputLayoutKind.Single, 1, 0, screen.Id);
        Assert.Single(outputs.Slices(screen.Id));
        Assert.Equal(OutputLayoutKind.Single, outputs.Layout(screen.Id));
    }

    [Fact]
    public void HandEditedRegionsReadBackAsCustom()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Wall", 384, 108)!;
        outputs.ApplyLayout(OutputLayoutKind.Grouped, 2, 0, screen.Id);
        var tile = outputs.Slices(screen.Id).Skip(1).First();
        outputs.SetSliceSourceRect(tile.Id, new SceneRect(0.1, 0.2, 0.3, 0.5));
        // a shape the picker can't express stays fully editable as Custom
        Assert.Equal(OutputLayoutKind.Custom, outputs.Layout(screen.Id));
    }

    [Fact]
    public void GroupedReleasesTheDefaultSlicesBacking()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Wall", 384, 108)!;
        outputs.AssignDevice(screen.Id, "DISPLAY-A");
        var released = new List<Guid>();
        outputs.ApplyLayout(OutputLayoutKind.EdgeBlend, 2, 0.05, screen.Id, released.Add);
        // the default slice becomes whole-canvas plumbing; the tiles own the glass
        Assert.Null(outputs.Device(screen.Id));
        Assert.Contains(screen.Id, released); // carries get the release callback too
    }

    [Fact]
    public void TileMathAndNames()
    {
        var tiles = OutputLayout.Tiles(3, 0.06);
        Assert.Equal(3, tiles.Count);
        Assert.Equal(0, tiles[0].X, 0.0001);
        Assert.Equal(1, tiles[2].Right, 0.0001);
        Assert.Equal(0.06, tiles[0].Right - tiles[1].X, 0.0001);
        Assert.Equal("Left", OutputLayout.TileName(0, 2));
        Assert.Equal("Right", OutputLayout.TileName(2, 3));
        Assert.Equal("Output 4", OutputLayout.TileName(3, 4));
    }

    // Beyond the Mac tests: the edges of the classifier and the tile maths.

    [Fact]
    public void BlendsOnFullCanvasMirrorsReadAsCustom()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Wall", 384, 108)!;
        var copy = outputs.AddSlice(screen.Id, "Copy")!;
        Assert.Equal(OutputLayoutKind.Mirror, outputs.Layout(screen.Id));
        outputs.SetAdjustments(copy.Id, new OutputAdjustments { BlendLeft = new OutputEdgeBlend(0.1) });
        Assert.Equal(OutputLayoutKind.Custom, outputs.Layout(screen.Id));
    }

    [Fact]
    public void TilesWithAGapOrNotSpanningReadAsCustom()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Wall", 384, 108)!;
        outputs.AddSlice(screen.Id, "A", new SceneRect(0, 0, 0.4, 1));
        outputs.AddSlice(screen.Id, "B", new SceneRect(0.5, 0, 0.5, 1));
        Assert.Equal(OutputLayoutKind.Custom, outputs.Layout(screen.Id));

        var short_ = outputs.AddScreen("Short", 384, 108)!;
        outputs.AddSlice(short_.Id, "A", new SceneRect(0, 0, 0.4, 1));
        outputs.AddSlice(short_.Id, "B", new SceneRect(0.4, 0, 0.4, 1));
        Assert.Equal(OutputLayoutKind.Custom, outputs.Layout(short_.Id));
    }

    [Fact]
    public void OneColumnHasNoOverlapAndOverlapIsCappedAtAFifth()
    {
        Assert.Equal([new SceneRect(0, 0, 1, 1)], OutputLayout.Tiles(1, 0.1));
        var capped = OutputLayout.Tiles(2, 0.9);
        Assert.Equal(0.2, capped[0].Right - capped[1].X, 1e-9);
        Assert.Equal(1, capped[1].Right, 1e-9);
    }

    [Fact]
    public void EdgeBlendRampsSpanTheOverlapInTileUnits()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Wall", 384, 108)!;
        outputs.ApplyLayout(OutputLayoutKind.EdgeBlend, 2, 0.1, screen.Id);
        var tiles = outputs.ExtraSlices(screen.Id);
        var width = tiles[0].SourceRect.Width; // 0.55
        Assert.Equal(0.55, width, 1e-9);
        Assert.Equal(0.1 / width, outputs.Adjustments(tiles[0].Id).BlendRight!.Width, 1e-9);
        Assert.Equal(2.2, outputs.Adjustments(tiles[0].Id).BlendRight!.Curve);
    }

    [Fact]
    public void LedWallOnABigScreenUsesA4KFrameAndAnHdScreenNeedsNoPlacement()
    {
        var outputs = new OutputConfiguration();
        var big = outputs.AddScreen("Big", 2500, 900)!;
        outputs.ApplyLayout(OutputLayoutKind.LedWall, 1, 0, big.Id);
        Assert.Equal(new OutputPlacement(3840, 2160, 0, 0, 2500, 900), outputs.Placement(big.Id));

        // A 1920×1080 wall fills its 1080p frame, which is the same as no placement.
        var hd = outputs.AddScreen("HD", 1920, 1080)!;
        outputs.ApplyLayout(OutputLayoutKind.LedWall, 1, 0, hd.Id);
        Assert.Null(outputs.Placement(hd.Id));
    }

    [Fact]
    public void RawNamesAndDisplayNamesMatchTheMac()
    {
        Assert.Equal(["single", "mirror", "grouped", "edgeBlend", "ledWall", "custom"], OutputLayoutKinds.All.Select(kind => kind.Raw()));
        Assert.Equal("Edge Blend", OutputLayoutKind.EdgeBlend.DisplayName());
        Assert.Equal("LED Wall", OutputLayoutKind.LedWall.DisplayName());
    }
}
