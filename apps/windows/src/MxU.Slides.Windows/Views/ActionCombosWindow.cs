using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Action Combos editor: every combo on its board (in folders, in your order) on the
/// left, and the selected combo on the right: its name, colour and icon, whether it runs
/// when the app starts, and its actions. Ctrl+click or Shift+click picks several combos to
/// colour, move or delete together. Port of Sources/Modules/ActionCombosModule.swift and
/// ControlBoardFolders.swift (the Mac shows this as a module in the sidebar; Windows opens a window).
/// <para>
/// Every change is saved at once through <see cref="AutomationLibrary"/>, which keeps the
/// board (folders and order) in step with the combos.
/// </para>
/// </summary>
public sealed class ActionCombosWindow : Window
{
    /// <summary>What the copy on the clipboard is filed under, so only MxU Slides reads it back.</summary>
    private const string ClipboardFormat = "MxU Slides action combo";

    private static ActionCombosWindow? openWindow;

    /// <summary>The Folder picker's last entry, which makes a folder (no real folder id looks like this).</summary>
    private const string NewFolderChoice = "+new-folder";

    private sealed record Row(string Id, bool IsFolder);

    private readonly MainViewModel model;
    private readonly ListBox list = new() { SelectionMode = SelectionMode.Extended, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox search = new() { Tag = "Search combos…" };
    private readonly TextBlock boardNote = EditorKit.Muted("", 11, 0);
    private readonly ContentControl detail = new();
    private string? shownSelection;
    private bool refreshing;
    private int ownSaves;

    /// <summary>The selected combo's action list (null when no single combo is selected).</summary>
    internal ActionListEditor? Editor { get; private set; }

    /// <summary>Opens the editor (or brings the open one to the front).</summary>
    public static ActionCombosWindow Open(Window owner, MainViewModel model)
    {
        if (openWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        var window = new ActionCombosWindow(owner, model);
        window.Show();
        return window;
    }

    private ActionCombosWindow(Window owner, MainViewModel model)
    {
        this.model = model;
        openWindow = this;
        EditorKit.Dress(this, owner, "Action Combos", 1020, 660);

        var title = new TextBlock { Text = "Action Combos", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var newCombo = EditorKit.Button("+ Combo", () => NewCombo(), "Make a new, empty combo");
        var newFolder = EditorKit.Button("+ Folder", NewFolder, "Make a folder to group combos");
        newFolder.Margin = new Thickness(0);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { newCombo, newFolder } };
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(title);

        search.TextChanged += (_, _) => Refresh(rebuildDetail: false);
        list.SelectionChanged += (_, _) =>
        {
            if (!refreshing) ShowDetail(force: false);
        };
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) DeleteSelected();
        };

        var left = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(search, Dock.Top);
        DockPanel.SetDock(boardNote, Dock.Bottom);
        left.Children.Add(header);
        left.Children.Add(search);
        left.Children.Add(boardNote);
        left.Children.Add(list);
        var leftPanel = new Border { Style = EditorKit.Res<Style>("Panel"), Child = left, Margin = new Thickness(0, 0, 10, 0) };

        var rightPanel = new Border
        {
            Style = EditorKit.Res<Style>("Panel"), Padding = new Thickness(0),
            Child = new ScrollViewer { Content = new Border { Padding = new Thickness(16, 14, 16, 14), Child = detail }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };

        var done = EditorKit.Button("Done", Close, accent: true);
        done.IsCancel = true;
        done.MinWidth = 90;
        done.Margin = new Thickness(0);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);
        footer.Children.Add(EditorKit.Muted(
            "Run a combo from the Combos tab, from here, with MIDI “Run Action Combo” (velocity n runs the nth combo by name), or from the Local API.", 11, 0));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340), MinWidth = 260 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(rightPanel, 1);
        grid.Children.Add(leftPanel);
        grid.Children.Add(rightPanel);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(grid);
        // Padding (not a margin) so the window's own edge is the content's edge.
        Content = new Border { Padding = new Thickness(12), Child = root };

        model.Library.Changed += OnLibraryChanged;
        Closed += (_, _) =>
        {
            model.Library.Changed -= OnLibraryChanged;
            if (openWindow == this) openWindow = null;
        };
        Activated += (_, _) => Editor?.RefreshChoices();
        Refresh(rebuildDetail: true);
        if (list.Items.Count > 0) list.SelectedIndex = list.Items.Cast<ListBoxItem>().ToList().FindIndex(i => i.Tag is Row { IsFolder: false });
    }

    // MARK: Keeping up with the library

    private void OnLibraryChanged(Type type, string id)
    {
        if (type != typeof(ActionCombo) && type != typeof(ControlBoard)) return;
        // Our own saves refresh the list themselves; anything else (an import, the Local API) redraws everything.
        var ours = ownSaves > 0 && Dispatcher.CheckAccess();
        if (ours) return;
        Dispatcher.BeginInvoke(() => Refresh(rebuildDetail: true));
    }

    /// <summary>Saves something from this window, then redraws the list (but not the editor being typed in).</summary>
    private void Save(Action save, bool rebuildDetail = false)
    {
        ownSaves++;
        try { save(); }
        catch (Exception ex)
        {
            Log.Error("Action combos: a change couldn't be saved", ex);
            MessageBox.Show(this, $"That change couldn't be saved: {ex.Message}", Title);
        }
        finally { ownSaves--; }
        Refresh(rebuildDetail);
    }

    private ActionCombo? Combo(string id) => model.Library.Get<ActionCombo>(id);

    // MARK: The board (left)

    private List<Row> SelectedRows() => [.. list.SelectedItems.Cast<ListBoxItem>().Select(i => (Row)i.Tag)];

    private List<string> SelectedComboIds() => [.. SelectedRows().Where(r => !r.IsFolder).Select(r => r.Id)];

    /// <summary>Redraws the board, keeping the selection. The editor on the right is redrawn only when asked or when the selection changed.</summary>
    private void Refresh(bool rebuildDetail)
    {
        var keep = SelectedRows().ToHashSet();
        refreshing = true;
        try
        {
            list.Items.Clear();
            var board = model.Automation.ComboBoard();
            var query = search.Text.Trim();
            if (query.Length > 0)
            {
                var ids = board.FilteredItemIds(query, id => Combo(id)?.Name);
                foreach (var id in ids) AddComboRow(id, indent: false);
                boardNote.Text = ids.Count == 0 ? "No combos match." : "";
            }
            else
            {
                foreach (var node in board.Nodes)
                {
                    if (board.Folder(node) is { } folder)
                    {
                        AddFolderRow(folder);
                        if (folder.Collapsed != true) foreach (var id in folder.ItemIds) AddComboRow(id, indent: true);
                    }
                    else
                    {
                        AddComboRow(node, indent: false);
                    }
                }
                boardNote.Text = board.AllItemIds().Count == 0 ? "Chains of actions: one click runs them in order. Press + Combo to make one." : "";
            }
            foreach (ListBoxItem item in list.Items)
                if (keep.Contains((Row)item.Tag)) list.SelectedItems.Add(item);
        }
        finally
        {
            refreshing = false;
        }
        ShowDetail(force: rebuildDetail);
    }

    private void AddFolderRow(ControlFolder folder)
    {
        var collapsed = folder.Collapsed == true;
        var id = folder.Id;
        var chevron = EditorKit.Ghost(collapsed ? "\uE76C" : "\uE70D", collapsed ? "Show its combos" : "Hide its combos",
            () => Save(() => model.Automation.UpdateComboBoard(b => b.SetFolderCollapsed(id, !collapsed))), 10);
        chevron.FontFamily = EditorKit.IconFont;
        var row = new DockPanel();
        row.Children.Add(chevron);
        row.Children.Add(EditorKit.Icon("folder", 12));
        row.Children.Add(new TextBlock
        {
            Text = folder.Name, FontWeight = FontWeights.SemiBold, Foreground = EditorKit.Res<Brush>("MutedText"),
            Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        row.Children.Add(EditorKit.Faint(folder.ItemIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var item = new ListBoxItem { Content = row, Tag = new Row(id, true), Padding = new Thickness(4, 3, 8, 3) };
        item.ContextMenu = new ContextMenu
        {
            Items =
            {
                EditorKit.Command("Rename…", () => SelectOnly(new Row(id, true), focusName: true)),
                EditorKit.Command("Delete Folder", () => DeleteFolder(id)),
            },
        };
        list.Items.Add(item);
    }

    private void AddComboRow(string id, bool indent)
    {
        if (Combo(id) is not { } combo) return;
        var run = new Button
        {
            Content = "Run", Padding = new Thickness(10, 0, 10, 0), MinHeight = 22, FontSize = 11, IsEnabled = combo.Actions.Count > 0,
            ToolTip = EditorKit.RunTooltip(combo.Actions.Count),
        };
        run.Click += (_, _) => Run(id);
        var row = new DockPanel { Margin = new Thickness(indent ? 18 : 0, 0, 0, 0) };
        DockPanel.SetDock(run, Dock.Right);
        row.Children.Add(run);
        row.Children.Add(EditorKit.Icon(ActionCombos.Icon(combo), 14, EditorKit.Tint(combo.ColorHex)));
        var text = new StackPanel { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = combo.Name, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(EditorKit.Faint(ActionCombos.StepCaption(combo) + (combo.RunOnStartup == true ? " · runs at start-up" : "")));
        row.Children.Add(text);
        var item = new ListBoxItem { Content = row, Tag = new Row(id, false), Padding = new Thickness(6, 4, 6, 4) };
        item.ContextMenu = new ContextMenu
        {
            Items =
            {
                EditorKit.Command("Run", () => Run(id), combo.Actions.Count > 0),
                new Separator(),
                EditorKit.Command("Duplicate", () => Duplicate(id)),
                EditorKit.Command("Copy", () => Copy(id)),
                EditorKit.Command("Paste", Paste, CanPaste()),
                new Separator(),
                EditorKit.Command("Delete", () => DeleteCombos([id])),
            },
        };
        list.Items.Add(item);
    }

    private void SelectOnly(Row row, bool focusName = false)
    {
        list.SelectedItems.Clear();
        if (list.Items.Cast<ListBoxItem>().FirstOrDefault(i => (Row)i.Tag == row) is { } item)
        {
            list.SelectedItem = item;
            list.ScrollIntoView(item);
        }
        ShowDetail(force: true);
        if (focusName && nameBox is { } box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    /// <summary>Selects a combo or folder by id (the self-test uses this).</summary>
    internal void Select(string id, bool isFolder = false) => SelectOnly(new Row(id, isFolder));

    // MARK: The editor (right)

    private TextBox? nameBox;

    private void ShowDetail(bool force)
    {
        var rows = SelectedRows();
        var key = string.Join("|", rows.Select(r => (r.IsFolder ? "f:" : "c:") + r.Id));
        if (!force && key == shownSelection) return;
        shownSelection = key;
        nameBox = null;
        Editor = null;
        var combos = rows.Where(r => !r.IsFolder).Select(r => r.Id).ToList();
        detail.Content = rows switch
        {
            [] => Placeholder(),
            [{ IsFolder: true } folder] => FolderDetail(folder.Id),
            _ when combos.Count == 1 => ComboDetail(combos[0]),
            _ when combos.Count > 1 => BulkDetail(combos),
            _ => Placeholder(),
        };
    }

    private StackPanel Placeholder()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Action combos", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(EditorKit.Muted("A combo is a list of actions that one click runs in order: put up a background, show a message, "
            + "start a countdown, switch the output preset, send MIDI to the lights… Select a combo on the left, or press + Combo to make one.", 12));
        return panel;
    }

    private UIElement ComboDetail(string id)
    {
        if (Combo(id) is not { } combo) return Placeholder();
        var panel = new StackPanel();

        nameBox = EditorKit.CommitBox(combo.Name, typed =>
        {
            var renamed = typed;
            Save(() => model.Automation.UpdateActionCombo(id, c =>
            {
                ActionCombos.Rename(c, typed);
                renamed = c.Name;
            }));
            return Combo(id)?.Name ?? renamed;
        }, "Combo name");
        nameBox.FontSize = 16;
        var run = EditorKit.Button("▶ Run", () => Run(id), EditorKit.RunTooltip(combo.Actions.Count), accent: true);
        run.Margin = new Thickness(10, 0, 0, 0);
        run.IsEnabled = combo.Actions.Count > 0;
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(run, Dock.Right);
        top.Children.Add(run);
        top.Children.Add(nameBox);
        panel.Children.Add(top);

        panel.Children.Add(EditorKit.Row("Colour", ColourRow([id])));
        panel.Children.Add(EditorKit.Row("Icon", IconRow([id])));
        var startup = new CheckBox
        {
            Content = "Run when MxU Slides starts", IsChecked = combo.RunOnStartup == true,
            ToolTip = "Runs once, a moment after the app opens (the Mac's \"Trigger on Startup\")",
        };
        startup.Click += (_, _) => Save(() => model.Automation.UpdateActionCombo(id, c => ActionCombos.SetRunOnStartup(c, startup.IsChecked == true)));
        panel.Children.Add(EditorKit.Row("", startup));
        panel.Children.Add(EditorKit.Row("Folder", PlaceRow([id], single: true)));

        var tools = new WrapPanel { Margin = new Thickness(96, 2, 0, 0) };
        tools.Children.Add(EditorKit.Button("Duplicate", () => Duplicate(id), "A copy with \" Copy\" on its name"));
        tools.Children.Add(EditorKit.Button("Copy", () => Copy(id), "Copy this combo, to paste here or in another library"));
        var paste = EditorKit.Button("Paste", Paste, "Paste a copied combo as a new one");
        paste.IsEnabled = CanPaste();
        tools.Children.Add(paste);
        tools.Children.Add(EditorKit.Button("Cut", () =>
        {
            if (Copy(id)) DeleteCombos([id], ask: false);
        }, "Copy this combo, then delete it"));
        tools.Children.Add(EditorKit.Button("Delete", () => DeleteCombos([id])));
        panel.Children.Add(tools);

        panel.Children.Add(EditorKit.Section("ACTIONS", 18));
        panel.Children.Add(EditorKit.Muted("They run from the top, each after its delay. A combo can run another combo (up to four deep), but never itself.", 11, 8));
        var editor = new ActionListEditor(model.ActionChoices, editingComboId: id, emptyText: "No actions yet. Press + Add action.");
        editor.Load(combo.Actions);
        editor.Changed += actions =>
        {
            Save(() => model.Automation.UpdateActionCombo(id, c => c.Actions = [.. actions]));
            run.IsEnabled = actions.Count > 0;
            run.ToolTip = EditorKit.RunTooltip(actions.Count);
        };
        Editor = editor;
        panel.Children.Add(editor);
        return panel;
    }

    private UIElement BulkDetail(List<string> ids)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"{ids.Count} Combos", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12),
        });
        panel.Children.Add(EditorKit.Row("Colour", ColourRow(ids)));
        panel.Children.Add(EditorKit.Row("Icon", IconRow(ids)));
        panel.Children.Add(EditorKit.Row("Folder", PlaceRow(ids, single: false)));
        var delete = EditorKit.Button($"Delete {ids.Count} Combos", () => DeleteCombos(ids));
        delete.Margin = new Thickness(96, 8, 0, 0);
        delete.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(delete);
        return panel;
    }

    private UIElement FolderDetail(string folderId)
    {
        if (model.Automation.ComboBoard().Folder(folderId) is not { } folder) return Placeholder();
        var panel = new StackPanel();
        nameBox = EditorKit.CommitBox(folder.Name, typed =>
        {
            Save(() => model.Automation.UpdateComboBoard(b => b.RenameFolder(folderId, typed)));
            return model.Automation.ComboBoard().Folder(folderId)?.Name ?? typed;
        }, "Folder name");
        nameBox.FontSize = 16;
        nameBox.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(nameBox);
        var moves = new StackPanel { Orientation = Orientation.Horizontal };
        moves.Children.Add(EditorKit.Button("Move up", () => Save(() => model.Automation.UpdateComboBoard(b => b.StepFolder(folderId, -1)))));
        moves.Children.Add(EditorKit.Button("Move down", () => Save(() => model.Automation.UpdateComboBoard(b => b.StepFolder(folderId, 1)))));
        panel.Children.Add(EditorKit.Row("Place", moves));
        var collapsed = new CheckBox { Content = "Hide its combos in the list", IsChecked = folder.Collapsed == true };
        collapsed.Click += (_, _) => Save(() => model.Automation.UpdateComboBoard(b => b.SetFolderCollapsed(folderId, collapsed.IsChecked == true)));
        panel.Children.Add(EditorKit.Row("", collapsed));
        panel.Children.Add(EditorKit.Muted($"{folder.ItemIds.Count} combo{(folder.ItemIds.Count == 1 ? "" : "s")}. To put a combo in, select it and choose this folder. Deleting a folder keeps its combos.", 11, 8));
        var delete = EditorKit.Button("Delete Folder", () => DeleteFolder(folderId), "The combos go back to the top level");
        delete.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(delete);
        return panel;
    }

    // MARK: Look and place

    private UIElement ColourRow(List<string> ids)
    {
        var combos = ids.Select(Combo).OfType<ActionCombo>().ToList();
        var row = new WrapPanel();
        void Add(string? hex, string name)
        {
            var current = combos.Count > 0 && combos.All(c => c.ColorHex == hex);
            var swatch = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, ToolTip = name,
                Background = EditorKit.Tint(hex) ?? Brushes.Transparent,
                BorderBrush = current ? EditorKit.Res<Brush>("Text") : EditorKit.Res<Brush>("Border"), BorderThickness = new Thickness(current ? 2 : 1),
            };
            if (hex is null)
                swatch.Child = new System.Windows.Shapes.Line { X1 = 6, Y1 = 18, X2 = 18, Y2 = 6, Stroke = EditorKit.Res<Brush>("MutedText"), StrokeThickness = 1.5 };
            swatch.MouseLeftButtonUp += (_, _) => Save(() =>
            {
                foreach (var id in ids) model.Automation.UpdateActionCombo(id, c => c.ColorHex = hex);
            }, rebuildDetail: true);
            row.Children.Add(swatch);
        }
        Add(null, "None");
        foreach (var (name, hex) in ActionCombos.Palette) Add(hex, name);
        return row;
    }

    private UIElement IconRow(List<string> ids)
    {
        var combos = ids.Select(Combo).OfType<ActionCombo>().ToList();
        var row = new WrapPanel();
        foreach (var (glyph, name) in ActionCombos.Icons)
        {
            var button = new ToggleButton
            {
                Content = new TextBlock { Text = EditorKit.Glyph(glyph), FontFamily = EditorKit.IconFont, FontSize = 14 },
                ToolTip = name, Width = 34, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4),
                IsChecked = combos.Count > 0 && combos.All(c => ActionCombos.Icon(c) == glyph),
            };
            button.Click += (_, _) => Save(() =>
            {
                foreach (var id in ids) model.Automation.UpdateActionCombo(id, c => c.IconName = ActionCombos.StoredIcon(glyph));
            }, rebuildDetail: true);
            row.Children.Add(button);
        }
        return row;
    }

    private UIElement PlaceRow(List<string> ids, bool single)
    {
        var board = model.Automation.ComboBoard();
        var choices = new List<EditorChoice> { new("", "Not in a folder") };
        choices.AddRange(board.OrderedFolders().Select(f => new EditorChoice(f.Id, f.Name)));
        choices.Add(new EditorChoice(NewFolderChoice, "New folder…"));
        var folders = ids.Select(id => board.FolderContaining(id)?.Id ?? "").Distinct().ToList();
        var picker = EditorKit.Picker(choices, folders.Count == 1 ? folders[0] : null, picked =>
        {
            Save(() => model.Automation.UpdateComboBoard(b =>
            {
                var target = picked switch
                {
                    "" => null,
                    NewFolderChoice => b.AddFolder("New Folder"),
                    _ => picked,
                };
                foreach (var id in ids) b.PutInFolder(id, target);
            }), rebuildDetail: true);
        }, minWidth: 180);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(picker);
        if (single)
        {
            var id = ids[0];
            var up = EditorKit.Button("Move up", () => Save(() => model.Automation.UpdateComboBoard(b => b.StepItem(id, -1))));
            up.Margin = new Thickness(8, 0, 6, 0);
            row.Children.Add(up);
            row.Children.Add(EditorKit.Button("Move down", () => Save(() => model.Automation.UpdateComboBoard(b => b.StepItem(id, 1)))));
        }
        return row;
    }

    // MARK: Commands

    /// <summary>Makes a combo, selects it and puts the cursor in its name. Returns its id (null if it couldn't be made).</summary>
    internal string? NewCombo(string name = "New Combo")
    {
        string? id = null;
        Save(() => id = model.Automation.CreateActionCombo(name));
        if (id is null) return null;
        search.Text = "";
        SelectOnly(new Row(id, false), focusName: true);
        return id;
    }

    private void NewFolder()
    {
        string? id = null;
        Save(() => model.Automation.UpdateComboBoard(b => id = b.AddFolder("New Folder")));
        if (id is not null) SelectOnly(new Row(id, true), focusName: true);
    }

    /// <summary>Runs a combo now, as the Combos tab does.</summary>
    internal void Run(string id)
    {
        if (Combo(id) is not { } combo) return;
        model.RunActionCombo(id);
        model.Notice = $"Ran \"{combo.Name}\"";
    }

    private void Duplicate(string id)
    {
        string? copy = null;
        Save(() => copy = model.Automation.DuplicateActionCombo(id));
        if (copy is not null) SelectOnly(new Row(copy, false));
    }

    private bool Copy(string id)
    {
        if (Combo(id) is not { } combo) return false;
        try
        {
            var data = new DataObject();
            data.SetData(ClipboardFormat, System.Text.Encoding.UTF8.GetString(MxuJson.SerializeToUtf8(combo)));
            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (ExternalException ex)
        {
            MessageBox.Show(this, $"The clipboard is busy; try again. ({ex.Message})", Title);
            return false;
        }
    }

    private static bool CanPaste()
    {
        try { return Clipboard.ContainsData(ClipboardFormat); }
        catch (ExternalException) { return false; }
    }

    private void Paste()
    {
        ActionCombo? copied = null;
        try
        {
            if (Clipboard.GetData(ClipboardFormat) is string json) copied = MxuJson.Deserialize<ActionCombo>(System.Text.Encoding.UTF8.GetBytes(json));
        }
        catch (Exception ex) when (ex is ExternalException or System.Text.Json.JsonException)
        {
            Log.Warn($"Action combos: couldn't paste: {ex.Message}");
        }
        if (copied is null) return;
        string? id = null;
        Save(() => id = model.Automation.PasteActionCombo(copied));
        if (id is not null) SelectOnly(new Row(id, false));
    }

    private void DeleteSelected()
    {
        var combos = SelectedComboIds();
        if (combos.Count > 0) DeleteCombos(combos);
        else if (SelectedRows() is [{ IsFolder: true } folder]) DeleteFolder(folder.Id);
    }

    private void DeleteCombos(List<string> ids, bool ask = true)
    {
        var names = ids.Select(Combo).OfType<ActionCombo>().Select(c => c.Name).ToList();
        if (names.Count == 0) return;
        var what = names.Count == 1 ? $"the combo \"{names[0]}\"" : $"{names.Count} combos";
        if (ask && MessageBox.Show(this, $"Delete {what}? It goes to the library's Trash folder.", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Save(() =>
        {
            foreach (var id in ids) model.Automation.DeleteActionCombo(id);
        }, rebuildDetail: true);
    }

    private void DeleteFolder(string folderId)
    {
        Save(() => model.Automation.UpdateComboBoard(b => b.RemoveFolder(folderId)), rebuildDetail: true);
    }

    /// <summary>What the board shows, row by row (for the self-test's checks).</summary>
    internal IReadOnlyList<string> RowTexts() =>
        [.. list.Items.Cast<ListBoxItem>().Select(item => string.Join(" ", Texts(item)))];

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        if (root is TextBlock { Text.Length: > 0 } text && text.FontFamily != EditorKit.IconFont) yield return text.Text;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var found in Texts(child)) yield return found;
    }
}
