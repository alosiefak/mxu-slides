using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Library;

/// <summary>
/// Giving a presentation a theme: the whole deck (its theme, and what happens to slides that chose a
/// theme of their own), or just some of its slides ("Apply Theme to selected slides"). Picking the
/// theme a slide already follows puts its text back the way the theme has it, as on the Mac.
/// <para>
/// Port of the Mac's AppModel <c>applyTheme(to:themeID:design:)</c> and
/// <c>applyTheme(to:slideIDs:themeID:design:)</c>, with SlideEditorModel.<c>setTheme</c>. The Mac's
/// "No Theme", which bakes each slide's look into it first, isn't ported: on Windows choosing no theme
/// drops the theme (see PORTING).
/// </para>
/// </summary>
public static class ThemeApplication
{
    /// <summary>What happens to slides with a theme of their own when the deck's theme changes.</summary>
    public enum OwnThemes
    {
        /// <summary>They follow the new deck theme too (the Mac's only choice).</summary>
        Replace,

        /// <summary>They keep the theme they chose (Windows: the scripture slides of a lyric deck stay scripture).</summary>
        Keep,
    }

    /// <summary>
    /// The deck takes <paramref name="themeId"/> (optionally on one <paramref name="design"/> for every slide).
    /// Returns true when the presentation changed.
    /// </summary>
    public static bool ApplyToDeck(Presentation presentation, string themeId, string? design, IReadOnlyDictionary<string, Theme> themes,
        OwnThemes own = OwnThemes.Replace)
    {
        var every = presentation.Slides.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (design is not null && themes.TryGetValue(themeId, out var designed))
        {
            if (presentation.SlidesAllFollow(every, themeId, design))
            {
                foreach (var slide in presentation.Slides) SlideSceneBuilder.ResetThemeOverrides(slide);
                return true;
            }
            var reached = own == OwnThemes.Keep ? FollowingDeck(presentation) : every;
            presentation.ApplyTheme(designed, reached, design, themes);
            if (own == OwnThemes.Replace) presentation.ClearSlideThemes();
            presentation.ThemeId = themeId;
            foreach (var slide in presentation.Slides.Where(s => s.ThemeId == themeId)) slide.ThemeId = null;
            return true;
        }
        if (presentation.ThemeId == themeId && !presentation.HasSlideThemes())
        {
            // The theme it already has: give the text back its theme look (the Mac's reapplyTheme).
            if (themeId.Length == 0) return false;
            foreach (var slide in presentation.Slides) SlideSceneBuilder.ResetThemeOverrides(slide);
            return true;
        }
        if (presentation.ThemeId == themeId && own == OwnThemes.Keep) return false;
        if (own == OwnThemes.Keep)
        {
            // Only the slides that follow the deck swap their design actions; the others keep theirs.
            var newTheme = themes.GetValueOrDefault(themeId);
            var oldTheme = themes.GetValueOrDefault(presentation.ThemeId);
            foreach (var slide in presentation.Slides.Where(s => s.Unthemed != true && string.IsNullOrEmpty(s.ThemeId)))
                slide.AdoptActions(newTheme?.Design(slide.ThemeSlideName), oldTheme?.Design(slide.ThemeSlideName));
            presentation.ThemeId = themeId;
            // A slide that had chosen the new deck theme for itself simply follows the deck now.
            foreach (var slide in presentation.Slides.Where(s => s.ThemeId == themeId)) slide.ThemeId = null;
            return true;
        }
        presentation.AdoptDesignActions(themes.GetValueOrDefault(themeId), themes);
        presentation.ClearSlideThemes();
        presentation.ThemeId = themeId;
        return true;
    }

    private static HashSet<string> FollowingDeck(Presentation presentation) =>
        presentation.Slides.Where(s => string.IsNullOrEmpty(s.ThemeId)).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Some slides take <paramref name="themeId"/> (keeping their designs unless <paramref name="design"/>
    /// names one). Slides that already follow it get their theme look back instead. False when there's
    /// no such theme or no such slides.
    /// </summary>
    public static bool ApplyToSlides(Presentation presentation, IReadOnlyCollection<string> slideIds, string themeId, string? design,
        IReadOnlyDictionary<string, Theme> themes)
    {
        if (!themes.TryGetValue(themeId, out var theme)) return false;
        var ids = slideIds.ToHashSet(StringComparer.Ordinal);
        if (!presentation.Slides.Any(s => ids.Contains(s.Id))) return false;
        if (presentation.SlidesAllFollow(ids, themeId, design))
        {
            foreach (var slide in presentation.Slides.Where(s => ids.Contains(s.Id))) SlideSceneBuilder.ResetThemeOverrides(slide);
            return true;
        }
        presentation.ApplyTheme(theme, ids, design, themes);
        return true;
    }

    /// <summary>
    /// Whether changing the deck's theme to <paramref name="themeId"/> needs the question "slides with a
    /// theme of their own: those too?": only when some slide has its own theme (other than the new one).
    /// </summary>
    public static int SlidesWithOwnTheme(Presentation presentation, string themeId) =>
        presentation.Slides.Count(s => !string.IsNullOrEmpty(s.ThemeId) && s.ThemeId != themeId);
}
