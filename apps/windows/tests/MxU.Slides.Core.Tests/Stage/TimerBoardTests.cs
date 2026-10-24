using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Stage;

// Port of SlideSceneTests/TimerBoardTests.swift (featured and the roll-ups are in
// TimerControllerTests), plus the Windows-only Move up / Move down / Folder steps.
public class TimerBoardTests
{
    private static (TimerBoard Board, string FolderId) MakeBoard()
    {
        var board = new TimerBoard();
        board.Reconcile(["a", "b", "c", "d"]);
        var folderId = board.AddFolder("Pre-service");
        board.MoveTimerIntoFolder("c", folderId);
        board.MoveTimerIntoFolder("d", folderId);
        return (board, folderId);
    }

    private static void AssertExactlyOnce(TimerBoard board, params string[] ids)
    {
        var all = board.AllTimerIds;
        Assert.Equal(ids.Order(), all.Order()); // board lost or duplicated timers
        Assert.Equal(all.Count, all.Distinct().Count()); // a timer appears twice
    }

    [Fact]
    public void ReconcileAppendsUnknownAndPrunesDeleted()
    {
        var board = new TimerBoard();
        board.Reconcile(["a", "b"]);
        Assert.Equal(["a", "b"], board.AllTimerIds);

        var folderId = board.AddFolder("F");
        board.MoveTimerIntoFolder("b", folderId);
        board.Reconcile(["a", "b", "new"]);
        Assert.Equal(["a", "b", "new"], board.AllTimerIds);
        Assert.Equal(["b"], board.Folder(folderId)?.TimerIds);

        board.Reconcile(["a", "new"]);
        Assert.Empty(board.Folder(folderId)!.TimerIds); // a deleted timer leaves the folder
        Assert.NotNull(board.Folder(folderId)); // an emptied folder survives: grouping is not garbage
        AssertExactlyOnce(board, "a", "new");
    }

    [Fact]
    public void MoveTimerIntoAndOutOfFolder()
    {
        var (board, folderId) = MakeBoard();
        AssertExactlyOnce(board, "a", "b", "c", "d");
        Assert.Equal(["c", "d"], board.Folder(folderId)?.TimerIds);
        Assert.Equal(folderId, board.FolderContaining("c")?.Id);

        board.MoveTimer("c", beforeNode: "a");
        Assert.Null(board.FolderContaining("c"));
        Assert.Equal("c", board.Nodes[0].Id);
        Assert.Equal(["d"], board.Folder(folderId)?.TimerIds);
        AssertExactlyOnce(board, "a", "b", "c", "d");

        board.MoveTimer("a", beforeNode: "d");
        Assert.Equal(["a", "d"], board.Folder(folderId)?.TimerIds);
        AssertExactlyOnce(board, "a", "b", "c", "d");

        board.MoveTimerIntoFolder("b", folderId);
        Assert.Equal(["a", "d", "b"], board.Folder(folderId)?.TimerIds);
        AssertExactlyOnce(board, "a", "b", "c", "d");

        board.MoveTimer("a", beforeNode: null);
        Assert.Null(board.FolderContaining("a"));
        Assert.Equal("a", board.Nodes[^1].Id);
        AssertExactlyOnce(board, "a", "b", "c", "d");
    }

    [Fact]
    public void MoveTimerBeforeTopLevelFolderInsertsAtTopLevel()
    {
        var (board, folderId) = MakeBoard();
        board.MoveTimer("a", beforeNode: folderId);
        Assert.Null(board.FolderContaining("a")); // inserting before a folder is a top-level move
        Assert.Equal(["b", "a", folderId], board.Nodes.Select(n => n.Id));
        AssertExactlyOnce(board, "a", "b", "c", "d");
    }

    [Fact]
    public void MoveFolderReordersTopLevelAndNeverNests()
    {
        var (board, folderId) = MakeBoard();
        board.MoveFolder(folderId, beforeNode: "a");
        Assert.Equal([folderId, "a", "b"], board.Nodes.Select(n => n.Id));

        board.MoveFolder(folderId, beforeNode: "c");
        Assert.Equal([folderId, "a", "b"], board.Nodes.Select(n => n.Id));

        board.MoveFolder(folderId, beforeNode: null);
        Assert.Equal(["a", "b", folderId], board.Nodes.Select(n => n.Id));
        AssertExactlyOnce(board, "a", "b", "c", "d");
    }

    [Fact]
    public void RemoveFolderDissolvesInPlace()
    {
        var (board, folderId) = MakeBoard();
        board.RemoveFolder(folderId);
        Assert.Null(board.Folder(folderId));
        Assert.Equal(["a", "b", "c", "d"], board.Nodes.Select(n => n.Id)); // members return to the folder's place, in order
        AssertExactlyOnce(board, "a", "b", "c", "d");
    }

    [Fact]
    public void RenameAndCollapse()
    {
        var (board, folderId) = MakeBoard();
        board.RenameFolder(folderId, "Walk-in");
        Assert.Equal("Walk-in", board.Folder(folderId)?.Name);
        board.RenameFolder(folderId, "   ");
        Assert.Equal("Walk-in", board.Folder(folderId)?.Name); // a blank rename is a no-op
        board.SetCollapsed(folderId, true);
        Assert.True(board.Folder(folderId)?.Collapsed);
    }

    [Fact]
    public void JsonRoundTripInTheSwiftCodableShape()
    {
        var (board, folderId) = MakeBoard();
        var json = board.ToJson();
        // Swift encodes enum cases with associated values as {"timer":{"_0":…}} and Folder's ids as timerIDs.
        Assert.Contains("\"timer\"", json);
        Assert.Contains("\"_0\"", json);
        Assert.Contains("\"timerIDs\"", json);
        Assert.Equal(board, TimerBoard.FromJson(json));
        Assert.Equal(["c", "d"], TimerBoard.FromJson(json).Folder(folderId)?.TimerIds);

        const string mac = """{"nodes":[{"timer":{"_0":"x"}},{"folder":{"_0":{"id":"F","name":"Band","collapsed":true,"timerIDs":["y"]}}}]}""";
        var read = TimerBoard.FromJson(mac);
        Assert.Equal(["x", "y"], read.AllTimerIds);
        Assert.True(read.Folder("F")?.Collapsed);
        Assert.Empty(TimerBoard.FromJson("not json").Nodes); // unreadable: an empty board (Reconcile then lists every timer)
    }

    [Fact]
    public void CopiesAreIndependent()
    {
        var (board, folderId) = MakeBoard();
        var copy = board.Copy();
        copy.RenameFolder(folderId, "Changed");
        copy.MoveTimer("a", beforeNode: null);
        Assert.Equal("Pre-service", board.Folder(folderId)?.Name);
        Assert.Equal("a", board.Nodes[0].Id);
        Assert.NotEqual(board, copy);
    }

    [Fact]
    public void FilterMatchesTimerAndFolderNames()
    {
        var (board, _) = MakeBoard();
        var names = new Dictionary<string, string> { ["a"] = "Sermon", ["b"] = "Countdown", ["c"] = "Walk-in Clock", ["d"] = "Doors" };
        string? Name(string id) => names.GetValueOrDefault(id);

        Assert.Equal(["a", "b", "c", "d"], board.FilteredTimerIds(" ", Name));
        Assert.Equal(["a"], board.FilteredTimerIds("SERMON", Name));
        Assert.Equal(["c", "d"], board.FilteredTimerIds("pre-s", Name)); // a folder's name matches all its timers
        Assert.Equal(["d"], board.FilteredTimerIds("doors", Name));
        Assert.Empty(board.FilteredTimerIds("zzz", Name));
    }

    [Fact]
    public void OrderSnapshotRestoreInvertsMoves()
    {
        var (original, folderId) = MakeBoard();
        var board = original.Copy();
        var before = board.OrderSnapshot;

        board.MoveTimer("a", beforeNode: "d");
        Assert.Equal(["c", "a", "d"], board.Folder(folderId)?.TimerIds);
        board.Restore(before);
        Assert.Equal(original, board); // the inverse is the move back

        board.MoveFolder(folderId, beforeNode: "a");
        board.Restore(before);
        Assert.Equal(original, board);
    }

    [Fact]
    public void OrderRestoreToleratesDriftAndKeepsRenames()
    {
        var (board, folderId) = MakeBoard();
        var before = board.OrderSnapshot;

        board.MoveTimer("c", beforeNode: "a");
        board.Reconcile(["a", "c", "d", "e"]);
        board.RenameFolder(folderId, "Post-service");

        board.Restore(before);
        Assert.Equal(["c", "d"], board.Folder(folderId)?.TimerIds);
        Assert.Equal("Post-service", board.Folder(folderId)?.Name); // order-only snapshots never revert a rename
        AssertExactlyOnce(board, "a", "c", "d", "e");
        Assert.Equal(["a", "c", "d", "e"], board.AllTimerIds);
    }

    // Windows: Move up / Move down and the Folder picker.

    [Fact]
    public void StepsMoveWithinTheFolderOrTheTopLevel()
    {
        var (board, folderId) = MakeBoard();
        Assert.True(board.StepTimer("d", -1));
        Assert.Equal(["d", "c"], board.Folder(folderId)?.TimerIds);
        Assert.False(board.StepTimer("d", -1)); // already first in its folder
        Assert.True(board.StepTimer("a", +1));
        Assert.Equal(["b", "a", folderId], board.Nodes.Select(n => n.Id));
        Assert.True(board.StepFolder(folderId, -1));
        Assert.Equal(["b", folderId, "a"], board.Nodes.Select(n => n.Id));
        Assert.False(board.StepFolder("a", -1)); // a timer isn't a folder
        AssertExactlyOnce(board, "a", "b", "c", "d");
    }

    [Fact]
    public void PutInFolderFilesAndTakesOut()
    {
        var (board, folderId) = MakeBoard();
        board.PutInFolder("a", folderId);
        Assert.Equal(["c", "d", "a"], board.Folder(folderId)?.TimerIds);
        board.PutInFolder("a", folderId); // already there: stays where it is
        Assert.Equal(["c", "d", "a"], board.Folder(folderId)?.TimerIds);
        board.PutInFolder("c", null);
        Assert.Equal("c", board.Nodes[^1].Id);
        board.PutInFolder("b", null); // not in a folder: stays put
        Assert.Equal("b", board.Nodes[0].Id);
        AssertExactlyOnce(board, "a", "b", "c", "d");
    }
}
