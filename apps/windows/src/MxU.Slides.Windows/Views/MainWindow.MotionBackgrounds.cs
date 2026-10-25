using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Media bin's way into motion backgrounds: its "Motion background…" button opens
/// <see cref="MotionBackgroundWindow"/>, and a tile made with it gets "Edit motion background…" in
/// its right-click menu. That menu item isn't written in MainWindow.xaml (whose tile menu is shared
/// by every video and picture): it's added as the menu opens, only on tiles that have a recipe.
/// </summary>
public partial class MainWindow
{
    private const string EditMotionHeader = "Edit motion background…";
    private bool motionMenusHooked;

    private void MotionBackground_Click(object sender, RoutedEventArgs e) => MotionBackgroundWindow.Open(this, model);

    /// <summary>When the button first appears, start watching the tile menus (once).</summary>
    private void MotionBackground_Loaded(object sender, RoutedEventArgs e)
    {
        if (motionMenusHooked) return;
        motionMenusHooked = true;
        // ContextMenuOpening bubbles up from the element whose menu is opening, so the window sees every one.
        AddHandler(ContextMenuOpeningEvent, new ContextMenuEventHandler(OnMotionTileMenuOpening), handledEventsToo: true);
    }

    private void OnMotionTileMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // The tile is the nearest element (from where the click landed, upward) that has its own menu.
        DependencyObject? at = e.OriginalSource as DependencyObject;
        while (at is not null && (at as FrameworkElement)?.ContextMenu is null)
            at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at);
        if (at is FrameworkElement element) PrepareMotionMenu(element);
    }

    /// <summary>
    /// Gives a media tile's menu its "Edit motion background…" item (made the first time), shown
    /// only when the tile's item has a recipe. Returns the menu item, or null when
    /// <paramref name="tileElement"/> isn't a media tile.
    /// </summary>
    internal MenuItem? PrepareMotionMenu(FrameworkElement tileElement)
    {
        if (tileElement is not { ContextMenu: { } menu, DataContext: MediaTileViewModel tile }) return null;
        var item = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == EditMotionHeader);
        if (item is null)
        {
            item = new MenuItem { Header = EditMotionHeader, ToolTip = "Change its style, colours or motion and render it again" };
            // Whatever tile the menu was opened on (the tile it belongs to).
            item.Click += (_, _) =>
            {
                if ((menu.PlacementTarget as FrameworkElement ?? tileElement).DataContext is MediaTileViewModel clicked) EditMotionBackground(clicked);
            };
            // Beside Rename… and Actions…, the other things you change about the item.
            var actions = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == "Actions…");
            menu.Items.Insert(actions is null ? 0 : menu.Items.IndexOf(actions) + 1, item);
        }
        item.Visibility = model.MotionRecipeOf(tile.Item) is null ? Visibility.Collapsed : Visibility.Visible;
        return item;
    }

    /// <summary>Opens a motion background made here in the window, from its recipe.</summary>
    internal void EditMotionBackground(MediaTileViewModel tile)
    {
        if (model.MotionRecipeOf(tile.Item) is null) return;
        MotionBackgroundWindow.Open(this, model, tile.Item);
    }
}
