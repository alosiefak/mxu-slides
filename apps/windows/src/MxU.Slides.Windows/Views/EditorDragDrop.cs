using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.ViewModels;
using Point = System.Windows.Point;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Drag and drop in the main window (the rules are in Core/Editing/Dropping.cs):
/// <list type="bullet">
/// <item><b>Slides</b> dragged in the grid or the filmstrip (Edit mode) take the picked slides to a new place.</item>
/// <item><b>The bins</b> are drag sources: media onto a slide's tile (its background), between tiles (new
/// slides), onto the canvas (a picture box, or a shape's fill), into the service list; music and
/// playlists into the service list; overlays and music onto a timeline's lanes.</item>
/// <item><b>Files from Explorer</b> onto the library (imported), the media bin, the grid or the canvas.</item>
/// </list>
/// Each target shows where the drop will land (<see cref="DropFeedback"/>), and Esc cancels.
/// <para>
/// In Show mode a click on a tile does something live (a slide goes up, a video plays), so a drag
/// only starts there after a long pull (<see cref="ShowModeDistance"/>): a shaky click still fires.
/// </para>
/// </summary>
public static class EditorDragDrop
{
    /// <summary>How far the mouse must move in Show mode before a press on a tile becomes a drag.</summary>
    public const double ShowModeDistance = 18;

    private const double RowGap = 12;

    public static void Attach(MainWindow window, MainViewModel model, ItemsControl grid, ItemsControl filmstrip, SlideCanvasEditor canvas,
        ListBox library, Func<IReadOnlyList<string>, Task> importFiles)
    {
        AttachSources(window, model);
        AttachGrid(grid, model);
        AttachGrid(filmstrip, model);
        AttachCanvas(canvas, model);
        AttachLibrary(library, importFiles);
    }

    // MARK: Drag sources

    private static void AttachSources(Window window, MainViewModel model)
    {
        Point pressedAt = default;
        object? candidate = null;
        FrameworkElement? from = null;
        window.PreviewMouseLeftButtonDown += (_, e) =>
        {
            (candidate, from) = SourceUnder(e.OriginalSource as DependencyObject);
            pressedAt = e.GetPosition(window);
        };
        window.PreviewMouseLeftButtonUp += (_, _) => candidate = null;
        window.PreviewMouseMove += (_, e) =>
        {
            if (candidate is null || from is null || e.LeftButton != MouseButtonState.Pressed) return;
            var moved = e.GetPosition(window) - pressedAt;
            var distance = model.IsEditing ? Math.Max(SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance) : ShowModeDistance;
            if (Math.Abs(moved.X) < distance && Math.Abs(moved.Y) < distance) return;
            var data = DataFor(candidate, model);
            candidate = null;
            if (data is null) return;
            DragDrop.DoDragDrop(from, data, DragDropEffects.Copy | DragDropEffects.Move);
        };
    }

    /// <summary>The bin tile or slide tile a press landed on (not one of its buttons).</summary>
    private static (object? Item, FrameworkElement? Element) SourceUnder(DependencyObject? hit)
    {
        for (var node = hit; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or TextBoxBase or Slider or ScrollBar or Thumb or SlideCanvasEditor or ListBox) return (null, null);
            if (node is FrameworkElement { DataContext: SlideTileViewModel or MediaTileViewModel or OverlayTileViewModel or AudioTileViewModel } element)
                return (element.DataContext, element);
        }
        return (null, null);
    }

    private static DataObject? DataFor(object item, MainViewModel model)
    {
        switch (item)
        {
            case SlideTileViewModel tile when model.IsEditing && model.CurrentPresentation is { } presentation:
                // The tile dragged comes with the other picked slides; a tile that isn't picked goes alone.
                var ids = model.PickedSlideIds.Contains(tile.Slide.Id) ? model.PickedSlideIds : [tile.Slide.Id];
                return Data(SlideDrag.Format, new SlideDrag(presentation.Id, ids).Encode());
            case MediaTileViewModel media:
                return Data(LibraryDrag.Format, new LibraryDrag(typeof(MediaItem), media.Item.Id).Encode());
            case OverlayTileViewModel overlay:
                return Data(LibraryDrag.Format, new LibraryDrag(typeof(Overlay), overlay.Overlay.Id).Encode());
            case AudioTileViewModel { Item: { } song }:
                return Data(LibraryDrag.Format, new LibraryDrag(typeof(AudioItem), song.Id).Encode());
            case AudioTileViewModel { Playlist: { } playlist }:
                return Data(LibraryDrag.Format, new LibraryDrag(typeof(Playlist), playlist.Id).Encode());
            default:
                return null;
        }
    }

    private static DataObject Data(string format, string payload)
    {
        var data = new DataObject();
        data.SetData(format, payload);
        return data;
    }

    /// <summary>What's being dragged, read from a drag's data (null: not ours).</summary>
    public static LibraryDrag? LibraryItem(IDataObject data) =>
        data.GetDataPresent(LibraryDrag.Format) ? LibraryDrag.Decode(data.GetData(LibraryDrag.Format) as string) : null;

    public static SlideDrag? Slides(IDataObject data) =>
        data.GetDataPresent(SlideDrag.Format) ? SlideDrag.Decode(data.GetData(SlideDrag.Format) as string) : null;

    public static string[] Files(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files ? files : [];

    private static bool HasMedia(IDataObject data) =>
        LibraryItem(data)?.Kind == typeof(MediaItem) || Files(data).Any(f => LibraryStore.MediaKindOf(f) is not null);

    // MARK: The slide grid and the filmstrip

    private static void AttachGrid(ItemsControl grid, MainViewModel model)
    {
        grid.AllowDrop = true;
        // The empty space round the tiles takes drops too (a control with no background lets them through).
        grid.Background ??= Brushes.Transparent;
        grid.DragOver += (_, e) => GridDrop(grid, model, e, drop: false);
        grid.Drop += (_, e) => GridDrop(grid, model, e, drop: true);
        grid.DragLeave += (_, _) => DropFeedback.For(grid)?.Clear();
    }

    /// <summary>Each tile's frame in the grid's own coordinates (the tile itself, without its margin).</summary>
    private static Dictionary<int, (Rect Frame, SlideTileViewModel Tile)> Tiles(ItemsControl grid)
    {
        var tiles = new Dictionary<int, (Rect, SlideTileViewModel)>();
        for (var i = 0; i < grid.Items.Count; i++)
        {
            if (grid.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container
                || VisualTreeHelper.GetChildrenCount(container) == 0 || VisualTreeHelper.GetChild(container, 0) is not FrameworkElement tile
                || tile.DataContext is not SlideTileViewModel model || !tile.IsVisible) continue;
            tiles[i] = (tile.TransformToAncestor(grid).TransformBounds(new Rect(tile.RenderSize)), model);
        }
        return tiles;
    }

    private static void GridDrop(ItemsControl grid, MainViewModel model, DragEventArgs e, bool drop)
    {
        e.Handled = true;
        var feedback = DropFeedback.For(grid);
        var tiles = Tiles(grid);
        var point = e.GetPosition(grid);
        var slides = Slides(e.Data);
        var reorder = slides is not null && model.IsEditing && slides.PresentationId == model.CurrentPresentation?.Id;
        if (!reorder && !HasMedia(e.Data) || model.CurrentPresentation is null)
        {
            e.Effects = DragDropEffects.None;
            feedback?.Clear();
            return;
        }
        e.Effects = reorder ? DragDropEffects.Move : DragDropEffects.Copy;

        // On a tile's middle, media is that slide's background; anywhere else it's a new slide between tiles.
        var under = tiles.FirstOrDefault(t => t.Value.Frame.Contains(point));
        SlideDropZone? zone = under.Value.Tile is null ? null
            : SlideDropZones.Tile(new EditorGeometry.Point(point.X - under.Value.Frame.X, point.Y - under.Value.Frame.Y), under.Value.Frame.Width, reorder, insertOnly: false);
        SlideGridDrop.Spot spot;
        if (zone is SlideDropZone.On)
        {
            if (!drop)
            {
                feedback?.Outline(under.Value.Frame);
                return;
            }
            feedback?.Clear();
            var target = under.Value.Tile!.Slide.Id;
            if (LibraryItem(e.Data) is { } item) model.DropMediaOnSlide(target, item.Id);
            else _ = model.DropFilesOnGrid(Files(e.Data), null, target);
            return;
        }
        spot = zone is { } side && under.Value.Tile is not null
            ? new SlideGridDrop.Spot(under.Key, side == SlideDropZone.After)
            : SlideGridDrop.SpotAt(new EditorGeometry.Point(point.X, point.Y), tiles.ToDictionary(t => t.Key, t => Scene(t.Value.Frame)), grid.Items.Count, RowGap);
        if (!drop)
        {
            if (tiles.TryGetValue(spot.Index, out var at))
            {
                var x = spot.After ? at.Frame.Right + 5 : at.Frame.Left - 5;
                feedback?.VerticalLine(x, at.Frame.Top, at.Frame.Bottom);
            }
            else
            {
                feedback?.VerticalLine(6, 0, 80);
            }
            return;
        }
        feedback?.Clear();
        // The slide the drop lands before (null: the end), by the tile at the insertion index.
        var before = tiles.TryGetValue(spot.InsertionIndex, out var next) ? next.Tile.Slide.Id : null;
        if (reorder) model.MoveSlidesTo(slides!.SlideIds, before);
        else if (LibraryItem(e.Data) is { } media && model.Library.Get<MediaItem>(media.Id) is { } mediaItem) model.InsertMediaSlides([mediaItem], before);
        else _ = model.DropFilesOnGrid(Files(e.Data), before, null);
    }

    private static SceneRect Scene(Rect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>Shows the line a drag would show before tile <paramref name="index"/>, or the outline on it (for the self-test's pictures).</summary>
    internal static void PreviewGridDrop(ItemsControl grid, int index, bool onTile)
    {
        if (!Tiles(grid).TryGetValue(index, out var at)) return;
        if (onTile) DropFeedback.For(grid)?.Outline(at.Frame);
        else DropFeedback.For(grid)?.VerticalLine(at.Frame.Left - 5, at.Frame.Top, at.Frame.Bottom);
    }

    /// <summary>Shows the line a drag into the service would show above row <paramref name="index"/> (for the self-test's pictures).</summary>
    internal static void PreviewServiceDrop(ListBox list, int index)
    {
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem row) return;
        var frame = row.TransformToAncestor(list).TransformBounds(new Rect(row.RenderSize));
        DropFeedback.For(list)?.HorizontalLine(frame.Top, 4, list.ActualWidth - 4);
    }

    // MARK: The canvas

    private static void AttachCanvas(SlideCanvasEditor canvas, MainViewModel model)
    {
        canvas.AllowDrop = true;
        canvas.DragOver += (_, e) => CanvasDrop(canvas, model, e, drop: false);
        canvas.Drop += (_, e) => CanvasDrop(canvas, model, e, drop: true);
        canvas.DragLeave += (_, _) => DropFeedback.For(canvas)?.Clear();
    }

    private static void CanvasDrop(SlideCanvasEditor canvas, MainViewModel model, DragEventArgs e, bool drop)
    {
        e.Handled = true;
        var feedback = DropFeedback.For(canvas);
        if (!model.IsEditing || canvas.Slide is null || !HasMedia(e.Data))
        {
            e.Effects = DragDropEffects.None;
            feedback?.Clear();
            return;
        }
        e.Effects = DragDropEffects.Copy;
        var at = canvas.ScenePointAt(e.GetPosition(canvas));
        if (!drop)
        {
            // A shape under the pointer takes the picture as its fill: outline it.
            if (canvas.ObjectAt(at) is { ObjectKind: SlideObjectKind.Shape } shape) feedback?.Outline(canvas.ViewRectOf(canvas.FrameOf(shape)));
            else feedback?.Clear();
            return;
        }
        feedback?.Clear();
        if (LibraryItem(e.Data) is { } item) model.DropMediaOnCanvas(item.Id, at);
        else _ = model.DropFilesOnCanvas(Files(e.Data), at);
    }

    // MARK: The library

    private static void AttachLibrary(ListBox library, Func<IReadOnlyList<string>, Task> importFiles)
    {
        library.AllowDrop = true;
        library.DragOver += (_, e) =>
        {
            var files = Files(e.Data);
            e.Effects = files.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            if (files.Length > 0) DropFeedback.For(library)?.Outline(new Rect(library.RenderSize));
            e.Handled = true;
        };
        library.DragLeave += (_, _) => DropFeedback.For(library)?.Clear();
        library.Drop += (_, e) =>
        {
            DropFeedback.For(library)?.Clear();
            // Songs, PowerPoint, ProPresenter and .mxuslides files: the same importers as Import…
            if (Files(e.Data) is { Length: > 0 } files) _ = importFiles(files);
            e.Handled = true;
        };
    }

    // MARK: The service list (its handlers are in MainWindow.xaml.cs)

    /// <summary>The row a drop lands before (null: the end), and where the line goes.</summary>
    private static (string? BeforeId, double LineY, int Index) ServiceSpot(ListBox list, MainViewModel model, DragEventArgs e)
    {
        var point = e.GetPosition(list);
        var service = model.SelectedService;
        string? beforeId = null;
        double y = 0;
        var found = false;
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem row || !row.IsVisible || row.DataContext is not ServiceItemViewModel item) continue;
            var frame = row.TransformToAncestor(list).TransformBounds(new Rect(row.RenderSize));
            y = frame.Bottom;
            if (found) continue;
            if (point.Y < frame.Top + frame.Height / 2)
            {
                beforeId = item.Item.Id;
                y = frame.Top;
                found = true;
            }
        }
        var index = beforeId is null || service is null ? service?.Items.Count ?? 0 : Math.Max(0, service.Items.FindIndex(i => i.Id == beforeId));
        return (beforeId, y, index);
    }

    public static void ServiceDragOver(ListBox list, MainViewModel model, DragEventArgs e)
    {
        var data = e.Data;
        var takes = data.GetDataPresent("mxu/presentation") || data.GetDataPresent("mxu/service-item")
            || LibraryItem(data) is { } item && item.Kind != typeof(Overlay)
            || Files(data).Any(f => LibraryStore.MediaKindOf(f) is not null);
        e.Effects = !takes ? DragDropEffects.None : data.GetDataPresent("mxu/service-item") ? DragDropEffects.Move : DragDropEffects.Copy;
        var feedback = DropFeedback.For(list);
        if (takes)
        {
            var (_, y, _) = ServiceSpot(list, model, e);
            feedback?.HorizontalLine(y, 4, list.ActualWidth - 4);
        }
        else
        {
            feedback?.Clear();
        }
        e.Handled = true;
    }

    public static void ServiceDragLeave(ListBox list) => DropFeedback.For(list)?.Clear();

    public static void ServiceDrop(ListBox list, MainViewModel model, DragEventArgs e)
    {
        DropFeedback.For(list)?.Clear();
        var (beforeId, _, index) = ServiceSpot(list, model, e);
        if (e.Data.GetData("mxu/service-item") is string itemId) model.MoveServiceItemBefore(itemId, beforeId);
        else if (e.Data.GetData("mxu/presentation") is string presentationId) model.DropIntoService(new LibraryDrag(typeof(Presentation), presentationId), index);
        else if (LibraryItem(e.Data) is { } item) model.DropIntoService(item, index);
        else if (Files(e.Data) is { Length: > 0 } files) _ = model.DropFilesIntoService(files, index);
        e.Handled = true;
    }
}
