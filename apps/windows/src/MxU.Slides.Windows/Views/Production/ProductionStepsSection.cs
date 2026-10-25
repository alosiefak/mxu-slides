using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Automation;
using MxU.Slides.Production.Actions;
using MxU.Slides.Production.Devices;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Production;

/// <summary>
/// "Production (Windows)": the production steps of one owner (a slide, media item, combo,
/// schedule trigger or timeline cue), drawn under its shared steps in every action editor.
/// Each step is a card like a shared step's (its kind at the top, its settings, a delay) with
/// its device's status light and a ▶ to try it now. Every change is saved at once, like the
/// shared steps, but to the library's Windows extras rather than the shared document, so a
/// Mac opening the library never meets a kind of step it doesn't know.
/// <para>
/// What each kind lets you set comes from <see cref="ProductionEditing"/> (in the Production
/// project, with its tests); this only draws it.
/// </para>
/// </summary>
internal sealed class ProductionStepsSection : StackPanel
{
    private readonly MainViewModel model;
    private readonly string ownerId;
    private readonly StackPanel cards = new();
    private readonly TextBlock empty;
    private readonly Button addButton;
    private List<ProductionAction> steps;
    private ProductionContext context;

    private ProductionStepsSection(MainViewModel model, string ownerId)
    {
        this.model = model;
        this.ownerId = ownerId;
        Margin = new Thickness(0, 10, 0, 0);
        steps = model.ProductionSteps(ownerId);
        context = model.Production.Context();
        Children.Add(EditorKit.Section("PRODUCTION (WINDOWS)", 8));
        Children.Add(EditorKit.Muted(
            "Cameras, OBS, vMix and lights, run with the steps above, each after its own delay. They're kept beside the library "
            + "for Windows only: a Mac opening it doesn't see them.", 11, 8));
        Children.Add(cards);
        empty = EditorKit.Muted("No production steps.", 12, 6);
        Children.Add(empty);
        addButton = EditorKit.Button("+ Add production step", () => ShowAddMenu(), "Switch a camera, change an OBS scene, cut in vMix, fire a lighting look…");
        addButton.HorizontalAlignment = HorizontalAlignment.Left;
        Children.Add(addButton);
        Rebuild();

        // The status lights follow the devices (their events come on device threads).
        void OnStatus(ProductionDevice _) => ProductionUi.OnUi(this, UpdateDots);
        Loaded += (_, _) => model.Production.StatusChanged += OnStatus;
        Unloaded += (_, _) => model.Production.StatusChanged -= OnStatus;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) RefreshChoices();
        };
    }

    /// <summary>Adds the section under an action list (its last child) and returns it.</summary>
    public static ProductionStepsSection Attach(ActionListEditor editor, MainViewModel model, string ownerId)
    {
        var section = new ProductionStepsSection(model, ownerId);
        editor.Children.Add(section);
        return section;
    }

    /// <summary>The section under an action list, if it has one (the self-test drives it).</summary>
    public static ProductionStepsSection? Of(ActionListEditor? editor) => editor?.Children.OfType<ProductionStepsSection>().FirstOrDefault();

    public string OwnerId => ownerId;

    /// <summary>The steps as they are now.</summary>
    public IReadOnlyList<ProductionAction> Steps => steps;

    /// <summary>Reads the devices' names, scenes and inputs again, and the library's looks (they may have changed).</summary>
    public void RefreshChoices()
    {
        context = model.Production.Context();
        Rebuild();
    }

    // MARK: Editing the list

    /// <summary>The Add menu (ATEM ›, OBS ›, vMix ›, Lights ›) under the button, as clicking it opens it.</summary>
    internal ContextMenu ShowAddMenu()
    {
        context = model.Production.Context();
        var menu = new ContextMenu();
        var style = (Style)EditorKit.Styles["CascadingMenuItem"];
        foreach (var group in ProductionEditing.AddMenu(context))
        {
            var top = new MenuItem { Header = group.Title, Style = style };
            foreach (var item in group.Items) top.Items.Add(MenuEntry(item, style));
            if (group.Title == "Lights")
            {
                top.Items.Add(EditorKit.Separator());
                top.Items.Add(EditorKit.Command("Lighting looks and cues…", () => LightingWindow.Open(Window.GetWindow(this), model)));
            }
            menu.Items.Add(top);
        }
        EditorKit.Open(menu, addButton);
        return menu;
    }

    private MenuItem MenuEntry(ProductionMenuItem item, Style style)
    {
        var entry = new MenuItem { Header = item.Title, Style = style, IsEnabled = item.Make is not null || item.Children is not null };
        if (item.Children is { } children)
            foreach (var child in children) entry.Items.Add(MenuEntry(child, style));
        else if (item.Make is { } make)
            entry.Click += (_, _) => Append(make());
        return entry;
    }

    public void Append(ProductionAction step)
    {
        steps.Add(step);
        var card = Card(step);
        cards.Children.Add(card);
        UpdateEmpty();
        Commit();
        card.BringIntoView();
    }

    public void Remove(string id)
    {
        if (steps.RemoveAll(s => s.Id == id) == 0) return;
        Rebuild();
        Commit();
    }

    public void Move(string id, int offset)
    {
        var index = steps.FindIndex(s => s.Id == id);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= steps.Count) return;
        (steps[index], steps[target]) = (steps[target], steps[index]);
        Rebuild();
        Commit();
    }

    /// <summary>Changes one step; <paramref name="reshape"/> redraws its card (a change of device or kind alters what shows).</summary>
    public void Edit(string id, Action<ProductionAction> change, bool reshape = false)
    {
        var index = steps.FindIndex(s => s.Id == id);
        if (index < 0) return;
        change(steps[index]);
        Commit();
        if (reshape && index < cards.Children.Count)
        {
            cards.Children.RemoveAt(index);
            cards.Children.Insert(index, Card(steps[index]));
        }
    }

    private void Commit()
    {
        model.SaveProductionSteps(ownerId, [.. steps.Select(s => s.Copy())]);
        Changed?.Invoke();
    }

    /// <summary>The steps were changed and saved (a combo's Run button wakes up when it gains one).</summary>
    public event Action? Changed;

    private void Rebuild()
    {
        cards.Children.Clear();
        foreach (var step in steps) cards.Children.Add(Card(step));
        UpdateEmpty();
    }

    private void UpdateEmpty() => empty.Visibility = steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Recolours each card's status light.</summary>
    private void UpdateDots()
    {
        for (var i = 0; i < cards.Children.Count && i < steps.Count; i++)
        {
            if (cards.Children[i] is not Border { Tag: DotHolder holder }) continue;
            var status = DeviceStatusFor(steps[i]);
            holder.Dot.Fill = ProductionUi.StateBrush(status.State);
            holder.Dot.ToolTip = $"{ProductionUi.StateWord(status.State)}: {status.Detail}";
        }
    }

    private sealed record DotHolder(System.Windows.Shapes.Ellipse Dot);

    /// <summary>The light for a step's device: the one it names, else the first of its kind; lights use the DMX nodes'.</summary>
    private DeviceStatus DeviceStatusFor(ProductionAction step)
    {
        if (step.Kind is not { } kind) return DeviceStatus.Off;
        var devices = model.Production.Devices;
        if (ProductionAction.DeviceFor(kind) is not { } wanted)
        {
            var nodes = devices.Where(d => d.Settings.IsDmx).ToList();
            if (nodes.Count == 0) return new DeviceStatus(DeviceState.Off, "No DMX node is set up on this PC (Settings › Production)");
            return nodes.FirstOrDefault(n => n.Status.State == DeviceState.Problem)?.Status ?? nodes[0].Status;
        }
        var device = devices.FirstOrDefault(d => d.Id == step.DeviceId && d.Kind == wanted) ?? devices.FirstOrDefault(d => d.Kind == wanted);
        return device?.Status ?? new DeviceStatus(DeviceState.Off, $"No {DeviceSettings.KindName(wanted)} is set up and on, on this PC (Settings › Production)");
    }

    // MARK: One step

    private Border Card(ProductionAction step)
    {
        var id = step.Id;
        var body = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(EditorKit.Ghost("", "Try it now", () => TryNow(id)));
        tools.Children.Add(EditorKit.Ghost("", "Move up", () => Move(id, -1)));
        tools.Children.Add(EditorKit.Ghost("", "Move down", () => Move(id, 1)));
        tools.Children.Add(EditorKit.Ghost("", "Remove this step", () => Remove(id)));
        foreach (Button tool in tools.Children) tool.FontFamily = EditorKit.IconFont;
        DockPanel.SetDock(tools, Dock.Right);
        header.Children.Add(tools);
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = ProductionUi.Dot(DeviceStatusFor(step), 8);
        title.Children.Add(dot);
        title.Children.Add(EditorKit.Icon(ProductionUi.GlyphName(step.Kind is { } k ? ProductionAction.DeviceFor(k) : null), 12));
        title.Children.Add(new TextBlock
        {
            Text = ProductionEditing.Header(step), FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0),
            Foreground = EditorKit.Res<Brush>("MutedText"), VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(title);
        body.Children.Add(header);
        if (step.Kind is null)
            body.Children.Add(EditorKit.Muted("A newer MxU Slides made this step; this version skips it.", 11, 6));
        foreach (var field in ProductionEditing.Fields(step)) body.Children.Add(FieldRow(step, field));
        body.Children.Add(DelayRow(step));
        var card = EditorKit.Card(body);
        card.Tag = new DotHolder(dot);
        card.ContextMenu = new ContextMenu();
        card.ContextMenuOpening += (_, _) =>
        {
            card.ContextMenu.Items.Clear();
            card.ContextMenu.Items.Add(EditorKit.Command("Try It Now", () => TryNow(id)));
            card.ContextMenu.Items.Add(EditorKit.Separator());
            card.ContextMenu.Items.Add(EditorKit.Command("Move Up", () => Move(id, -1), steps.FirstOrDefault()?.Id != id));
            card.ContextMenu.Items.Add(EditorKit.Command("Move Down", () => Move(id, 1), steps.LastOrDefault()?.Id != id));
            card.ContextMenu.Items.Add(EditorKit.Separator());
            card.ContextMenu.Items.Add(EditorKit.Command("Remove", () => Remove(id)));
        };
        return card;
    }

    /// <summary>Runs one step now, without its delay, and says in the main window whether it got there.</summary>
    private async void TryNow(string id)
    {
        if (steps.FirstOrDefault(s => s.Id == id) is not { } step) return;
        var label = ProductionEditing.Label(step, context);
        var copy = step.Copy();
        copy.DelaySeconds = null;
        var done = await model.Production.PerformAsync(copy);
        model.Notice = done ? $"Done: {label}" : $"Didn't happen: {label} (see the device's light in Settings › Production)";
    }

    private UIElement FieldRow(ProductionAction step, ProductionField field)
    {
        var id = step.Id;
        switch (field)
        {
            case DeviceField device:
            {
                var choices = new List<EditorChoice>
                {
                    new("", $"The first {ProductionEditing.DeviceWord(device.Kind)}"),
                };
                choices.AddRange(context.Devices.Where(d => d.Kind == device.Kind).Select(d => new EditorChoice(d.Id, d.Name)));
                var picker = EditorKit.Picker(choices, step.DeviceId ?? "", picked => Edit(id, s => s.DeviceId = picked.Length == 0 ? null : picked, reshape: true));
                if (choices.Count > 1) return EditorKit.Row(field.Label, picker);
                var none = new StackPanel();
                none.Children.Add(picker);
                none.Children.Add(EditorKit.Muted($"No {DeviceSettings.KindName(device.Kind)} is set up on this PC yet: add one in Settings › Production.", 11, 0));
                return EditorKit.Row(field.Label, none);
            }
            case SuggestField suggest:
            {
                // Type it, or pick from what the device reported (its inputs, scenes, macros) with the ▾.
                var choices = suggest.Suggestions(context, step);
                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                var known = new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText") };
                // "2 · Wide" offered for source 2: beside the box, just "Wide".
                void ShowKnown(string? value) =>
                    known.Text = choices.FirstOrDefault(c => c.Id == value) is { } match && match.Name != match.Id
                        ? match.Name[(match.Name.IndexOf(" · ", StringComparison.Ordinal) + 3)..]
                        : "";
                TextBox? box = null;
                box = EditorKit.CommitBox(suggest.Get(step) ?? "", typed =>
                {
                    Edit(id, s => suggest.Set(s, typed));
                    var stored = steps.FirstOrDefault(s => s.Id == id) is { } current ? suggest.Get(current) ?? "" : typed;
                    ShowKnown(stored);
                    return stored;
                }, suggest.Hint, 200);
                box.ToolTip = suggest.Hint;
                panel.Children.Add(box);
                var pick = EditorKit.Ghost("", choices.Count == 0 ? "The device hasn't said what it has yet: type it" : "Pick from what the device has", () => { }, 11);
                pick.FontFamily = EditorKit.IconFont;
                pick.IsEnabled = choices.Count > 0;
                pick.Click += (_, _) =>
                {
                    var menu = new ContextMenu();
                    foreach (var choice in choices)
                        menu.Items.Add(EditorKit.Command(choice.Name, () =>
                        {
                            Edit(id, s => suggest.Set(s, choice.Id));
                            box.Text = steps.FirstOrDefault(s => s.Id == id) is { } current ? suggest.Get(current) ?? "" : choice.Id;
                            ShowKnown(box.Text);
                        }));
                    EditorKit.Open(menu, pick);
                };
                panel.Children.Add(pick);
                panel.Children.Add(known);
                ShowKnown(suggest.Get(step));
                return EditorKit.Row(field.Label, panel);
            }
            case ListField list:
            {
                var choices = list.Choices(context).Select(c => new EditorChoice(c.Id, c.Name)).ToList();
                if (choices.Count == 0) return EditorKit.Row(field.Label, EditorKit.Muted("None yet: make some in Lighting looks and cues…", 11, 0));
                var picker = EditorKit.Picker(choices, list.Get(step), picked => Edit(id, s => list.Set(s, picked)));
                return EditorKit.Row(field.Label, picker);
            }
            case WholeNumberField number:
            {
                var box = EditorKit.NumberBox(number.Get(step), value =>
                {
                    var stored = Math.Clamp(value, number.Min, number.Max);
                    Edit(id, s => number.Set(s, stored));
                    return stored;
                }, tooltip: $"{number.Min.ToString(CultureInfo.InvariantCulture)} to {number.Max.ToString(CultureInfo.InvariantCulture)}");
                return EditorKit.Row(field.Label, box);
            }
            case DecimalField value:
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                var box = EditorKit.CommitBox(ProductionEditing.SecondsText(value.Get(step)), typed =>
                {
                    var parsed = typed.Trim().Length == 0 ? null : ProductionEditing.ParseSeconds(typed, value.Min, value.Max);
                    if (typed.Trim().Length == 0 || parsed is not null) Edit(id, s => value.Set(s, parsed));
                    return steps.FirstOrDefault(s => s.Id == id) is { } current ? ProductionEditing.SecondsText(value.Get(current)) : typed;
                }, hint: value.EmptyText, width: 96);
                box.TextAlignment = TextAlignment.Center;
                box.ToolTip = $"Empty: {value.EmptyText}";
                panel.Children.Add(box);
                panel.Children.Add(new TextBlock { Text = value.Unit, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText") });
                return EditorKit.Row(field.Label, panel);
            }
            case SwitchField toggle:
            {
                var options = new List<EditorChoice> { new("on", toggle.OnText), new("off", toggle.OffText) };
                if (toggle.ToggleText is { } either) options.Add(new EditorChoice("toggle", either));
                var current = toggle.Get(step) switch { true => "on", false => "off", null => toggle.ToggleText is null ? "on" : "toggle" };
                var picker = EditorKit.Picker(options, current, picked => Edit(id, s => toggle.Set(s, picked switch { "on" => true, "off" => false, _ => null })), minWidth: 170);
                return EditorKit.Row(field.Label, picker);
            }
            default:
                return new TextBlock();
        }
    }

    private DockPanel DelayRow(ProductionAction step)
    {
        var id = step.Id;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        TextBox? box = null;
        box = EditorKit.CommitBox(ProductionEditing.SecondsText(step.DelaySeconds ?? 0), typed =>
        {
            if (ProductionEditing.ParseSeconds(typed) is { } seconds) Edit(id, s => ProductionEditing.SetDelay(s, seconds));
            return steps.FirstOrDefault(s => s.Id == id) is { } current ? ProductionEditing.SecondsText(current.DelaySeconds ?? 0) : typed;
        }, width: 56);
        box.TextAlignment = TextAlignment.Center;
        box.ToolTip = "Wait this many seconds (up to 3600) after the slide, combo, trigger or cue runs";
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = "seconds", Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText") });
        void Step(int by)
        {
            Edit(id, s => ProductionEditing.SetDelay(s, (s.DelaySeconds ?? 0) + by));
            if (steps.FirstOrDefault(s => s.Id == id) is { } current) box!.Text = ProductionEditing.SecondsText(current.DelaySeconds ?? 0);
        }
        panel.Children.Add(EditorKit.Ghost("−", "One second less", () => Step(-1), 13));
        panel.Children.Add(EditorKit.Ghost("+", "One second more", () => Step(1), 13));
        return EditorKit.Row("Delay", panel, bottom: 2);
    }
}
