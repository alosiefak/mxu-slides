using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// The rules for organising overlays: making, copying, renaming and re-filing
/// them, and their <b>packs</b>. A pack is the first part of an overlay's folder
/// ("Clean Geometric" in "Clean Geometric/Lower Thirds"); the rest is its
/// category. These functions only change documents in memory and say which ones
/// changed; the caller saves them. Follows the Mac's AppModel (createEntity,
/// duplicate, rename, renameFolder, moveToFolder).
/// </summary>
public static class OverlayLibrary
{
    /// <summary>The pack shown for overlays that aren't in any folder.</summary>
    public const string NoPack = "Other";

    public const string UntitledName = "Untitled Overlay";

    /// <summary>The pack an overlay belongs to: its folder's first part, or <see cref="NoPack"/>.</summary>
    public static string PackOf(Overlay overlay) =>
        overlay.Folder is { } folder && FolderPaths.Components(folder) is { Length: > 0 } parts ? parts[0] : NoPack;

    /// <summary>The part of the folder after the pack ("Lower Thirds"), or "".</summary>
    public static string CategoryOf(Overlay overlay) =>
        overlay.Folder is { } folder && FolderPaths.Components(folder) is { Length: > 1 } parts ? string.Join("/", parts.Skip(1)) : "";

    /// <summary>Every pack, alphabetically, with <see cref="NoPack"/> last.</summary>
    public static List<string> Packs(IEnumerable<Overlay> overlays) =>
        overlays.Select(PackOf).Distinct(StringComparer.Ordinal)
            .OrderBy(p => p == NoPack ? 1 : 0).ThenBy(p => p, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>The overlays in one pack.</summary>
    public static List<Overlay> InPack(IEnumerable<Overlay> overlays, string pack) =>
        overlays.Where(o => PackOf(o) == pack).ToList();

    /// <summary>The folder for a pack and category: "Pack/Category", just "Pack", or null for <see cref="NoPack"/> (unfiled).</summary>
    public static string? FolderFor(string pack, string category = "")
    {
        if (pack == NoPack || string.IsNullOrWhiteSpace(pack)) return null;
        return string.IsNullOrWhiteSpace(category) ? pack.Trim() : $"{pack.Trim()}/{category.Trim()}";
    }

    /// <summary>
    /// One of the overlays the app ships (its id is "pack.&lt;pack&gt;.overlay.&lt;item&gt;"). The app
    /// puts these back when they are missing and refreshes them when its starter packs
    /// change, so lasting changes belong on a copy.
    /// </summary>
    public static bool IsStarter(Overlay overlay) =>
        overlay.Id.StartsWith("pack.", StringComparison.Ordinal) && overlay.Id.Contains(".overlay.", StringComparison.Ordinal);

    /// <summary>A blank overlay, filed in <paramref name="pack"/> (as the Mac makes it: "Untitled Overlay", no objects).</summary>
    public static Overlay Create(string pack, string name = UntitledName, string category = "") => new()
    {
        Id = LibraryStore.NewId(),
        Name = name,
        Objects = [],
        Folder = FolderFor(pack, category),
    };

    /// <summary>A copy beside the original: a new id and " Copy" on the name, everything else the same.</summary>
    public static Overlay Duplicate(Overlay overlay)
    {
        var copy = MxuJson.Clone(overlay);
        copy.Id = LibraryStore.NewId();
        copy.Name += " Copy";
        return copy;
    }

    /// <summary>Renames an overlay; false (and no change) for a blank or unchanged name.</summary>
    public static bool Rename(Overlay overlay, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed == overlay.Name) return false;
        overlay.Name = trimmed;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="name"/> can be a pack's name: not blank, no "/", not
    /// the name for unfiled overlays. Returns the trimmed name, or null.
    /// </summary>
    public static string? ValidPackName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length == 0 || trimmed.Contains('/') || trimmed == NoPack ? null : trimmed;
    }

    /// <summary>
    /// Moves an overlay into another pack, keeping its category ("Clean Geometric/Lower Thirds"
    /// moved to "Mine" becomes "Mine/Lower Thirds"). False when nothing changes.
    /// </summary>
    public static bool MoveToPack(Overlay overlay, string pack)
    {
        var folder = FolderFor(pack, CategoryOf(overlay));
        if (folder == overlay.Folder) return false;
        overlay.Folder = folder;
        overlay.FolderId = null;
        return true;
    }

    /// <summary>
    /// Renames a pack by re-filing every overlay in it (the Mac's renameFolder). Returns
    /// the overlays that changed; none when the new name isn't valid or is the same.
    /// Renaming onto an existing pack merges the two, as folders do on the Mac.
    /// </summary>
    public static List<Overlay> RenamePack(IEnumerable<Overlay> overlays, string pack, string newName)
    {
        if (ValidPackName(newName) is not { } name || name == pack || pack == NoPack) return [];
        var changed = new List<Overlay>();
        foreach (var overlay in overlays)
        {
            if (overlay.Folder is not { } folder || !FolderPaths.IsUnder(folder, pack)) continue;
            overlay.Folder = FolderPaths.Renamed(folder, [pack], name);
            overlay.FolderId = null;
            changed.Add(overlay);
        }
        return changed;
    }
}
