using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// "Slide Actions": the steps that run when a slide goes live (or a media item is fired):
/// put up a background, start a timer, take down an overlay, send MIDI, run a combo… in
/// order, each after its delay. Every change is saved at once, as on the Mac, so the next
/// time the slide fires it does the new thing. Port of SlideActionsSheet (Sources/ActionEditors.swift).
/// <para>
/// The main window opens it with <see cref="Edit"/> (a slide), <see cref="EditMedia"/>
/// (a media item, from the bin or the service) or <see cref="EditServiceItem"/>, and can
/// put <see cref="ActionsMenu"/> in a slide tile's right-click menu.
/// </para>
/// </summary>
public sealed class SlideActionsWindow : Window
{
    private readonly Func<IReadOnlyList<SlideAction>, bool> save;

    /// <summary>The list editor (the self-test drives it directly).</summary>
    internal ActionListEditor Editor { get; }

    internal SlideActionsWindow(Window? owner, MainViewModel model, string title, string help,
        IReadOnlyList<SlideAction> actions, Func<IReadOnlyList<SlideAction>, bool> save)
    {
        this.save = save;
        EditorKit.Dress(this, owner, title, 520, 600);

        Editor = new ActionListEditor(model.ActionChoices, emptyText: "No actions yet. Press + Add action to add one.");
        Editor.Load(actions);
        Editor.Changed += Save;

        var done = EditorKit.Button("Done", Close, accent: true);
        done.IsDefault = true;
        done.IsCancel = true;
        done.MinWidth = 90;
        done.Margin = new Thickness(0);
        var footer = new DockPanel { Margin = new Thickness(16, 8, 16, 14), LastChildFill = false };
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);

        var body = new StackPanel { Margin = new Thickness(16, 14, 16, 0) };
        body.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        body.Children.Add(EditorKit.Muted(help, 12, 12));
        body.Children.Add(Editor);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        // The library may have gained media, overlays or timers while another window had focus.
        Activated += (_, _) => Editor.RefreshChoices();
    }

    private void Save(IReadOnlyList<SlideAction> actions)
    {
        if (!save(actions))
            MessageBox.Show(this, "These actions couldn't be saved: the slide or media item is no longer in the library.", Title);
    }

    // MARK: Opening it

    /// <summary>
    /// Edits a slide's actions and waits until the window is closed. The slide is looked up in
    /// <paramref name="presentation"/> (or, when null, the open presentation, then the library).
    /// Returns false when the slide couldn't be found.
    /// </summary>
    public static bool Edit(Window owner, MainViewModel model, Slide slide, Presentation? presentation = null)
    {
        if (ForSlide(owner, model, slide, presentation) is not { } window) return false;
        window.ShowDialog();
        return true;
    }

    /// <summary>Edits a media item's actions (they run every time it's fired) and waits until the window is closed.</summary>
    public static bool EditMedia(Window owner, MainViewModel model, MediaItem item)
    {
        if (ForMedia(owner, model, item) is not { } window) return false;
        window.ShowDialog();
        return true;
    }

    /// <summary>
    /// Edits the actions of a service item: a media item's own actions. A presentation's actions
    /// belong to its slides, so for one of those the person is told to pick a slide (false is returned).
    /// </summary>
    public static bool EditServiceItem(Window owner, MainViewModel model, ServiceItem item)
    {
        if (item.ItemKind == ServiceItemKind.Media && model.Library.Get<MediaItem>(item.RefId) is { } media)
            return EditMedia(owner, model, media);
        MessageBox.Show(owner, item.ItemKind == ServiceItemKind.Presentation
                ? "A presentation's actions belong to its slides. Select a slide, then choose Slide Actions."
                : "Only slides and media items carry actions.", "Actions");
        return false;
    }

    /// <summary>The window for a slide, not yet shown (null when the slide isn't in any presentation).</summary>
    internal static SlideActionsWindow? ForSlide(Window? owner, MainViewModel model, Slide slide, Presentation? presentation = null)
    {
        var deck = presentation?.Slides.Any(s => s.Id == slide.Id) == true ? presentation : model.PresentationContaining(slide.Id);
        if (deck?.Slides.FirstOrDefault(s => s.Id == slide.Id) is not { } stored) return null;
        var name = string.IsNullOrWhiteSpace(stored.Name) ? "" : stored.Name;
        var title = name.Length == 0 ? "Slide Actions" : $"Slide Actions — {name}";
        var deckId = deck.Id;
        var slideId = stored.Id;
        return new SlideActionsWindow(owner, model, title,
            "These run, in order, each time this slide goes live. A delay waits that many seconds first. "
            + "A Clear step never clears what this slide itself just put up.",
            stored.Actions ?? [], actions => model.SaveSlideActions(deckId, slideId, actions));
    }

    /// <summary>The window for a media item, not yet shown.</summary>
    internal static SlideActionsWindow? ForMedia(Window? owner, MainViewModel model, MediaItem item)
    {
        if (model.Library.Get<MediaItem>(item.Id) is not { } stored) return null;
        var id = stored.Id;
        return new SlideActionsWindow(owner, model, $"Media Actions — {stored.Name}",
            "These run, in order, each time this media item is fired (from the bin, a service or a slide). A delay waits that many seconds first.",
            stored.Actions ?? [], actions => model.SaveMediaActions(id, actions));
    }

    // MARK: The slide's right-click menu

    /// <summary>
    /// The Mac's "Actions" menu for a slide tile's right-click menu: "Actions: 2" › Add Action ›
    /// (the whole menu), each step with Edit… and Remove, and Edit All… / Remove All for two or
    /// more. Adding a step that still needs a choice (Fire Media, a timer) opens the editor.
    /// </summary>
    public static MenuItem ActionsMenu(Window owner, MainViewModel model, Slide slide, Presentation? presentation = null)
    {
        var deck = presentation?.Slides.Any(s => s.Id == slide.Id) == true ? presentation : model.PresentationContaining(slide.Id);
        var stored = deck?.Slides.FirstOrDefault(s => s.Id == slide.Id);
        var actions = stored?.Actions ?? [];
        var style = (Style)EditorKit.Styles["CascadingMenuItem"];
        var menu = new MenuItem { Header = ActionEditing.ActionsMenuTitle(actions.Count), Style = style, IsEnabled = deck is not null };
        if (deck is null || stored is null) return menu;
        var deckId = deck.Id;
        var slideId = stored.Id;

        var add = new MenuItem { Header = "Add Action", Style = style };
        foreach (var entry in EditorKit.MenuEntries(ActionEditing.AddMenu(model.ActionChoices()), action =>
                 {
                     var updated = (stored.Actions ?? []).Append(action).ToList();
                     model.SaveSlideActions(deckId, slideId, updated);
                     if (ActionEditing.NeedsChoice(action)) Edit(owner, model, stored, deck);
                     else model.Notice = $"Added \"{action.Kind.DisplayName()}\" to the slide";
                 }))
            add.Items.Add(entry);
        menu.Items.Add(add);

        if (actions.Count > 0)
        {
            menu.Items.Add(EditorKit.Separator());
            menu.Items.Add(new MenuItem { Header = "On This Slide", Style = style, IsEnabled = false });
            var names = model.ActionLabelNames();
            foreach (var action in actions)
            {
                var id = action.Id;
                var row = new MenuItem { Header = SlideActions.Label(action, names), Style = style };
                row.Items.Add(EditorKit.Command("Edit…", () => Edit(owner, model, stored, deck)));
                row.Items.Add(EditorKit.Command("Remove", () =>
                    model.SaveSlideActions(deckId, slideId, [.. (stored.Actions ?? []).Where(a => a.Id != id)])));
                menu.Items.Add(row);
            }
        }
        if (ActionEditing.OffersEditAll(actions.Count))
        {
            menu.Items.Add(EditorKit.Separator());
            menu.Items.Add(EditorKit.Command("Edit All…", () => Edit(owner, model, stored, deck)));
            menu.Items.Add(EditorKit.Command("Remove All", () => model.SaveSlideActions(deckId, slideId, [])));
        }
        return menu;
    }
}
