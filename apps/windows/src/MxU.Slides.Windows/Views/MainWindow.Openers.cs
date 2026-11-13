using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Media bin's way into the opener maker (Phase 13): its "Make an opener…" button opens
/// <see cref="OpenerWindow"/>, and a tile made with it gets "Edit opener…" in its right-click
/// menu. Like "Edit motion background…" (MainWindow.MotionBackgrounds.cs, which calls
/// <see cref="PrepareOpenerMenu"/> as a tile's menu opens), the item isn't written in
/// MainWindow.xaml: it's added only to tiles that have an opener's recipe.
/// </summary>
public partial class MainWindow
{
    private const string EditOpenerHeader = "Edit opener…";

    private void Opener_Click(object sender, RoutedEventArgs e) => OpenerWindow.Open(this, model);

    /// <summary>
    /// Gives a media tile's menu its "Edit opener…" item (made the first time), shown only when
    /// the tile's item was made by the opener maker. Returns the menu item, or null when
    /// <paramref name="tileElement"/> isn't a media tile.
    /// </summary>
    internal MenuItem? PrepareOpenerMenu(FrameworkElement tileElement)
    {
        if (tileElement is not { ContextMenu: { } menu, DataContext: MediaTileViewModel tile }) return null;
        var item = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == EditOpenerHeader);
        if (item is null)
        {
            item = new MenuItem { Header = EditOpenerHeader, ToolTip = "Change its words, pictures or look and make it again" };
            // Whatever tile the menu was opened on (the tile it belongs to).
            item.Click += (_, _) =>
            {
                if ((menu.PlacementTarget as FrameworkElement ?? tileElement).DataContext is MediaTileViewModel clicked) EditOpener(clicked);
            };
            var actions = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == "Actions…");
            menu.Items.Insert(actions is null ? 0 : menu.Items.IndexOf(actions) + 1, item);
        }
        item.Visibility = model.OpenerRecipeOf(tile.Item) is null ? Visibility.Collapsed : Visibility.Visible;
        return item;
    }

    /// <summary>Opens an opener made here in the window, from its recipe.</summary>
    internal void EditOpener(MediaTileViewModel tile)
    {
        if (model.OpenerRecipeOf(tile.Item) is null) return;
        OpenerWindow.Open(this, model, tile.Item);
    }
}
