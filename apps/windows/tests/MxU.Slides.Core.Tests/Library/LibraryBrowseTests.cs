using MxU.Slides.Core.Library;

namespace MxU.Slides.Core.Tests.Library;

/// <summary>
/// Sorting, folders and multi-select in the library browser. The sort tests are ports of the Mac's
/// LibraryViewOptionsTests.swift (minus its cloud rows, which Windows hasn't got); the multi-select
/// ones of ListMultiSelectTests.swift; the folder ones follow LibraryBrowseLogic and FolderTreeLogic.
/// </summary>
public sealed class LibraryBrowseTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.FromUnixTimeSeconds(0);

    private static LibraryEntry Entry(string id, double updated = 0, double? used = null, double? added = null, string folder = "",
        LibraryItemKind kind = LibraryItemKind.Song) =>
        new(id, kind, id, folder, Epoch.AddSeconds(updated), added is { } a ? Epoch.AddSeconds(a) : null, used is { } u ? Epoch.AddSeconds(u) : null);

    // MARK: Sorting (LibraryViewOptionsTests.swift)

    [Fact]
    public void SortByNameIsAToZAndTheOthersPutNewestFirst()
    {
        var byName = new[] { Entry("a", updated: 1, used: 5), Entry("b", updated: 3), Entry("c", updated: 3, used: 9) };
        Assert.Equal(["a", "b", "c"], LibrarySort.Name.Ordered(byName).Select(e => e.Id));
        Assert.Equal(["b", "c", "a"], LibrarySort.DateModified.Ordered(byName).Select(e => e.Id)); // a tie keeps name order
        Assert.Equal(["c", "a", "b"], LibrarySort.RecentlyUsed.Ordered(byName).Select(e => e.Id)); // never used sorts last
        Assert.Equal(["Name", "Date Modified", "Recently Used", "Date Added"], LibrarySorts.All.Select(s => s.Title()));
    }

    [Fact]
    public void DateAddedPutsNewestImportsFirst()
    {
        var entries = new[] { Entry("a"), Entry("b", added: 10), Entry("c", added: 20) };
        Assert.Equal(["c", "b", "a"], LibrarySort.DateAdded.Ordered(entries).Select(e => e.Id));
    }

    [Fact]
    public void NamesSortIgnoringCaseUnlikeTheMacsByteOrder()
    {
        // The Mac's index orders "export" after "Zion" (bytes: capitals first); Windows reads A–Z.
        var entries = new[] { Entry("Zion"), Entry("export"), Entry("Battle") };
        Assert.Equal(["Battle", "export", "Zion"], LibrarySort.Name.Ordered(entries).Select(e => e.Id));
    }

    [Fact]
    public void ASortedListIsInputOrderIndependent()
    {
        var entries = new[] { Entry("b", updated: 5), Entry("a", updated: 5), Entry("c", updated: 1) };
        Assert.Equal(["a", "b", "c"], LibrarySort.DateModified.Ordered(entries.Reverse()).Select(e => e.Id));
    }

    // MARK: Folders

    [Fact]
    public void FoldersAreTheOnesInUsePlusTheKeptEmptyOnes()
    {
        var items = new[] { Entry("1", folder: "Hymns"), Entry("2", folder: "Hymns/Easter"), Entry("3") };
        Assert.Equal(["Christmas", "Hymns", "Hymns/Easter"], LibraryFolders.Folders(items, ["Christmas", ""]));
    }

    [Fact]
    public void ChildFoldersCountWhatsAtOrBelowThemAndKeptOnesCountNothing()
    {
        var items = new[] { Entry("1", folder: "Hymns"), Entry("2", folder: "Hymns/Easter"), Entry("3", folder: "Hymns/Easter"), Entry("4", folder: "Modern") };
        var folders = LibraryFolders.Folders(items, ["Christmas"]);
        var held = items.Select(i => i.Folder).ToList();
        Assert.Equal([("Christmas", 0), ("Hymns", 3), ("Modern", 1)], LibraryFolders.ChildFolders(folders, held, []));
        Assert.Equal([("Easter", 2)], LibraryFolders.ChildFolders(folders, held, ["Hymns"]));
        Assert.Equal(["1"], LibraryFolders.ItemsAt(items, ["Hymns"]).Select(i => i.Id));
        Assert.Equal(["3"], LibraryFolders.ItemsAt(items.Where(i => i.Id == "3"), ["Hymns", "Easter"]).Select(i => i.Id));
    }

    [Fact]
    public void TheTreeNestsFoldersAToZ()
    {
        var tree = LibraryFolders.Tree(["Songs/Hymns", "Announcements", "Songs/Modern", "Songs/Hymns/Old"]);
        Assert.Equal(["Announcements", "Songs"], tree.Select(n => n.Name));
        var songs = tree[1];
        Assert.Equal(["Hymns", "Modern"], songs.Children.Select(n => n.Name));
        Assert.Equal("Songs/Hymns/Old", songs.Children[0].Children[0].Path);
    }

    [Fact]
    public void MoveToFolderShowsFoldersWithinFolders()
    {
        // The Mac's moveToFolderShowsFoldersWithinFolders (TeamDrivesTests.swift).
        var tree = LibraryFolders.Tree(["Worship/Lyrics/2026", "youth", "Worship", "Worship/Loops", "Archive/Old"]);
        Assert.Equal(["Archive", "Worship", "youth"], tree.Select(n => n.Name)); // a parent nobody listed still holds its child
        Assert.Equal(["Worship/Loops", "Worship/Lyrics"], tree[1].Children.Select(n => n.Path));
        Assert.Equal(["Worship/Lyrics/2026"], tree[1].Children[1].Children.Select(n => n.Path));
        Assert.Empty(tree[1].Children[1].Children[0].Children);
        Assert.Empty(tree[2].Children);
    }

    [Fact]
    public void AFolderMovesIntoAnyFolderButItselfAndItsOwn()
    {
        var folders = new[] { "Worship", "Worship/Lyrics", "Worship/Lyrics/2026", "Worship/Loops", "Archive" };
        var lyrics = LibraryFolders.MoveTargets(folders, "Worship/Lyrics");
        Assert.True(lyrics.TopLevel); // it's inside another folder, so it can go back to the top
        Assert.Equal("Worship", lyrics.Current); // ticked: where it is now
        Assert.Equal(["Archive", "Worship", "Worship/Loops"], lyrics.Folders);

        var archive = LibraryFolders.MoveTargets(folders, "Archive");
        Assert.False(archive.TopLevel); // already at the top
        Assert.Equal("", archive.Current);
        Assert.False(LibraryFolders.MoveTargets(["Archive"], "Archive").Any); // nowhere to go: the menu is greyed
        Assert.True(archive.Any);
    }

    [Theory]
    [InlineData("  Easter  ", "Easter")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("A/B", null)]
    [InlineData("..", null)]
    public void FolderNamesAreTrimmedAndRefusedWithASlash(string typed, string? expected) =>
        Assert.Equal(expected, LibraryFolders.CleanName(typed));

    [Fact]
    public void RenamingAFolderRefilesEverythingUnderIt()
    {
        var items = new[] { Entry("1", folder: "Hymns"), Entry("2", folder: "Hymns/Easter"), Entry("3", folder: "Hymnsong") };
        var move = LibraryFolders.Rename(items, ["Hymns/Empty"], ["Hymns"], "Classic Hymns");
        Assert.True(move.Changed);
        Assert.Equal([("1", "Classic Hymns"), ("2", "Classic Hymns/Easter")], move.Refiles.Select(r => (r.Entry.Id, r.Folder)));
        Assert.Equal(["Classic Hymns/Empty"], move.Empties);
        Assert.False(LibraryFolders.Rename(items, [], ["Hymns"], "Hymns").Changed);
        Assert.False(LibraryFolders.Rename(items, [], ["Hymns"], "A/B").Changed);
    }

    [Fact]
    public void MovingAFolderPutsItInsideAnotherButNeverInsideItself()
    {
        var items = new[] { Entry("1", folder: "Lyrics"), Entry("2", folder: "Lyrics/2026"), Entry("3", folder: "Worship") };
        var move = LibraryFolders.Move(items, [], ["Lyrics"], ["Worship"]);
        Assert.Equal([("1", "Worship/Lyrics"), ("2", "Worship/Lyrics/2026")], move.Refiles.Select(r => (r.Entry.Id, r.Folder)));
        Assert.False(LibraryFolders.Move(items, [], ["Lyrics"], ["Lyrics", "2026"]).Changed);
        Assert.False(LibraryFolders.Move(items, [], ["Lyrics"], ["Lyrics"]).Changed);
        Assert.False(LibraryFolders.Move(items, [], ["Lyrics", "2026"], ["Lyrics"]).Changed); // already there
        Assert.Equal("2026", LibraryFolders.Move(items, [], ["Lyrics", "2026"], []).Refiles.Single().Folder); // out to the top
    }

    [Fact]
    public void DeletingAFolderMovesWhatWasInItUpAndThrowsNothingAway()
    {
        var items = new[] { Entry("1", folder: "Hymns/Easter"), Entry("2", folder: "Hymns/Easter/Old"), Entry("3", folder: "Hymns") };
        var move = LibraryFolders.Delete(items, ["Hymns/Easter", "Hymns/Easter/Kept", "Other"], ["Hymns", "Easter"]);
        Assert.Equal([("1", "Hymns"), ("2", "Hymns/Old")], move.Refiles.Select(r => (r.Entry.Id, r.Folder)));
        Assert.Equal(["Hymns/Kept", "Other"], move.Empties);
        Assert.Equal([("1", "Easter"), ("2", "Easter/Old"), ("3", "")], LibraryFolders.Delete(items, [], ["Hymns"]).Refiles.Select(r => (r.Entry.Id, r.Folder)));
    }

    // MARK: Multi-select (ListMultiSelectTests.swift)

    private static readonly string[] Order = ["a", "b", "c", "d", "e"];

    private static ListMultiSelect.State State(string? anchor, params string[] selected) => new(selected.ToHashSet(), anchor);

    [Fact]
    public void PlainClickReplacesAndAnchors()
    {
        var state = ListMultiSelect.Click("c", ListMultiSelect.Gesture.Replace, Order, State("a", "a", "b"));
        Assert.Equal(["c"], state.Selected);
        Assert.Equal("c", state.Anchor);
    }

    [Fact]
    public void ToggleAddsRemovesAndMovesTheAnchor()
    {
        var state = ListMultiSelect.Click("b", ListMultiSelect.Gesture.Toggle, Order, ListMultiSelect.State.Empty);
        Assert.Equal(["b"], state.Selected);
        state = ListMultiSelect.Click("d", ListMultiSelect.Gesture.Toggle, Order, state);
        Assert.Equal(["b", "d"], state.Selected.Order());
        Assert.Equal("d", state.Anchor);
        state = ListMultiSelect.Click("b", ListMultiSelect.Gesture.Toggle, Order, state);
        Assert.Equal(["d"], state.Selected); // the second Ctrl+click deselects; the anchor still follows the click
        Assert.Equal("b", state.Anchor);
    }

    [Fact]
    public void ExtendSpansTheAnchorToTheClickBothWays()
    {
        var down = ListMultiSelect.Click("d", ListMultiSelect.Gesture.Extend, Order, State("b", "b"));
        Assert.Equal(["b", "c", "d"], down.Selected.Order());
        Assert.Equal("b", down.Anchor); // Shift never moves the anchor
        var up = ListMultiSelect.Click("a", ListMultiSelect.Gesture.Extend, Order, down);
        Assert.Equal(["a", "b", "c", "d"], up.Selected.Order()); // extend unions: the earlier range stays
    }

    [Fact]
    public void ExtendWithoutAnAnchorSelectsTheClickedRow()
    {
        var state = ListMultiSelect.Click("c", ListMultiSelect.Gesture.Extend, Order, ListMultiSelect.State.Empty);
        Assert.Equal(["c"], state.Selected);
        Assert.Equal("c", state.Anchor);
    }

    [Fact]
    public void ExtendCopesWithIdsThatAreGone()
    {
        var missing = ListMultiSelect.Click("z", ListMultiSelect.Gesture.Extend, Order, State("b", "b"));
        Assert.Equal(["b"], missing.Selected);
        var stale = ListMultiSelect.Click("d", ListMultiSelect.Gesture.Extend, Order, State("z", "b"));
        Assert.Equal(["b", "d"], stale.Selected.Order());
    }

    [Fact]
    public void ABatchFollowsTheFindersRule()
    {
        Assert.Equal(["a", "b", "d"], ListMultiSelect.Batch("d", new HashSet<string> { "b", "d", "a" }, Order)); // the whole set, in list order
        Assert.Equal(["c"], ListMultiSelect.Batch("c", new HashSet<string> { "b", "d" }, Order)); // outside the selection: just that row
        Assert.Equal(["c"], ListMultiSelect.Batch("c", new HashSet<string> { "c" }, Order));
    }
}
