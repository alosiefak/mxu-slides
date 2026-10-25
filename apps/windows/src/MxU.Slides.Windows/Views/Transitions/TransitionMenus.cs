using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Transitions;

/// <summary>
/// The "Transition" items in three right-click menus of the main window: a slide tile's (this
/// slide, or the whole presentation), a media tile's, and the library list's (the presentation).
/// <para>
/// Rather than each menu listing the item itself, this adds it as the menu opens: WPF lets one
/// handler hear every <see cref="ContextMenu"/> opening (a class handler), and the element the menu
/// opened on says what it's for (its DataContext). So all of the transition menus live here, in
/// one file, and each shows what's chosen now ("Transition: Push ← 0.8 s").
/// </para>
/// </summary>
public static class TransitionMenus
{
    private const string Marker = "transition-menu";
    private static Window? owner;
    private static MainViewModel? model;
    private static bool registered;

    /// <summary>Starts adding the items, for <paramref name="window"/>'s menus (called once, as the main window is made).</summary>
    public static void Attach(Window window, MainViewModel viewModel)
    {
        owner = window;
        model = viewModel;
        if (registered) return;
        registered = true;
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnOpened));
    }

    private static void OnOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || model is null || !ReferenceEquals(Window.GetWindow(menu.PlacementTarget ?? menu), owner)) return;
        // A menu written in XAML is the same object every time it opens: take out last time's item first.
        foreach (var old in menu.Items.OfType<MenuItem>().Where(i => Equals(i.Tag, Marker)).ToList()) menu.Items.Remove(old);
        var item = menu.PlacementTarget switch
        {
            FrameworkElement { DataContext: SlideTileViewModel tile } => SlideMenu(model, tile.Slide),
            FrameworkElement { DataContext: MediaTileViewModel media } => Command($"Transition: {Words(model.MediaTransitionChoice(media.Item))}…",
                () => TransitionPickerWindow.ForMedia(owner, model, media.Item)?.ShowDialog()),
            ListBox { Name: "LibraryList", SelectedItem: LibraryEntryViewModel entry } => PresentationCommand(model, entry.Presentation),
            _ => null,
        };
        if (item is null) return;
        item.Tag = Marker;
        menu.Items.Insert(InsertAt(menu), item);
    }

    /// <summary>After the "Actions" item when there is one (they're both about what happens when it goes live), else before the last group.</summary>
    private static int InsertAt(ContextMenu menu)
    {
        var items = menu.Items.Cast<object>().ToList();
        var actions = items.FindIndex(i => i is MenuItem { Header: string header } && header.StartsWith("Actions", StringComparison.Ordinal));
        if (actions >= 0) return actions + 1;
        var lastGroup = items.FindLastIndex(i => i is Separator);
        return lastGroup >= 0 ? lastGroup : items.Count;
    }

    /// <summary>"Transition" › "This slide: …" and "Whole presentation: …".</summary>
    private static MenuItem? SlideMenu(MainViewModel model, Slide slide)
    {
        var deck = model.CurrentPresentation?.Slides.Any(s => s.Id == slide.Id) == true ? model.CurrentPresentation : model.PresentationContaining(slide.Id);
        if (deck is null) return null;
        var style = (Style)EditorKit.Styles["CascadingMenuItem"];
        var own = model.SlideTransitionChoice(slide);
        var menu = new MenuItem { Header = $"Transition: {(own is null ? "Default" : WindowsTransitions.Describe(own))}", Style = style };
        var thisSlide = Command($"This Slide: {(own is null ? "None of Its Own" : WindowsTransitions.Describe(own))}…",
            () => TransitionPickerWindow.ForSlide(owner, model, slide, deck)?.ShowDialog());
        thisSlide.Style = style;
        menu.Items.Add(thisSlide);
        var whole = PresentationCommand(model, deck, "Whole Presentation");
        whole.Style = style;
        menu.Items.Add(whole);
        return menu;
    }

    private static MenuItem PresentationCommand(MainViewModel model, Presentation presentation, string label = "Transition") =>
        Command($"{label}: {Words(model.PresentationTransitionChoice(presentation))}…",
            () => TransitionPickerWindow.ForPresentation(owner, model, presentation).ShowDialog());

    private static string Words(WindowsTransition? choice) => choice is null ? "Default" : WindowsTransitions.Describe(choice);

    private static MenuItem Command(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }
}
