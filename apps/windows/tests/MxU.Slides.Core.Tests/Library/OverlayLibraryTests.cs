using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

// The folder maths is ported from PresenterCoreTests/TeamDrivesTests.swift; the overlay rules
// follow the Mac's AppModel (createEntity, duplicate, rename, renameFolder, moveToFolder).
public class FolderPathsTests
{
    [Fact]
    public void IsUnderMatchesWholePathParts()
    {
        Assert.True(FolderPaths.IsUnder("Worship/Lyrics", "Worship"));
        Assert.False(FolderPaths.IsUnder("Worship2", "Worship"));
        Assert.True(FolderPaths.IsUnder("", ""));
        Assert.True(FolderPaths.IsUnder("Worship", "Worship"));
    }

    [Fact]
    public void FoldersCountWhatSitsUnderThemAndMovesKeepTheSubtree()
    {
        string[] folders = ["Worship", "Worship/Lyrics", "Worship/Lyrics/2026", "Youth/Loops", ""];
        Assert.Equal(new Dictionary<string, int> { ["Worship"] = 3, ["Youth"] = 1 }, FolderPaths.ChildCounts(folders, []));
        Assert.Equal(new Dictionary<string, int> { ["Lyrics"] = 2 }, FolderPaths.ChildCounts(folders, ["Worship"]));
        Assert.Empty(FolderPaths.ChildCounts(folders, ["Worship", "Lyrics", "2026"]));
        Assert.Equal("Worship/Lyrics/2026", FolderPaths.Rebased("Lyrics/2026", ["Lyrics"], ["Worship"]));
        Assert.Equal("Lyrics", FolderPaths.Rebased("A/Lyrics", ["A", "Lyrics"], []));
        Assert.Equal("Lyrics/2026", FolderPaths.Rebased("Lyrics/2026", ["Lyrics"], [])); // a move across areas keeps the path
        Assert.Equal("Loops/Calm/2026", FolderPaths.Renamed("Loops/Slow/2026", ["Loops", "Slow"], "Calm"));
        Assert.Equal("Motion", FolderPaths.Renamed("Loops", ["Loops"], "Motion"));
    }
}

public class OverlayLibraryTests
{
    private static Overlay Make(string id, string? folder, string name = "Overlay") => new() { Id = id, Name = name, Objects = [], Folder = folder };

    [Fact]
    public void PacksAndCategoriesComeFromTheFolder()
    {
        var plate = Make("a", "Clean Geometric/Lower Thirds");
        Assert.Equal("Clean Geometric", OverlayLibrary.PackOf(plate));
        Assert.Equal("Lower Thirds", OverlayLibrary.CategoryOf(plate));
        Assert.Equal("Other", OverlayLibrary.PackOf(Make("b", null)));
        Assert.Equal("Other", OverlayLibrary.PackOf(Make("c", "")));
        Assert.Equal("", OverlayLibrary.CategoryOf(Make("d", "Mine")));
        Assert.Equal("A/B", OverlayLibrary.CategoryOf(Make("e", "Pack/A/B")));
    }

    [Fact]
    public void PacksAreSortedWithUnfiledLast()
    {
        var all = new[] { Make("1", null), Make("2", "Digital/Full Screen"), Make("3", "bold blocks"), Make("4", "Digital/Lower Thirds") };
        Assert.Equal(["bold blocks", "Digital", "Other"], OverlayLibrary.Packs(all));
        Assert.Equal(["2", "4"], OverlayLibrary.InPack(all, "Digital").Select(o => o.Id));
    }

    [Fact]
    public void ANewOverlayIsUntitledAndEmptyInItsPack()
    {
        var overlay = OverlayLibrary.Create("Mine");
        Assert.Equal("Untitled Overlay", overlay.Name);
        Assert.Empty(overlay.Objects);
        Assert.Equal("Mine", overlay.Folder);
        Assert.NotEqual(overlay.Id, OverlayLibrary.Create("Mine").Id);
        Assert.Null(OverlayLibrary.Create("Other").Folder); // unfiled
        Assert.Equal("Mine/Lower Thirds", OverlayLibrary.Create("Mine", category: "Lower Thirds").Folder);
    }

    [Fact]
    public void DuplicatesGetANewIdAndCopyName()
    {
        var original = Make("orig", "Pack/Lower Thirds", "Name + Title");
        original.Objects.Add(new SlideObject { Id = "t", ObjectKind = SlideObjectKind.Text, Name = "Name", Text = "Full Name" });
        var copy = OverlayLibrary.Duplicate(original);
        Assert.NotEqual("orig", copy.Id);
        Assert.Equal("Name + Title Copy", copy.Name);
        Assert.Equal("Pack/Lower Thirds", copy.Folder);
        Assert.Equal("Full Name", copy.Objects.Single().Text);
        copy.Objects[0].Text = "Changed";
        Assert.Equal("Full Name", original.Objects[0].Text); // a deep copy
    }

    [Fact]
    public void RenamingTrimsAndRefusesBlanks()
    {
        var overlay = Make("a", null, "Old");
        Assert.False(OverlayLibrary.Rename(overlay, "   "));
        Assert.False(OverlayLibrary.Rename(overlay, "Old"));
        Assert.True(OverlayLibrary.Rename(overlay, "  New name "));
        Assert.Equal("New name", overlay.Name);
    }

    [Fact]
    public void MovingToAPackKeepsTheCategory()
    {
        var overlay = Make("a", "Clean Geometric/Lower Thirds");
        overlay.FolderId = "team-folder";
        Assert.True(OverlayLibrary.MoveToPack(overlay, "Mine"));
        Assert.Equal("Mine/Lower Thirds", overlay.Folder);
        Assert.Null(overlay.FolderId);
        Assert.False(OverlayLibrary.MoveToPack(overlay, "Mine"));
        Assert.True(OverlayLibrary.MoveToPack(overlay, "Other"));
        Assert.Null(overlay.Folder);
    }

    [Fact]
    public void RenamingAPackRefilesEverythingInIt()
    {
        var all = new[] { Make("1", "Mine/Lower Thirds"), Make("2", "Mine"), Make("3", "Mine2/Lower Thirds"), Make("4", null) };
        var changed = OverlayLibrary.RenamePack(all, "Mine", "Sunday");
        Assert.Equal(["1", "2"], changed.Select(o => o.Id));
        Assert.Equal("Sunday/Lower Thirds", all[0].Folder);
        Assert.Equal("Sunday", all[1].Folder);
        Assert.Equal("Mine2/Lower Thirds", all[2].Folder); // a pack whose name only starts the same is left alone
        Assert.Empty(OverlayLibrary.RenamePack(all, "Sunday", "Sunday"));
        Assert.Empty(OverlayLibrary.RenamePack(all, "Sunday", "  "));
        Assert.Empty(OverlayLibrary.RenamePack(all, "Sunday", "A/B"));
        Assert.Empty(OverlayLibrary.RenamePack(all, "Other", "Named")); // unfiled overlays have no folder to rename
    }

    [Fact]
    public void PackNamesMustBeUsable()
    {
        Assert.Equal("Youth", OverlayLibrary.ValidPackName("  Youth "));
        Assert.Null(OverlayLibrary.ValidPackName(""));
        Assert.Null(OverlayLibrary.ValidPackName("A/B"));
        Assert.Null(OverlayLibrary.ValidPackName("Other"));
    }

    [Fact]
    public void StarterOverlaysAreRecognised()
    {
        Assert.True(OverlayLibrary.IsStarter(Make("pack.cleanGeometric.overlay.name", null)));
        Assert.False(OverlayLibrary.IsStarter(Make("7C9E6679-7425-40DE-944B-E07FC1F90AE7", null)));
        Assert.False(OverlayLibrary.IsStarter(Make("pack.cleanGeometric.theme", null)));
    }
}
