using MxU.Slides.Core.Json;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Stage;

// Each stage screen's layout: the assignment half of Sources/ConfidenceMonitorController.swift
// (setLayout, apply(layoutID:toScreen:), refresh's pruning, restore and persist).
public class ConfidenceScreenLayoutsTests
{
    private static readonly Guid Band = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Pastor = Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");
    private static readonly HashSet<string> Library = ["chords", "clock"];

    private static bool Exists(string id) => Library.Contains(id);

    [Fact]
    public void AScreenWithoutAChoiceShowsTheBuiltInLayout()
    {
        var layouts = new ConfidenceScreenLayouts();
        Assert.Null(layouts.LayoutId(Band));
        Assert.True(layouts.SetLayout("chords", Band));
        Assert.Equal("chords", layouts.LayoutId(Band));
        Assert.False(layouts.SetLayout("chords", Band)); // the same choice again changes nothing
        Assert.True(layouts.SetLayout("", Band)); // empty means the built-in layout, as nil does on the Mac
        Assert.Null(layouts.LayoutId(Band));
        Assert.Empty(layouts.Assignments);
    }

    [Fact]
    public void TheActionSetsOneScreenOrEveryStageScreen()
    {
        var layouts = new ConfidenceScreenLayouts();
        Assert.True(layouts.Apply("clock", Pastor, [Band, Pastor], Exists));
        Assert.Null(layouts.LayoutId(Band)); // only the screen it names
        Assert.True(layouts.Apply("chords", null, [Band, Pastor], Exists));
        Assert.Equal(("chords", "chords"), (layouts.LayoutId(Band), layouts.LayoutId(Pastor))); // no screen: all of them
        Assert.False(layouts.Apply("deleted", Band, [Band, Pastor], Exists)); // a layout the library hasn't got is refused
        Assert.Equal("chords", layouts.LayoutId(Band));
        Assert.True(layouts.Apply(null, null, [Band, Pastor], Exists)); // the built-in layout everywhere
        Assert.Empty(layouts.Assignments);
    }

    [Fact]
    public void ADeletedLayoutFallsBackToTheBuiltInOne()
    {
        var layouts = new ConfidenceScreenLayouts();
        layouts.SetLayout("chords", Band);
        layouts.SetLayout("gone", Pastor);
        Assert.True(layouts.Prune(Exists));
        Assert.Null(layouts.LayoutId(Pastor));
        Assert.Equal("chords", layouts.LayoutId(Band));
        Assert.False(layouts.Prune(Exists));
    }

    [Fact]
    public void SavedInTheMacsShape()
    {
        var layouts = new ConfidenceScreenLayouts();
        layouts.SetLayout("chords", Band);
        var stored = layouts.ToStored();
        Assert.Equal(new Dictionary<string, string> { ["11111111-2222-3333-4444-555555555555"] = "chords" }, stored); // Swift's upper-case uuidString
        Assert.Equal("{\"11111111-2222-3333-4444-555555555555\":\"chords\"}", MxuJson.Serialize(stored, indented: false));

        var read = ConfidenceScreenLayouts.FromStored(new Dictionary<string, string>
        {
            ["aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"] = "clock", ["not-a-screen"] = "chords", [SwiftUuid.Format(Band)] = "",
        });
        Assert.Equal("clock", read.LayoutId(Pastor)); // any case reads
        Assert.Single(read.Assignments); // keys that aren't screens, and empty choices, are skipped
        Assert.Empty(ConfidenceScreenLayouts.FromStored(null).Assignments);
    }

    [Fact]
    public void TheFirstTimeEveryStageScreenKeepsTheOneLayoutTheyShared()
    {
        var migrated = ConfidenceScreenLayouts.Migrated("classic", [Band, Pastor]);
        Assert.Equal(("classic", "classic"), (migrated.LayoutId(Band), migrated.LayoutId(Pastor)));
        Assert.Empty(ConfidenceScreenLayouts.Migrated(null, [Band]).Assignments);
    }
}
