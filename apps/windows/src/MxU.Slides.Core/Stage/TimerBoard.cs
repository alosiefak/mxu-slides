using System.Text.Json;
using System.Text.Json.Serialization;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Lyrics;

namespace MxU.Slides.Core.Stage;

/// <summary>A folder on the Timers panel: a name, open or folded, and the timers filed in it in order.</summary>
public sealed record TimerFolder(string Id, string Name, bool Collapsed, IReadOnlyList<string> TimerIds)
{
    public TimerFolder(string name) : this(StageIds.New(), name, false, []) { }

    // Records compare lists by reference; a folder is the same folder when its members read the same.
    public bool Equals(TimerFolder? other) =>
        other is not null && Id == other.Id && Name == other.Name && Collapsed == other.Collapsed && TimerIds.SequenceEqual(other.TimerIds);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Collapsed, TimerIds.Count);
}

/// <summary>One entry at the top level of the board: a timer, or a folder of timers.</summary>
public sealed record TimerBoardNode(string? TimerId, TimerFolder? Folder)
{
    public string Id => TimerId ?? Folder!.Id;

    public static TimerBoardNode Timer(string id) => new(id, null);

    public static TimerBoardNode Of(TimerFolder folder) => new(null, folder);
}

/// <summary>A board's order for undo: its top-level ids and each folder's members (TimerBoardOrder in Swift).</summary>
public sealed record TimerBoardOrder(IReadOnlyList<string> NodeIds, IReadOnlyDictionary<string, IReadOnlyList<string>> Memberships)
{
    /// <summary>Every id the order places, at the top level or in a folder.</summary>
    public HashSet<string> Membership
    {
        get
        {
            var all = new HashSet<string>(NodeIds);
            foreach (var members in Memberships.Values) all.UnionWith(members);
            return all;
        }
    }

    public bool Equals(TimerBoardOrder? other) =>
        other is not null && NodeIds.SequenceEqual(other.NodeIds) && Memberships.Count == other.Memberships.Count
        && Memberships.All(pair => other.Memberships.TryGetValue(pair.Key, out var members) && members.SequenceEqual(pair.Value));

    public override int GetHashCode() => HashCode.Combine(NodeIds.Count, Memberships.Count);
}

/// <summary>
/// How the Timers panel files its timers: top-level timers and folders in the operator's
/// order, every timer exactly once. Timers themselves live in <see cref="TimerController"/>;
/// the board only holds their ids, and <see cref="Reconcile"/> keeps it in step when timers
/// come and go. Port of SlideScene/TimerBoard.swift: the methods change the board in place,
/// as the Swift struct's mutating methods do, and <see cref="Copy"/> stands in for Swift
/// copying a value.
/// </summary>
public sealed class TimerBoard : IEquatable<TimerBoard>
{
    private readonly List<TimerBoardNode> nodes;

    public TimerBoard(IEnumerable<TimerBoardNode>? nodes = null) => this.nodes = [.. nodes ?? []];

    public IReadOnlyList<TimerBoardNode> Nodes => nodes;

    public TimerBoard Copy() => new(nodes);

    /// <summary>Every timer in board order, folders' members in place.</summary>
    public List<string> AllTimerIds => [.. nodes.SelectMany(node => node.Folder is { } folder ? folder.TimerIds : [node.TimerId!])];

    public TimerFolder? Folder(string id) => nodes.Select(n => n.Folder).FirstOrDefault(f => f?.Id == id);

    public TimerFolder? FolderContaining(string timerId) => nodes.Select(n => n.Folder).FirstOrDefault(f => f?.TimerIds.Contains(timerId) == true);

    /// <summary>The search: timers whose name matches, and every timer of a folder whose name matches.</summary>
    public List<string> FilteredTimerIds(string query, Func<string, string?> name)
    {
        var trimmed = query.TrimmingWhitespacesAndNewlines();
        if (trimmed.Length == 0) return AllTimerIds;
        bool Matches(string? text) => text is not null && SwiftString.LocalizedCaseInsensitiveContains(text, trimmed);
        return [.. nodes.SelectMany(node => node.Folder is { } folder
            ? Matches(folder.Name) ? folder.TimerIds : folder.TimerIds.Where(id => Matches(name(id)))
            : Matches(name(node.TimerId!)) ? [node.TimerId!] : Enumerable.Empty<string>())];
    }

    /// <summary>Adds an empty folder at the end and returns its id.</summary>
    public string AddFolder(string name, string? id = null)
    {
        var folder = new TimerFolder(id ?? StageIds.New(), name, false, []);
        nodes.Add(TimerBoardNode.Of(folder));
        return folder.Id;
    }

    /// <summary>Makes sure a well-known folder (Service Tracking) exists, adding it at the end.</summary>
    public void EnsureFolder(string id, string name)
    {
        if (Folder(id) is null) nodes.Add(TimerBoardNode.Of(new TimerFolder(id, name, false, [])));
    }

    /// <summary>A blank name is ignored; the name is trimmed of spaces.</summary>
    public void RenameFolder(string id, string name)
    {
        var trimmed = name.TrimmingWhitespaces();
        if (trimmed.Length == 0) return;
        MutateFolder(id, folder => folder with { Name = trimmed });
    }

    public void SetCollapsed(string id, bool collapsed) => MutateFolder(id, folder => folder with { Collapsed = collapsed });

    /// <summary>Removes a folder; its timers take its place at the top level, in order (deleting a folder keeps its timers).</summary>
    public void RemoveFolder(string id)
    {
        var index = nodes.FindIndex(n => n.Id == id);
        if (index < 0 || nodes[index].Folder is not { } folder) return;
        nodes.RemoveAt(index);
        nodes.InsertRange(index, folder.TimerIds.Select(TimerBoardNode.Timer));
    }

    /// <summary>
    /// Moves a timer before a node. When that node is a timer inside a folder, the timer joins the
    /// folder there; before a folder (or with null, at the end) it goes to the top level.
    /// </summary>
    public void MoveTimer(string id, string? beforeNode)
    {
        if (!KnowsTimer(id) || id == beforeNode) return;
        if (beforeNode is not null && FolderContaining(beforeNode) is { } host && host.Id != id)
        {
            DetachTimer(id);
            MutateFolder(host.Id, folder =>
            {
                var members = folder.TimerIds.ToList();
                var at = members.IndexOf(beforeNode);
                members.Insert(at >= 0 ? at : members.Count, id);
                return folder with { TimerIds = members };
            });
        }
        else
        {
            DetachTimer(id);
            var at = beforeNode is null ? -1 : nodes.FindIndex(n => n.Id == beforeNode);
            nodes.Insert(at >= 0 ? Math.Min(at, nodes.Count) : nodes.Count, TimerBoardNode.Timer(id));
        }
    }

    /// <summary>Files a timer at the end of a folder (Swift's moveTimer(id:intoFolder:)).</summary>
    public void MoveTimerIntoFolder(string id, string folderId)
    {
        if (!KnowsTimer(id) || Folder(folderId) is null) return;
        DetachTimer(id);
        MutateFolder(folderId, folder => folder with { TimerIds = [.. folder.TimerIds, id] });
    }

    /// <summary>Moves a folder before a node (before the folder holding that node); null moves it to the end. Folders never nest.</summary>
    public void MoveFolder(string id, string? beforeNode)
    {
        if (id == beforeNode) return;
        var from = nodes.FindIndex(n => n.Id == id);
        if (from < 0 || nodes[from].Folder is null) return;
        var target = beforeNode is null ? null : FolderContaining(beforeNode)?.Id ?? beforeNode;
        if (target == id) return;
        var node = nodes[from];
        nodes.RemoveAt(from);
        var at = target is null ? -1 : nodes.FindIndex(n => n.Id == target);
        nodes.Insert(at >= 0 ? Math.Min(at, nodes.Count) : nodes.Count, node);
    }

    public TimerBoardOrder OrderSnapshot
    {
        get
        {
            var memberships = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var folder in nodes.Select(n => n.Folder).OfType<TimerFolder>()) memberships.TryAdd(folder.Id, [.. folder.TimerIds]);
            return new TimerBoardOrder([.. nodes.Select(n => n.Id)], memberships);
        }
    }

    /// <summary>
    /// Puts back an order from <see cref="OrderSnapshot"/>, keeping only timers that still exist,
    /// keeping folder names as they are now, and adding anything new at the end.
    /// </summary>
    public void Restore(TimerBoardOrder order)
    {
        var currentOrdered = AllTimerIds;
        var currentTimers = currentOrdered.ToHashSet();
        var foldersById = new Dictionary<string, TimerFolder>();
        var folderOrder = new List<string>();
        foreach (var folder in nodes.Select(n => n.Folder).OfType<TimerFolder>())
        {
            var restoredFolder = order.Memberships.TryGetValue(folder.Id, out var members)
                ? folder with { TimerIds = [.. members.Where(currentTimers.Contains)] }
                : folder;
            foldersById[folder.Id] = restoredFolder;
            folderOrder.Add(folder.Id);
        }
        var foldered = foldersById.Values.SelectMany(f => f.TimerIds).ToHashSet();
        var restored = new List<TimerBoardNode>();
        foreach (var id in order.NodeIds)
        {
            if (foldersById.TryGetValue(id, out var folder)) restored.Add(TimerBoardNode.Of(folder));
            else if (currentTimers.Contains(id) && !foldered.Contains(id)) restored.Add(TimerBoardNode.Timer(id));
        }
        var placedFolders = restored.Select(n => n.Folder?.Id).OfType<string>().ToHashSet();
        foreach (var id in folderOrder.Where(id => !placedFolders.Contains(id))) restored.Add(TimerBoardNode.Of(foldersById[id]));
        nodes.Clear();
        nodes.AddRange(restored);
        var placed = AllTimerIds.ToHashSet();
        foreach (var id in currentOrdered.Where(id => !placed.Contains(id))) nodes.Add(TimerBoardNode.Timer(id));
    }

    /// <summary>
    /// Brings the board in line with the timers that exist: deleted timers leave (an emptied
    /// folder stays: grouping isn't garbage), new ones join at the end of the top level.
    /// </summary>
    public void Reconcile(IReadOnlyList<string> timerIds)
    {
        var known = timerIds.ToHashSet();
        var kept = new List<TimerBoardNode>();
        foreach (var node in nodes)
        {
            if (node.Folder is { } folder) kept.Add(TimerBoardNode.Of(folder with { TimerIds = [.. folder.TimerIds.Where(known.Contains)] }));
            else if (known.Contains(node.TimerId!)) kept.Add(node);
        }
        nodes.Clear();
        nodes.AddRange(kept);
        var present = AllTimerIds.ToHashSet();
        foreach (var id in timerIds.Where(id => !present.Contains(id))) nodes.Add(TimerBoardNode.Timer(id));
    }

    // MARK: Windows-only steps (the Mac reorders the board by dragging)

    /// <summary>Moves a timer one place up or down within its folder, or at the top level; false at either end.</summary>
    public bool StepTimer(string id, int offset)
    {
        if (FolderContaining(id) is { } folder)
        {
            var members = folder.TimerIds.ToList();
            if (!Swap(members, members.IndexOf(id), offset)) return false;
            MutateFolder(folder.Id, f => f with { TimerIds = members });
            return true;
        }
        var index = nodes.FindIndex(n => n.TimerId == id);
        return index >= 0 && Swap(nodes, index, offset);
    }

    /// <summary>Moves a folder one place up or down among the top-level entries; false at either end.</summary>
    public bool StepFolder(string id, int offset)
    {
        var index = nodes.FindIndex(n => n.Folder?.Id == id);
        return index >= 0 && Swap(nodes, index, offset);
    }

    /// <summary>Files a timer in a folder, or (<paramref name="folderId"/> null) takes it out to the end of the top level.</summary>
    public void PutInFolder(string id, string? folderId)
    {
        if (folderId is null)
        {
            if (FolderContaining(id) is not null) MoveTimer(id, beforeNode: null);
        }
        else if (FolderContaining(id)?.Id != folderId)
        {
            MoveTimerIntoFolder(id, folderId);
        }
    }

    private static bool Swap<T>(List<T> list, int index, int offset)
    {
        var target = index + offset;
        if (index < 0 || target < 0 || target >= list.Count) return false;
        (list[index], list[target]) = (list[target], list[index]);
        return true;
    }

    // MARK: Internals

    private bool KnowsTimer(string id) => AllTimerIds.Contains(id);

    private void DetachTimer(string id)
    {
        var kept = new List<TimerBoardNode>();
        foreach (var node in nodes)
        {
            if (node.Folder is { } folder) kept.Add(TimerBoardNode.Of(folder with { TimerIds = [.. folder.TimerIds.Where(t => t != id)] }));
            else if (node.TimerId != id) kept.Add(node);
        }
        nodes.Clear();
        nodes.AddRange(kept);
    }

    private void MutateFolder(string id, Func<TimerFolder, TimerFolder> change)
    {
        var index = nodes.FindIndex(n => n.Id == id);
        if (index < 0 || nodes[index].Folder is not { } folder) return;
        nodes[index] = TimerBoardNode.Of(change(folder));
    }

    // MARK: Equality

    public bool Equals(TimerBoard? other) => other is not null && nodes.SequenceEqual(other.nodes);

    public override bool Equals(object? obj) => Equals(obj as TimerBoard);

    public override int GetHashCode() => nodes.Count;

    // MARK: Featured timer and roll-ups (the folder row's readout)

    /// <summary>The live timer nearest its end; an unlimited Count Up ranks last.</summary>
    public static TimerSnapshot? Featured(IEnumerable<TimerSnapshot> timers, DateTimeOffset at) => TimerController.Featured(timers, at);

    public static TimerUrgency RollupUrgency(IEnumerable<TimerSnapshot> timers, DateTimeOffset at) => TimerController.RollupUrgency(timers, at);

    public static TimerWarning? RollupWarning(IEnumerable<TimerSnapshot> timers, DateTimeOffset at) => TimerController.RollupWarning(timers, at);

    // MARK: Saving, in the Swift Codable shape

    // Swift encodes `enum Node { case timer(String); case folder(Folder) }` as {"timer":{"_0":"id"}}
    // or {"folder":{"_0":{…}}}, and Folder's properties by their Swift names (timerIDs, not timerIds).
    private sealed record Wrapped<T>([property: JsonPropertyName("_0")] T Value);

    private sealed record FolderJson(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("collapsed")] bool Collapsed,
        [property: JsonPropertyName("timerIDs")] List<string> TimerIds);

    private sealed record NodeJson(
        [property: JsonPropertyName("timer")] Wrapped<string>? Timer,
        [property: JsonPropertyName("folder")] Wrapped<FolderJson>? Folder);

    private sealed record BoardJson([property: JsonPropertyName("nodes")] List<NodeJson> Nodes);

    /// <summary>The board as the Mac's TimersController saves it (UserDefaults "serviceControls.timerBoard").</summary>
    public string ToJson() => MxuJson.Serialize(new BoardJson([.. nodes.Select(n => n.Folder is { } f
        ? new NodeJson(null, new Wrapped<FolderJson>(new FolderJson(f.Id, f.Name, f.Collapsed, [.. f.TimerIds])))
        : new NodeJson(new Wrapped<string>(n.TimerId!), null))]));

    /// <summary>Reads a saved board; an unreadable one reads as an empty board (Reconcile then lists every timer).</summary>
    public static TimerBoard FromJson(string json)
    {
        try
        {
            var board = MxuJson.Deserialize<BoardJson>(json);
            return new TimerBoard(board.Nodes.Select(n => n.Folder?.Value is { } f
                    ? TimerBoardNode.Of(new TimerFolder(f.Id, f.Name, f.Collapsed, [.. f.TimerIds ?? []]))
                    : n.Timer?.Value is { } id ? TimerBoardNode.Timer(id) : null)
                .OfType<TimerBoardNode>());
        }
        catch (JsonException)
        {
            return new TimerBoard();
        }
    }
}
