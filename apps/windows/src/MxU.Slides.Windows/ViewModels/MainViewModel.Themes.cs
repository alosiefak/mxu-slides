using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Giving presentations and slides a theme (the header's Theme picker, the slide menu's Apply
/// Theme…), with the Mac's question first when the deck's new theme would replace slides' own; and
/// saving what the theme editor changes. The rules are Core's <see cref="ThemeApplication"/>.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Asked before a deck's new theme reaches slides that chose a theme of their own: true to restyle
    /// them too (as the Mac does), false to keep theirs, null to cancel. Set by the main window; the
    /// self-test answers it itself. With no answer set, the Mac's way: they're restyled.
    /// </summary>
    public Func<Presentation, Theme?, int, bool?>? AskReplaceOwnThemes { get; set; }

    private Dictionary<string, Theme> ThemesById() => Library.All<Theme>().ToDictionary(t => t.Id, StringComparer.Ordinal);

    /// <summary>
    /// The deck takes a theme (and, with <paramref name="design"/>, every slide that design). Asks
    /// first about slides with a theme of their own. False when nothing changed (or it was cancelled).
    /// </summary>
    public bool ApplyDeckTheme(Presentation presentation, string themeId, string? design = null)
    {
        var own = ThemeApplication.OwnThemes.Replace;
        if (ThemeApplication.SlidesWithOwnTheme(presentation, themeId) is var count and > 0 && AskReplaceOwnThemes is { } ask)
        {
            switch (ask(presentation, Library.Get<Theme>(themeId), count))
            {
                case null: return false;
                case false: own = ThemeApplication.OwnThemes.Keep; break;
            }
        }
        if (!ThemeApplication.ApplyToDeck(presentation, themeId, design, ThemesById(), own)) return false;
        SavePresentationEdit(presentation);
        if (CurrentPresentation?.Id == presentation.Id)
        {
            CurrentTheme = Library.Get<Theme>(presentation.ThemeId);
            RebuildSlides();
            OnPropertyChanged(nameof(CurrentThemeChoice));
        }
        return true;
    }

    /// <summary>Some slides take a theme (the slide menu's Apply Theme…): they keep their designs unless one is picked.</summary>
    public bool ApplySlidesTheme(Presentation presentation, IReadOnlyCollection<string> slideIds, string themeId, string? design = null)
    {
        if (!ThemeApplication.ApplyToSlides(presentation, slideIds, themeId, design, ThemesById())) return false;
        SlidesRestyled(presentation);
        return true;
    }

    /// <summary>
    /// A theme design dropped on a slide (dragged from the theme designs browser): the slide takes that
    /// theme and design. False when the payload isn't a design that's still there. Port of the Mac's
    /// PresentGridView drop (<c>model.themeDesign(payload)</c>, then <c>applyTheme(to:slideIDs:)</c>).
    /// </summary>
    public bool DropThemeDesign(Presentation presentation, string slideId, string? payload) =>
        ThemeDesignDrag.Resolve(payload, Library.Get<Theme>) is { } dropped
        && ApplySlidesTheme(presentation, [slideId], dropped.ThemeId, dropped.Design.Name);

    /// <summary>Saves slides that changed their look, and redraws them when their presentation is the open one.</summary>
    private void SlidesRestyled(Presentation presentation)
    {
        SavePresentationEdit(presentation);
        if (CurrentPresentation?.Id == presentation.Id) RebuildSlides();
    }

    /// <summary>The slides follow no theme at all ("Blank"): only their own objects show.</summary>
    public void SetSlidesBlank(Presentation presentation, IReadOnlyCollection<string> slideIds)
    {
        foreach (var slide in presentation.Slides.Where(s => slideIds.Contains(s.Id))) slide.Unthemed = true;
        SlidesRestyled(presentation);
    }

    /// <summary>The slides follow the deck's theme again, with their own kind of design ("Automatic").</summary>
    public void SetSlidesAutomatic(Presentation presentation, IReadOnlyCollection<string> slideIds)
    {
        var deckTheme = Library.Get<Theme>(presentation.ThemeId);
        foreach (var slide in presentation.Slides.Where(s => slideIds.Contains(s.Id)))
        {
            var previous = Library.Get<Theme>(presentation.ThemeIdFor(slide))?.Design(slide.ThemeSlideName);
            slide.ThemeId = null;
            slide.Unthemed = null;
            slide.AdoptActions(deckTheme?.Design(slide.ThemeSlideName), previous);
        }
        SlidesRestyled(presentation);
    }

    // MARK: The theme editor's saves

    /// <summary>
    /// A theme was changed by the theme editor: everything drawn with it is drawn again (the open
    /// presentation's grid, and the outputs if a slide using it is live). Saving is the editor's.
    /// </summary>
    public void ThemeEdited(Theme theme)
    {
        if (CurrentPresentation is { } p && p.Slides.Any(s => p.ThemeIdFor(s) == theme.Id))
        {
            if (CurrentTheme?.Id == theme.Id) CurrentTheme = theme;
            RebuildSlides();
        }
        if (Show.Live is { } live && live.Presentation is { } deck && deck.ThemeIdFor(live.Slide) == theme.Id)
        {
            Show.PresentationEdited(deck);
            RenderOutputs(Rendering.SceneTransition.Cut, refreshOnly: true);
        }
    }

    /// <summary>Saves a theme straight away (a new, copied, renamed or re-filed one) and refreshes the pickers.</summary>
    public bool SaveThemeNow(Theme theme)
    {
        try
        {
            Library.Save(theme);
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Error($"Saving the theme \"{theme.Name}\" failed", ex);
            Problem = $"Couldn't save the theme \"{theme.Name}\": {ex.Message}";
            return false;
        }
        ThemeEdited(theme);
        return true;
    }
}
