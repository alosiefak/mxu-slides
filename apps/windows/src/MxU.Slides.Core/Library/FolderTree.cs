using System.Globalization;

namespace MxU.Slides.Core.Library;

/// <summary>One folder in a menu of folders: its name, its whole path ("Sunday/Band") and the folders inside it.</summary>
public sealed record FolderNode(string Name, string Path, IReadOnlyList<FolderNode> Children)
{
    public bool Equals(FolderNode? other) =>
        other is not null && Name == other.Name && Path == other.Path && Children.SequenceEqual(other.Children);

    public override int GetHashCode() => HashCode.Combine(Name, Path, Children.Count);
}

/// <summary>
/// Folder paths ("Sunday", "Sunday/Band", "Youth") as a tree, for menus with a submenu per folder:
/// the Stage panel's layout picker lists layouts with no folder first, then a submenu for each
/// folder. Folders at each level are sorted by name, ignoring case; a path names every folder on
/// the way down, so "Sunday/Band" alone still makes a Sunday submenu. Port of
/// PresenterCore/Library/FolderTreeLogic.swift.
/// </summary>
public static class FolderTree
{
    public static List<FolderNode> Tree(IEnumerable<string?> paths)
    {
        var root = new Builder();
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            var node = root;
            foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!node.Children.TryGetValue(component, out var child)) node.Children[component] = child = new Builder();
                node = child;
            }
        }
        return Emit(root, "");
    }

    /// <summary>The entries filed directly in a folder (not in its subfolders), by their folder path.</summary>
    public static List<T> In<T>(IEnumerable<T> entries, Func<T, string?> folderOf, string? path) =>
        [.. entries.Where(e => (folderOf(e) ?? "") == (path ?? ""))];

    private sealed class Builder
    {
        public Dictionary<string, Builder> Children { get; } = new(StringComparer.Ordinal);
    }

    private static readonly StringComparer ByName = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);

    private static List<FolderNode> Emit(Builder builder, string prefix) =>
        [.. builder.Children.Keys.Order(ByName).Select(name =>
        {
            var path = prefix.Length == 0 ? name : $"{prefix}/{name}";
            return new FolderNode(name, path, Emit(builder.Children[name], path));
        })];
}
