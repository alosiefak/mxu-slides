using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Automation;

// Dragging on the boards: which move a drop makes (the Mac's handleControlBoardRowDrop, the folder
// header's drop and the end strip in ControlBoardFolders.swift), and that each board applies it
// with its ported moves so every item stays exactly once.
public class BoardDropsTests
{
    private static ControlBoard Board(params string[] items)
    {
        var board = new ControlBoard { Id = ControlBoardLogic.ComboBoardId };
        board.Reconcile(items);
        return board;
    }

    [Fact]
    public void ADropBecomesOneMove()
    {
        Assert.Equal(new BoardMove.ItemBefore("a", "c"), BoardDrops.Resolve(new BoardDrag.Item("a"), new BoardDropTarget.BeforeRow("c")));
        Assert.Equal(new BoardMove.ItemIntoFolder("a", "f"), BoardDrops.Resolve(new BoardDrag.Item("a"), new BoardDropTarget.OntoFolder("f")));
        Assert.Equal(new BoardMove.ItemBefore("a", null), BoardDrops.Resolve(new BoardDrag.Item("a"), new BoardDropTarget.End()));
        Assert.Equal(new BoardMove.FolderBefore("f", "a"), BoardDrops.Resolve(new BoardDrag.Folder("f"), new BoardDropTarget.BeforeRow("a")));
        Assert.Equal(new BoardMove.FolderBefore("f", "g"), BoardDrops.Resolve(new BoardDrag.Folder("f"), new BoardDropTarget.OntoFolder("g")));
        Assert.Equal(new BoardMove.FolderBefore("f", null), BoardDrops.Resolve(new BoardDrag.Folder("f"), new BoardDropTarget.End()));
    }

    [Fact]
    public void DroppingOnItselfDoesNothing()
    {
        Assert.Null(BoardDrops.Resolve(new BoardDrag.Item("a"), new BoardDropTarget.BeforeRow("a")));
        Assert.Null(BoardDrops.Resolve(new BoardDrag.Folder("f"), new BoardDropTarget.BeforeRow("f")));
        Assert.Null(BoardDrops.Resolve(new BoardDrag.Folder("f"), new BoardDropTarget.OntoFolder("f")));
    }

    [Fact]
    public void DragsOnAComboBoardKeepEveryItemOnce()
    {
        var board = Board("a", "b", "c");
        var folder = board.AddFolder("Sundays");
        void Drop(BoardDrag drag, BoardDropTarget target) => board.Apply(BoardDrops.Resolve(drag, target)!);

        Drop(new BoardDrag.Item("b"), new BoardDropTarget.OntoFolder(folder));
        Assert.Equal(["b"], board.Folder(folder)!.ItemIds);
        // Above a row inside the folder: joins the folder there.
        Drop(new BoardDrag.Item("c"), new BoardDropTarget.BeforeRow("b"));
        Assert.Equal(["c", "b"], board.Folder(folder)!.ItemIds);
        // Above a top-level row: out of the folder.
        Drop(new BoardDrag.Item("b"), new BoardDropTarget.BeforeRow("a"));
        Assert.Equal(["b", "a", folder], board.Nodes);
        // The folder above everything, then to the end.
        Drop(new BoardDrag.Folder(folder), new BoardDropTarget.BeforeRow("b"));
        Assert.Equal([folder, "b", "a"], board.Nodes);
        Drop(new BoardDrag.Folder(folder), new BoardDropTarget.End());
        Assert.Equal(["b", "a", folder], board.Nodes);
        Drop(new BoardDrag.Item("c"), new BoardDropTarget.End());
        Assert.Equal(["b", "a", folder, "c"], board.Nodes);
        Assert.Equal(3, board.AllItemIds().Distinct().Count());
        Assert.Equal(3, board.AllItemIds().Count);
    }

    [Fact]
    public void AFolderDroppedAboveAMemberGoesAboveItsFolder()
    {
        var board = Board("a", "b");
        var first = board.AddFolder("One");
        var second = board.AddFolder("Two");
        board.MoveItemIntoFolder("b", first);
        board.Apply(BoardDrops.Resolve(new BoardDrag.Folder(second), new BoardDropTarget.BeforeRow("b"))!);
        Assert.Equal(["a", second, first], board.Nodes); // folders never nest
    }

    [Fact]
    public void DragsOnTheSchedulerBoard()
    {
        var board = new SchedulerBoard { Id = "scheduler-board" };
        board.Reconcile(["t1", "t2", "t3"]);
        var folder = board.AddFolder("Evenings");
        board.Apply(BoardDrops.Resolve(new BoardDrag.Item("t3"), new BoardDropTarget.OntoFolder(folder))!);
        board.Apply(BoardDrops.Resolve(new BoardDrag.Item("t2"), new BoardDropTarget.BeforeRow("t1"))!);
        Assert.Equal(["t3"], board.Folder(folder)!.TriggerIds);
        Assert.Equal(["t2", "t1", "t3"], board.AllTriggerIds());
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 7, 19, 9, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public void TimersDragButNothingJoinsServiceTracking()
    {
        var timers = new TimerController(new Clock());
        var sermon = timers.AddCountdown("Sermon", 1800);
        var walkIn = timers.AddCountdown("Walk-in", 600);
        timers.SetSystemTimers([new TimerSnapshot("Item Remaining", TimerMode.Countdown, id: "service_tracking.item.remaining")]);
        var folder = timers.AddFolder("Sunday");

        timers.Apply(BoardDrops.Resolve(new BoardDrag.Item(sermon), new BoardDropTarget.OntoFolder(folder))!);
        Assert.Equal(folder, timers.Board.FolderContaining(sermon)?.Id);
        timers.Apply(BoardDrops.Resolve(new BoardDrag.Item(walkIn), new BoardDropTarget.BeforeRow(sermon))!);
        Assert.Equal([walkIn, sermon], timers.Board.FolderContaining(sermon)!.TimerIds);

        // Above a service tracking timer, or onto its folder: it stays out of that folder.
        timers.Apply(BoardDrops.Resolve(new BoardDrag.Item(walkIn), new BoardDropTarget.BeforeRow("service_tracking.item.remaining"))!);
        Assert.NotEqual(ServiceTrackingTimers.FolderId, timers.Board.FolderContaining(walkIn)?.Id);
        timers.Apply(BoardDrops.Resolve(new BoardDrag.Item(walkIn), new BoardDropTarget.OntoFolder(ServiceTrackingTimers.FolderId))!);
        Assert.NotEqual(ServiceTrackingTimers.FolderId, timers.Board.FolderContaining(walkIn)?.Id);
        Assert.Equal(ServiceTrackingTimers.FolderId, timers.Board.FolderContaining("service_tracking.item.remaining")?.Id);
    }
}
