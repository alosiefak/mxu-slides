using System.Text.Json.Nodes;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Automation;

// Ported from PresenterCoreTests/ControlBoardTests.swift.
public class ControlBoardTests
{
    private static ControlBoard MakeBoard(params string[] items)
    {
        var board = new ControlBoard { Id = ControlBoardLogic.ComboBoardId };
        board.Reconcile(items);
        return board;
    }

    [Fact]
    public void ControlBoardFolderLifecycle()
    {
        var board = MakeBoard("a", "b");
        Assert.Equal(["a", "b"], board.Nodes);

        var folderId = board.AddFolder("Sundays");
        board.MoveItemIntoFolder("b", folderId);
        Assert.Equal(["b"], board.Folder(folderId)!.ItemIds);
        Assert.Equal(["a", "b"], board.AllItemIds());

        board.Reconcile(["a"]);
        Assert.Empty(board.Folder(folderId)!.ItemIds);
        Assert.NotNull(board.Folder(folderId));

        board.Reconcile(["a", "c"]);
        board.MoveItemIntoFolder("c", folderId);
        board.RemoveFolder(folderId);
        Assert.Empty(board.Folders);
        Assert.Equal(["a", "c"], board.Nodes);
    }

    [Fact]
    public void ControlBoardOrderedFoldersWalkNodeOrder()
    {
        var board = MakeBoard("a", "b", "c");
        var zulu = board.AddFolder("Zulu");
        var alpha = board.AddFolder("Alpha");
        board.MoveFolder(alpha, zulu);
        Assert.Equal(["Alpha", "Zulu"], board.OrderedFolders().Select(f => f.Name));

        board.MoveItemIntoFolder("b", alpha);
        board.MoveFolder(alpha, "a");
        Assert.Equal(["b", "a", "c"], board.AllItemIds());
    }

    [Fact]
    public void ControlBoardMovesKeepExactlyOnce()
    {
        var board = MakeBoard("a", "b", "c");
        var folderId = board.AddFolder("F");
        board.MoveItemIntoFolder("a", folderId);
        Assert.Equal(folderId, board.FolderContaining("a")?.Id);

        board.MoveItem("b", "a");
        Assert.Equal(folderId, board.FolderContaining("b")?.Id);
        Assert.Equal(["b", "a"], board.Folder(folderId)!.ItemIds);

        board.MoveItem("a", "c");
        Assert.Null(board.FolderContaining("a"));
        Assert.Equal(["a", "c", folderId], board.Nodes);
        Assert.Equal(["a", "c", "b"], board.AllItemIds());

        board.MoveItem("b", null);
        Assert.Equal(["a", "c", folderId, "b"], board.Nodes);
        Assert.Empty(board.Folder(folderId)!.ItemIds);
    }

    [Fact]
    public void ControlBoardFolderReorderResolvesFolderedTargets()
    {
        var board = MakeBoard("a", "b", "c");
        var folderId = board.AddFolder("F");
        board.MoveItemIntoFolder("b", folderId);
        board.MoveFolder(folderId, "a");
        Assert.Equal([folderId, "a", "c"], board.Nodes);

        board.MoveFolder(folderId, "b");
        Assert.Equal([folderId, "a", "c"], board.Nodes);
        board.MoveFolder(folderId, null);
        Assert.Equal(["a", "c", folderId], board.Nodes);
    }

    [Fact]
    public void ControlBoardReconcileAppendsUnknownAndPrunesGone()
    {
        var board = MakeBoard("a", "b");
        var folderId = board.AddFolder("F");
        board.MoveItemIntoFolder("a", folderId);

        board.Reconcile(["b", "c", "a"]);
        Assert.Equal(["a"], board.Folder(folderId)!.ItemIds);
        Assert.Equal(["b", folderId, "c"], board.Nodes);

        board.Reconcile(["c"]);
        Assert.Equal(["c"], board.AllItemIds());
        Assert.Empty(board.Folder(folderId)!.ItemIds);
    }

    [Fact]
    public void ControlBoardOptionalsStayAbsentWhenNil()
    {
        var folder = new ControlFolder { Id = "f1", Name = "F", ItemIds = ["a"] };
        var json = JsonNode.Parse(MxuJson.Serialize(folder))!.AsObject();
        Assert.False(json.ContainsKey("collapsed"));
    }

    [Fact]
    public void ControlBoardFilterMatchesItemAndFolderNames()
    {
        var board = MakeBoard("a", "b", "c");
        var folderId = board.AddFolder("Walk-in");
        board.MoveItemIntoFolder("c", folderId);
        var names = new Dictionary<string, string> { ["a"] = "Lights Up", ["b"] = "Lights Down", ["c"] = "House Mix" };
        string? Name(string id) => names.GetValueOrDefault(id);

        Assert.Equal(["a", "b", "c"], board.FilteredItemIds("", Name));
        Assert.Equal(["a", "b", "c"], board.FilteredItemIds("  ", Name));
        Assert.Equal(["a", "b"], board.FilteredItemIds("lights", Name));
        Assert.Equal(["c"], board.FilteredItemIds("walk", Name));
        Assert.Empty(board.FilteredItemIds("nope", Name));
    }

    [Fact]
    public void BoardFoldersTakeCallerMintedIds()
    {
        var control = MakeBoard("a");
        Assert.Equal("f-1", control.AddFolder("Sundays", "f-1"));
        Assert.Equal("Sundays", control.Folder("f-1")?.Name);

        var scheduler = new SchedulerBoard { Id = SchedulerBoardLogic.WellKnownId };
        Assert.Equal("s-1", scheduler.AddFolder("Morning", "s-1"));
        Assert.Equal("s-2", scheduler.DuplicateFolder("s-1", [], "s-2"));
        Assert.Equal("Morning Copy", scheduler.Folder("s-2")?.Name);
    }

    [Fact]
    public void ControlBoardShiftRangeSkipsCollapsedFolders()
    {
        var board = MakeBoard("a", "b", "c", "d");
        var folderId = board.AddFolder("Walk-in");
        board.MoveItemIntoFolder("b", folderId);
        board.MoveItemIntoFolder("c", folderId);
        Assert.Equal(["a", "d", "b", "c"], board.VisibleItemIds());
        Assert.Equal(["d", "b", "c"], ControlBoardLogic.Range(board.VisibleItemIds(), "c", "d"));

        board.SetFolderCollapsed(folderId, true);
        Assert.Equal(["a", "d"], board.VisibleItemIds());
        Assert.Equal(["a", "d"], ControlBoardLogic.Range(board.VisibleItemIds(), "a", "d"));
        Assert.Equal(["d"], ControlBoardLogic.Range(board.VisibleItemIds(), null, "d"));
        Assert.Equal(["d"], ControlBoardLogic.Range(board.VisibleItemIds(), "b", "d"));
    }

    // C#: the undo snapshot round trip (MoveUndoJournal's BoardOrder) puts a reshuffled board back.
    [Fact]
    public void OrderSnapshotRestoresAReshuffledBoard()
    {
        var board = MakeBoard("a", "b", "c");
        var folderId = board.AddFolder("F", "f");
        board.MoveItemIntoFolder("b", folderId);
        var before = board.OrderSnapshot();
        Assert.Equal(new HashSet<string> { "a", "b", "c", "f" }, before.Membership);

        board.MoveItem("b", "a");
        board.MoveFolder(folderId, "a");
        board.Reconcile(["a", "b", "c", "d"]);
        board.Restore(before);
        Assert.Equal(["a", "c", "f", "d"], board.Nodes);
        Assert.Equal(["b"], board.Folder("f")!.ItemIds);
        Assert.Equal(before.Memberships["f"], board.OrderSnapshot().Memberships["f"]);
        Assert.NotEqual(before, board.OrderSnapshot());
    }
}
