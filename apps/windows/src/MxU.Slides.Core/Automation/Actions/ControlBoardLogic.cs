using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// A board's order for undo: its top-level nodes and each folder's members.
/// Port of BoardOrder in PresenterCore/Library/MoveUndoJournal.swift.
/// </summary>
public sealed record BoardOrder(IReadOnlyList<string> Nodes, IReadOnlyDictionary<string, IReadOnlyList<string>> Memberships)
{
    /// <summary>Every id the order places, at the top level or in a folder.</summary>
    public HashSet<string> Membership
    {
        get
        {
            var all = new HashSet<string>(Nodes);
            foreach (var members in Memberships.Values) all.UnionWith(members);
            return all;
        }
    }

    public bool Equals(BoardOrder? other) =>
        other is not null && Nodes.SequenceEqual(other.Nodes) && Memberships.Count == other.Memberships.Count
        && Memberships.All(pair => other.Memberships.TryGetValue(pair.Key, out var members) && members.SequenceEqual(pair.Value));

    public override int GetHashCode() => HashCode.Combine(Nodes.Count, Memberships.Count);
}

/// <summary>
/// A control board (the Action Combos module's list, and the alert and stream boards):
/// items and folders in the user's order. <see cref="ControlBoard.Nodes"/> holds the
/// top-level order (item ids and folder ids); each folder holds its items. Every item
/// appears exactly once. The methods edit the board in place, as the Mac's mutating
/// methods do. Port of PresenterCore/Library/ControlBoardLogic.swift.
/// </summary>
public static class ControlBoardLogic
{
    /// <summary>The well-known board ids (the documents are stored under these).</summary>
    public const string ComboBoardId = "combo-board";
    public const string AlertBoardId = "alert-board";
    public const string StreamBoardId = "stream-board";

    /// <summary>Every item in board order, folders' members in place.</summary>
    public static List<string> AllItemIds(this ControlBoard board) =>
        board.Nodes.SelectMany(id => board.Folder(id) is { } folder ? folder.ItemIds : [id]).ToList();

    /// <summary>The items showing: a collapsed folder's members are hidden.</summary>
    public static List<string> VisibleItemIds(this ControlBoard board) =>
        board.Nodes.SelectMany(id => board.Folder(id) is not { } folder ? [id] : folder.Collapsed == true ? [] : folder.ItemIds).ToList();

    /// <summary>A shift-click selection: everything from the anchor to the item, in this order (just the item without an anchor).</summary>
    public static List<string> Range(IReadOnlyList<string> order, string? anchor, string through)
    {
        var to = order.ToList().IndexOf(through);
        var from = anchor is null ? -1 : order.ToList().IndexOf(anchor);
        if (from < 0 || to < 0) return [through];
        return order.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1).ToList();
    }

    public static ControlFolder? Folder(this ControlBoard board, string id) => board.Folders.FirstOrDefault(f => f.Id == id);

    public static List<ControlFolder> OrderedFolders(this ControlBoard board) => board.Nodes.Select(board.Folder).OfType<ControlFolder>().ToList();

    public static ControlFolder? FolderContaining(this ControlBoard board, string itemId) => board.Folders.FirstOrDefault(f => f.ItemIds.Contains(itemId));

    /// <summary>The search: items whose name matches, and every item of a folder whose name matches.</summary>
    public static List<string> FilteredItemIds(this ControlBoard board, string query, Func<string, string?> name)
    {
        var trimmed = Lyrics.SwiftString.TrimmingWhitespacesAndNewlines(query);
        if (trimmed.Length == 0) return board.AllItemIds();
        bool Matches(string? text) => text is not null && Lyrics.SwiftString.LocalizedCaseInsensitiveContains(text, trimmed);
        return board.Nodes.SelectMany(id =>
        {
            if (board.Folder(id) is { } folder)
                return Matches(folder.Name) ? folder.ItemIds : folder.ItemIds.Where(item => Matches(name(item)));
            return Matches(name(id)) ? [id] : Enumerable.Empty<string>();
        }).ToList();
    }

    /// <summary>Adds an empty folder at the end and returns its id (<paramref name="id"/> lets the caller mint it).</summary>
    public static string AddFolder(this ControlBoard board, string name, string? id = null)
    {
        var folder = new ControlFolder { Id = id ?? TimelineClips.NewId(), Name = name, ItemIds = [] };
        board.Folders.Add(folder);
        board.Nodes.Add(folder.Id);
        return folder.Id;
    }

    public static void RenameFolder(this ControlBoard board, string id, string name)
    {
        var trimmed = Lyrics.SwiftString.TrimmingWhitespaces(name);
        if (trimmed.Length == 0) return;
        if (board.Folder(id) is { } folder) folder.Name = trimmed;
    }

    /// <summary>Collapsed is stored as true, expanded as absent.</summary>
    public static void SetFolderCollapsed(this ControlBoard board, string id, bool collapsed)
    {
        if (board.Folder(id) is { } folder) folder.Collapsed = collapsed ? true : null;
    }

    /// <summary>Removes a folder; its items take its place in the top-level order.</summary>
    public static void RemoveFolder(this ControlBoard board, string id)
    {
        if (board.Folder(id) is not { } folder) return;
        var index = board.Nodes.IndexOf(id);
        if (index < 0) return;
        board.Nodes.RemoveAt(index);
        board.Nodes.InsertRange(index, folder.ItemIds);
        board.Folders.RemoveAll(f => f.Id == id);
    }

    /// <summary>Moves an item before a node (into that node's folder when it is in one); null moves it to the end.</summary>
    public static void MoveItem(this ControlBoard board, string id, string? beforeNode)
    {
        if (!board.AllItemIds().Contains(id) || id == beforeNode) return;
        if (beforeNode is not null && board.FolderContaining(beforeNode) is { } host && host.Id != id)
        {
            board.DetachItem(id);
            var at = host.ItemIds.IndexOf(beforeNode);
            host.ItemIds.Insert(at >= 0 ? at : host.ItemIds.Count, id);
        }
        else
        {
            board.DetachItem(id);
            var at = beforeNode is null ? -1 : board.Nodes.IndexOf(beforeNode);
            board.Nodes.Insert(at >= 0 ? Math.Min(at, board.Nodes.Count) : board.Nodes.Count, id);
        }
    }

    public static void MoveItemIntoFolder(this ControlBoard board, string id, string folderId)
    {
        if (!board.AllItemIds().Contains(id) || board.Folder(folderId) is not { } folder) return;
        board.DetachItem(id);
        folder.ItemIds.Add(id);
    }

    /// <summary>Moves a folder before a node (before the folder holding that node); null moves it to the end.</summary>
    public static void MoveFolder(this ControlBoard board, string id, string? beforeNode)
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

    public static BoardOrder OrderSnapshot(this ControlBoard board)
    {
        var memberships = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var folder in board.Folders) memberships.TryAdd(folder.Id, folder.ItemIds.ToList());
        return new BoardOrder(board.Nodes.ToList(), memberships);
    }

    /// <summary>Puts back an order from <see cref="OrderSnapshot"/>, keeping only what still exists and appending anything new.</summary>
    public static void Restore(this ControlBoard board, BoardOrder order)
    {
        var currentOrdered = board.AllItemIds();
        var currentItems = currentOrdered.ToHashSet();
        var currentFolderIds = board.Folders.Select(f => f.Id).ToHashSet();
        foreach (var folder in board.Folders)
            if (order.Memberships.TryGetValue(folder.Id, out var members)) folder.ItemIds = members.Where(currentItems.Contains).ToList();
        var restored = order.Nodes.Where(id => currentItems.Contains(id) || currentFolderIds.Contains(id)).ToList();
        foreach (var id in board.Folders.Select(f => f.Id))
            if (!restored.Contains(id)) restored.Add(id);
        var foldered = board.Folders.SelectMany(f => f.ItemIds).ToHashSet();
        restored.RemoveAll(id => foldered.Contains(id) && !currentFolderIds.Contains(id));
        board.Nodes = restored;
        var placed = board.AllItemIds().ToHashSet();
        foreach (var id in currentOrdered)
            if (!placed.Contains(id)) board.Nodes.Add(id);
    }

    /// <summary>Brings the board in line with the items that exist: gone items leave, new ones join at the end.</summary>
    public static void Reconcile(this ControlBoard board, IReadOnlyList<string> itemIds)
    {
        var known = itemIds.ToHashSet();
        var folderIds = board.Folders.Select(f => f.Id).ToHashSet();
        board.Nodes = board.Nodes.Where(id => known.Contains(id) || folderIds.Contains(id)).ToList();
        foreach (var folder in board.Folders)
            if (!board.Nodes.Contains(folder.Id)) board.Nodes.Add(folder.Id);
        foreach (var folder in board.Folders) folder.ItemIds.RemoveAll(id => !known.Contains(id));
        var present = board.AllItemIds().ToHashSet();
        foreach (var id in itemIds)
            if (!present.Contains(id)) board.Nodes.Add(id);
    }

    private static void DetachItem(this ControlBoard board, string id)
    {
        var folderIds = board.Folders.Select(f => f.Id).ToHashSet();
        board.Nodes.RemoveAll(node => node == id && !folderIds.Contains(id));
        foreach (var folder in board.Folders) folder.ItemIds.RemoveAll(item => item == id);
    }
}
