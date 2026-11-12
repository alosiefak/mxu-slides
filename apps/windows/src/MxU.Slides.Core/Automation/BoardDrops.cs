using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Automation;

/// <summary>What's being dragged on a board: one item (a combo, alert, timer or trigger) or a folder.</summary>
public abstract record BoardDrag(string Id)
{
    public sealed record Item(string Id) : BoardDrag(Id);

    public sealed record Folder(string Id) : BoardDrag(Id);
}

/// <summary>Where it's dropped: just above a row, onto a folder's name, or below everything.</summary>
public abstract record BoardDropTarget
{
    private BoardDropTarget() { }

    /// <summary>Above this row (an item, or a folder's header row).</summary>
    public sealed record BeforeRow(string Id) : BoardDropTarget;

    /// <summary>Onto a folder's name: an item goes in at the end; a folder goes above it.</summary>
    public sealed record OntoFolder(string Id) : BoardDropTarget;

    /// <summary>The strip under the last row: the end of the top level.</summary>
    public sealed record End : BoardDropTarget;
}

/// <summary>The one move a drop makes. Port of the moves ControlBoardLogic offers.</summary>
public abstract record BoardMove
{
    private BoardMove() { }

    /// <summary>The item goes before <paramref name="BeforeNode"/> (into that row's folder if it's in one); null is the end of the top level.</summary>
    public sealed record ItemBefore(string Id, string? BeforeNode) : BoardMove;

    public sealed record ItemIntoFolder(string Id, string FolderId) : BoardMove;

    /// <summary>The folder goes before <paramref name="BeforeNode"/> (before the folder holding it); null is the end. Folders never nest.</summary>
    public sealed record FolderBefore(string Id, string? BeforeNode) : BoardMove;
}

/// <summary>
/// Dragging on the boards: the combos, the alerts, the timers and the scheduler's triggers. A drop
/// becomes exactly one of the board's own moves, so every item still appears once (the moves'
/// rule). Port of the Mac's handleControlBoardRowDrop, ControlBoardFolderBlock's drop and
/// ControlBoardEndDropStrip (Sources/Modules/ControlBoardFolders.swift); the boards' Move up,
/// Move down and Folder › stay beside dragging.
/// </summary>
public static class BoardDrops
{
    /// <summary>What a drop does, or null when it does nothing (dropped on itself).</summary>
    public static BoardMove? Resolve(BoardDrag drag, BoardDropTarget target) => (drag, target) switch
    {
        (BoardDrag.Folder folder, BoardDropTarget.BeforeRow row) when row.Id != folder.Id => new BoardMove.FolderBefore(folder.Id, row.Id),
        (BoardDrag.Folder folder, BoardDropTarget.OntoFolder onto) when onto.Id != folder.Id => new BoardMove.FolderBefore(folder.Id, onto.Id),
        (BoardDrag.Folder folder, BoardDropTarget.End) => new BoardMove.FolderBefore(folder.Id, null),
        (BoardDrag.Item item, BoardDropTarget.BeforeRow row) when row.Id != item.Id => new BoardMove.ItemBefore(item.Id, row.Id),
        (BoardDrag.Item item, BoardDropTarget.OntoFolder onto) => new BoardMove.ItemIntoFolder(item.Id, onto.Id),
        (BoardDrag.Item item, BoardDropTarget.End) => new BoardMove.ItemBefore(item.Id, null),
        _ => null,
    };

    /// <summary>The insertion line for a drop: above the row, around the folder, or at the end.</summary>
    public static bool ShowsLineAbove(BoardDropTarget target) => target is BoardDropTarget.BeforeRow or BoardDropTarget.End;

    /// <summary>Applies a move to a combo or alert board.</summary>
    public static void Apply(this ControlBoard board, BoardMove move)
    {
        switch (move)
        {
            case BoardMove.ItemBefore m: board.MoveItem(m.Id, m.BeforeNode); break;
            case BoardMove.ItemIntoFolder m: board.MoveItemIntoFolder(m.Id, m.FolderId); break;
            case BoardMove.FolderBefore m: board.MoveFolder(m.Id, m.BeforeNode); break;
        }
    }

    /// <summary>Applies a move to the scheduler's board.</summary>
    public static void Apply(this SchedulerBoard board, BoardMove move)
    {
        switch (move)
        {
            case BoardMove.ItemBefore m: board.MoveTrigger(m.Id, m.BeforeNode); break;
            case BoardMove.ItemIntoFolder m: board.MoveTriggerIntoFolder(m.Id, m.FolderId); break;
            case BoardMove.FolderBefore m: board.MoveFolder(m.Id, m.BeforeNode); break;
        }
    }

    /// <summary>
    /// Applies a move to the timers (through the controller, which saves and keeps the service
    /// tracking timers where they are: they and their folder never move into or out of it).
    /// </summary>
    public static void Apply(this TimerController timers, BoardMove move)
    {
        switch (move)
        {
            // Above a service tracking timer means above their folder: nothing joins it by hand.
            case BoardMove.ItemBefore m when m.BeforeNode is { } before && timers.Board.FolderContaining(before)?.Id == ServiceTrackingTimers.FolderId:
                timers.MoveTimer(m.Id, ServiceTrackingTimers.FolderId);
                break;
            case BoardMove.ItemBefore m: timers.MoveTimer(m.Id, m.BeforeNode); break;
            case BoardMove.ItemIntoFolder m: timers.MoveTimerIntoFolder(m.Id, m.FolderId); break;
            case BoardMove.FolderBefore m: timers.MoveFolder(m.Id, m.BeforeNode); break;
        }
    }
}
