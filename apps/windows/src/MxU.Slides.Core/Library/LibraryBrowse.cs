using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>What a row in the library is: a song, another presentation, a picture or video, or a piece of music.</summary>
public enum LibraryItemKind
{
    Song,
    Presentation,
    Media,
    Audio,
}

/// <summary>
/// One document as the library browser sees it: enough to sort, file and filter it without
/// opening the document again. The Mac's twin is <c>LibraryIndex.Entry</c> (its SQLite index);
/// on Windows the documents are already in memory, so an entry is made from the document plus
/// the dates <see cref="LibraryDates"/> and <see cref="LibraryUsage"/> keep.
/// </summary>
/// <param name="Folder">The folder path ("Hymns/Easter"), or "" for none.</param>
/// <param name="UpdatedAt">When it was last saved.</param>
/// <param name="AddedAt">When it came into this library (imported, made or restored).</param>
/// <param name="LastUsedAt">When it last went live or into a service on this PC; null when never.</param>
public sealed record LibraryEntry(
    string Id, LibraryItemKind Kind, string Name, string Folder,
    DateTimeOffset? UpdatedAt = null, DateTimeOffset? AddedAt = null, DateTimeOffset? LastUsedAt = null,
    bool Favorite = false, IReadOnlyList<string>? Tags = null)
{
    public IReadOnlyList<string> Tags { get; init; } = Tags ?? [];

    public bool IsPresentation => Kind is LibraryItemKind.Song or LibraryItemKind.Presentation;

    /// <summary>A presentation's kind for the browser: a song when it says so or has sections (as the library list always decided).</summary>
    public static LibraryItemKind KindOf(Presentation presentation) =>
        presentation.PresentationKind == PresentationKind.Song || presentation.Sections is { Count: > 0 }
            ? LibraryItemKind.Song
            : LibraryItemKind.Presentation;
}

/// <summary>
/// How the library list is ordered. Port of the Mac's <c>LibrarySort</c> (LibraryViewOptions.swift):
/// by name, or newest first by date modified, recently used or date added. Ties keep name order,
/// and anything never used sorts last under Recently Used.
/// <para>
/// Two deliberate differences: the Mac offers Date Added for media only (it's the only kind its
/// index records it for); here every kind has it, from when its file first landed in this library.
/// And "by name" is Windows' own alphabetical order, ignoring case ("export" before "Zion"), where
/// the Mac's index sorts by bytes (capitals first).
/// </para>
/// </summary>
public enum LibrarySort
{
    Name,
    DateModified,
    RecentlyUsed,
    DateAdded,
}

public static class LibrarySorts
{
    public static string Title(this LibrarySort sort) => sort switch
    {
        LibrarySort.DateModified => "Date Modified",
        LibrarySort.RecentlyUsed => "Recently Used",
        LibrarySort.DateAdded => "Date Added",
        _ => "Name",
    };

    public static IReadOnlyList<LibrarySort> All { get; } = [LibrarySort.Name, LibrarySort.DateModified, LibrarySort.RecentlyUsed, LibrarySort.DateAdded];

    /// <summary>The order names compare in: alphabetical, ignoring case, in this PC's language.</summary>
    public static readonly StringComparer NameOrder = StringComparer.CurrentCultureIgnoreCase;

    /// <summary>
    /// <paramref name="entries"/> in this order. Name sorts A–Z; the others put the newest first and
    /// keep A–Z among equals (the Mac sorts its name-ordered list stably, which comes to the same).
    /// </summary>
    public static IReadOnlyList<LibraryEntry> Ordered(this LibrarySort sort, IEnumerable<LibraryEntry> entries)
    {
        var byName = entries.OrderBy(e => e.Name, NameOrder).ThenBy(e => e.Id, StringComparer.Ordinal);
        return sort switch
        {
            LibrarySort.DateModified => byName.OrderByDescending(e => e.UpdatedAt ?? DateTimeOffset.MinValue).ToList(),
            LibrarySort.RecentlyUsed => byName.OrderByDescending(e => e.LastUsedAt ?? DateTimeOffset.MinValue).ToList(),
            LibrarySort.DateAdded => byName.OrderByDescending(e => e.AddedAt ?? DateTimeOffset.MinValue).ToList(),
            _ => byName.ToList(),
        };
    }
}

/// <summary>A folder and the folders inside it, for a folder picker ("Move to Folder ›").</summary>
public sealed record FolderNode(string Name, string Path, IReadOnlyList<FolderNode> Children);

/// <summary>
/// Library folders: which exist, which sit inside a folder, and how they nest. A folder exists while
/// something is filed in it, or while it's newly made and still empty (the Mac keeps those per
/// computer, as "pending folders"; so does Windows, in settings.json). Ports of the Mac's
/// <c>LibraryBrowseLogic</c> (IndexSnapshot.swift) and <c>FolderTreeLogic</c>; the path maths is
/// <see cref="FolderPaths"/>.
/// </summary>
public static class LibraryFolders
{
    /// <summary>Every folder path in use or kept empty, sorted.</summary>
    public static IReadOnlyList<string> Folders(IEnumerable<LibraryEntry> items, IEnumerable<string> empties) =>
        items.Select(i => i.Folder)
            .Where(f => f.Length > 0)
            .Union(empties.Where(f => f.Length > 0), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The folders directly inside <paramref name="prefix"/>, each with how many items sit at or below it.
    /// A folder that's only kept (no items yet) counts 0. Port of <c>LibraryBrowseLogic.childFolders</c>.
    /// </summary>
    public static IReadOnlyList<(string Name, int Count)> ChildFolders(IEnumerable<string> folders, IEnumerable<string> held, IReadOnlyList<string> prefix)
    {
        var counts = FolderPaths.ChildCounts(folders, prefix).ToDictionary(pair => pair.Key, _ => 0, StringComparer.Ordinal);
        foreach (var (name, count) in FolderPaths.ChildCounts(held, prefix))
            counts[name] = counts.GetValueOrDefault(name) + count;
        return counts.OrderBy(pair => pair.Key, LibrarySorts.NameOrder).Select(pair => (pair.Key, pair.Value)).ToList();
    }

    /// <summary>The items filed exactly at <paramref name="path"/> (not in its subfolders).</summary>
    public static IEnumerable<LibraryEntry> ItemsAt(IEnumerable<LibraryEntry> items, IReadOnlyList<string> path)
    {
        var joined = string.Join("/", path);
        return items.Where(i => i.Folder == joined);
    }

    /// <summary>The folders as a tree, for menus. Port of <c>FolderTreeLogic.tree</c>.</summary>
    public static IReadOnlyList<FolderNode> Tree(IEnumerable<string> paths)
    {
        var root = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var node = root;
            foreach (var part in FolderPaths.Components(path))
            {
                if (!node.TryGetValue(part, out var child)) node[part] = child = new Dictionary<string, object>(StringComparer.Ordinal);
                node = (Dictionary<string, object>)child;
            }
        }
        return Emit(root, "");

        static IReadOnlyList<FolderNode> Emit(Dictionary<string, object> node, string prefix) =>
            node.Keys.OrderBy(k => k, LibrarySorts.NameOrder).Select(name =>
            {
                var path = prefix.Length == 0 ? name : $"{prefix}/{name}";
                return new FolderNode(name, path, Emit((Dictionary<string, object>)node[name], path));
            }).ToList();
    }

    /// <summary>
    /// A typed folder name, cleaned: trimmed, and null when it's empty or holds a "/" (which would
    /// make it two folders). The Mac refuses the same names in <c>createFolder</c> and <c>renameFolder</c>.
    /// </summary>
    public static string? CleanName(string? typed)
    {
        var name = typed?.Trim() ?? "";
        return name.Length == 0 || name.Contains('/') || name is "." or ".." ? null : name;
    }

    /// <summary>The path of a new folder <paramref name="name"/> inside <paramref name="parent"/>.</summary>
    public static string Join(IReadOnlyList<string> parent, string name) => string.Join("/", parent.Append(name));

    /// <summary>
    /// What renaming the folder at <paramref name="path"/> to <paramref name="name"/> does: each item
    /// at or under it moves to the renamed path, and kept-empty folders follow. Empty when the name
    /// is unusable or unchanged.
    /// </summary>
    public static FolderMove Rename(IEnumerable<LibraryEntry> items, IEnumerable<string> empties, IReadOnlyList<string> path, string typed)
    {
        if (CleanName(typed) is not { } name || path.Count == 0 || path[^1] == name) return FolderMove.None;
        return Refile(items, empties, path, folder => FolderPaths.Renamed(folder, path, name));
    }

    /// <summary>
    /// What moving the folder at <paramref name="from"/> into <paramref name="destination"/> does
    /// ("Lyrics" into "Worship" makes "Worship/Lyrics"). Empty when that's where it already is, or
    /// would put a folder inside itself. Port of the Mac's <c>moveFolder</c>.
    /// </summary>
    public static FolderMove Move(IEnumerable<LibraryEntry> items, IEnumerable<string> empties, IReadOnlyList<string> from, IReadOnlyList<string> destination)
    {
        var fromPath = string.Join("/", from);
        var destinationPath = string.Join("/", destination);
        var parent = string.Join("/", from.SkipLast(1));
        if (from.Count == 0 || destinationPath == fromPath || destinationPath == parent || destinationPath.StartsWith(fromPath + "/", StringComparison.Ordinal))
            return FolderMove.None;
        return Refile(items, empties, from, folder => FolderPaths.Rebased(folder, from, destination));
    }

    /// <summary>
    /// What deleting the folder at <paramref name="path"/> does (Windows only: on the Mac a folder
    /// goes when it's emptied). Nothing is thrown away: what was in it moves up into its parent,
    /// keeping its own subfolders ("Hymns/Easter/Old" becomes "Hymns/Old" when "Easter" goes).
    /// </summary>
    public static FolderMove Delete(IEnumerable<LibraryEntry> items, IEnumerable<string> empties, IReadOnlyList<string> path)
    {
        if (path.Count == 0) return FolderMove.None;
        var parent = path.SkipLast(1).ToList();
        var prefix = string.Join("/", path);
        return Refile(items, empties, path, folder =>
        {
            var rest = folder.Length > prefix.Length ? folder[(prefix.Length + 1)..] : "";
            return string.Join("/", parent.Append(rest).Where(p => p.Length > 0));
        }, dropping: prefix);
    }

    private static FolderMove Refile(IEnumerable<LibraryEntry> items, IEnumerable<string> empties, IReadOnlyList<string> path,
        Func<string, string> refiled, string? dropping = null)
    {
        var prefix = string.Join("/", path);
        var moves = items.Where(i => FolderPaths.IsUnder(i.Folder, prefix) && i.Folder.Length > 0)
            .Select(i => new FolderMove.Refile(i, refiled(i.Folder)))
            .ToList();
        var kept = empties.Select(e => FolderPaths.IsUnder(e, prefix) && e.Length > 0 ? (e == dropping ? "" : refiled(e)) : e)
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new FolderMove(moves, kept, Changed: true);
    }
}

/// <summary>
/// The result of renaming, moving or deleting a folder: each item's new folder, and the new list of
/// kept-empty folders. The app saves each item with its new folder; nothing else changes.
/// </summary>
public sealed record FolderMove(IReadOnlyList<FolderMove.Refile> Refiles, IReadOnlyList<string> Empties, bool Changed)
{
    public sealed record Refile(LibraryEntry Entry, string Folder);

    public static FolderMove None { get; } = new([], [], false);
}

/// <summary>
/// Clicking rows in a list that holds several selected: a plain click picks one, Ctrl+click adds or
/// removes one, Shift+click takes everything from the anchor to the click. Port of the Mac's
/// <c>ListMultiSelect</c> (⌘ is Ctrl here, ⇧ is Shift).
/// </summary>
public static class ListMultiSelect
{
    public enum Gesture
    {
        Replace,
        Toggle,
        Extend,
    }

    public sealed record State(IReadOnlySet<string> Selected, string? Anchor)
    {
        public static State Empty { get; } = new(new HashSet<string>(StringComparer.Ordinal), null);
    }

    public static State Click(string id, Gesture gesture, IReadOnlyList<string> order, State state)
    {
        switch (gesture)
        {
            case Gesture.Toggle:
            {
                var selected = new HashSet<string>(state.Selected, StringComparer.Ordinal);
                if (!selected.Add(id)) selected.Remove(id);
                return new State(selected, id);
            }
            case Gesture.Extend:
            {
                var clicked = IndexOf(order, id);
                if (clicked < 0) return state;
                var anchor = state.Anchor is { } a && IndexOf(order, a) is var at && at >= 0 ? at : clicked;
                var selected = new HashSet<string>(state.Selected, StringComparer.Ordinal);
                for (var i = Math.Min(anchor, clicked); i <= Math.Max(anchor, clicked); i++) selected.Add(order[i]);
                // Shift never moves the anchor, so a second Shift+click spans from the same row.
                return new State(selected, state.Anchor ?? id);
            }
            default:
                return new State(new HashSet<string>([id], StringComparer.Ordinal), id);
        }
    }

    /// <summary>
    /// What a right-click acts on (the Finder's rule): the whole selection, in list order, when the
    /// clicked row is part of a selection of several; otherwise just the clicked row.
    /// </summary>
    public static IReadOnlyList<string> Batch(string clicked, IReadOnlySet<string> selection, IReadOnlyList<string> order) =>
        selection.Count > 1 && selection.Contains(clicked) ? order.Where(selection.Contains).ToList() : [clicked];

    private static int IndexOf(IReadOnlyList<string> order, string id)
    {
        for (var i = 0; i < order.Count; i++)
            if (order[i] == id) return i;
        return -1;
    }
}
