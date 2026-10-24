using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The auto-advance chip, the video transport strip and the Clear controls under the audience
/// preview. The menus are built here when they open, so they always list what's live now.
/// </summary>
public partial class LiveShowControls : UserControl
{
    public LiveShowControls() => InitializeComponent();

    private MainViewModel? Model => DataContext as MainViewModel;

    // MARK: Scrubbing

    private DateTime lastLiveSeek;

    private void Scrub_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Model is { } model) model.TransportScrubbing = true;
    }

    private void Scrub_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Model is not { } model) return;
        model.TransportScrubbing = false;
        model.TransportSeek(Scrub.Value);
    }

    /// <summary>While dragging, the picture follows the thumb (a seek at most every 120 ms, as on the Mac).</summary>
    private void Scrub_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Model is not { TransportScrubbing: true } model || DateTime.UtcNow - lastLiveSeek < TimeSpan.FromMilliseconds(120)) return;
        lastLiveSeek = DateTime.UtcNow;
        model.TransportSeek(e.NewValue);
    }

    // MARK: Menus

    private void GoToTime_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || Window.GetWindow(this) is not { } owner) return;
        if (InputDialog.Ask(owner, "Go to a time", "Where to go in the video (minutes:seconds, e.g. 1:30)", model.TransportElapsedText) is not { } typed) return;
        if (!model.TransportGoTo(typed)) MessageBox.Show(owner, "Type the time as minutes:seconds, for example 1:30.", "Go to a time");
    }

    /// <summary>The jump amount: the very end, or 5 seconds to 5 minutes before it.</summary>
    private void SkipToEnd_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (Model is not { } model) return;
        var menu = new ContextMenu { PlacementTarget = SkipToEnd, Placement = PlacementMode.Bottom };
        int[] amounts = [0, 5, 10, 15, 20, 25, 30, 60, 90, 120, 180, 240, 300];
        foreach (var seconds in amounts)
        {
            var item = new MenuItem
            {
                Header = seconds == 0 ? "End" : seconds < 120 ? $"-{seconds}s before end" : $"-{seconds / 60}m before end",
                IsCheckable = true,
                IsChecked = model.Settings.TransportSkipToEndSeconds == seconds,
            };
            item.Click += (_, _) => model.SetTransportSkip(seconds);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Each layer, top of the stack first: look at it, or pin one of its videos.</summary>
    private void LayerPicker_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        var menu = new ContextMenu { PlacementTarget = LayerPicker, Placement = PlacementMode.Bottom };
        foreach (var (layer, viewed, videos) in model.TransportPicker())
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            var view = new MenuItem { Header = $"View {layer.DisplayName()}", IsCheckable = true, IsChecked = viewed };
            view.Click += (_, _) => model.SelectTransportLayer(layer);
            menu.Items.Add(view);
            foreach (var (mediaId, label, pinned) in videos)
            {
                var pin = new MenuItem { Header = "    " + label, IsCheckable = true, IsChecked = pinned };
                pin.Click += (_, _) => model.PinTransport(mediaId);
                menu.Items.Add(pin);
            }
        }
        menu.IsOpen = true;
    }

    /// <summary>"Clear All clears": tick what F1 takes down.</summary>
    private void ClearAll_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (Model is not { } model) return;
        var menu = new ContextMenu { PlacementTarget = ClearAllButton, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Clear All clears", IsEnabled = false });
        var skips = model.ClearAllSkips;
        foreach (var function in ShowFunctions.All)
        {
            var item = new MenuItem { Header = function.DisplayName(), IsCheckable = true, IsChecked = !skips.Contains(function) };
            item.Click += (_, _) => model.SetClearAllSweeps(function, item.IsChecked);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
        e.Handled = true;
    }
}
