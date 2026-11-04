using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MxU.Slides.Core.Library;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The library list's behaviour: a click opens a song (or goes into a folder), Ctrl+click and
/// Shift+click pick several (WPF's own extended selection does the clicking; which rows a menu
/// acts on is the Mac's Finder rule, <see cref="ListMultiSelect.Batch"/>), a drag puts a song in
/// the service, files dropped from Explorer are imported, and Delete moves the picked rows to the
/// Trash. Placed in the main window by one line of its XAML.
/// </summary>
public partial class LibraryPanel : UserControl
{
    private MainViewModel? model;
    private bool restoring;
    private HashSet<string> pickedIds = new(StringComparer.Ordinal);
    private Point dragStart;
    private LibraryEntryViewModel? dragCandidate;

    public LibraryPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
    }

    private MainWindow? Window => System.Windows.Window.GetWindow(this) as MainWindow;

    private void Attach(MainViewModel? next)
    {
        if (model is not null)
        {
            model.PropertyChanged -= OnModelChanged;
            model.LibraryRowsChanged -= OnRowsChanged;
        }
        model = next;
        if (model is null) return;
        model.PropertyChanged += OnModelChanged;
        model.LibraryRowsChanged += OnRowsChanged;
    }

    /// <summary>Puts the cursor in the search box (Ctrl+F).</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    // MARK: Selection

    /// <summary>The songs and presentations picked, in list order.</summary>
    public IReadOnlyList<string> PickedPresentationIds() =>
        [.. LibraryList.Items.OfType<LibraryEntryViewModel>().Where(LibraryList.SelectedItems.Contains).Select(e => e.Presentation.Id)];

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The app opened a song some other way (from the service, an import): show it picked here too.
        if (e.PropertyName != nameof(MainViewModel.SelectedLibraryEntry) || model is null || restoring) return;
        if (model.SelectedLibraryEntry is { } entry && !LibraryList.SelectedItems.Contains(entry))
        {
            restoring = true;
            LibraryList.SelectedItem = entry;
            restoring = false;
        }
        else if (model.SelectedLibraryEntry is null && LibraryList.SelectedItems.Count == 1 && LibraryList.SelectedItem is LibraryEntryViewModel)
        {
            restoring = true;
            LibraryList.SelectedItem = null;
            restoring = false;
        }
    }

    /// <summary>The rows were rebuilt: pick again the ones that were picked (by id), so a batch survives a redraw.</summary>
    private void OnRowsChanged()
    {
        if (model is null) return;
        restoring = true;
        LibraryList.SelectedItems.Clear();
        foreach (var row in LibraryList.Items.OfType<LibraryEntryViewModel>().Where(r => pickedIds.Contains(r.Presentation.Id)))
            LibraryList.SelectedItems.Add(row);
        if (pickedIds.Count <= 1 && model.SelectedLibraryEntry is { } open && !LibraryList.SelectedItems.Contains(open))
            LibraryList.SelectedItems.Add(open);
        restoring = false;
        ShowPicked();
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (restoring || model is null) return;
        // The rows being replaced (a redraw) takes their selection with them: that isn't the operator unpicking.
        if (e.AddedItems.Count == 0 && e.RemovedItems.Cast<object>().All(row => !LibraryList.Items.Contains(row))) return;
        pickedIds = PickedPresentationIds().ToHashSet(StringComparer.Ordinal);
        ShowPicked();
        if (LibraryList.SelectedItems.Count != 1) return; // several picked: nothing opens
        switch (LibraryList.SelectedItem)
        {
            case LibraryEntryViewModel entry:
                model.SelectedLibraryEntry = entry;
                break;
            case LibraryFolderRowViewModel folder:
                // Into the folder after this click has finished (the rows are about to be replaced).
                Dispatcher.BeginInvoke(() => model.OpenLibraryFolder(folder.Path), DispatcherPriority.Input);
                break;
            case LibraryAllRowViewModel:
                Dispatcher.BeginInvoke(() => model.LibraryShowsAll = true, DispatcherPriority.Input);
                break;
            case LibraryMediaHitViewModel hit:
                model.ShowLibraryHit(hit);
                break;
        }
    }

    private void ShowPicked()
    {
        var count = pickedIds.Count;
        PickedNote.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PickedNote.Text = $"{count} picked · right-click for actions · Esc unpicks";
    }

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (model is null) return;
        if (e.Key == Key.Delete && PickedPresentationIds() is { Count: > 0 } ids)
        {
            model.DeletePresentations(ids);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && LibraryList.SelectedItems.Count > 1)
        {
            LibraryList.SelectedItems.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && model.LibraryPath.Count > 0)
        {
            model.OpenLibraryFolder([.. model.LibraryPath.SkipLast(1)]);
            e.Handled = true;
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Down && LibraryList.Items.Count > 0)
        {
            // From the search box down into the results, as in Explorer.
            LibraryList.SelectedIndex = 0;
            (LibraryList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is LibraryEntryViewModel { IsSong: true } entry) Window?.EditLyricsFromLibrary(entry.Presentation);
    }

    // MARK: Filters, sort, crumbs

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var filter) && model is not null) model.LibraryFilter = filter;
    }

    private void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if (model is null || (sender as FrameworkElement)?.DataContext is not LibraryCrumb crumb) return;
        // "Library" also leaves the everything view and the filters.
        if (crumb.Path.Count == 0)
        {
            model.LibraryFavoritesOnly = false;
            model.LibraryTag = null;
            model.LibraryShowsAll = false;
        }
        model.OpenLibraryFolder(crumb.Path);
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (model is null) return;
        var menu = new ContextMenu();
        foreach (var sort in LibrarySorts.All)
            menu.Items.Add(LibraryMenus.Item((model.LibrarySortOrder == sort ? "✓  " : "     ") + sort.Title(), () => model.LibrarySortOrder = sort));
        EditorKit.Open(menu, SortButton);
    }

    private void Tag_Click(object sender, RoutedEventArgs e)
    {
        if (model is null) return;
        var menu = new ContextMenu();
        menu.Items.Add(LibraryMenus.Item((model.LibraryTag is null ? "✓  " : "     ") + "Any tag", () => model.LibraryTag = null));
        var tags = model.LibraryTags();
        if (tags.Count == 0) menu.Items.Add(LibraryMenus.Item("     No tags yet: right-click a song › Tag", () => { }, enabled: false));
        else menu.Items.Add(EditorKit.Separator());
        foreach (var tag in tags)
            menu.Items.Add(LibraryMenus.Item((string.Equals(model.LibraryTag, tag, StringComparison.OrdinalIgnoreCase) ? "✓  " : "     ") + tag, () => model.LibraryTag = tag));
        EditorKit.Open(menu, TagButton);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (model is null || Window is not { } window) return;
        var menu = new ContextMenu();
        LibraryMenus.ForEmptySpace(menu, window, model);
        EditorKit.Open(menu, MoreButton);
    }

    // MARK: Right-click

    private object? clickedRow;

    /// <summary>Opens the right-click menu for a row as if it had been right-clicked (the self-test pictures it).</summary>
    internal ContextMenu OpenMenuFor(object? row)
    {
        clickedRow = row;
        var menu = LibraryList.ContextMenu!;
        menu.PlacementTarget = LibraryList;
        menu.IsOpen = true;
        return menu;
    }

    private void List_ContextMenuOpening(object sender, ContextMenuEventArgs e) =>
        clickedRow = (e.OriginalSource as FrameworkElement)?.DataContext is var context && context != model ? context : null;

    /// <summary>
    /// Fills the menu for what was right-clicked. TransitionMenus adds its "Transition" item for the
    /// selected presentation as the menu opens (before this runs); with several picked it would only
    /// speak for one of them, so it's taken out.
    /// </summary>
    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || model is null || Window is not { } window) return;
        var transition = menu.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Tag, "transition-menu"));
        menu.Items.Clear();
        switch (clickedRow)
        {
            case LibraryEntryViewModel entry:
            {
                var order = LibraryList.Items.OfType<LibraryEntryViewModel>().Select(r => r.Presentation.Id).ToList();
                var batch = ListMultiSelect.Batch(entry.Presentation.Id, PickedPresentationIds().ToHashSet(StringComparer.Ordinal), order);
                // A search hit right-clicks like the item in its folder, with Show in Library first (as on the Mac).
                var hit = model.IsLibrarySearching && batch.Count == 1;
                if (hit)
                {
                    menu.Items.Add(LibraryMenus.Item("Show in Library", () => model.ShowInLibrary(entry.Presentation.Id)));
                    menu.Items.Add(EditorKit.Separator());
                }
                LibraryMenus.ForPresentations(menu, window, model, batch);
                if (batch.Count == 1 && transition is not null) menu.Items.Insert(Math.Min(hit ? 6 : 4, menu.Items.Count), transition);
                break;
            }
            case LibraryFolderRowViewModel folder:
                LibraryMenus.ForFolder(menu, window, model, folder);
                break;
            case LibraryMediaHitViewModel hit:
                LibraryMenus.ForMediaHit(menu, window, model, hit);
                break;
            default:
                LibraryMenus.ForEmptySpace(menu, window, model);
                break;
        }
        if (menu.Items.Count == 0) menu.IsOpen = false;
    }

    // MARK: Dragging a song to the service, dropping files from Explorer

    private void List_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(null);
        dragCandidate = (e.OriginalSource as FrameworkElement)?.DataContext as LibraryEntryViewModel;
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragCandidate is not { } entry) return;
        var moved = e.GetPosition(null) - dragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragCandidate = null;
        // The same data the library list always dragged: the service list takes "mxu/presentation" (MainWindow.xaml.cs).
        DragDrop.DoDragDrop(LibraryList, new DataObject("mxu/presentation", entry.Presentation.Id), DragDropEffects.Copy);
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || Window is not { } window) return;
        e.Handled = true;
        // After the drop returns, so Explorer isn't kept waiting while a question is asked.
        Dispatcher.BeginInvoke(async () => await window.ImportFiles(files), DispatcherPriority.Background);
    }
}
