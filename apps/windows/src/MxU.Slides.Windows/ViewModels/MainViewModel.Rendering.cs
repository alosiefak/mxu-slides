using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Rendering a Mac or ProPresenter document the way the Mac draws it: which theme each slide
/// uses (a slide can follow its own theme, or none when it's Blank).
/// </summary>
public sealed partial class MainViewModel
{
    // MARK: Per-slide themes

    /// <summary>
    /// The theme <paramref name="slide"/> is drawn with: its own when it names one, none when
    /// it's a Blank slide, else its presentation's (<see cref="PerSlideTheme.ThemeIdFor"/>).
    /// Every place that draws a slide asks this, so the outputs, the grid and the editor agree.
    /// </summary>
    public Theme? ThemeFor(Slide slide, Presentation presentation) => Library.Get<Theme>(presentation.ThemeIdFor(slide));

    /// <summary>The theme of the slide being edited (the canvas, its inspector and the Design picker use it).</summary>
    private Theme? EditedSlideTheme => EditedSlide is { } slide && CurrentPresentation is { } p ? ThemeFor(slide, p) : CurrentTheme;

    // MARK: Library fonts

    /// <summary>
    /// Loads fonts newly saved into the library (an import's embedded fonts) and, if any were new,
    /// redraws what's on screen so text that had a stand-in font gets its real one.
    /// </summary>
    public void ActivateLibraryFonts()
    {
        if (Rendering.FontMap.ActivateStored(Library.Root) == 0) return;
        RebuildSlides();
        if (Show.Live is not null || Show.LiveOverlays.Count > 0) RenderOutputs(Rendering.SceneTransition.Cut, refreshOnly: true);
    }
}
