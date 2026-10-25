using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Library;

/// <summary>
/// What the library's batch actions do to the documents, for one item or many: duplicate, file
/// into a folder, apply a theme, and what deleting would leave behind. The rules are the Mac's
/// (AppModel's <c>duplicate</c>, <c>moveToFolder</c> and <c>applyTheme(to:themeID:)</c>); the app
/// saves what these return, so the windows only ask and show.
/// </summary>
public static class LibraryBatch
{
    /// <summary>A copy's name: "Amazing Grace" becomes "Amazing Grace Copy", as the Mac names every duplicate.</summary>
    public static string CopyName(string name) => name + " Copy";

    /// <summary>
    /// A copy of a presentation with its own id and the Mac's "Copy" name. Its origin says what it
    /// was copied from ("of Amazing Grace", as the Mac words it). Slides keep their ids, as the
    /// app's single Duplicate always did, so Windows extras tied to them keep applying.
    /// </summary>
    public static Presentation Duplicate(Presentation original, DateTimeOffset now)
    {
        var copy = MxuJson.Clone(original);
        copy.Id = LibraryStore.NewId();
        copy.Name = CopyName(original.Name);
        copy.Origin = new PresentationOrigin
        {
            Source = PresentationOriginSource.Duplicate, Detail = $"of {original.Name}",
            CreatedAt = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
        };
        return copy;
    }

    /// <summary>A copy of a media item: same file, new id, "Copy" name, and imported now (so it sorts as new).</summary>
    public static MediaItem Duplicate(MediaItem original, DateTimeOffset now)
    {
        var copy = MxuJson.Clone(original);
        copy.Id = LibraryStore.NewId();
        copy.Name = CopyName(original.Name);
        copy.ImportedAt = now.ToUnixTimeMilliseconds() / 1000.0;
        return copy;
    }

    public static AudioItem Duplicate(AudioItem original)
    {
        var copy = MxuJson.Clone(original);
        copy.Id = LibraryStore.NewId();
        copy.Name = CopyName(original.Name);
        return copy;
    }

    /// <summary>
    /// Files a document into <paramref name="folder"/> ("" or null: out of every folder). Returns
    /// false when it's already there, so nothing needs saving. The folder id (the Mac's team-drive
    /// folder) is cleared: Windows has no team drives, and a stale id would point a Mac elsewhere.
    /// </summary>
    public static bool File(Presentation document, string? folder) => Refile(document.Folder, folder, f => (document.Folder, document.FolderId) = (f, null));

    public static bool File(MediaItem document, string? folder) => Refile(document.Folder, folder, f => (document.Folder, document.FolderId) = (f, null));

    public static bool File(AudioItem document, string? folder) => Refile(document.Folder, folder, f => (document.Folder, document.FolderId) = (f, null));

    private static bool Refile(string? current, string? folder, Action<string?> set)
    {
        var target = string.IsNullOrEmpty(folder) ? null : folder;
        if ((current ?? "") == (target ?? "")) return false;
        set(target);
        return true;
    }

    /// <summary>
    /// Gives a presentation another theme, the Mac's way (<c>applyTheme</c> without a design): the
    /// same theme again resets the slides' overrides to it; a different one becomes the deck's theme
    /// and every slide's own theme is cleared, so all of it follows. Returns false when nothing changed.
    /// The Mac also lets the slides pick up the new theme's design actions (DesignActions.swift);
    /// that isn't ported, so a theme's actions don't move onto the slides here.
    /// </summary>
    public static bool ApplyTheme(Presentation presentation, string themeId)
    {
        if (presentation.ThemeId == themeId && !presentation.HasSlideThemes())
        {
            if (themeId.Length == 0) return false;
            var before = MxuJson.SerializeToUtf8(presentation.Slides, indented: false);
            foreach (var slide in presentation.Slides) SlideSceneBuilder.ResetThemeOverrides(slide);
            return !before.AsSpan().SequenceEqual(MxuJson.SerializeToUtf8(presentation.Slides, indented: false));
        }
        presentation.ClearSlideThemes();
        presentation.ThemeId = themeId;
        return true;
    }

    /// <summary>One presentation that's in a service, and the services it's in, for the warning before deleting it.</summary>
    public sealed record InService(string PresentationId, string PresentationName, IReadOnlyList<string> Services);

    /// <summary>
    /// The presentations among <paramref name="ids"/> that some service still lists, with those
    /// services' names. Deleting moves the document to the Trash; its service rows then say it's
    /// missing, so the operator is told first (as media deletion already does).
    /// </summary>
    public static IReadOnlyList<InService> ServicesUsing(LibraryStore store, IEnumerable<string> ids)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        var services = store.All<Service>();
        var found = new List<InService>();
        foreach (var presentation in store.All<Presentation>().Where(p => wanted.Contains(p.Id)))
        {
            var names = services
                .Where(s => ServiceVersions.AllItems(s).Any(i => i.RefId == presentation.Id && i.ItemKind == ServiceItemKind.Presentation))
                .Select(s => s.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (names.Count > 0) found.Add(new InService(presentation.Id, presentation.Name, names));
        }
        return found;
    }

    /// <summary>
    /// File names for exporting several items into one folder: each item's name made safe for
    /// Windows, and numbered when two would clash ("Amazing Grace.pdf", "Amazing Grace 2.pdf").
    /// Names already in the folder count as taken, so nothing there is overwritten.
    /// </summary>
    public static IReadOnlyList<string> UniqueFileNames(IEnumerable<string> names, string extension, IEnumerable<string>? taken = null)
    {
        var used = new HashSet<string>(taken ?? [], StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in names)
        {
            var stem = SafeFileStem(raw);
            var candidate = stem + extension;
            for (var n = 2; used.Contains(candidate); n++) candidate = $"{stem} {n}{extension}";
            used.Add(candidate);
            result.Add(candidate);
        }
        return result;
    }

    /// <summary>
    /// A name as a file name: the Mac's "/ : \" become "-", as do the other characters Windows
    /// forbids (&lt;&gt;"|?*), control characters go, and trailing dots and spaces are trimmed (Windows
    /// drops them silently). An empty result becomes "Untitled".
    /// </summary>
    public static string SafeFileStem(string name) => SanitizedName(name) is { Length: > 0 } stem ? stem : "Untitled";

    /// <summary>The same, but "" when nothing is left (the export panel then picks its own word, "Presentation").</summary>
    public static string SanitizedName(string? name)
    {
        var chars = (name ?? "").Select(c => c < ' ' ? ' ' : "/:\\<>\"|?*".Contains(c) ? '-' : c).ToArray();
        return new string(chars).Trim().TrimEnd('.', ' ');
    }
}
