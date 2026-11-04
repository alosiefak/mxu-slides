using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Library;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// A theme design dropped on a slide tile (dragged from <see cref="ThemeDesignsWindow"/>) gives that
/// slide the design, as Apply Theme does (the Mac's PresentGridView drop). Wired with class handlers
/// on the tiles' borders, so the main window's XAML stays as it is: a tile takes drops once it's
/// shown, and only a theme design's payload is handled here (any other drag passes on to the slide
/// grid's own drag-and-drop).
/// </summary>
public static class ThemeDesignDrop
{
    private static Window? owner;
    private static MainViewModel? model;
    private static bool registered;

    /// <summary>Starts listening (called once, with the main window; EditorMenus.Attach calls it).</summary>
    public static void Attach(Window window, MainViewModel viewModel)
    {
        owner = window;
        model = viewModel;
        if (registered) return;
        registered = true;
        EventManager.RegisterClassHandler(typeof(Border), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnTileLoaded));
        EventManager.RegisterClassHandler(typeof(Border), UIElement.DragOverEvent, new DragEventHandler(OnDragOver));
        EventManager.RegisterClassHandler(typeof(Border), UIElement.DropEvent, new DragEventHandler(OnDrop));
    }

    private static bool IsTile(Border border) => border.DataContext is SlideTileViewModel && ReferenceEquals(Window.GetWindow(border), owner);

    private static void OnTileLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border border && IsTile(border)) border.AllowDrop = true;
    }

    private static string? Payload(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.UnicodeText) ? e.Data.GetData(DataFormats.UnicodeText) as string : null;

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (sender is not Border border || !IsTile(border) || ThemeDesignDrag.Parse(Payload(e)) is null) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: SlideTileViewModel tile } border || !IsTile(border) || model is null) return;
        var payload = Payload(e);
        if (ThemeDesignDrag.Parse(payload) is null) return;
        e.Handled = true;
        if (Drop(model, tile, payload)) e.Effects = DragDropEffects.Copy;
    }

    /// <summary>Gives the tile's slide the dropped design (the self-test calls this with a card's payload).</summary>
    internal static bool Drop(MainViewModel model, SlideTileViewModel tile, string? payload)
    {
        var deck = model.CurrentPresentation?.Slides.Any(s => s.Id == tile.Slide.Id) == true ? model.CurrentPresentation : model.PresentationContaining(tile.Slide.Id);
        return deck is not null && model.DropThemeDesign(deck, tile.Slide.Id, payload);
    }
}
