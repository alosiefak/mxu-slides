using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// The scheduler's list: triggers and folders in the user's order, like a control board,
/// except a folder can be switched off (gating every trigger in it) and duplicated.
/// The methods edit the board in place. Port of PresenterCore/Library/SchedulerBoardLogic.swift.
/// </summary>
public static class SchedulerBoardLogic
{
    /// <summary>The scheduler board's well-known document id.</summary>
    public const string WellKnownId = "scheduler-board";

    public static List<string> AllTriggerIds(this SchedulerBoard board) =>
        board.Nodes.SelectMany(id => board.Folder(id) is { } folder ? folder.TriggerIds : [id]).ToList();

    public static ScheduleFolder? Folder(this SchedulerBoard board, string id) => board.Folders.FirstOrDefault(f => f.Id == id);

    public static ScheduleFolder? FolderContaining(this SchedulerBoard board, string triggerId) =>
        board.Folders.FirstOrDefault(f => f.TriggerIds.Contains(triggerId));

    /// <summary>A trigger outside any folder is never gated by one; a folder is on unless switched off.</summary>
    public static bool FolderEnabled(this SchedulerBoard board, string triggerId) =>
        board.FolderContaining(triggerId) is not { } folder || (folder.Enabled ?? true);

    public static string AddFolder(this SchedulerBoard board, string name, string? id = null)
    {
        var folder = new ScheduleFolder { Id = id ?? TimelineClips.NewId(), Name = name, TriggerIds = [] };
        board.Folders.Add(folder);
        board.Nodes.Add(folder.Id);
        return folder.Id;
    }

    public static void RenameFolder(this SchedulerBoard board, string id, string name)
    {
        var trimmed = name.TrimmingWhitespaces();
        if (trimmed.Length == 0) return;
        if (board.Folder(id) is { } folder) folder.Name = trimmed;
    }

    /// <summary>On is stored as absent, off as false.</summary>
    public static void SetFolderEnabled(this SchedulerBoard board, string id, bool enabled)
    {
        if (board.Folder(id) is { } folder) folder.Enabled = enabled ? null : false;
    }

    public static void SetFolderCollapsed(this SchedulerBoard board, string id, bool collapsed)
    {
        if (board.Folder(id) is { } folder) folder.Collapsed = collapsed ? true : null;
    }

    /// <summary>Removes a folder; its triggers take its place in the order.</summary>
    public static void RemoveFolder(this SchedulerBoard board, string id)
    {
        if (board.Folder(id) is not { } folder) return;
        var index = board.Nodes.IndexOf(id);
        if (index < 0) return;
        board.Nodes.RemoveAt(index);
        board.Nodes.InsertRange(index, folder.TriggerIds);
        board.Folders.RemoveAll(f => f.Id == id);
    }

    /// <summary>A new trigger (a duplicate) goes right after the one it came from, in the same folder.</summary>
    public static void PlaceTrigger(this SchedulerBoard board, string id, string afterSibling)
    {
        if (board.AllTriggerIds().Contains(id)) return;
        if (board.FolderContaining(afterSibling) is { } host)
        {
            var at = host.TriggerIds.IndexOf(afterSibling);
            host.TriggerIds.Insert(at >= 0 ? at + 1 : host.TriggerIds.Count, id);
        }
        else
        {
            var at = board.Nodes.IndexOf(afterSibling);
            board.Nodes.Insert(at >= 0 ? Math.Min(at + 1, board.Nodes.Count) : board.Nodes.Count, id);
        }
    }

    /// <summary>
    /// A copy of a folder right after it: " Copy" on the name, the same on/off, holding the
    /// given (duplicated) triggers that aren't on the board yet. Null when there is no such folder.
    /// </summary>
    public static string? DuplicateFolder(this SchedulerBoard board, string id, IReadOnlyList<string> memberIds, string? copyId = null)
    {
        if (board.Folder(id) is not { } source) return null;
        var known = board.AllTriggerIds().ToHashSet();
        var copy = new ScheduleFolder
        {
            Id = copyId ?? TimelineClips.NewId(),
            Name = source.Name + " Copy",
            Enabled = source.Enabled,
            Collapsed = source.Collapsed,
            TriggerIds = memberIds.Where(member => !known.Contains(member)).ToList(),
        };
        board.Folders.Add(copy);
        var at = board.Nodes.IndexOf(id);
        board.Nodes.Insert(at >= 0 ? Math.Min(at + 1, board.Nodes.Count) : board.Nodes.Count, copy.Id);
        return copy.Id;
    }

    public static void MoveTrigger(this SchedulerBoard board, string id, string? beforeNode)
    {
        if (!board.AllTriggerIds().Contains(id) || id == beforeNode) return;
        if (beforeNode is not null && board.FolderContaining(beforeNode) is { } host && host.Id != id)
        {
            board.DetachTrigger(id);
            var at = host.TriggerIds.IndexOf(beforeNode);
            host.TriggerIds.Insert(at >= 0 ? at : host.TriggerIds.Count, id);
        }
        else
        {
            board.DetachTrigger(id);
            var at = beforeNode is null ? -1 : board.Nodes.IndexOf(beforeNode);
            board.Nodes.Insert(at >= 0 ? Math.Min(at, board.Nodes.Count) : board.Nodes.Count, id);
        }
    }

    public static void MoveTriggerIntoFolder(this SchedulerBoard board, string id, string folderId)
    {
        if (!board.AllTriggerIds().Contains(id) || board.Folder(folderId) is not { } folder) return;
        board.DetachTrigger(id);
        folder.TriggerIds.Add(id);
    }

    public static void MoveFolder(this SchedulerBoard board, string id, string? beforeNode)
    {
        if (id == beforeNode || board.Folder(id) is null) return;
        var from = board.Nodes.IndexOf(id);
        if (from < 0) return;
        var target = beforeNode is null ? null : board.FolderContaining(beforeNode)?.Id ?? beforeNode;
        if (target == id) return;
        board.Nodes.RemoveAt(from);
        var at = target is null ? -1 : board.Nodes.IndexOf(target);
        board.Nodes.Insert(at >= 0 ? Math.Min(at, board.Nodes.Count) : board.Nodes.Count, id);
    }

    public static BoardOrder OrderSnapshot(this SchedulerBoard board)
    {
        var memberships = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var folder in board.Folders) memberships.TryAdd(folder.Id, folder.TriggerIds.ToList());
        return new BoardOrder(board.Nodes.ToList(), memberships);
    }

    public static void Restore(this SchedulerBoard board, BoardOrder order)
    {
        var currentOrdered = board.AllTriggerIds();
        var currentTriggers = currentOrdered.ToHashSet();
        var currentFolderIds = board.Folders.Select(f => f.Id).ToHashSet();
        foreach (var folder in board.Folders)
            if (order.Memberships.TryGetValue(folder.Id, out var members)) folder.TriggerIds = members.Where(currentTriggers.Contains).ToList();
        var restored = order.Nodes.Where(id => currentTriggers.Contains(id) || currentFolderIds.Contains(id)).ToList();
        foreach (var id in board.Folders.Select(f => f.Id))
            if (!restored.Contains(id)) restored.Add(id);
        var foldered = board.Folders.SelectMany(f => f.TriggerIds).ToHashSet();
        restored.RemoveAll(id => foldered.Contains(id) && !currentFolderIds.Contains(id));
        board.Nodes = restored;
        var placed = board.AllTriggerIds().ToHashSet();
        foreach (var id in currentOrdered)
            if (!placed.Contains(id)) board.Nodes.Add(id);
    }

    public static void Reconcile(this SchedulerBoard board, IReadOnlyList<string> triggerIds)
    {
        var known = triggerIds.ToHashSet();
        var folderIds = board.Folders.Select(f => f.Id).ToHashSet();
        board.Nodes = board.Nodes.Where(id => known.Contains(id) || folderIds.Contains(id)).ToList();
        foreach (var folder in board.Folders)
            if (!board.Nodes.Contains(folder.Id)) board.Nodes.Add(folder.Id);
        foreach (var folder in board.Folders) folder.TriggerIds.RemoveAll(id => !known.Contains(id));
        var present = board.AllTriggerIds().ToHashSet();
        foreach (var id in triggerIds)
            if (!present.Contains(id)) board.Nodes.Add(id);
    }

    private static void DetachTrigger(this SchedulerBoard board, string id)
    {
        var folderIds = board.Folders.Select(f => f.Id).ToHashSet();
        board.Nodes.RemoveAll(node => node == id && !folderIds.Contains(id));
        foreach (var folder in board.Folders) folder.TriggerIds.RemoveAll(trigger => trigger == id);
    }
}

/// <summary>Small edits to a schedule trigger document, stored the way the Mac stores them.</summary>
public static class ScheduleTriggers
{
    /// <summary>On is stored as absent, off as false.</summary>
    public static void SetEnabled(ScheduleTrigger trigger, bool enabled) => trigger.Enabled = enabled ? null : false;
}
