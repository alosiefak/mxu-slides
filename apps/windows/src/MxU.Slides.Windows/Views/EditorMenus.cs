using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Phase 7 editors' items in the main window's right-click menus, added as each menu opens
/// (the same class-handler trick as Views/Transitions/TransitionMenus.cs, so the menus written in
/// the main window's XAML stay as they are):
/// <list type="bullet">
/// <item>a media tile: Edit… (the media editor) and Auto Advance…;</item>
/// <item>a slide tile: Auto Advance… and Apply Theme…; for a picture or video slide, its media's
/// Layer, a picture's Scale and a video's end;</item>
/// <item>the library's presentation: Slide Show…, Theme…, Edit Theme… and Theme Designs….</item>
/// </list>
/// </summary>
public static class EditorMenus
{
    private const string Marker = "editors7-menu";
    private static Window? owner;
    private static MainViewModel? model;
    private static bool registered;

    /// <summary>Starts adding the items to <paramref name="window"/>'s menus (called once, as the main window is made).</summary>
    public static void Attach(Window window, MainViewModel viewModel)
    {
        owner = window;
        model = viewModel;
        // A theme design dragged from Theme Designs… onto a slide tile restyles that slide.
        ThemeDesignDrop.Attach(window, viewModel);
        // The Mac's question before a deck's new theme replaces slides' own themes (MainViewModel.Themes.cs).
        viewModel.AskReplaceOwnThemes ??= (presentation, theme, count) => MessageBox.Show(window,
            $"{count} slide{(count == 1 ? "" : "s")} in “{presentation.Name}” {(count == 1 ? "has a theme of its own" : "have a theme of their own")}. "
            + $"Give {(count == 1 ? "it" : "them")} “{theme?.Name ?? "the new theme"}” too?\n\nYes: every slide takes the new theme.\n"
            + $"No: {(count == 1 ? "that slide keeps its" : "those slides keep their")} own theme.",
            "Theme", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };
        if (registered) return;
        registered = true;
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnOpened));
    }

    private static void OnOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || model is null || owner is null || !ReferenceEquals(Window.GetWindow(menu.PlacementTarget ?? menu), owner)) return;
        // A menu written in XAML is the same object every time it opens: take out last time's items first.
        foreach (var old in menu.Items.OfType<FrameworkElement>().Where(i => Equals(i.Tag, Marker)).ToList()) menu.Items.Remove(old);
        var items = menu.PlacementTarget switch
        {
            FrameworkElement { DataContext: MediaTileViewModel media } => MediaItems(model, media.Item),
            FrameworkElement { DataContext: SlideTileViewModel tile } => SlideItems(model, tile.Slide),
            ListBox { Name: "LibraryList", SelectedItem: LibraryEntryViewModel entry } => PresentationItems(model, entry.Presentation),
            _ => [],
        };
        if (items.Count == 0) return;
        var at = InsertAt(menu);
        foreach (var item in items)
        {
            item.Tag = Marker;
            menu.Items.Insert(at++, item);
        }
    }

    /// <summary>After the "Actions" or "Transition" item when there is one (all about how it goes live), else before the last group.</summary>
    private static int InsertAt(ContextMenu menu)
    {
        var items = menu.Items.Cast<object>().ToList();
        var last = items.FindLastIndex(i => i is MenuItem { Header: string header }
            && (header.StartsWith("Actions", StringComparison.Ordinal) || header.StartsWith("Transition", StringComparison.Ordinal)));
        if (last >= 0) return last + 1;
        var lastGroup = items.FindLastIndex(i => i is Separator);
        return lastGroup >= 0 ? lastGroup : items.Count;
    }

    private static MenuItem Command(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    private static List<FrameworkElement> MediaItems(MainViewModel model, MediaItem item) =>
    [
        Command("Edit…", () => MediaEditorWindow.Open(owner, model, item)),
        Command(item.AutoAdvance is null ? "Auto Advance…" : $"Auto Advance: {AutoAdvanceEditing.Value([item.AutoAdvance])}…",
            () => AutoAdvanceWindow.ForMedia(owner, model, item)?.ShowDialog()),
    ];

    internal static List<FrameworkElement> SlideItems(MainViewModel model, Slide slide)
    {
        var deck = model.CurrentPresentation?.Slides.Any(s => s.Id == slide.Id) == true ? model.CurrentPresentation : model.PresentationContaining(slide.Id);
        if (deck is null) return [];
        List<FrameworkElement> items =
        [
            Command($"Auto Advance: {AutoAdvanceEditing.Value([slide.AutoAdvance])}…", () => AutoAdvanceWindow.ForSlides(owner, model, deck, [slide.Id])?.ShowDialog()),
            Command("Apply Theme…", () => ThemePickerWindow.ForSlides(owner!, model, deck, [slide.Id])?.ShowDialog()),
        ];
        items.AddRange(SlideMediaItems(model, deck, slide));
        return items;
    }

    /// <summary>
    /// A picture or video slide's media: its Layer (the Mac's media cue menu), a picture's Scale and a
    /// video's end (which the Mac sets when it makes slides from notes).
    /// </summary>
    private static List<FrameworkElement> SlideMediaItems(MainViewModel model, Presentation deck, Slide slide)
    {
        if (slide.Background is not { } cue || cue.IsLiveInput() || model.Library.Get<MediaItem>(cue.MediaId) is not { } item) return [];
        var custom = model.CustomLayers;
        var chosen = CueMediaEditing.ChosenLayer(cue, item.MediaKind, custom);
        var layer = new MenuItem { Header = "Layer" };
        // Default, then the built-in layers, then the custom ones, as the Mac's picker groups them.
        var choices = CueMediaEditing.LayerChoices(cue, item.MediaKind, custom);
        var builtIn = CueMediaEditing.BuiltInLayers.Count;
        for (var i = 0; i < choices.Count; i++)
        {
            if (i == 1 || i == 1 + builtIn) layer.Items.Add(new Separator());
            var choice = choices[i];
            layer.Items.Add(Check(choice.Name, choice.Id == chosen, () => model.SetSlideMediaLayer(deck, slide, choice.Id)));
        }
        List<FrameworkElement> items = [new Separator(), layer];
        if (item.MediaKind == MediaKind.Image)
        {
            var scale = new MenuItem { Header = $"Scale: {CueMediaEditing.ScaleName(CueMediaEditing.Scale(cue))}" };
            foreach (var (mode, name) in CueMediaEditing.ScaleChoices)
                scale.Items.Add(Check(name, CueMediaEditing.Scale(cue) == mode, () => model.SetSlideMediaScale(deck, slide, mode)));
            items.Add(scale);
        }
        else
        {
            var current = CueMediaEditing.VideoEnd(slide);
            var end = new MenuItem { Header = $"When the video ends: {CueMediaEditing.VideoEndName(current)}" };
            foreach (var (value, name) in CueMediaEditing.VideoEndChoices)
                end.Items.Add(Check(name, current == value, () => model.SetSlideVideoEnd(deck, slide, value)));
            items.Add(end);
        }
        return items;
    }

    private static MenuItem Check(string header, bool on, Action click)
    {
        var item = new MenuItem { Header = header, IsCheckable = false, IsChecked = on };
        item.Click += (_, _) => click();
        return item;
    }

    private static List<FrameworkElement> PresentationItems(MainViewModel model, Presentation presentation) =>
    [
        Command($"Slide Show: {(presentation.AutoAdvance is { } show ? AutoAdvanceEditing.SlideShowText(show) : "Off")}…",
            () => AutoAdvanceWindow.ForPresentation(owner, model, presentation).ShowDialog()),
        Command("Theme…", () => ThemePickerWindow.ForPresentation(owner!, model, presentation).ShowDialog()),
        Command("Edit Theme…", () => ThemeEditorWindow.Open(owner!, model, presentation.ThemeId)),
        Command("Theme Designs…", () => ThemeDesignsWindow.Open(owner!, model, presentation.ThemeId)),
    ];
}
