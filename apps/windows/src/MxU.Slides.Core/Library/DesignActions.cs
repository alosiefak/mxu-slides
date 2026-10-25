using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// What happens to a slide's actions when it takes a theme's design: a design can carry actions
/// (switch the look to a lower third, clear the background), and a slide that takes the design
/// takes copies of them; switching to another design swaps the old design's actions for the new
/// one's, leaving the slide's own actions (and any changed since) alone. And applying a theme to
/// some slides of a deck, or to all of them.
/// <para>Line-for-line port of the Mac's PresenterCore/Library/DesignActions.swift.</para>
/// </summary>
public static class DesignActions
{
    /// <summary>The theme's design called <paramref name="name"/> (any case), else its first; null for a theme without designs.</summary>
    public static Slide? Design(this Theme theme, string? name)
    {
        var designs = theme.Slides ?? [];
        if (!string.IsNullOrEmpty(name) && designs.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) is { } match)
            return match;
        return designs.FirstOrDefault();
    }

    /// <summary>
    /// The slide takes <paramref name="design"/>'s actions (fresh-id copies of any it hasn't got),
    /// dropping those that came from <paramref name="previous"/> and that the new design doesn't give.
    /// No actions are stored as absent.
    /// </summary>
    public static void AdoptActions(this Slide slide, Slide? design, Slide? previous = null, Func<string>? newId = null)
    {
        newId ??= LibraryStore.NewId;
        var given = design?.Actions ?? [];
        var old = previous?.Actions ?? [];
        var own = (slide.Actions ?? []).ToList();
        own.RemoveAll(action => old.Any(o => o.SameWork(action)) && !given.Any(g => g.SameWork(action)));
        foreach (var action in given.Where(a => !own.Any(o => o.SameWork(a))))
        {
            var copy = MxuJson.Clone(action);
            copy.Id = newId();
            own.Add(copy);
        }
        slide.Actions = own.Count == 0 ? null : own;
    }

    /// <summary>The deck's theme becomes <paramref name="theme"/>: every themed slide swaps the design actions it had for the new theme's.</summary>
    public static void AdoptDesignActions(this Presentation presentation, Theme? theme, IReadOnlyDictionary<string, Theme> themes)
    {
        foreach (var slide in presentation.Slides.Where(s => s.Unthemed != true))
        {
            var previous = themes.GetValueOrDefault(presentation.ThemeIdFor(slide))?.Design(slide.ThemeSlideName);
            slide.AdoptActions(theme?.Design(slide.ThemeSlideName), previous);
        }
    }

    /// <summary>
    /// Some slides follow <paramref name="theme"/> (the deck's own theme clears their own), keeping their
    /// design unless <paramref name="design"/> names another; a Blank slide takes the theme too; each
    /// swaps its design actions.
    /// </summary>
    public static void ApplyTheme(this Presentation presentation, Theme theme, IReadOnlySet<string> slideIds, string? design, IReadOnlyDictionary<string, Theme> themes)
    {
        foreach (var slide in presentation.Slides.Where(s => slideIds.Contains(s.Id)))
        {
            var previous = themes.GetValueOrDefault(presentation.ThemeIdFor(slide))?.Design(slide.ThemeSlideName);
            var name = design ?? slide.ThemeSlideName;
            slide.ThemeId = theme.Id == presentation.ThemeId ? null : theme.Id;
            slide.ThemeSlideName = name;
            slide.Unthemed = null;
            slide.AdoptActions(theme.Design(name), previous);
        }
    }

    /// <summary>True when every one of those slides already follows <paramref name="themeId"/> (on <paramref name="design"/>, when given).</summary>
    public static bool SlidesAllFollow(this Presentation presentation, IReadOnlySet<string> slideIds, string themeId, string? design = null)
    {
        var chosen = presentation.Slides.Where(s => slideIds.Contains(s.Id)).ToList();
        return chosen.Count > 0 && chosen.All(slide => presentation.ThemeIdFor(slide) == themeId
            && (design is null || string.Equals(slide.ThemeSlideName, design, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Two actions that do the same thing (everything but the id the same).</summary>
    public static bool SameWork(this SlideAction action, SlideAction other)
    {
        var aligned = MxuJson.Clone(other);
        aligned.Id = action.Id;
        return MxuJson.StructurallyEqual(aligned, action);
    }
}
