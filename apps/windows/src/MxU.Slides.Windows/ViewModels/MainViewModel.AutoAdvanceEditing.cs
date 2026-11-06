using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Saving what the Auto Advance sheet (Views/AutoAdvanceWindow.cs) sets: a slide's (or several
/// slides') Auto Advance, a presentation's Slide Show, and a media item's. The timers that play them
/// are <see cref="AutoAdvanceController"/>'s (MainViewModel.AutoAdvance.cs); saving a presentation
/// tells it, so a slide that's live re-arms with its new timing.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Gives the slides the same Auto Advance (or none), and saves the presentation.</summary>
    public void SaveSlidesAutoAdvance(Presentation presentation, IReadOnlyCollection<string> slideIds, AutoAdvance? advance)
    {
        if (AutoAdvanceEditing.SetOnSlides(presentation, slideIds, advance) == 0) return;
        SavePresentationEdit(presentation);
    }

    /// <summary>A presentation's Slide Show: every slide without its own Auto Advance moves on by itself.</summary>
    public void SavePresentationSlideShow(Presentation presentation, AutoAdvance? advance)
    {
        presentation.AutoAdvance = AutoAdvanceEditing.Copy(advance);
        SavePresentationEdit(presentation);
    }

    /// <summary>A media item's Auto Advance (it fires the next service item).</summary>
    public void SaveMediaAutoAdvance(MediaItem item, AutoAdvance? advance)
    {
        item.AutoAdvance = AutoAdvanceEditing.Copy(advance);
        SaveMediaItem(item);
    }

    /// <summary>Saves a presentation an editor changed, and redraws the grid when it's the open one.</summary>
    private void SavePresentationEdit(Presentation presentation)
    {
        try
        {
            SavePresentation(presentation);
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Error($"Saving \"{presentation.Name}\" failed", ex);
            Problem = $"Couldn't save \"{presentation.Name}\": {ex.Message}";
            return;
        }
        if (CurrentPresentation?.Id == presentation.Id) RebuildSlides();
    }
}
