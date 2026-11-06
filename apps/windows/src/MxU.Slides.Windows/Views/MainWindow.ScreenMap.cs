using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

// A media tile's Layer menu (the Screen Map): fire the item onto one of the active preset's
// custom layers, or clear one. Shown only while the preset has custom layers. Port of the
// Mac's media-item Layer menu (LibraryView / MediaCueMenu).
public partial class MainWindow
{
    private const string LayerMenuHeader = "Play on Layer";

    internal MenuItem? PrepareLayerMenu(FrameworkElement tileElement)
    {
        if (tileElement is not { ContextMenu: { } menu, DataContext: MediaTileViewModel }) return null;
        var item = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == LayerMenuHeader);
        if (item is null)
        {
            item = new MenuItem { Header = LayerMenuHeader, ToolTip = "Play this on one of the output preset's own layers (Screen Map)" };
            menu.Items.Insert(0, item);
        }
        item.Items.Clear();
        foreach (var layer in model.CustomLayers)
        {
            var play = new MenuItem { Header = layer.Name };
            play.Click += (_, _) =>
            {
                if ((menu.PlacementTarget as FrameworkElement ?? tileElement).DataContext is MediaTileViewModel clicked)
                    model.FireMediaOnLayer(clicked.Item, layer.Id);
            };
            item.Items.Add(play);
        }
        if (model.CustomLayers.Count > 0)
        {
            item.Items.Add(new Separator());
            foreach (var layer in model.CustomLayers)
            {
                var clear = new MenuItem { Header = $"Clear {layer.Name}" };
                clear.Click += (_, _) => model.ClearLayer(layer.Id);
                item.Items.Add(clear);
            }
        }
        item.Visibility = model.CustomLayers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        return item;
    }
}
