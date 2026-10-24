using MxU.Slides.Core.Outputs;
using static MxU.Slides.Core.Tests.Outputs.OutputFixtures;

namespace MxU.Slides.Core.Tests.Outputs;

// Ported from OutputEngineTests/OutputMaskRoutingTests.swift. Mask "textures"
// are the coverage arrays the configuration caches, filled by a test polygon filler.
public class OutputMaskRoutingTests
{
    private static (OutputConfiguration Outputs, PlaceholderScreen Screen) MakeScreen(string name = "Main")
    {
        var outputs = new OutputConfiguration { MaskPathFill = FillPolygon };
        return (outputs, outputs.AddScreen(name, 192, 108)!);
    }

    [Fact]
    public void ActiveSetIsAlwaysOnPlusPresetActivated()
    {
        var (outputs, screen) = MakeScreen();
        var column = new OutputMask("Column", CenterBand, alwaysOn: true);
        var sermon = new OutputMask("Sermon wings", CenterBand, alwaysOn: false);
        outputs.SetMasks(screen.Id, [column, sermon]);

        // room geometry applies with no preset activation
        Assert.Equal([column.Id], outputs.ActiveMasks(screen.Id).Select(mask => mask.Id));

        outputs.SetActiveMaskIds(screen.Id, [sermon.Id]);
        // a preset switch adds its masks to the always-on set
        Assert.Equal([column.Id, sermon.Id], outputs.ActiveMasks(screen.Id).Select(mask => mask.Id));

        outputs.SetActiveMaskIds(screen.Id, []);
        Assert.Equal([column.Id], outputs.ActiveMasks(screen.Id).Select(mask => mask.Id));
    }

    [Fact]
    public void MaskPushReachesScreenRasterAndProvider()
    {
        var (outputs, screen) = MakeScreen();
        var provider = outputs.MaskProvider(SwiftUuid.Format(screen.Id));
        Assert.Null(provider()); // no masks = raw feed
        Assert.Null(outputs.MaskCoverage(screen.Id));

        outputs.SetMasks(screen.Id, [new OutputMask("Column", CenterBand)]);
        var pushed = outputs.MaskCoverage(screen.Id);
        Assert.NotNull(pushed); // the screen's own path gets the raster
        Assert.Same(pushed, provider()); // feed mirrors read the same raster
        Assert.Equal(192 * 108, pushed.Length); // the raster is canvas-sized

        outputs.SetMasks(screen.Id, []);
        Assert.Null(provider()); // clearing the library clears the feed
        Assert.Null(outputs.MaskCoverage(screen.Id));
    }

    [Fact]
    public void PresetFlipReusesCachedRasters()
    {
        var (outputs, screen) = MakeScreen();
        var column = new OutputMask("Column", CenterBand, alwaysOn: true);
        outputs.SetMasks(screen.Id, [column]);
        var first = outputs.MaskCoverage(screen.Id);
        Assert.NotNull(first);
        outputs.SetActiveMaskIds(screen.Id, []);
        Assert.Same(first, outputs.MaskCoverage(screen.Id)); // an unchanged active set keeps its raster
        outputs.SetActiveMaskIds(screen.Id, [Guid.NewGuid()]); // activating an id that isn't in the library changes nothing
        Assert.Same(first, outputs.MaskCoverage(screen.Id));
    }

    [Fact]
    public void MasksPersistViaConfigurationHookAndCleanUpOnRemoval()
    {
        var (outputs, screen) = MakeScreen("Doomed");
        var saves = 0;
        outputs.ConfigurationChanged += () => saves++;
        var mask = new OutputMask("Column", CenterBand);
        outputs.SetMasks(screen.Id, [mask]);
        Assert.Equal(1, saves); // library edits persist
        outputs.SetMasks(screen.Id, [mask]);
        Assert.Equal(1, saves); // no-op sets don't churn the save path
        outputs.SetActiveMaskIds(screen.Id, [mask.Id]);
        Assert.Equal(1, saves); // preset activation is runtime state, never persisted

        var provider = outputs.MaskProvider(SwiftUuid.Format(screen.Id));
        outputs.RemoveScreen(screen.Id);
        Assert.Empty(outputs.Masks(screen.Id));
        Assert.Null(provider()); // running feeds read the cleared value
    }

    [Fact]
    public void ANewSliceIsMaskedStraightAway()
    {
        // The Mac only re-rasters when masks change, so a slice added after them waits; here it doesn't.
        var (outputs, screen) = MakeScreen();
        outputs.SetMasks(screen.Id, [new OutputMask("Column", CenterBand)]);
        var mirror = outputs.AddSlice(screen.Id, "Lobby")!;
        Assert.Same(outputs.MaskCoverage(screen.Id), outputs.MaskCoverage(mirror.Id));
    }

    [Fact]
    public void ResizingTheScreenRebuildsItsRaster()
    {
        var (outputs, screen) = MakeScreen();
        outputs.SetMasks(screen.Id, [new OutputMask("Column", CenterBand)]);
        Assert.Equal(192 * 108, outputs.MaskCoverage(screen.Id)!.Length);
        outputs.ReconfigureScreen(screen.Id, screen.Name, 96, 54);
        Assert.Equal(96 * 54, outputs.MaskCoverage(screen.Id)!.Length);
    }
}
