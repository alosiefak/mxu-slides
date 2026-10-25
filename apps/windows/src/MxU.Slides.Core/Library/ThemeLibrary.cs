using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// The rules for organising themes and their designs: making, copying, renaming and deleting
/// themes; adding, copying, renaming, filing, reordering and deleting the designs inside one
/// (the Mac calls a design a "category"); and which presentations use a theme or a design. These
/// functions change documents in memory and say what changed; the caller saves them.
/// <para>
/// Follows the Mac's AppModel (<c>createEntity(in: .themes)</c>, <c>duplicate</c>), its slide editor's
/// theme host (<c>addSlide</c>, <c>duplicateSlides</c>, <c>renameSlide</c>, <c>deleteSlides</c>),
/// PresenterCore/Library/ThemeSlides.swift (<c>defaultSlides</c>) and SlideBulkEdit
/// (<c>freshIDCopy</c>, <c>uniqueThemeSlideName</c>). Windows differences: a copied theme's
/// objects get fresh ids (so a Windows extra on one, like a kinetic build, belongs to one theme
/// only), design names are kept unique (slides find their design by name), and renaming a design
/// can rename it in the slides that use it.
/// </para>
/// </summary>
public static class ThemeLibrary
{
    public const string UntitledName = "Untitled Theme";

    /// <summary>The designs a new theme starts with (the Mac's Theme.defaultSlides).</summary>
    public static readonly IReadOnlyList<string> DefaultDesigns = ["Lyrics", "Bible", "Announcements", "Sermon Points"];

    /// <summary>The name of the one text box in a new design.</summary>
    public const string PlaceholderName = "Text Placeholder";

    /// <summary>A new theme, as the Mac makes one: white Helvetica Neue at 96 on black, and four designs with a text placeholder each.</summary>
    public static Theme Create(string name = UntitledName) => new()
    {
        Id = LibraryStore.NewId(),
        Name = name,
        FontFamily = "Helvetica Neue",
        FontSize = 96,
        TextColorHex = "#FFFFFF",
        BackgroundColorHex = "#000000",
        Slides = [.. DefaultDesigns.Select(category => Design(category, $"Sample {category}"))],
    };

    private static Slide Design(string name, string sample) => new()
    {
        Id = LibraryStore.NewId(),
        Name = name,
        Objects = [new SlideObject { Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Text, Name = PlaceholderName, Text = sample }],
    };

    /// <summary>One of the themes the app ships (id "pack.&lt;pack&gt;.theme"): refreshed by updates and put back if deleted.</summary>
    public static bool IsStarter(Theme theme) =>
        theme.Id.StartsWith("pack.", StringComparison.Ordinal) && theme.Id.EndsWith(".theme", StringComparison.Ordinal);

    /// <summary>What a copy changed: its new design ids and object ids, by the originals' (to copy Windows extras across).</summary>
    public sealed record CopyIds(IReadOnlyDictionary<string, string> Designs, IReadOnlyDictionary<string, string> Objects);

    /// <summary>A copy beside the original: a new id and " Copy" on the name; fresh ids inside, everything else the same.</summary>
    public static (Theme Copy, CopyIds Ids) Duplicate(Theme theme)
    {
        var copy = MxuJson.Clone(theme);
        copy.Id = LibraryStore.NewId();
        copy.Name = theme.Name + " Copy";
        var designs = new Dictionary<string, string>(StringComparer.Ordinal);
        var objects = new Dictionary<string, string>(StringComparer.Ordinal);
        copy.Slides = [.. (copy.Slides ?? []).Select(design =>
        {
            var fresh = FreshCopy(design, objects);
            designs[design.Id] = fresh.Id;
            return fresh;
        })];
        return (copy, new CopyIds(designs, objects));
    }

    /// <summary>
    /// A slide with fresh ids: its own, its objects' (masks follow the object they name) and its
    /// actions'. The animation steps keep theirs, so the play order still names them. Port of the
    /// Mac's Slide.freshIDCopy. <paramref name="objectIds"/> collects old → new object ids.
    /// </summary>
    public static Slide FreshCopy(Slide slide, Dictionary<string, string>? objectIds = null)
    {
        var copy = MxuJson.Clone(slide);
        copy.Id = LibraryStore.NewId();
        var fresh = copy.Objects.ToDictionary(o => o.Id, _ => LibraryStore.NewId(), StringComparer.Ordinal);
        foreach (var o in copy.Objects)
        {
            objectIds?.TryAdd(o.Id, fresh[o.Id]);
            o.Id = fresh[o.Id];
            if (o.MaskObjectId is { } mask && fresh.TryGetValue(mask, out var moved)) o.MaskObjectId = moved;
        }
        foreach (var action in copy.Actions ?? []) action.Id = LibraryStore.NewId();
        return copy;
    }

    /// <summary>Renames a theme; false (and no change) for a blank or unchanged name.</summary>
    public static bool Rename(Theme theme, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed == theme.Name) return false;
        theme.Name = trimmed;
        return true;
    }

    // MARK: Designs

    /// <summary><paramref name="name"/>, or "name 2", "name 3"… so no two designs share a name (any case). SlideBulkEdit.uniqueThemeSlideName.</summary>
    public static string UniqueDesignName(string name, IEnumerable<string> existing)
    {
        var taken = existing.Select(n => n.ToLowerInvariant()).ToHashSet();
        var candidate = name;
        for (var counter = 2; taken.Contains(candidate.ToLowerInvariant()); counter++) candidate = $"{name} {counter}";
        return candidate;
    }

    private static IEnumerable<string> NamesBut(Theme theme, string? exceptId) => (theme.Slides ?? []).Where(d => d.Id != exceptId).Select(d => d.Name);

    /// <summary>A new design after <paramref name="afterId"/> (or at the end): "Category N" with one text placeholder, as the Mac adds one.</summary>
    public static Slide AddDesign(Theme theme, string? afterId = null)
    {
        var designs = theme.Slides ??= [];
        var design = Design(UniqueDesignName($"Category {designs.Count + 1}", NamesBut(theme, null)), "Sample Text");
        var at = designs.FindIndex(d => d.Id == afterId);
        designs.Insert(at < 0 ? designs.Count : at + 1, design);
        return design;
    }

    /// <summary>A copy of a design just after it, named "… Copy" (made unique). Null when there's no such design.</summary>
    public static (Slide Design, Dictionary<string, string> ObjectIds)? DuplicateDesign(Theme theme, string designId)
    {
        var designs = theme.Slides ?? [];
        var index = designs.FindIndex(d => d.Id == designId);
        if (index < 0) return null;
        var objectIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var copy = FreshCopy(designs[index], objectIds);
        copy.Name = UniqueDesignName(string.IsNullOrEmpty(copy.Name) ? "Category" : copy.Name + " Copy", NamesBut(theme, null));
        designs.Insert(index + 1, copy);
        return (copy, objectIds);
    }

    /// <summary>Renames a design (made unique among the theme's); false for a blank or unchanged name.</summary>
    public static bool RenameDesign(Theme theme, string designId, string name)
    {
        if ((theme.Slides ?? []).FirstOrDefault(d => d.Id == designId) is not { } design) return false;
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed == design.Name) return false;
        design.Name = UniqueDesignName(trimmed, NamesBut(theme, designId));
        return true;
    }

    /// <summary>Files a design in a group of the theme's designs ("Lower Thirds"), or none for blank.</summary>
    public static bool SetDesignFolder(Theme theme, string designId, string? folder)
    {
        if ((theme.Slides ?? []).FirstOrDefault(d => d.Id == designId) is not { } design) return false;
        var trimmed = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        if (trimmed == design.Folder) return false;
        design.Folder = trimmed;
        return true;
    }

    /// <summary>Deletes a design; a theme keeps at least one (the Mac won't delete the last). False when nothing changed.</summary>
    public static bool DeleteDesign(Theme theme, string designId)
    {
        var designs = theme.Slides ?? [];
        if (designs.Count <= 1) return false;
        return designs.RemoveAll(d => d.Id == designId) > 0;
    }

    /// <summary>Moves a design one place earlier (−1) or later (+1). The first design is the one a slide without a design name uses.</summary>
    public static bool MoveDesign(Theme theme, string designId, int by)
    {
        var designs = theme.Slides ?? [];
        var index = designs.FindIndex(d => d.Id == designId);
        var target = index + by;
        if (index < 0 || target < 0 || target >= designs.Count) return false;
        var design = designs[index];
        designs.RemoveAt(index);
        designs.Insert(target, design);
        return true;
    }

    // MARK: Who uses a theme

    /// <summary>A presentation that uses a theme: how many of its slides do (every slide for its deck theme, or those that chose it).</summary>
    public sealed record ThemeUse(Presentation Presentation, int Slides, bool IsDeckTheme);

    /// <summary>The presentations drawn with <paramref name="themeId"/>, as the deck's theme or by some of their slides.</summary>
    public static List<ThemeUse> Uses(IEnumerable<Presentation> presentations, string themeId)
    {
        var uses = new List<ThemeUse>();
        foreach (var p in presentations)
        {
            var count = p.Slides.Count(s => p.ThemeIdFor(s) == themeId);
            if (count > 0 || p.ThemeId == themeId) uses.Add(new ThemeUse(p, count, p.ThemeId == themeId));
        }
        return uses;
    }

    /// <summary>The slides (with their presentations) drawn with a theme's design: they follow the theme and name the design (any case).</summary>
    public static List<(Presentation Presentation, Slide Slide)> DesignUses(IEnumerable<Presentation> presentations, string themeId, string designName) =>
        [.. presentations.SelectMany(p => p.Slides
            .Where(s => p.ThemeIdFor(s) == themeId && string.Equals(s.ThemeSlideName, designName, StringComparison.OrdinalIgnoreCase))
            .Select(s => (p, s)))];

    /// <summary>
    /// After a design is renamed, the slides that named it name it again (Windows: on the Mac they'd
    /// fall back to the theme's first design). Returns the presentations changed, to save.
    /// </summary>
    public static List<Presentation> RenameDesignUses(IEnumerable<Presentation> presentations, string themeId, string oldName, string newName)
    {
        var changed = new List<Presentation>();
        foreach (var group in DesignUses(presentations, themeId, oldName).GroupBy(u => u.Presentation))
        {
            foreach (var (_, slide) in group) slide.ThemeSlideName = newName;
            changed.Add(group.Key);
        }
        return changed;
    }
}
