using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MxU.Slides.Core.MotionTemplates;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The bins' ways into motion templates (Phase 11). The Overlays bin's "Motion templates…"
/// button opens <see cref="MotionTemplatesWindow"/>; pointing at a motion template's tile in that
/// bin plays it in the tile (its still shows otherwise); and a full-screen template's tile in the
/// Media bin gets "Edit motion template…" in its right-click menu. None of that is written in
/// MainWindow.xaml: it's hooked here when the button first appears, as the motion backgrounds' menu is.
/// </summary>
public partial class MainWindow
{
    private const string EditMotionTemplateHeader = "Edit motion template…";
    private const string BinPreviewPrefix = MotionTemplateStore.PicturePrefix + "bin:";
    private bool motionTemplatesHooked;
    private OverlayTileViewModel? hovered;
    private DispatcherTimer? binLoop;
    private Action? restartBinPreview;

    private void MotionTemplates_Loaded(object sender, RoutedEventArgs e)
    {
        if (motionTemplatesHooked) return;
        motionTemplatesHooked = true;
        AddHandler(ContextMenuOpeningEvent, new ContextMenuEventHandler(OnMotionTemplateMenuOpening), handledEventsToo: true);
        // Mouse moves tunnel down from the window, so it sees every tile without each one being wired up.
        PreviewMouseMove += (_, args) => HoverOverlayTile((args.OriginalSource as DependencyObject) is { } at ? TileUnder(at) : null);
        MouseLeave += (_, _) => HoverOverlayTile(null);
    }

    private static OverlayTileViewModel? TileUnder(DependencyObject at)
    {
        for (var i = 0; at is not null && i < 12; i++)
        {
            if (at is FrameworkElement { DataContext: OverlayTileViewModel tile } element && element.Cursor == Cursors.Hand) return tile;
            at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at);
        }
        return null;
    }

    /// <summary>Plays a motion template in its bin tile while the mouse is over it (and stops the last one).</summary>
    internal void HoverOverlayTile(OverlayTileViewModel? tile)
    {
        if (ReferenceEquals(tile, hovered)) return;
        if (hovered is { } last)
        {
            model.MotionPlayers.Stop(BinPreviewPrefix + last.Overlay.Id);
            last.Preview = OverlayTileViewModel.PreviewScene(last.Overlay);
            binLoop?.Stop();
        }
        hovered = null;
        if (tile is null || model.MotionUseOf(tile.Overlay) is not { } use || model.MotionStore.Lottie(use.LottieHash) is not { } lottie) return;
        hovered = tile;
        var id = BinPreviewPrefix + tile.Overlay.Id;
        // The tile's own picture of the overlay, with the animation where the still was.
        var timeline = MotionTimeline.For(lottie, MotionPlayback.Once);
        tile.Preview = OverlayTileViewModel.PreviewScene(MotionTemplateStore.LiveCopy(tile.Overlay, use, id, timeline));
        restartBinPreview = () => model.MotionPlayers.Start(id, model.FilledIn(use.LottieHash, lottie, use.Values), timeline, 320, 180, ShowState.HostClock());
        restartBinPreview();
        // Round again a moment after each play-through, while the mouse stays.
        binLoop ??= new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) =>
        {
            if (hovered is { } now && model.MotionPlayers[BinPreviewPrefix + now.Overlay.Id] is { IsFinished: true }) restartBinPreview?.Invoke();
        }, Dispatcher);
        binLoop.Start();
    }

    /// <summary>The picture id of the bin tile playing now, if any (the self-test).</summary>
    internal string? HoveredMotionPreview => hovered is null ? null : BinPreviewPrefix + hovered.Overlay.Id;

    private void OnMotionTemplateMenuOpening(object sender, ContextMenuEventArgs e)
    {
        DependencyObject? at = e.OriginalSource as DependencyObject;
        while (at is not null && (at as FrameworkElement)?.ContextMenu is null)
            at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at);
        if (at is FrameworkElement element) PrepareMotionTemplateMenu(element);
    }

    /// <summary>Gives a media tile's menu its "Edit motion template…" item, shown only on a full-screen template's video.</summary>
    internal MenuItem? PrepareMotionTemplateMenu(FrameworkElement tileElement)
    {
        if (tileElement is not { ContextMenu: { } menu, DataContext: MediaTileViewModel tile }) return null;
        var item = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == EditMotionTemplateHeader);
        if (item is null)
        {
            item = new MenuItem { Header = EditMotionTemplateHeader, ToolTip = "Change its words, colours and how it plays (it plays live on Windows; a Mac plays its video)" };
            item.Click += (_, _) =>
            {
                if ((menu.PlacementTarget as FrameworkElement ?? tileElement).DataContext is MediaTileViewModel clicked && model.MotionUseOf(clicked.Item) is not null)
                    MotionTemplatesWindow.Open(this, model, editingMedia: clicked.Item);
            };
            var actions = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == "Actions…");
            menu.Items.Insert(actions is null ? 0 : menu.Items.IndexOf(actions) + 1, item);
        }
        item.Visibility = model.MotionUseOf(tile.Item) is null ? Visibility.Collapsed : Visibility.Visible;
        return item;
    }
}
