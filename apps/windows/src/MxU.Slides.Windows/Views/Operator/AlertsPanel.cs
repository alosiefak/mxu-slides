using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Alerts panel: every saved alert (imported ProPresenter messages too), in folders and in
/// the operator's order. A row's Go Live sends it with its {blanks} filled in; while it's up the
/// button reads Turn Off, and Send Update resends what the blanks say now. Clicking a row opens
/// its editor: name, phrase, where it goes (Confidence, Audience or both), its layer and theme,
/// and Flash or Persist. Port of Sources/Modules/AlertsModule.swift; the rules are Core's
/// (<see cref="AlertEditing"/>, <see cref="AlertLibrary"/>), so this only draws. The Mac reorders
/// by dragging; here a row's right-click menu has Move up, Move down and Folder.
/// </summary>
public sealed class AlertsPanel : UserControl
{
    private readonly StackPanel list = new();
    private readonly TextBox search = new() { Tag = "Search alerts…", Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
    private readonly HashSet<string> expanded = new(StringComparer.Ordinal);
    private MainViewModel? model;
    private Button? searchButton;
    private readonly Button addButton;

    public AlertsPanel()
    {
        searchButton = OperatorKit.IconButton("", "Search the alerts", ToggleSearch);
        var add = addButton = OperatorKit.IconButton("", "Add an alert (write the phrase once, fill the blanks at fire time) or a folder", () => { });
        add.Click += (_, _) => OperatorKit.Open(AddMenu(), add);
        var panel = new StackPanel();
        panel.Children.Add(OperatorKit.Header("ALERTS", searchButton, add));
        panel.Children.Add(search);
        panel.Children.Add(list);
        Content = panel;
        search.TextChanged += (_, _) => Rebuild();
        search.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            search.Text = "";
            ToggleSearch();
            e.Handled = true;
        };
        DataContextChanged += (_, _) => Attach(OperatorKit.Model(this));
    }

    private void Attach(MainViewModel? next)
    {
        if (model is not null) model.AlertsChanged -= Rebuild;
        model = next;
        if (model is not null) model.AlertsChanged += Rebuild;
        Rebuild();
    }

    private void ToggleSearch()
    {
        var showing = search.Visibility == Visibility.Visible;
        search.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
        if (showing) search.Text = "";
        else search.Focus();
    }

    private ContextMenu AddMenu() => OperatorKit.Menu(
    [
        EditorKit.Command("New Alert", () =>
        {
            if (model?.Alerts.CreateNewAlert() is { } id) expanded.Add(id);
        }),
        EditorKit.Separator(),
        EditorKit.Command("New Folder", () => model?.Alerts.AddFolder()),
    ]);

    /// <summary>Opens the + menu as a click would (the self-test pictures it).</summary>
    internal ContextMenu OpenAddMenu()
    {
        var menu = AddMenu();
        OperatorKit.Open(menu, addButton);
        return menu;
    }

    /// <summary>Opens an alert's editor as clicking its row does.</summary>
    internal void Open(string alertId)
    {
        expanded.Add(alertId);
        Rebuild();
    }

    // MARK: The list

    /// <summary>The alert names shown, in order (the self-test reads these).</summary>
    internal List<string> RowNames { get; } = [];

    internal void Rebuild()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Rebuild);
            return;
        }
        list.Children.Clear();
        RowNames.Clear();
        if (model is null) return;
        var board = model.Alerts.AlertBoard();
        var ids = board.AllItemIds();
        if (searchButton is not null) searchButton.Visibility = ids.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (ids.Count == 0) list.Children.Add(EditorKit.Muted(AlertEditing.EmptyHint, 11, 2));

        var query = search.Text.Trim();
        if (search.Visibility == Visibility.Visible && query.Length > 0)
        {
            var found = board.FilteredItemIds(query, id => model.Library.Get<AlertPreset>(id)?.Name);
            foreach (var id in found) AddAlertRow(list, id, board);
            if (found.Count == 0) list.Children.Add(EditorKit.Muted("No alerts match.", 11, 2));
            return;
        }
        foreach (var node in board.Nodes)
        {
            if (board.Folder(node) is { } folder) AddFolder(folder, board);
            else AddAlertRow(list, node, board);
        }
    }

    private void AddFolder(ControlFolder folder, ControlBoard board)
    {
        var collapsed = folder.Collapsed == true;
        var id = folder.Id;
        var row = new DockPanel { Margin = new Thickness(8, 5, 8, 5) };
        row.Children.Add(new TextBlock
        {
            Text = collapsed ? "" : "", FontFamily = EditorKit.IconFont, FontSize = 8, Width = 14,
            VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("FaintText"),
        });
        row.Children.Add(EditorKit.Icon("folder", 11));
        row.Children.Add(new TextBlock
        {
            Text = folder.Name, FontSize = 12, FontWeight = FontWeights.Medium, Margin = new Thickness(6, 0, 6, 0),
            Foreground = EditorKit.Res<Brush>("MutedText"), VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(EditorKit.Faint(folder.ItemIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var card = OperatorKit.RowCard(row);
        card.Cursor = Cursors.Hand;
        card.MouseLeftButtonUp += (_, _) => model?.Alerts.UpdateAlertBoard(b => b.SetFolderCollapsed(id, !collapsed));
        card.ContextMenu = OperatorKit.Menu(
        [
            EditorKit.Command("Rename…", () =>
            {
                if (Window.GetWindow(this) is { } owner && InputDialog.Ask(owner, "Rename folder", "Name", folder.Name) is { } name)
                    model?.Alerts.UpdateAlertBoard(b => b.RenameFolder(id, name));
            }),
            EditorKit.Command("Move up", () => model?.Alerts.UpdateAlertBoard(b => b.StepFolder(id, -1))),
            EditorKit.Command("Move down", () => model?.Alerts.UpdateAlertBoard(b => b.StepFolder(id, +1))),
            EditorKit.Separator(),
            EditorKit.Command("Delete Folder (keeps its alerts)", () => model?.Alerts.UpdateAlertBoard(b => b.RemoveFolder(id))),
        ]);
        list.Children.Add(card);
        if (collapsed) return;
        var members = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        foreach (var alertId in folder.ItemIds) AddAlertRow(members, alertId, board);
        if (folder.ItemIds.Count == 0) members.Children.Add(EditorKit.Muted("Right-click an alert › Folder to file it here.", 11, 4));
        list.Children.Add(members);
    }

    private void AddAlertRow(Panel into, string id, ControlBoard board)
    {
        if (model?.Library.Get<AlertPreset>(id) is not { } preset) return;
        RowNames.Add(preset.Name);
        var isLive = model.LiveAlertId == id;
        var isOpen = expanded.Contains(id);
        var target = AlertEditing.Target(preset);
        var tokens = AlertEditing.FillInTokens(preset.Message, model.TimerNames);

        var body = new StackPanel();
        // The strip: chevron, name, where it goes, and Go Live / Turn Off.
        var strip = new DockPanel { Margin = new Thickness(8, 5, 8, 5), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var live = OperatorKit.SmallButton(AlertEditing.LiveButtonTitle(isLive), AlertEditing.LiveButtonHelp(isLive), () =>
        {
            if (isLive) model.DismissAlert();
            else model.FireAlertPreset(preset);
        }, isLive ? OperatorKit.Orange : null, isLive ? OperatorKit.Orange : null);
        DockPanel.SetDock(live, Dock.Right);
        strip.Children.Add(live);
        strip.Children.Add(new TextBlock
        {
            Text = isOpen ? "" : "", FontFamily = EditorKit.IconFont, FontSize = 8, Width = 14,
            VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("FaintText"),
        });
        strip.Children.Add(new TextBlock
        {
            Text = preset.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Foreground = isLive ? OperatorKit.Orange : EditorKit.Res<Brush>("Text"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170,
        });
        strip.Children.Add(new TextBlock
        {
            Text = AlertEditing.LocationCaption(target), FontSize = 10, Margin = new Thickness(6, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = EditorKit.Res<Brush>("FaintText"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        strip.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInside<Button>(source)) return;
            Toggle(id);
        };
        body.Children.Add(strip);

        // The blanks, typed in before sending (Enter in one sends it), and Send Update while it's up.
        if (tokens.Count > 0 || isLive)
        {
            var blanks = new WrapPanel { Margin = new Thickness(22, 0, 8, 6) };
            var typed = model.AlertBlanks.TryGetValue(id, out var values) ? values : model.AlertBlanks[id] = new(StringComparer.OrdinalIgnoreCase);
            foreach (var token in tokens)
            {
                var field = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 2) };
                field.Children.Add(OperatorKit.FieldLabel(token.ToUpperInvariant()));
                var box = new TextBox { Text = typed.GetValueOrDefault(token, ""), Tag = token, Width = 110, FontSize = 12, Padding = new Thickness(4, 2, 4, 2) };
                box.TextChanged += (_, _) => typed[token] = box.Text;
                box.KeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter) return;
                    model.FireAlertPreset(preset);
                    e.Handled = true;
                };
                field.Children.Add(box);
                blanks.Children.Add(field);
            }
            if (isLive) blanks.Children.Add(OperatorKit.SmallButton("Send Update", AlertEditing.SendUpdateHelp, () => model.FireAlertPreset(preset)));
            body.Children.Add(blanks);
        }
        if (isOpen) body.Children.Add(Editor(preset));

        var card = OperatorKit.RowCard(body, isLive ? OperatorKit.OrangeWash : null);
        card.ContextMenu = RowMenu(id, board, isOpen);
        into.Children.Add(card);
    }

    private static bool IsInside<T>(DependencyObject source) where T : DependencyObject
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is T) return true;
        return false;
    }

    private void Toggle(string id)
    {
        if (!expanded.Remove(id)) expanded.Add(id);
        Rebuild();
    }

    private ContextMenu RowMenu(string id, ControlBoard board, bool isOpen)
    {
        var folders = board.OrderedFolders();
        var current = board.FolderContaining(id)?.Id;
        List<object> folderItems = [OperatorKit.Choice("Not in a folder", current is null, () => model?.Alerts.UpdateAlertBoard(b => b.PutInFolder(id, null)))];
        folderItems.AddRange(folders.Select(f => OperatorKit.Choice(f.Name, current == f.Id, () => model?.Alerts.UpdateAlertBoard(b => b.PutInFolder(id, f.Id)))));
        return OperatorKit.Menu(
        [
            EditorKit.Command(isOpen ? "Collapse" : "Edit…", () => Toggle(id)),
            EditorKit.Separator(),
            EditorKit.Command("Move up", () => model?.Alerts.UpdateAlertBoard(b => b.StepItem(id, -1))),
            EditorKit.Command("Move down", () => model?.Alerts.UpdateAlertBoard(b => b.StepItem(id, +1))),
            OperatorKit.Submenu("Folder", folderItems),
            EditorKit.Separator(),
            EditorKit.Command("Delete", () => Delete(id)),
        ]);
    }

    private void Delete(string id)
    {
        if (model?.Library.Get<AlertPreset>(id) is not { } preset) return;
        if (Window.GetWindow(this) is { } owner
            && MessageBox.Show(owner, $"Move the alert \"{preset.Name}\" to the library's Trash folder?", "Delete alert",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        expanded.Remove(id);
        model.Alerts.DeleteAlertPreset(id);
    }

    // MARK: The inline editor

    private StackPanel Editor(AlertPreset preset)
    {
        var id = preset.Id;
        var editor = new StackPanel { Margin = new Thickness(22, 2, 10, 8) };
        void Update(Action<AlertPreset> change) => model?.Alerts.UpdateAlertPreset(id, change);

        var name = EditorKit.CommitBox(preset.Name, typed =>
        {
            Update(p => AlertEditing.Commit(p, typed, p.Message));
            return model?.Library.Get<AlertPreset>(id)?.Name ?? typed;
        }, "Name");
        editor.Children.Add(Line("NAME", name));

        var phrase = EditorKit.CommitBox(preset.Message, typed =>
        {
            Update(p => AlertEditing.Commit(p, p.Name, typed));
            return model?.Library.Get<AlertPreset>(id)?.Message ?? typed;
        }, "Phrase. {token} makes a fill-in blank");
        phrase.TextWrapping = TextWrapping.Wrap;
        editor.Children.Add(Line("TEXT", phrase));

        var target = AlertEditing.Target(preset);
        var location = OperatorKit.Chip(null, AlertEditing.LocationTitle(target), "Where this alert shows", chip => OperatorKit.Open(OperatorKit.Menu(
            AlertEditing.LocationChoices.Select(choice => (object)OperatorKit.Choice(AlertEditing.LocationTitle(choice), choice == target,
                () => Update(p => AlertEditing.SetTarget(p, choice))))), chip));
        var help = OperatorKit.IconButton("", AlertEditing.LocationHelp, () => { }, 10);
        help.ToolTip = new ToolTip { Content = new TextBlock { Text = AlertEditing.LocationHelp, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 } };
        editor.Children.Add(Line("OUTPUT LOCATION", Row(location, help)));

        if (AlertEditing.ShowsLayerAndTheme(target))
        {
            var assigned = AlertEditing.AssignedLayer(preset);
            var layer = OperatorKit.Chip(null, assigned.DisplayName(), "The audience-side layer this alert draws on: output presets route it like any layer",
                chip => OperatorKit.Open(OperatorKit.Menu(AlertEditing.LayerChoices.Select(choice => (object)OperatorKit.Choice(choice.DisplayName(), choice == assigned,
                    () => Update(p => AlertEditing.SetLayer(p, choice))))), chip));
            var themeName = AlertEditing.ThemeName(preset, themeId => model?.Library.Get<Theme>(themeId)?.Name);
            var theme = OperatorKit.Chip(null, themeName, "Styling only: the phrase shows through the theme's “Alerts” design", chip =>
            {
                List<object> items = [OperatorKit.Choice("Built-in Banner", string.IsNullOrEmpty(preset.ThemeId), () => Update(p => p.ThemeId = null)), EditorKit.Separator()];
                items.AddRange((model?.Library.All<Theme>() ?? []).Select(t => (object)OperatorKit.Choice(t.Name, preset.ThemeId == t.Id, () => Update(p => p.ThemeId = t.Id))));
                OperatorKit.Open(OperatorKit.Menu(items), chip);
            });
            editor.Children.Add(Line("LAYER", Row(layer, OperatorKit.FieldLabel("THEME"), theme)));
        }

        var behavior = OperatorKit.Chip(null, AlertEditing.BehaviorName(preset.Behavior), AlertEditing.BehaviorHelp, chip => OperatorKit.Open(OperatorKit.Menu(
            new[] { AlertBehavior.Flash, AlertBehavior.Persist }.Select(choice => (object)OperatorKit.Choice(AlertEditing.BehaviorName(choice), preset.Behavior == choice,
                () => Update(p => p.Behavior = choice)))), chip));
        var delete = OperatorKit.IconButton("", "Delete alert", () => Delete(id), 11);
        var last = new DockPanel();
        DockPanel.SetDock(delete, Dock.Right);
        last.Children.Add(delete);
        last.Children.Add(Line("BEHAVIOR", behavior, bottom: 0));
        editor.Children.Add(last);
        return editor;
    }

    private static DockPanel Line(string label, UIElement control, double bottom = 6)
    {
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, bottom) };
        var text = OperatorKit.FieldLabel(label, width: 96);
        DockPanel.SetDock(text, Dock.Left);
        line.Children.Add(text);
        line.Children.Add(control);
        return line;
    }

    private static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var item in items) row.Children.Add(item);
        return row;
    }
}
