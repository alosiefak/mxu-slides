using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// Move Up and Move Down for the combo and scheduler boards. The Mac reorders these boards
/// by dragging; Windows also offers buttons, which step an item one place within the list
/// it is in (its folder, or the top level, where a whole folder counts as one place).
/// Windows-only; the moves themselves are the ported <see cref="ControlBoardLogic"/> and
/// <see cref="SchedulerBoardLogic"/> rules, so every item still appears exactly once.
/// </summary>
public static class BoardSteps
{
    /// <summary>Swaps the entry with its neighbour (<paramref name="offset"/> -1 up, +1 down); false at either end.</summary>
    private static bool Step(List<string> list, string id, int offset)
    {
        var index = list.IndexOf(id);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= list.Count) return false;
        (list[index], list[target]) = (list[target], list[index]);
        return true;
    }

    /// <summary>Moves a combo one place up or down in its folder, or at the top level.</summary>
    public static bool StepItem(this ControlBoard board, string id, int offset) =>
        Step(board.FolderContaining(id) is { } folder ? folder.ItemIds : board.Nodes, id, offset);

    /// <summary>Moves a folder one place up or down among the top-level entries.</summary>
    public static bool StepFolder(this ControlBoard board, string folderId, int offset) =>
        board.Folder(folderId) is not null && Step(board.Nodes, folderId, offset);

    /// <summary>Moves a trigger one place up or down in its folder, or at the top level.</summary>
    public static bool StepTrigger(this SchedulerBoard board, string id, int offset) =>
        Step(board.FolderContaining(id) is { } folder ? folder.TriggerIds : board.Nodes, id, offset);

    /// <summary>Moves a scheduler folder one place up or down among the top-level entries.</summary>
    public static bool StepFolder(this SchedulerBoard board, string folderId, int offset) =>
        board.Folder(folderId) is not null && Step(board.Nodes, folderId, offset);

    /// <summary>
    /// Puts a combo in a folder, or (<paramref name="folderId"/> null) takes it out to the end of
    /// the top level, as the Mac's "Out of the Folder" does.
    /// </summary>
    public static void PutInFolder(this ControlBoard board, string id, string? folderId)
    {
        if (folderId is null)
        {
            if (board.FolderContaining(id) is not null) board.MoveItem(id, beforeNode: null);
        }
        else if (board.FolderContaining(id)?.Id != folderId)
        {
            board.MoveItemIntoFolder(id, folderId);
        }
    }

    /// <summary>Puts a trigger in a folder, or (<paramref name="folderId"/> null) takes it out to the end of the top level.</summary>
    public static void PutInFolder(this SchedulerBoard board, string id, string? folderId)
    {
        if (folderId is null)
        {
            if (board.FolderContaining(id) is not null) board.MoveTrigger(id, beforeNode: null);
        }
        else if (board.FolderContaining(id)?.Id != folderId)
        {
            board.MoveTriggerIntoFolder(id, folderId);
        }
    }
}
