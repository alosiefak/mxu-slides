using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Edits a list of actions: add (from the Add Action menu), remove, move up and down, and
/// change each step's settings and delay. One control serves every place actions live: an
/// action combo, a slide, a media item and a schedule trigger. Port of the Mac's
/// ActionListEditor and ActionParameterFields (Sources/ActionEditors.swift).
/// <para>
/// Which settings a step has, their defaults and their limits come from
/// <see cref="ActionEditing.Fields"/> in Core, so they match the Mac. This control only draws
/// them. It edits its own copy of the list and raises <see cref="Changed"/> after every edit;
/// the window that owns it decides where to save.
/// </para>
/// </summary>
public sealed class ActionListEditor : StackPanel
{
    private readonly Func<ActionChoices> loadChoices;
    private ActionChoices choices;
    private readonly StackPanel cards = new();
    private readonly TextBlock empty;
    private readonly Button addButton;
    private List<SlideAction> actions = [];

    /// <param name="loadChoices">Gathers what the pickers can offer (the app's library, timers, inputs …); asked again when the menu opens.</param>
    /// <param name="editingComboId">The combo being edited, if any: it is never offered to its own Fire Combo step.</param>
    /// <param name="emptyText">What shows while there are no steps.</param>
    public ActionListEditor(Func<ActionChoices> loadChoices, string? editingComboId = null, string? emptyText = null)
    {
        this.loadChoices = loadChoices;
        choices = loadChoices();
        EditingComboId = editingComboId;
        empty = EditorKit.Muted(emptyText ?? "No actions yet.", 12, 8);
        addButton = EditorKit.Button("+ Add action", () => ShowAddMenu(), "Add a step: media, a message, an overlay, a timer, MIDI…");
        addButton.HorizontalAlignment = HorizontalAlignment.Left;
        Children.Add(cards);
        Children.Add(empty);
        Children.Add(addButton);
    }

    /// <summary>The combo this list belongs to (null for slides, media and triggers).</summary>
    public string? EditingComboId { get; }

    /// <summary>The list after an edit (a copy, safe to keep and save).</summary>
    public event Action<IReadOnlyList<SlideAction>>? Changed;

    /// <summary>The steps as they are now.</summary>
    public IReadOnlyList<SlideAction> Actions => actions;

    /// <summary>Shows a list to edit (copied, so the caller's documents are only changed through <see cref="Changed"/>).</summary>
    public void Load(IEnumerable<SlideAction> list)
    {
        actions = [.. list.Select(MxuJson.Clone)];
        Rebuild();
    }

    /// <summary>Reads the pickers' choices again (after the library changed) and redraws.</summary>
    public void RefreshChoices()
    {
        choices = loadChoices();
        Rebuild();
    }

    /// <summary>The Add Action menu for this list, as the Core builds it.</summary>
    public IReadOnlyList<ActionMenuItem> AddMenu() => ActionEditing.AddMenu(choices, EditingComboId);

    /// <summary>Opens the Add Action menu under the + Add action button (what clicking it does) and returns it.</summary>
    internal ContextMenu ShowAddMenu()
    {
        choices = loadChoices();
        var menu = EditorKit.ActionMenu(AddMenu(), action => Append(action, askForTarget: true));
        EditorKit.Open(menu, addButton);
        return menu;
    }

    // MARK: Editing the list

    /// <summary>
    /// Adds a step at the end. With <paramref name="askForTarget"/>, a Fire Media or Music File
    /// step opens its search window straight away (the Mac's "…" items open a picker too).
    /// </summary>
    public void Append(SlideAction action, bool askForTarget = false)
    {
        actions.Add(action);
        var card = Card(action);
        cards.Children.Add(card);
        UpdateEmpty();
        Commit();
        card.BringIntoView();
        if (askForTarget && action.Kind is SlideActionKind.FireMedia or SlideActionKind.FireAudio
            && ActionEditing.Fields(action).OfType<PickField>().FirstOrDefault(f => f.Searchable) is { } field)
            PickSearchable(action.Id, field);
    }

    public void Remove(string actionId)
    {
        if (!ActionCombos.Remove(actions, actionId)) return;
        Rebuild();
        Commit();
    }

    /// <summary>Moves a step one place (-1 up, +1 down); nothing happens at the ends.</summary>
    public void Move(string actionId, int offset)
    {
        if (!ActionCombos.Move(actions, actionId, offset)) return;
        Rebuild();
        Commit();
    }

    /// <summary>Changes one step. <paramref name="reshape"/> redraws its card (when the change alters which settings show).</summary>
    public void Edit(string actionId, Action<SlideAction> change, bool reshape = false)
    {
        var index = actions.FindIndex(a => a.Id == actionId);
        if (index < 0) return;
        change(actions[index]);
        Commit();
        if (reshape && index < cards.Children.Count)
        {
            cards.Children.RemoveAt(index);
            cards.Children.Insert(index, Card(actions[index]));
        }
    }

    private void Commit() => Changed?.Invoke([.. actions.Select(MxuJson.Clone)]);

    private void Rebuild()
    {
        cards.Children.Clear();
        foreach (var action in actions) cards.Children.Add(Card(action));
        UpdateEmpty();
    }

    private void UpdateEmpty() => empty.Visibility = actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // MARK: One step

    private Border Card(SlideAction action)
    {
        var id = action.Id;
        var body = new StackPanel();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(EditorKit.Ghost("\uE70E", "Move up", () => Move(id, -1)));
        tools.Children.Add(EditorKit.Ghost("\uE70D", "Move down", () => Move(id, 1)));
        tools.Children.Add(EditorKit.Ghost("\uE711", "Remove this action", () => Remove(id)));
        foreach (Button tool in tools.Children) tool.FontFamily = EditorKit.IconFont;
        DockPanel.SetDock(tools, Dock.Right);
        header.Children.Add(tools);
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(EditorKit.Icon(SlideActions.IconName(action, ComboIcon), 12));
        title.Children.Add(new TextBlock
        {
            Text = ActionEditing.Header(action), FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0),
            Foreground = EditorKit.Res<Brush>("MutedText"), VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(title);
        body.Children.Add(header);

        foreach (var field in ActionEditing.Fields(action))
            body.Children.Add(FieldRow(action, field));
        body.Children.Add(DelayRow(action));

        var card = EditorKit.Card(body);
        card.ContextMenu = new ContextMenu
        {
            Items =
            {
                EditorKit.Command("Move Up", () => Move(id, -1), actions.FirstOrDefault()?.Id != id),
                EditorKit.Command("Move Down", () => Move(id, 1), actions.LastOrDefault()?.Id != id),
                new Separator(),
                EditorKit.Command("Remove", () => Remove(id)),
            },
        };
        return card;
    }

    private string? ComboIcon(string comboId) => choices.ComboIcon?.Invoke(comboId);

    private UIElement FieldRow(SlideAction action, ActionField field)
    {
        var id = action.Id;
        switch (field)
        {
            case PickField { Searchable: true } pick:
            {
                var title = ActionEditing.CurrentTitle(pick, action, choices);
                var button = new Button
                {
                    Content = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis },
                    HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 200, MaxWidth = 340, HorizontalContentAlignment = HorizontalAlignment.Left,
                    ToolTip = $"{title}: click to choose from the library (you can search)",
                };
                button.Click += (_, _) => PickSearchable(id, pick);
                return EditorKit.Row(field.Label, button);
            }
            case PickField pick:
            {
                var entries = ActionEditing.Entries(pick, action, choices, EditingComboId);
                var picker = EditorKit.Picker(entries, pick.Get(action), picked => Edit(id, a => pick.Apply(a, picked)));
                return EditorKit.Row(field.Label, picker);
            }
            case NumberField number:
            {
                var display = number.Display;
                if (field.Label == "Slide" && action.PresentationId is { } deck && choices.SlideCount?.Invoke(deck) is { } count)
                    display = shown => $"of {count.ToString(CultureInfo.InvariantCulture)}";
                var box = EditorKit.NumberBox(number.Get(action), value =>
                {
                    var stored = value;
                    Edit(id, a =>
                    {
                        number.Apply(a, value);
                        stored = number.Get(a);
                    });
                    return stored;
                }, display, tooltip: $"{number.Min.ToString(CultureInfo.InvariantCulture)} to {number.Max.ToString(CultureInfo.InvariantCulture)}");
                return EditorKit.Row(field.Label, box);
            }
            case OptionField option:
            {
                var picker = EditorKit.Picker(option.Options, option.Get(action),
                    picked => Edit(id, a => option.Set(a, picked), reshape: option.Reshapes), minWidth: 170);
                return EditorKit.Row(field.Label, picker);
            }
            case ToggleField toggle:
            {
                var box = new CheckBox { IsChecked = toggle.Get(action), VerticalAlignment = VerticalAlignment.Center };
                box.Click += (_, _) => Edit(id, a => toggle.Set(a, box.IsChecked == true));
                return EditorKit.Row(field.Label, box);
            }
            case NoteField note:
                return EditorKit.Muted(note.Text, 11, 6);
            default:
                return new TextBlock();
        }
    }

    /// <summary>Opens the search window for a media, presentation or music picker and stores what's chosen.</summary>
    private void PickSearchable(string actionId, PickField field)
    {
        if (actions.FirstOrDefault(a => a.Id == actionId) is not { } action) return;
        var title = field.Source switch
        {
            ActionPickSource.Media => "Choose media",
            ActionPickSource.Presentation => "Choose a presentation",
            _ => "Choose music",
        };
        var entries = ActionEditing.Entries(field, action, choices, EditingComboId);
        if (LibraryPickerWindow.Pick(Window.GetWindow(this), title, entries, field.Get(action)) is not { } picked) return;
        // A different presentation starts again at its first slide (as the Mac's chip does).
        var resetSlide = field.Source == ActionPickSource.Presentation && picked != action.PresentationId;
        Edit(actionId, a =>
        {
            field.Apply(a, picked);
            if (resetSlide) a.SlideIndex = null;
        }, reshape: true);
    }

    private DockPanel DelayRow(SlideAction action)
    {
        var id = action.Id;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        TextBox? box = null;
        box = EditorKit.CommitBox(ActionEditing.DelayText(action), typed =>
        {
            if (ActionEditing.ParseDelay(typed) is { } seconds) Edit(id, a => ActionCombos.SetDelay(a, seconds));
            return actions.FirstOrDefault(a => a.Id == id) is { } current ? ActionEditing.DelayText(current) : typed;
        }, width: 56);
        box.TextAlignment = TextAlignment.Center;
        box.ToolTip = "Wait this many seconds (up to 3600) before this step runs";
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = "seconds", Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText") });
        void Step(int by)
        {
            Edit(id, a => ActionEditing.StepDelay(a, by));
            if (actions.FirstOrDefault(a => a.Id == id) is { } current) box!.Text = ActionEditing.DelayText(current);
        }
        panel.Children.Add(EditorKit.Ghost("−", "One second less", () => Step(-1), 13));
        panel.Children.Add(EditorKit.Ghost("+", "One second more", () => Step(1), 13));
        return EditorKit.Row("Delay", panel, bottom: 2);
    }
}
