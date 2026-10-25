using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MxU.Slides.Core.Animation;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Transitions;

/// <summary>
/// The room default transitions' popover: what a slide (or a background or video) does when it
/// doesn't name a transition of its own. The live header's two chips open it, and so does Settings ›
/// Transitions; both change the same two settings, which save at once.
/// </summary>
public static class RoomTransitionPopover
{
    /// <summary>Opens it under <paramref name="under"/>; null when there's no view model yet.</summary>
    public static Popup? Open(FrameworkElement under, MainViewModel? model, bool media)
    {
        if (model is null) return null;
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = media ? "Backgrounds and videos" : "Slides", FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 2),
        });
        content.Children.Add(EditorKit.Faint("The room default: items with their own transition always win."));
        var picker = new TransitionPicker(model.RoomTransitionChoice(media), defaultLabel: null, compact: true);
        // A room default is always some transition (a cut at least), so there's no "default" to go back to.
        picker.Changed += choice =>
        {
            if (choice is not null) model.SetRoomTransition(media, choice);
        };
        content.Children.Add(new ScrollViewer
        {
            Content = picker, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 0),
            MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height * 0.75),
        });
        return OperatorKit.Popover(under, content, 690);
    }
}
