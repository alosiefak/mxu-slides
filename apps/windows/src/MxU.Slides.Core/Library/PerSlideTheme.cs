using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// Which theme a slide is drawn with. A presentation names one theme for all its slides,
/// but on the Mac a single slide can follow another theme (a scripture slide in a lyric
/// deck: <see cref="Slide.ThemeId"/>), and a Blank slide follows none at all
/// (<see cref="Slide.Unthemed"/>), so it shows only its own objects on the theme-less black.
/// Port of PresenterCore/Library/PerSlideTheme.swift.
/// <para>
/// Everything that draws a slide (the outputs, the slide grid, the editor, the lyrics
/// preview) asks <see cref="ThemeIdFor"/> rather than reading the presentation's theme.
/// </para>
/// </summary>
public static class PerSlideTheme
{
    /// <summary>
    /// The id of the theme <paramref name="slide"/> is drawn with: "" for a Blank slide (no theme,
    /// whatever else it says), else its own theme, else the presentation's. An empty own theme counts as none.
    /// </summary>
    public static string ThemeIdFor(this Presentation presentation, Slide slide)
    {
        if (slide.Unthemed == true) return "";
        if (!string.IsNullOrEmpty(slide.ThemeId)) return slide.ThemeId;
        return presentation.ThemeId;
    }

    /// <summary>True when any slide doesn't simply follow the presentation's theme (its own theme, or Blank).</summary>
    public static bool HasSlideThemes(this Presentation presentation) =>
        presentation.Slides.Any(s => !string.IsNullOrEmpty(s.ThemeId) || s.Unthemed == true);

    /// <summary>Every slide follows the presentation's theme again (a Blank slide stays blank).</summary>
    public static void ClearSlideThemes(this Presentation presentation)
    {
        foreach (var slide in presentation.Slides) slide.ThemeId = null;
    }

    /// <summary>The design this slide chose for when it's shown through <paramref name="themeId"/> as a look, if it chose one.</summary>
    public static string? OverrideDesignName(this Slide slide, string themeId) =>
        slide.OverrideDesigns?.FirstOrDefault(d => d.ThemeId == themeId)?.ThemeSlideName;

    /// <summary>Chooses (or with null, forgets) the design this slide uses through a look's theme; one choice per theme.</summary>
    public static void SetOverrideDesign(this Slide slide, string? name, string themeId)
    {
        var designs = (slide.OverrideDesigns ?? []).Where(d => d.ThemeId != themeId).ToList();
        if (!string.IsNullOrEmpty(name)) designs.Add(new OverrideDesign { ThemeId = themeId, ThemeSlideName = name });
        slide.OverrideDesigns = designs.Count == 0 ? null : designs;
    }
}
