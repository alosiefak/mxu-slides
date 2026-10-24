namespace MxU.Slides.Core.Library;

/// <summary>
/// Library folders are paths like "Clean Geometric/Lower Thirds", stored on
/// each document (an overlay's <c>Folder</c>). A folder exists while something
/// is filed in it, so renaming or moving a folder means re-filing everything
/// under it. The path maths of the Mac's TeamDriveLogic (PresenterCore/Library/TeamDrives.swift).
/// </summary>
public static class FolderPaths
{
    /// <summary>True when <paramref name="folder"/> is <paramref name="prefix"/> or inside it ("Worship/Lyrics" is under "Worship"; "Worship2" isn't). Everything is under "".</summary>
    public static bool IsUnder(string folder, string prefix) =>
        prefix.Length == 0 || folder == prefix || folder.StartsWith(prefix + "/", StringComparison.Ordinal);

    /// <summary>How many folders sit under each child of <paramref name="prefix"/> (its subfolders, counted with theirs).</summary>
    public static Dictionary<string, int> ChildCounts(IEnumerable<string> folders, IReadOnlyList<string> prefix)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            var components = Components(folder);
            if (components.Length <= prefix.Count || !components.Take(prefix.Count).SequenceEqual(prefix)) continue;
            counts[components[prefix.Count]] = counts.GetValueOrDefault(components[prefix.Count]) + 1;
        }
        return counts;
    }

    /// <summary>
    /// Where <paramref name="folder"/> (at or under <paramref name="from"/>) ends up when
    /// <paramref name="from"/> is moved into <paramref name="destination"/>:
    /// "Lyrics/2026" moved from ["Lyrics"] into ["Worship"] becomes "Worship/Lyrics/2026".
    /// </summary>
    public static string Rebased(string folder, IReadOnlyList<string> from, IReadOnlyList<string> destination, IReadOnlyList<string>? movedFrom = null)
    {
        var fromPath = string.Join("/", movedFrom ?? from);
        var newBase = string.Join("/", destination.Concat(from.TakeLast(1)));
        return newBase + folder[Math.Min(fromPath.Length, folder.Length)..];
    }

    /// <summary>Where <paramref name="folder"/> ends up when the folder <paramref name="from"/> is renamed to <paramref name="name"/>.</summary>
    public static string Renamed(string folder, IReadOnlyList<string> from, string name) =>
        Rebased(folder, [.. from.SkipLast(1), name], [.. from.SkipLast(1)], movedFrom: from);

    /// <summary>A path's parts: "A/B" is ["A", "B"]; "" has none.</summary>
    public static string[] Components(string folder) => folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
}
