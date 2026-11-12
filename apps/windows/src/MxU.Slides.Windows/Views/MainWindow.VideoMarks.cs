using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

// A video tile's "Video Actions…" menu item: opens the video's marks (VideoActionsWindow).
// Like "Edit motion background…", it's added as the menu opens rather than written in
// MainWindow.xaml, because the tile menu is shared by pictures, which have no timeline.
public partial class MainWindow
{
    private const string VideoActionsHeader = "Video Actions…";

    internal MenuItem? PrepareVideoActionsMenu(FrameworkElement tileElement)
    {
        if (tileElement is not { ContextMenu: { } menu, DataContext: MediaTileViewModel tile }) return null;
        var item = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == VideoActionsHeader);
        if (item is null)
        {
            item = new MenuItem { Header = VideoActionsHeader, ToolTip = "Named points in the video that run actions, and that a stage countdown can count to" };
            // Whatever tile the menu was opened on (the menu is shared by the tiles).
            item.Click += (_, _) =>
            {
                if ((menu.PlacementTarget as FrameworkElement ?? tileElement).DataContext is MediaTileViewModel clicked)
                    VideoActionsWindow.Edit(this, model, clicked.Item);
            };
            // Under Actions… (what runs when the item is fired): these run while it plays.
            var actions = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == "Actions…");
            menu.Items.Insert(actions is null ? 0 : menu.Items.IndexOf(actions) + 1, item);
        }
        item.Visibility = tile.Item.MediaKind == MediaKind.Video ? Visibility.Visible : Visibility.Collapsed;
        return item;
    }
}
