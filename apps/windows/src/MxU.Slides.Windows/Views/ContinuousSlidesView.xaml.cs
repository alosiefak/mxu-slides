using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The continuous view's code: a click on a slide takes it live from its row, a click on a card's
/// header takes the row live, picking a row in the service scrolls to its card, and the live slide
/// is kept in sight as Next moves on (the Mac's KeyboardFireFollow). The cards are in
/// ViewModels/MainViewModel.Continuous.cs.
/// </summary>
public partial class ContinuousSlidesView : UserControl
{
    private MainViewModel? model;

    public ContinuousSlidesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) model?.RefreshContinuous();
        };
    }

    private void Attach(MainViewModel? next)
    {
        if (model is not null)
        {
            model.ContinuousFocusRequested -= OnFocusRequested;
            model.ContinuousLiveMoved -= OnLiveMoved;
            model.ContinuousCards.CollectionChanged -= OnCardsChanged;
            model.PropertyChanged -= OnModelChanged;
        }
        model = next;
        if (model is null) return;
        model.ContinuousFocusRequested += OnFocusRequested;
        model.ContinuousLiveMoved += OnLiveMoved;
        model.ContinuousCards.CollectionChanged += OnCardsChanged;
        model.PropertyChanged += OnModelChanged;
        OnCardsChanged(null, null);
        ShowOrHide();
    }

    /// <summary>The volunteer layout's copy is always showing; the main window's shows in Show mode when Continuous is picked.</summary>
    public bool AlwaysShown
    {
        get => alwaysShown;
        set
        {
            alwaysShown = value;
            ShowOrHide();
        }
    }

    private bool alwaysShown;

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ShowsContinuous) or nameof(MainViewModel.IsEditing)) ShowOrHide();
    }

    private void ShowOrHide()
    {
        if (model is null) return;
        Visibility = AlwaysShown || model.ShowsContinuous && !model.IsEditing ? Visibility.Visible : Visibility.Collapsed;
        if (IsVisible || Visibility == Visibility.Visible) model.RefreshContinuous();
    }

    private void OnCardsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs? e) =>
        Empty.Visibility = model?.ContinuousCards.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

    private static T? ContextOf<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

    private static ContinuousCardViewModel? CardOf(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { DataContext: ContinuousCardViewModel card }) return card;
        return null;
    }

    private void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        if (model is null || ContextOf<SlideTileViewModel>(sender) is not { } tile || CardOf((DependencyObject)sender) is not { } card) return;
        model.FireContinuous(card, tile);
        // The keys go back to the show (→ moves on from here).
        Keyboard.ClearFocus();
        Window.GetWindow(this)?.Focus();
        e.Handled = true;
    }

    private void CardHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (model is null || ContextOf<ContinuousCardViewModel>(sender) is not { } card) return;
        if (card.IsSlides && card.Tiles.FirstOrDefault() is { } first) model.FireContinuous(card, first);
        else model.FireContinuousRow(card);
        e.Handled = true;
    }

    // MARK: Keeping things in sight

    private ScrollViewer? Scroller => Cards.Template.FindName("Scroller", Cards) as ScrollViewer;

    private VirtualizingStackPanel? Panel => FindChild<VirtualizingStackPanel>(Cards);

    private void OnFocusRequested(string? itemId)
    {
        if (itemId is null || model is null || !IsVisible) return;
        var index = model.ContinuousCards.ToList().FindIndex(c => c.ItemId == itemId);
        if (index >= 0) ScrollToCard(index, top: true);
    }

    private void OnLiveMoved()
    {
        if (model is null || !IsVisible) return;
        var index = model.ContinuousCards.ToList().FindIndex(c => c.Tiles.Any(t => t.IsLive));
        if (index < 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RevealLiveTile(index));
    }

    /// <summary>Brings a card to the top of the scroll (its container is made first: the panel virtualizes).</summary>
    private void ScrollToCard(int index, bool top)
    {
        Panel?.BringIndexIntoViewPublic(index);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (Cards.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container && Scroller is { } scroller)
            {
                var offset = container.TransformToAncestor(scroller).Transform(new Point(0, 0)).Y;
                if (top) scroller.ScrollToVerticalOffset(scroller.VerticalOffset + offset - 4);
            }
        });
    }

    /// <summary>The live slide stays on screen: scrolled to only when it's out of sight, so clicking a visible slide never jumps.</summary>
    private void RevealLiveTile(int cardIndex)
    {
        if (Scroller is not { } scroller) return;
        if (Cards.ItemContainerGenerator.ContainerFromIndex(cardIndex) is not FrameworkElement container)
        {
            Panel?.BringIndexIntoViewPublic(cardIndex);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RevealLiveTile(cardIndex));
            return;
        }
        if (FindTile(container, t => t.IsLive) is not { } tile) return;
        var top = tile.TransformToAncestor(scroller).Transform(new Point(0, 0)).Y;
        if (top < 0 || top + tile.ActualHeight > scroller.ViewportHeight) tile.BringIntoView();
    }

    /// <summary>Scrolls to a slide of a row's card (the arrangement pills' jump in the continuous view).</summary>
    internal void RevealSlide(string? itemId, string? presentationId, int occurrence)
    {
        if (model is null) return;
        var index = model.ContinuousCards.ToList().FindIndex(c => c.ItemId == itemId && c.Presentation?.Id == presentationId);
        if (index < 0) index = model.ContinuousCards.ToList().FindIndex(c => c.Presentation?.Id == presentationId);
        if (index < 0) return;
        Panel?.BringIndexIntoViewPublic(index);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (Cards.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container
                && FindTile(container, t => t.Index == occurrence) is { } tile && Scroller is { } scroller)
            {
                var offset = tile.TransformToAncestor(scroller).Transform(new Point(0, 0)).Y;
                scroller.ScrollToVerticalOffset(scroller.VerticalOffset + offset - 40);
            }
        });
    }

    private static FrameworkElement? FindTile(DependencyObject root, Func<SlideTileViewModel, bool> match)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border { DataContext: SlideTileViewModel tile, Name: "Frame" } frame && match(tile)) return frame;
            if (FindTile(child, match) is { } found) return found;
        }
        return null;
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>The cards' titles as shown (the self-test reads these).</summary>
    internal List<string> CardTitles => model?.ContinuousCards.Select(c => c.Title).ToList() ?? [];

    /// <summary>How many slide tiles are on screen now (the panel makes only the cards in sight).</summary>
    internal int RealizedTiles => CountTiles(Cards);

    private static int CountTiles(DependencyObject root)
    {
        var count = 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border { DataContext: SlideTileViewModel, Name: "Frame" }) count++;
            else count += CountTiles(child);
        }
        return count;
    }

    /// <summary>Scrolls to the bottom (the self-test checks a long service scrolls).</summary>
    internal void ScrollToEnd() => Scroller?.ScrollToEnd();

    internal double ScrollOffset => Scroller?.VerticalOffset ?? 0;
}
