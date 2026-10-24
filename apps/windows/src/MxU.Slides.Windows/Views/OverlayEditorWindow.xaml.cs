using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MxU.Slides.Core.Library;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The overlay editor window: design lower thirds, scripture plates, tickers and the
/// rest. One is open at a time; <see cref="Open"/> brings it forward on the overlay
/// you ask for. The logic is in <see cref="OverlayEditorViewModel"/>; this file only
/// asks questions (names, "are you sure?") and connects the canvas.
/// </summary>
public partial class OverlayEditorWindow : Window
{
    private static OverlayEditorWindow? open;

    private OverlayEditorWindow(MainViewModel main, string? overlayId)
    {
        InitializeComponent();
        Model = new OverlayEditorViewModel(main, overlayId);
        DataContext = Model;
        Model.Canvas = CanvasEditor;
        // A double-click on text jumps to its words in the inspector, as in the slide editor.
        CanvasEditor.TextEditRequested += _ => Dispatcher.BeginInvoke(() =>
        {
            Model.ShowingObjectTab = true;
            InspectorPanel.FocusText();
        }, DispatcherPriority.Loaded);
        // Picking a step scrolls its settings into view, as the Mac's inspector does.
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayEditorViewModel.StepEditor) && Model.StepEditor is not null)
                Dispatcher.BeginInvoke(() => StepEditorPanel.BringIntoView(), DispatcherPriority.Loaded);
        };
        Closed += (_, _) =>
        {
            Model.Dispose();
            if (ReferenceEquals(open, this)) open = null;
        };
    }

    /// <summary>The steps list grows to fit, so the mouse wheel scrolls the whole Animation tab instead.</summary>
    private void StepList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        AnimationScroll.ScrollToVerticalOffset(AnimationScroll.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    public OverlayEditorViewModel Model { get; }

    /// <summary>
    /// Opens the overlay editor (or brings the open one forward) on <paramref name="overlayId"/>,
    /// or on the first overlay when that's null. Call it from the main window; it doesn't block.
    /// </summary>
    public static OverlayEditorWindow Open(Window owner, MainViewModel main, string? overlayId)
    {
        if (open is { } existing)
        {
            if (overlayId is not null) existing.Model.Open(overlayId);
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        var window = new OverlayEditorWindow(main, overlayId) { Owner = owner };
        // Fit a small laptop screen: never bigger than the space left by the taskbar.
        var area = SystemParameters.WorkArea;
        window.MinWidth = Math.Min(window.MinWidth, area.Width - 20);
        window.MinHeight = Math.Min(window.MinHeight, area.Height - 20);
        window.Width = Math.Min(window.Width, area.Width - 20);
        window.Height = Math.Min(window.Height, area.Height - 20);
        open = window;
        window.Show();
        return window;
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Enter in a one-line box applies it straight away.</summary>
    private void NumberBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    // MARK: Overlays

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Current is not { } overlay) return;
        if (InputDialog.Ask(this, "Rename overlay", "A new name for the overlay:", overlay.Name) is { } name && !Model.Rename(name) && name.Trim().Length == 0)
            Model.Notice = "An overlay needs a name.";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Current is not { } overlay) return;
        var notes = new List<string>();
        if (Model.IsLive) notes.Add("It is up on the program now and will be taken down.");
        if (OverlayLibrary.IsStarter(overlay)) notes.Add("It's a starter overlay, so the app puts it back the next time it starts.");
        var message = $"Delete the overlay \"{overlay.Name}\"? It goes to the library's Trash folder." + (notes.Count > 0 ? "\n\n" + string.Join("\n", notes) : "");
        if (MessageBox.Show(this, message, "Overlays", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) Model.Delete();
    }

    // MARK: Packs

    private void PackMenu_Click(object sender, RoutedEventArgs e)
    {
        if (PackMenuButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = PackMenuButton;
        menu.IsOpen = true;
    }

    private void NewPack_Click(object sender, RoutedEventArgs e)
    {
        if (InputDialog.Ask(this, "New pack", "Name the new pack (it starts with one blank overlay):") is not { } name) return;
        if (!Model.NewPack(name)) Model.Notice = $"A pack needs a name without \"/\", and can't be called \"{OverlayLibrary.NoPack}\".";
    }

    private void RenamePack_Click(object sender, RoutedEventArgs e)
    {
        if (Model.SelectedPack is not { } pack) return;
        if (pack == OverlayLibrary.NoPack)
        {
            Model.Notice = "These overlays aren't in a pack. Move one into a pack from the Overlay tab.";
            return;
        }
        if (InputDialog.Ask(this, "Rename pack", $"A new name for the pack \"{pack}\":", pack) is not { } name) return;
        if (!Model.RenamePack(name)) Model.Notice = "That name can't be used for a pack.";
    }

    private void DeletePack_Click(object sender, RoutedEventArgs e)
    {
        if (Model.SelectedPack is not { } pack) return;
        var contents = Model.PackContents;
        if (contents.Count == 0) return;
        var starters = contents.Count(OverlayLibrary.IsStarter);
        var message = $"Delete the pack \"{pack}\" and its {contents.Count} overlay{(contents.Count == 1 ? "" : "s")}? They go to the library's Trash folder."
            + (starters > 0 ? $"\n\n{starters} of them {(starters == 1 ? "is a starter overlay" : "are starter overlays")}, which the app puts back the next time it starts." : "");
        if (MessageBox.Show(this, message, "Overlays", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) Model.DeletePack();
    }
}
