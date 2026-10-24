using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Timers panel: every timer in folders and in the operator's order, each with its live
/// readout (coloured by its warnings), a progress bar, start/pause and reset. Clicking a timer
/// opens its editor: name, length (or the time it counts to, or a count up's limit) and its
/// warning levels and colours. The + menu adds a countdown of a quick length, a custom one, a
/// countdown to a time of day, a count up or a folder. Port of Sources/Modules/TimersModule.swift;
/// the rules are Core's (<see cref="TimerPanel"/>, <see cref="TimerController"/>, <see cref="TimerBoard"/>).
/// The Mac reorders by dragging; here a row's right-click menu has Move up, Move down and Folder.
/// </summary>
public sealed class TimersPanel : UserControl
{
    private readonly StackPanel list = new();
    private readonly TextBox search = new() { Tag = "Search timers…", Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
    private readonly Button searchButton;
    private readonly HashSet<string> expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> editingFolders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimerEdit> edits = new(StringComparer.Ordinal);
    // Each row's readout updater: the digits, colour, bar and play/pause change every tick without redrawing the panel.
    private readonly List<Action<DateTimeOffset>> tickers = [];
    private MainViewModel? model;
    private string? shownSignature;

    private readonly Button addButton;

    public TimersPanel()
    {
        searchButton = OperatorKit.IconButton("", "Search the timers", ToggleSearch);
        var add = addButton = OperatorKit.IconButton("", "Add a timer: it shows on the confidence layout", () => { });
        add.Click += (_, _) => OperatorKit.Open(AddMenu(add), add);
        var panel = new StackPanel();
        panel.Children.Add(OperatorKit.Header("TIMERS", searchButton, add));
        panel.Children.Add(search);
        panel.Children.Add(list);
        Content = panel;
        search.TextChanged += (_, _) => Rebuild();
        DataContextChanged += (_, _) => Attach(OperatorKit.Model(this));
    }

    private void Attach(MainViewModel? next)
    {
        if (model is not null)
        {
            model.Timers.Changed -= OnTimersChanged;
            model.TimersTicked -= Tick;
        }
        model = next;
        if (model is not null)
        {
            model.Timers.Changed += OnTimersChanged;
            model.TimersTicked += Tick;
        }
        Rebuild();
    }

    private void ToggleSearch()
    {
        var showing = search.Visibility == Visibility.Visible;
        search.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
        if (showing) search.Text = "";
        else search.Focus();
    }

    // Start, pause and reset only move the readouts; anything else (a name, a length, a folder) redraws.
    private void OnTimersChanged()
    {
        if (Signature() == shownSignature) Tick();
        else Rebuild();
    }

    private string Signature()
    {
        if (model is null) return "";
        var timers = string.Join(";", model.Timers.Timers.Select(t =>
            $"{t.Id}|{t.Name}|{t.Mode}|{t.DurationSeconds}|{t.TargetTime?.ToUnixTimeSeconds()}|{string.Join(",", t.Warnings.Select(w => w.Id))}"));
        return model.Timers.Board.ToJson() + timers;
    }

    private void Tick()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Tick);
            return;
        }
        var now = DateTimeOffset.Now;
        foreach (var ticker in tickers) ticker(now);
    }

    // MARK: The add menu

    private ContextMenu AddMenu(FrameworkElement anchor)
    {
        List<object> items = [new MenuItem { Header = "COUNTDOWN", IsEnabled = false, Style = (Style)EditorKit.Styles["CascadingMenuItem"], FontSize = 10 }];
        items.AddRange(TimerPanel.QuickLengths.Select(minutes => (object)EditorKit.Command($"{minutes} minutes", () => model?.Timers.AddCountdown(seconds: minutes * 60))));
        items.Add(EditorKit.Command("Custom…", () =>
        {
            if (AskLength(Window.GetWindow(this), "Countdown length", 0, 10, 0) is { } seconds) model?.Timers.AddCountdown(seconds: seconds);
        }));
        items.Add(EditorKit.Separator());
        items.Add(EditorKit.Command("Countdown to Time…", () =>
        {
            var start = TimerPanel.CountdownToTimeDefault(DateTimeOffset.Now, TimeZoneInfo.Local);
            if (AskWallTime(Window.GetWindow(this), "Countdown to", start) is { } wall) model?.Timers.AddCountdownToTime(wall.Hour24, wall.Minute);
        }));
        items.Add(EditorKit.Command("Count Up Timer", () => model?.Timers.AddCountUp()));
        items.Add(EditorKit.Separator());
        items.Add(EditorKit.Command("New Folder", () => model?.Timers.AddFolder()));
        return OperatorKit.Menu(items);
    }

    /// <summary>Opens the + menu as a click would (the self-test pictures it and picks from it).</summary>
    internal ContextMenu OpenAddMenu()
    {
        var menu = AddMenu(addButton);
        OperatorKit.Open(menu, addButton);
        return menu;
    }

    /// <summary>Opens a timer's editor as clicking its row does.</summary>
    internal void Open(string timerId)
    {
        expanded.Add(timerId);
        Rebuild();
    }

    // MARK: The list

    /// <summary>The timer names shown, in order (the self-test reads these).</summary>
    internal List<string> RowNames { get; } = [];

    internal void Rebuild()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Rebuild);
            return;
        }
        list.Children.Clear();
        tickers.Clear();
        RowNames.Clear();
        shownSignature = Signature();
        if (model is null) return;
        var timers = model.Timers;
        searchButton.Visibility = timers.Timers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (timers.Timers.Count == 0) list.Children.Add(EditorKit.Muted("Timers show on the confidence layout.", 11, 2));
        var board = timers.Board;
        var query = search.Text.Trim();
        if (search.Visibility == Visibility.Visible && query.Length > 0)
        {
            var ids = board.FilteredTimerIds(query, id => timers.Snapshot(id)?.Name);
            foreach (var id in ids) AddTimerRow(list, id, board);
            if (ids.Count == 0) list.Children.Add(EditorKit.Muted("No timers match.", 11, 2));
        }
        else
        {
            foreach (var node in board.Nodes)
            {
                if (node.Folder is { } folder) AddFolder(folder, board);
                else AddTimerRow(list, node.TimerId!, board);
            }
        }
        Tick();
    }

    private void AddFolder(TimerFolder folder, TimerBoard board)
    {
        if (model is null) return;
        var id = folder.Id;
        var editable = TimerPanel.IsEditableFolder(id);
        var members = model.Timers.Snapshots(folder.TimerIds);
        var readout = Readout(15);
        var row = new DockPanel { Margin = new Thickness(8, 5, 8, 5), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        DockPanel.SetDock(readout, Dock.Right);
        row.Children.Add(readout);
        row.Children.Add(Chevron(!folder.Collapsed));
        row.Children.Add(EditorKit.Icon("folder", 11));
        var name = new TextBlock { Text = folder.Name, FontSize = 12, FontWeight = FontWeights.Medium, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(name);
        row.Children.Add(EditorKit.Faint(folder.TimerIds.Count.ToString(CultureInfo.InvariantCulture)));
        var body = new StackPanel();
        body.Children.Add(row);
        if (editingFolders.Contains(id) && editable) body.Children.Add(FolderEditor(folder));
        var card = OperatorKit.RowCard(body);
        row.MouseLeftButtonUp += (_, _) => model.Timers.SetFolderCollapsed(id, !folder.Collapsed);
        if (editable)
        {
            card.ContextMenu = OperatorKit.Menu(
            [
                EditorKit.Command(editingFolders.Contains(id) ? "Done Editing" : "Edit…", () =>
                {
                    if (!editingFolders.Remove(id)) editingFolders.Add(id);
                    Rebuild();
                }),
                EditorKit.Command("Move up", () => model.Timers.StepFolder(id, -1)),
                EditorKit.Command("Move down", () => model.Timers.StepFolder(id, +1)),
                EditorKit.Separator(),
                EditorKit.Command("Delete Folder (keeps its timers)", () => model.Timers.RemoveFolder(id)),
            ]);
        }
        tickers.Add(now =>
        {
            var (text, tint) = TimerPanel.FolderReadout(model.Timers.Snapshots(folder.TimerIds), now);
            readout.Text = text ?? "";
            readout.Foreground = MainViewModel.TimerBrush(tint);
            name.Foreground = members.Any(t => t.IsLive) ? EditorKit.Res<Brush>("Text") : EditorKit.Res<Brush>("MutedText");
            card.Background = new SolidColorBrush(model.Timers.Snapshots(folder.TimerIds).Any(t => t.IsLive) ? OperatorKit.GreenWash : Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));
        });
        list.Children.Add(card);
        if (folder.Collapsed) return;
        var inside = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        foreach (var timerId in folder.TimerIds) AddTimerRow(inside, timerId, board);
        if (folder.TimerIds.Count == 0) inside.Children.Add(EditorKit.Muted("Right-click a timer › Folder to file it here.", 11, 4));
        list.Children.Add(inside);
    }

    private StackPanel FolderEditor(TimerFolder folder)
    {
        var editor = new StackPanel { Margin = new Thickness(22, 0, 10, 8) };
        var name = new TextBox { Text = folder.Name, MinWidth = 140 };
        void Apply()
        {
            if (name.Text != folder.Name) model?.Timers.RenameFolder(folder.Id, name.Text);
            editingFolders.Remove(folder.Id);
            Rebuild();
        }
        name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Apply();
            e.Handled = true;
        };
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var apply = OperatorKit.SmallButton("Apply", "Apply (or press Enter)", Apply);
        apply.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(apply, Dock.Right);
        line.Children.Add(apply);
        var label = OperatorKit.FieldLabel("NAME", 40);
        DockPanel.SetDock(label, Dock.Left);
        line.Children.Add(label);
        line.Children.Add(name);
        editor.Children.Add(line);
        var foot = new DockPanel();
        var delete = OperatorKit.IconButton("", "Delete folder: its timers return to the top level", () => model?.Timers.RemoveFolder(folder.Id), 11);
        DockPanel.SetDock(delete, Dock.Right);
        foot.Children.Add(delete);
        foot.Children.Add(EditorKit.Faint("Deleting a folder keeps its timers."));
        editor.Children.Add(foot);
        return editor;
    }

    private void AddTimerRow(Panel into, string id, TimerBoard board)
    {
        if (model?.Timers.Snapshot(id) is not { } timer) return;
        RowNames.Add(timer.Name);
        var timers = model.Timers;
        var locked = TimerController.IsPermanent(id);
        var isOpen = expanded.Contains(id);

        var readout = Readout(18);
        var strip = new DockPanel { Margin = new Thickness(8, 4, 6, 2), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button? play = null;
        if (TimerPanel.ShowsPlayPause(timer))
        {
            play = OperatorKit.IconButton("", "Start", () => timers.Toggle(id), 10);
            buttons.Children.Add(play);
        }
        if (TimerPanel.ShowsReset(timer)) buttons.Children.Add(OperatorKit.IconButton("", TimerPanel.ResetTooltip(timer), () => timers.Reset(id), 10));
        DockPanel.SetDock(buttons, Dock.Right);
        strip.Children.Add(buttons);
        strip.Children.Add(Chevron(isOpen));
        strip.Children.Add(readout);
        strip.Children.Add(new TextBlock
        {
            Text = timer.Name, FontSize = 11, Margin = new Thickness(8, 3, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = EditorKit.Res<Brush>("FaintText"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        strip.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInside<Button>(source)) return;
            // Closing the editor forgets what was typed but not applied; it reopens on the timer as it is.
            if (expanded.Remove(id)) edits.Remove(id);
            else expanded.Add(id);
            Rebuild();
        };

        // The progress bar: two columns sized done : left.
        var done = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        var left = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        var fill = new Border { CornerRadius = new CornerRadius(1.5), MinWidth = 3 };
        var bar = new Grid { Height = 3, Margin = new Thickness(8, 0, 8, 5) };
        bar.ColumnDefinitions.Add(done);
        bar.ColumnDefinitions.Add(left);
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)) });
        Grid.SetColumnSpan(bar.Children[0], 2);
        bar.Children.Add(fill);

        var body = new StackPanel();
        body.Children.Add(strip);
        body.Children.Add(bar);
        if (isOpen)
        {
            if (TimerPanel.PermanentNote(id) is { } note) body.Children.Add(EditorKit.Muted(note, 11, 8).WithMargin(new Thickness(22, 0, 10, 8)));
            else body.Children.Add(Editor(timer));
        }
        var card = OperatorKit.RowCard(body);
        if (!locked) card.ContextMenu = RowMenu(timer, board, isOpen);
        into.Children.Add(card);

        tickers.Add(now =>
        {
            if (timers.Snapshot(id) is not { } live) return;
            var tint = TimerPanel.RowTint(live, now);
            var brush = MainViewModel.TimerBrush(tint);
            readout.Text = TimerPanel.RowText(live, now);
            readout.Foreground = brush;
            if (live.Progress(now) is { } progress)
            {
                bar.Visibility = Visibility.Visible;
                done.Width = new GridLength(progress, GridUnitType.Star);
                left.Width = new GridLength(1 - progress, GridUnitType.Star);
                fill.Background = brush;
                fill.Opacity = live.IsLive ? 0.9 : 0.45;
            }
            else
            {
                bar.Visibility = Visibility.Collapsed;
            }
            if (play is not null)
            {
                ((TextBlock)play.Content).Text = live.IsRunning ? "" : "";
                play.Foreground = live.IsRunning ? OperatorKit.Green : EditorKit.Res<Brush>("MutedText");
                play.ToolTip = live.IsRunning ? "Pause" : "Start";
            }
            card.Background = new SolidColorBrush(live.IsLive && !(locked && live.IsIdle) ? OperatorKit.GreenWash : Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));
        });
    }

    private ContextMenu RowMenu(TimerSnapshot timer, TimerBoard board, bool isOpen)
    {
        var id = timer.Id;
        var timers = model!.Timers;
        List<object> items = [EditorKit.Command(isOpen ? "Collapse" : "Edit…", () =>
        {
            if (!expanded.Remove(id)) expanded.Add(id);
            Rebuild();
        })];
        if (timer.Mode == TimerMode.Countdown)
        {
            var current = (int)(timer.DurationSeconds / 60);
            items.Add(OperatorKit.Submenu("Duration", TimerPanel.DurationChoices(current)
                .Select(minutes => (object)OperatorKit.Choice($"{minutes} minutes", minutes == current && timer.DurationSeconds % 60 == 0,
                    () => timers.SetDuration(id, minutes * 60)))));
        }
        items.Add(EditorKit.Separator());
        items.Add(EditorKit.Command("Move up", () => timers.StepTimer(id, -1)));
        items.Add(EditorKit.Command("Move down", () => timers.StepTimer(id, +1)));
        var currentFolder = board.FolderContaining(id)?.Id;
        List<object> folders = [OperatorKit.Choice("Not in a folder", currentFolder is null, () => timers.PutInFolder(id, null))];
        folders.AddRange(board.Nodes.Select(n => n.Folder).OfType<TimerFolder>().Where(f => TimerPanel.IsEditableFolder(f.Id))
            .Select(f => (object)OperatorKit.Choice(f.Name, currentFolder == f.Id, () => timers.PutInFolder(id, f.Id))));
        items.Add(OperatorKit.Submenu("Folder", folders));
        items.Add(EditorKit.Separator());
        items.Add(EditorKit.Command("Delete", () => timers.Remove(id)));
        return OperatorKit.Menu(items);
    }

    // MARK: The inline editor (TimerInlineEditor)

    private StackPanel Editor(TimerSnapshot timer)
    {
        var id = timer.Id;
        var timers = model!.Timers;
        if (!edits.TryGetValue(id, out var edit)) edit = edits[id] = TimerPanel.Seed(timer, timers.Clock.LocalTimeZone);
        void Set(TimerEdit next) => edits[id] = edit = next;
        void Apply()
        {
            if (timers.Snapshot(id) is not { } current) return;
            TimerPanel.Apply(timers, current, edit);
            edits.Remove(id);
            Keyboard.ClearFocus();
        }

        var editor = new StackPanel { Margin = new Thickness(22, 2, 10, 8) };
        var name = new TextBox { Text = edit.Name, MinWidth = 140 };
        name.TextChanged += (_, _) => Set(edit with { Name = name.Text });
        name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Apply();
            e.Handled = true;
        };
        editor.Children.Add(Line("NAME", name));

        StackPanel Hms() => Row(
            OperatorKit.NumberField(edit.Hours, 99, v => Set(edit with { Hours = v }), Apply, hint: "HH"), OperatorKit.Colon(),
            OperatorKit.NumberField(edit.Minutes, 59, v => Set(edit with { Minutes = v }), Apply, hint: "MM"), OperatorKit.Colon(),
            OperatorKit.NumberField(edit.Seconds, 59, v => Set(edit with { Seconds = v }), Apply, hint: "SS"));
        Button ApplyChip()
        {
            var chip = OperatorKit.SmallButton("Apply", "Apply (or press Enter in a field)", Apply);
            chip.Margin = new Thickness(8, 0, 0, 0);
            return chip;
        }

        switch (timer.Mode)
        {
            case TimerMode.Countdown:
                editor.Children.Add(Line("LENGTH", Row(Hms(), ApplyChip())));
                break;
            case TimerMode.CountdownToTime:
                editor.Children.Add(Line("UNTIL", Row(WallClock(edit.Until, until => Set(edit with { Until = until }), Apply), ApplyChip())));
                break;
            case TimerMode.CountUp:
                var limit = new CheckBox
                {
                    Content = "Count to", IsChecked = edit.HasLimit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = "Off: runs until paused. On: warnings and overrun read against the limit",
                };
                var fields = Row(Hms(), ApplyChip());
                fields.Visibility = edit.HasLimit ? Visibility.Visible : Visibility.Collapsed;
                limit.Checked += (_, _) =>
                {
                    Set(edit with { HasLimit = true });
                    fields.Visibility = Visibility.Visible;
                };
                limit.Unchecked += (_, _) =>
                {
                    Set(edit with { HasLimit = false });
                    fields.Visibility = Visibility.Collapsed;
                    timers.SetLimit(id, 0);
                };
                editor.Children.Add(Line("LIMIT", Row(limit, fields)));
                break;
        }

        if (TimerPanel.ShowsWarnings(timer))
        {
            var heading = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
            var add = OperatorKit.IconButton("", "Add a warning level", () => timers.SetWarnings(id, TimerPanel.AddingWarning(timer.Warnings)), 10);
            DockPanel.SetDock(add, Dock.Right);
            heading.Children.Add(add);
            heading.Children.Add(OperatorKit.FieldLabel("WARNINGS"));
            editor.Children.Add(heading);
            for (var index = 0; index < timer.Warnings.Count; index++) editor.Children.Add(WarningRow(timer, index));
            if (timer.Warnings.Count == 0) editor.Children.Add(EditorKit.Faint("No warnings: the timer stays its running colour."));
        }

        var foot = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var delete = OperatorKit.IconButton("", "Delete timer", () =>
        {
            expanded.Remove(id);
            timers.Remove(id);
        }, 11);
        DockPanel.SetDock(delete, Dock.Right);
        foot.Children.Add(delete);
        foot.Children.Add(new Border());
        editor.Children.Add(foot);
        return editor;
    }

    private DockPanel WarningRow(TimerSnapshot timer, int index)
    {
        var timers = model!.Timers;
        var warning = timer.Warnings[index];
        var (minutes, seconds) = TimerPanel.WarningFields(warning);
        void Commit() => timers.SetWarnings(timer.Id, TimerPanel.ChangingWarning(timer.Warnings, index,
            w => w with { RemainingSeconds = TimerPanel.WarningSeconds(minutes, seconds) }));
        var swatch = new Button
        {
            Content = OperatorKit.Swatch(warning.ColorHex), Style = EditorKit.Res<Style>("GhostButton"), Padding = new Thickness(4, 2, 4, 2),
            ToolTip = $"Warning colour: {TimerPanel.ColorName(warning.ColorHex)}", MinHeight = 20, Margin = new Thickness(6, 0, 0, 0),
        };
        swatch.Click += (_, _) => OperatorKit.Open(OperatorKit.Menu(TimerPanel.WarningPalette.Select(entry => (object)OperatorKit.Choice(entry.Name,
            string.Equals(entry.Hex, warning.ColorHex, StringComparison.OrdinalIgnoreCase),
            () => timers.SetWarnings(timer.Id, TimerPanel.ChangingWarning(timer.Warnings, index, w => w with { ColorHex = entry.Hex }))))), swatch);
        var remove = OperatorKit.IconButton("", "Remove this warning level", () => timers.SetWarnings(timer.Id, TimerPanel.RemovingWarning(timer.Warnings, index)), 9);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        DockPanel.SetDock(remove, Dock.Right);
        row.Children.Add(remove);
        row.Children.Add(Row(
            OperatorKit.FieldLabel("AT"),
            OperatorKit.NumberField(minutes, 999, v => minutes = v, Commit, hint: "MM"), OperatorKit.Colon(),
            OperatorKit.NumberField(seconds, 59, v => seconds = v, Commit, hint: "SS"),
            new TextBlock { Text = "left", Margin = new Thickness(6, 0, 0, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("FaintText") },
            swatch));
        return row;
    }

    // MARK: Pieces

    private static TextBlock Readout(double size)
    {
        var text = new TextBlock { FontSize = size, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
        // Digits of equal width, so a running timer doesn't jiggle.
        System.Windows.Documents.Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        return text;
    }

    private static TextBlock Chevron(bool open) => new()
    {
        Text = open ? "" : "", FontFamily = EditorKit.IconFont, FontSize = 8, Width = 14,
        VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("FaintText"),
    };

    private static bool IsInside<T>(DependencyObject source) where T : DependencyObject
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is T) return true;
        return false;
    }

    private static DockPanel Line(string label, UIElement control)
    {
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var text = OperatorKit.FieldLabel(label, 56);
        DockPanel.SetDock(text, Dock.Left);
        line.Children.Add(text);
        line.Children.Add(control);
        return line;
    }

    private static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var item in items) row.Children.Add(item);
        return row;
    }

    /// <summary>The H : MM AM/PM boxes (WallClockFields in Swift): hour 1–12, minute 0–59.</summary>
    private static StackPanel WallClock(WallClockFields start, Action<WallClockFields> changed, Action? enter)
    {
        var value = start;
        var meridiem = OperatorKit.SmallButton(start.IsPm ? "PM" : "AM", "Morning or afternoon", () => { });
        meridiem.Margin = new Thickness(6, 0, 0, 0);
        meridiem.Click += (_, _) =>
        {
            value = value with { IsPm = !value.IsPm };
            meridiem.Content = value.IsPm ? "PM" : "AM";
            changed(value);
        };
        return Row(
            OperatorKit.NumberField(start.Hour12, 12, v => changed(value = value with { Hour12 = Math.Max(1, v) }), enter, hint: "H"), OperatorKit.Colon(),
            OperatorKit.NumberField(start.Minute, 59, v => changed(value = value with { Minute = v }), enter, hint: "MM"),
            meridiem);
    }

    // MARK: The add menu's two small dialogs (the Mac's popovers)

    private static Window Dialog(Window? owner, string title, UIElement fields, out Button add)
    {
        add = new Button { Content = "Add Timer", IsDefault = true, MinWidth = 90, Style = EditorKit.Res<Style>("AccentButton"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(fields);
        panel.Children.Add(add);
        var window = new Window
        {
            Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        if (owner is { IsLoaded: true }) window.Owner = owner;
        add.Click += (_, _) => window.DialogResult = true;
        return window;
    }

    /// <summary>Custom…: a countdown length in hours, minutes and seconds; null when cancelled.</summary>
    internal static double? AskLength(Window? owner, string title, int hours, int minutes, int seconds)
    {
        var fields = Row(
            OperatorKit.NumberField(hours, 99, v => hours = v, hint: "HH"), OperatorKit.Colon(),
            OperatorKit.NumberField(minutes, 59, v => minutes = v, hint: "MM"), OperatorKit.Colon(),
            OperatorKit.NumberField(seconds, 59, v => seconds = v, hint: "SS"));
        var window = Dialog(owner, title, fields, out _);
        return window.ShowDialog() == true ? TimerPanel.Join(hours, minutes, seconds) : null;
    }

    /// <summary>Countdown to Time…: a time of day; null when cancelled.</summary>
    internal static WallClockFields? AskWallTime(Window? owner, string title, WallClockFields start)
    {
        var value = start;
        var window = Dialog(owner, title, WallClock(start, v => value = v, null), out _);
        return window.ShowDialog() == true ? value.Clamped : null;
    }
}

internal static class ElementMargins
{
    /// <summary>The same element with a margin, for one-line layouts.</summary>
    public static T WithMargin<T>(this T element, Thickness margin) where T : FrameworkElement
    {
        element.Margin = margin;
        return element;
    }
}
