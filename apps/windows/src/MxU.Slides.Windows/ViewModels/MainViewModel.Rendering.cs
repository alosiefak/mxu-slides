using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Outputs;

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

    // MARK: Live inputs and screen mirrors on slides

    private ScreenMirrors? screenMirrors;

    /// <summary>Mirrors of this app's screens, for slide objects filled with <c>screen::id</c> (started when first drawn).</summary>
    public ScreenMirrors ScreenMirrors => screenMirrors ??= new ScreenMirrors(EnsureScreenFeeds, () => Outputs?.Config);

    /// <summary>
    /// The inventory's engine id for a slide's live-input reference. A named input (<c>input::item::id</c>)
    /// is itself. A direct device reference from an older document (<c>input::camera::deviceId</c>,
    /// <c>input::ndi::name</c>) becomes the named input set up with that device, so it shows the same picture.
    /// </summary>
    private string NamedInputFor(string engineId)
    {
        if (inputs is null || engineId.StartsWith(VideoIO.Capture.VideoInputInventory.EngineIdPrefix, StringComparison.Ordinal)) return engineId;
        var parts = engineId.Split("::", 3);
        if (parts.Length < 3 || parts[0] != "input" || parts[2].Length == 0) return engineId;
        var kind = parts[1] == "ndi" ? CaptureSourceKind.Ndi : CaptureSourceKind.Camera;
        return inputs.Entries.FirstOrDefault(e => e.Kind == kind && string.Equals(e.SourceId, parts[2], StringComparison.OrdinalIgnoreCase))?.EngineId ?? engineId;
    }

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
