using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using MxU.Slides.Core.Library;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The media bin's own bar: search (by name, file name or tag), the order, which folder, and
/// favourites only. Built in code like the other small panels; placed by one line of the main
/// window's XAML, where the search box used to be.
/// </summary>
public sealed class MediaBinBar : UserControl
{
    private readonly Button sort;
    private readonly Button folder;

    public MediaBinBar()
    {
        var search = new TextBox { Width = 170, Tag = "Search media…", ToolTip = "Search media by name, file name or tag" };
        search.SetBinding(TextBox.TextProperty, new Binding(nameof(MainViewModel.MediaSearch)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, Delay = 120 });
        sort = EditorKit.Ghost(new TextBlock { Text = "", FontFamily = EditorKit.IconFont }, "Sort the media: date added, name, date modified or recently used", ShowSortMenu);
        folder = EditorKit.Ghost(new TextBlock { Text = "All folders", FontSize = 11 }, "Show one folder's media", ShowFolderMenu);
        var favorites = new ToggleButton { Content = new TextBlock { Text = "", FontFamily = EditorKit.IconFont }, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Show only favourites" };
        favorites.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.MediaFavoritesOnly)));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(search);
        row.Children.Add(sort);
        row.Children.Add(folder);
        row.Children.Add(favorites);
        Content = row;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel model)
                model.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.MediaFolder)) ShowFolder(model);
                };
        };
    }

    private MainViewModel? Model => DataContext as MainViewModel;

    private void ShowFolder(MainViewModel model) => ((TextBlock)folder.Content).Text = model.MediaFolder ?? "All folders";

    private void ShowSortMenu()
    {
        if (Model is not { } model) return;
        var menu = new ContextMenu();
        foreach (var choice in new[] { LibrarySort.DateAdded, LibrarySort.Name, LibrarySort.DateModified, LibrarySort.RecentlyUsed })
            menu.Items.Add(LibraryMenus.Item((model.MediaSortOrder == choice ? "✓  " : "     ") + choice.Title(), () => model.MediaSortOrder = choice));
        EditorKit.Open(menu, sort);
    }

    private void ShowFolderMenu()
    {
        if (Model is not { } model) return;
        var menu = new ContextMenu();
        menu.Items.Add(LibraryMenus.Item((model.MediaFolder is null ? "✓  " : "     ") + "All folders", () => model.MediaFolder = null));
        var folders = model.MediaBinFolders();
        if (folders.Count > 0) menu.Items.Add(EditorKit.Separator());
        foreach (var path in folders)
            menu.Items.Add(LibraryMenus.Item((model.MediaFolder == path ? "✓  " : "     ") + path.Replace("/", " › "), () => model.MediaFolder = path));
        menu.Items.Add(EditorKit.Separator());
        menu.Items.Add(LibraryMenus.Item("New Folder…", () =>
        {
            if (Window.GetWindow(this) is { } owner && InputDialog.Ask(owner, "New Folder", "A folder for media:") is { } name && model.NewMediaFolder(name) is { } made)
                model.MediaFolder = made;
        }));
        EditorKit.Open(menu, folder);
    }
}
