using System.Windows;

namespace MxU.Slides.Windows.Views;

/// <summary>The main window's Phase 8 helpers (operator depth): what other controls ask of the slide area.</summary>
public partial class MainWindow
{
    /// <summary>
    /// Scrolls the slides to one of the open song's slides (an arrangement pill was clicked): its
    /// tile in the grid, or its card's tile in the continuous view.
    /// </summary>
    internal void RevealSlide(int occurrence)
    {
        if (model.ShowsContinuous && !model.IsEditing)
        {
            ContinuousView.RevealSlide(model.CurrentContextItemId, model.CurrentPresentation?.Id, occurrence);
            return;
        }
        if (SlideGrid.ItemContainerGenerator.ContainerFromIndex(occurrence) is FrameworkElement tile)
        {
            // To the top of the view, not just into it: the block's slides follow below.
            var offset = tile.TransformToAncestor(SlideScroller).Transform(new Point(0, 0)).Y;
            SlideScroller.ScrollToVerticalOffset(SlideScroller.VerticalOffset + offset - 8);
        }
    }
}
