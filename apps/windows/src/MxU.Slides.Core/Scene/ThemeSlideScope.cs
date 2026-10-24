using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Scene;

/// <summary>
/// Narrowing a theme to some of its designs, for a look: "this theme, but only its
/// Lower Thirds folder" or "just this one design". A look scoped to a folder makes
/// every slide pick its design from that folder (a lyric slide named "Lyrics" finds the
/// lower-third "Lyrics" design instead of the full-screen one). Port of the Mac's
/// PresenterCore/ThemeSlideScope.swift.
/// <para>
/// The scoped theme is a new <see cref="Theme"/> that shares the original's design
/// slides (they are never changed through it), so the library's copy is left alone.
/// </para>
/// </summary>
public static class ThemeSlideScope
{
    /// <summary>The theme with only the designs in <paramref name="folder"/> (any case); the whole theme when the folder is empty or has none.</summary>
    public static Theme ScopedToSlideFolder(this Theme theme, string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return theme;
        var matching = (theme.Slides ?? []).Where(s => string.Equals(s.Folder ?? "", folder, StringComparison.OrdinalIgnoreCase)).ToList();
        return matching.Count == 0 ? theme : WithSlides(theme, matching);
    }

    /// <summary>The theme with just one design; the whole theme when the id is empty or not in it.</summary>
    public static Theme ScopedToSlideId(this Theme theme, string? slideId)
    {
        if (string.IsNullOrEmpty(slideId) || (theme.Slides ?? []).FirstOrDefault(s => s.Id == slideId) is not { } slide) return theme;
        return WithSlides(theme, [slide]);
    }

    public static bool HasSlide(this Theme theme, string slideId) => (theme.Slides ?? []).Any(s => s.Id == slideId);

    public static bool HasSlideFolder(this Theme theme, string folder) =>
        (theme.Slides ?? []).Any(s => string.Equals(s.Folder ?? "", folder, StringComparison.OrdinalIgnoreCase));

    /// <summary>The theme's design folders in the order they first appear, each named once (ignoring case).</summary>
    public static List<string> SlideFolders(this Theme theme)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var slide in theme.Slides ?? [])
            if (slide.Folder is { Length: > 0 } folder && seen.Add(folder)) result.Add(folder);
        return result;
    }

    // A copy of every field of the theme with a different list of designs. If the schema
    // gains a Theme field, add it here (ThemeSlideScopeTests checks every property is copied).
    private static Theme WithSlides(Theme theme, List<Slide> slides) => new()
    {
        Id = theme.Id,
        Name = theme.Name,
        FontFamily = theme.FontFamily,
        FontSize = theme.FontSize,
        TextColorHex = theme.TextColorHex,
        BackgroundColorHex = theme.BackgroundColorHex,
        Slides = slides,
    };
}
