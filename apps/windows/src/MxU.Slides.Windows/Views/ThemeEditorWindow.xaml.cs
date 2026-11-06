using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MxU.Slides.Core.Library;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The theme editor window: design a theme's designs, animate them, and preview them as slides. One
/// is open at a time; <see cref="Open"/> brings it forward on the theme (and design) asked for. The
/// logic is in <see cref="ThemeEditorViewModel"/>; this file only asks questions (names, "are you
/// sure?") and connects the canvas and the previews.
/// </summary>
public partial class ThemeEditorWindow : Window
{
    private static ThemeEditorWindow? open;

    private ThemeEditorWindow(MainViewModel main, string? themeId, string? designName)
    {
        InitializeComponent();
        Model = new ThemeEditorViewModel(main, themeId, designName);
        DataContext = Model;
        Model.Canvas = CanvasEditor;
        CanvasEditor.TextEditRequested += _ => Dispatcher.BeginInvoke(() =>
        {
            Model.ShowingObjectTab = true;
            InspectorPanel.FocusText();
        }, DispatcherPriority.Loaded);
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThemeEditorViewModel.Playhead)) Timeline.Playhead = Model.Playhead;
            if (e.PropertyName is nameof(ThemeEditorViewModel.SampleScene) or "") ShowSample();
        };
        Loaded += (_, _) => ShowSample();
        Closed += (_, _) =>
        {
            Model.Dispose();
            if (ReferenceEquals(open, this)) open = null;
        };
    }

    public ThemeEditorViewModel Model { get; }

    /// <summary>The Design tab's picture of a slide using the design.</summary>
    private void ShowSample()
    {
        if (Model.SampleScene is { } scene)
        {
            if (SampleView.Scene is null) SampleView.Present(scene, SceneTransition.Cut);
            else SampleView.Update(scene);
        }
        else SampleView.Present((Core.Scene.RenderScene?)null, SceneTransition.Cut);
    }

    /// <summary>
    /// Opens the theme editor (or brings the open one forward) on <paramref name="themeId"/>, or on the
    /// first theme when that's null or unknown. Call it from the main window; it doesn't block.
    /// </summary>
    public static ThemeEditorWindow Open(Window owner, MainViewModel main, string? themeId, string? designName = null)
    {
        if (open is { } existing)
        {
            if (themeId is not null) existing.Model.Open(themeId, designName);
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        var window = new ThemeEditorWindow(main, string.IsNullOrEmpty(themeId) ? null : themeId, designName) { Owner = owner };
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
    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box || box.AcceptsReturn) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    // MARK: Themes

    private void ThemeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ThemeMenuButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = ThemeMenuButton;
        menu.IsOpen = true;
    }

    private void NewTheme_Click(object sender, RoutedEventArgs e) => Model.NewThemeCommand.Execute(null);

    private void DuplicateTheme_Click(object sender, RoutedEventArgs e) => Model.DuplicateThemeCommand.Execute(null);

    private void RenameTheme_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Current is not { } theme) return;
        if (InputDialog.Ask(this, "Rename theme", "A new name for the theme:", theme.Name) is { } name && !Model.RenameTheme(name) && name.Trim().Length == 0)
            Model.Notice = "A theme needs a name.";
    }

    private void DeleteTheme_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Current is not { } theme) return;
        var uses = Model.ThemeUses;
        var notes = new List<string>();
        if (uses.Count > 0)
            notes.Add($"{uses.Count} presentation{(uses.Count == 1 ? " uses" : "s use")} it ({string.Join(", ", uses.Take(4).Select(u => u.Presentation.Name))}{(uses.Count > 4 ? "…" : "")}). Their slides will show with no theme.");
        if (ThemeLibrary.IsStarter(theme)) notes.Add("It's a starter theme, so the app puts it back the next time it starts.");
        var message = $"Delete the theme \"{theme.Name}\"? It goes to the library's Trash folder." + (notes.Count > 0 ? "\n\n" + string.Join("\n", notes) : "");
        if (MessageBox.Show(this, message, "Themes", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) Model.DeleteTheme();
    }

    // MARK: Designs

    private void RenameDesign_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Design is not { } design) return;
        if (InputDialog.Ask(this, "Rename design", "A new name for the design (slides pick their design by its name):", design.Name) is not { } name) return;
        var uses = Model.DesignUseCount;
        var follow = uses > 0 && MessageBox.Show(this,
            $"{uses} slide{(uses == 1 ? " uses" : "s use")} \"{design.Name}\". Rename it in {(uses == 1 ? "that slide" : "those slides")} too, so {(uses == 1 ? "it keeps" : "they keep")} this design?\n\n(On a Mac a renamed design isn't followed: slides naming the old one use the theme's first design.)",
            "Rename design", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        if (!Model.RenameDesign(name, follow) && name.Trim().Length == 0) Model.Notice = "A design needs a name.";
    }

    private void DeleteDesign_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Design is not { } design) return;
        if (!Model.CanDeleteDesign)
        {
            Model.Notice = "A theme keeps at least one design.";
            return;
        }
        var uses = Model.DesignUseCount;
        var message = $"Delete the design \"{design.Name}\"?" + (uses > 0 ? $"\n\n{uses} slide{(uses == 1 ? " uses" : "s use")} it and will use the theme's first design instead." : "");
        if (MessageBox.Show(this, message, "Themes", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) Model.DeleteDesignCommand.Execute(null);
    }
}
