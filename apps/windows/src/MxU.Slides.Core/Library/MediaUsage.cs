using System.Globalization;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// Where a media item is used, for the "Replace everywhere?" list and the warning before a
/// used item is deleted. Port of the Mac's MediaUsage (and MediaPickCardLogic). A presentation
/// whose theme uses the item counts too, listed "via" that theme, because replacing the
/// theme's background changes its slides.
/// </summary>
public static class MediaUsage
{
    /// <summary>The kinds of document that can use media, in the order the list shows them.</summary>
    public enum Kind
    {
        Theme,
        Presentation,
        Overlay,
        Service,
        Playlist,
        ConfidenceLayout,
        ActionCombo,
        ScheduleTrigger,
    }

    public sealed record Use(Kind Kind, string Id, string Name, string? Via = null);

    /// <summary>One document: which media it uses itself, and (presentations) which themes it's drawn with.</summary>
    public sealed record Document(Use Use, IReadOnlySet<string> MediaIds, IReadOnlySet<string>? ThemeIds = null);

    public sealed record Group(Kind Kind, string Title, IReadOnlyList<Use> Uses);

    /// <summary>The themes a presentation is drawn with: its own, any slide's own, and any per-slide design overrides.</summary>
    public static HashSet<string> ThemeIds(Presentation presentation)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { presentation.ThemeId };
        foreach (var slide in presentation.Slides)
        {
            if (slide.ThemeId is { } own) ids.Add(own);
            foreach (var design in slide.OverrideDesigns ?? []) ids.Add(design.ThemeId);
        }
        ids.Remove("");
        return ids;
    }

    public static IReadOnlyList<Use> Uses(string id, IEnumerable<Document> documents)
    {
        var all = documents.ToList();
        var direct = all.Where(d => d.MediaIds.Contains(id)).ToList();
        var themes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var theme in direct.Where(d => d.Use.Kind == Kind.Theme)) themes.TryAdd(theme.Use.Id, theme.Use.Name);
        var directIds = direct.Select(d => d.Use.Id).ToHashSet(StringComparer.Ordinal);
        var viaTheme = all.Select(d =>
        {
            var theme = (d.ThemeIds ?? new HashSet<string>()).Order(StringComparer.Ordinal).FirstOrDefault(themes.ContainsKey);
            return theme is not null && !directIds.Contains(d.Use.Id) ? d.Use with { Via = themes[theme] } : null;
        }).OfType<Use>();
        return [.. direct.Select(d => d.Use), .. viaTheme];
    }

    /// <summary>Finder's order: ignoring case, with numbers compared as numbers ("Song 2" before "Song 10").</summary>
    public static readonly StringComparer NameOrder =
        StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    public static IReadOnlyList<Group> Groups(IEnumerable<Use> uses)
    {
        var all = uses.ToList();
        return [.. Enum.GetValues<Kind>().Select(kind =>
        {
            var named = all.Where(u => u.Kind == kind).OrderBy(u => u.Name, NameOrder).ToList();
            return named.Count == 0 ? null : new Group(kind, Title(kind), named);
        }).OfType<Group>()];
    }

    public static string Title(Kind kind) => kind switch
    {
        Kind.Theme => "Themes",
        Kind.Presentation => "Presentations",
        Kind.Overlay => "Overlays",
        Kind.Service => "Services",
        Kind.Playlist => "Playlists",
        Kind.ConfidenceLayout => "Stage Layouts",
        Kind.ActionCombo => "Action Combos",
        Kind.ScheduleTrigger => "Schedules",
        _ => kind.ToString(),
    };

    /// <summary>True when a change to an item means its pictures must be decoded again: another file, or another first frame.</summary>
    public static bool FileChanged(MediaItem? old, MediaItem? @new) =>
        old is not null && @new is not null && (old.FileHash != @new.FileHash || old.InPoint != @new.InPoint);

    /// <summary>
    /// Every document in the library that uses media, or is drawn with a theme (which may).
    /// The Mac's mediaReferenceDocuments. Call where the library's documents can be read
    /// (they are read under the store's lock, one kind at a time).
    /// </summary>
    public static List<Document> Documents(LibraryStore library)
    {
        var documents = new List<Document>();
        void Walk<T>(Kind kind, Func<T, HashSet<string>> media, Func<T, HashSet<string>>? themes = null) where T : class, ILibraryDocument
        {
            foreach (var value in library.All<T>())
            {
                var mediaIds = media(value);
                var themeIds = themes?.Invoke(value);
                if (mediaIds.Count > 0 || themeIds is { Count: > 0 })
                    documents.Add(new Document(new Use(kind, value.Id, value.Name), mediaIds, themeIds));
            }
        }
        Walk<Presentation>(Kind.Presentation, MediaReferences.Ids, ThemeIds);
        Walk<Overlay>(Kind.Overlay, MediaReferences.Ids);
        Walk<Theme>(Kind.Theme, MediaReferences.Ids);
        Walk<ConfidenceLayout>(Kind.ConfidenceLayout, MediaReferences.Ids);
        Walk<Playlist>(Kind.Playlist, MediaReferences.Ids);
        Walk<Service>(Kind.Service, MediaReferences.Ids);
        Walk<ActionCombo>(Kind.ActionCombo, MediaReferences.Ids);
        Walk<ScheduleTrigger>(Kind.ScheduleTrigger, MediaReferences.Ids);
        return documents;
    }

    /// <summary>Where an item is used, straight from the library.</summary>
    public static IReadOnlyList<Use> Uses(LibraryStore library, string id) => Uses(id, Documents(library));

    /// <summary>How many documents use any of <paramref name="ids"/> (the Mac's mediaReferenceDocumentCount).</summary>
    public static int DocumentCount(IEnumerable<Document> documents, IReadOnlySet<string> ids) =>
        ids.Count == 0 ? 0 : documents.Count(d => d.MediaIds.Overlaps(ids));

    /// <summary>The line under a media pick: "Video · 1:32 · 1280 × 720", or that its file is missing (MediaPickCardLogic).</summary>
    public static string Detail(MediaKind kind, int? width, int? height, double? duration, bool missing)
    {
        if (missing) return "Missing — the file isn't on this PC";
        var parts = new List<string> { kind == MediaKind.Video ? "Video" : "Image" };
        if (kind == MediaKind.Video && duration is { } seconds) parts.Add(Clock(seconds));
        if (width is { } w && height is { } h) parts.Add($"{w} × {h}");
        return string.Join(" · ", parts);
    }

    internal static string Clock(double seconds)
    {
        var total = Math.Max(0, (int)Math.Round(seconds, MidpointRounding.AwayFromZero));
        var (h, m, s) = (total / 3600, total / 60 % 60, total % 60);
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }
}
