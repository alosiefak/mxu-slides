using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Threading;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Scheduler: schedule triggers on the left (in folders, each with when it fires next),
/// the selected trigger on the right: its name and notes, an optional end date, its
/// conditions (on chosen days at a time, once on a date, or when a timer reaches a value)
/// and its actions. Port of Sources/SchedulerView.swift.
/// <para>
/// The <see cref="Scheduler"/> (Core) does the firing; this window only edits the triggers,
/// through <see cref="AutomationLibrary"/>. Each save makes the scheduler look again
/// (MainViewModel.Scheduler.cs), and its <see cref="Scheduler.Changed"/> event redraws the
/// "next" times here.
/// </para>
/// </summary>
public sealed class SchedulerWindow : Window
{
    private static SchedulerWindow? openWindow;

    private sealed record Row(string Id, bool IsFolder);

    private readonly MainViewModel model;
    private readonly ListBox list = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly ToggleButton onChip = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock statusLine = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel recent = new();
    private readonly TextBlock boardNote = EditorKit.Muted("", 11, 0);
    private readonly ContentControl detail = new();
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(20) };
    private string? shownSelection;
    private bool refreshing;
    private int ownSaves;
    private int menusOpen;
    private bool refreshWhenMenusClose;
    private TextBlock? nextFireLine;
    private TextBox? nameBox;

    /// <summary>The selected trigger's actions (null when no trigger is selected).</summary>
    internal ActionListEditor? Editor { get; private set; }

    /// <summary>Opens the Scheduler (or brings the open one to the front).</summary>
    public static SchedulerWindow Open(Window owner, MainViewModel model)
    {
        if (openWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        var window = new SchedulerWindow(owner, model);
        window.Show();
        return window;
    }

    private SchedulerWindow(Window owner, MainViewModel model)
    {
        this.model = model;
        openWindow = this;
        EditorKit.Dress(this, owner, "Scheduler", 1060, 680);

        onChip.Style = (Style)EditorKit.Styles["ChipToggle"];
        onChip.ToolTip = "Turn the Scheduler on or off on THIS PC: nothing fires while it's off";
        onChip.Click += (_, _) =>
        {
            model.Schedule.Enabled = !model.Schedule.Enabled;
            Refresh(rebuildDetail: false);
        };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        title.Children.Add(new TextBlock { Text = "Scheduler", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(onChip);

        var newTrigger = EditorKit.Button("+ Trigger", () => NewTrigger(), "Make a new trigger");
        var newFolder = EditorKit.Button("+ Folder", NewFolder, "Make a folder (switch a folder off to pause all its triggers)");
        newFolder.Margin = new Thickness(0);
        var status = new DockPanel();
        var adds = new StackPanel { Orientation = Orientation.Horizontal, Children = { newTrigger, newFolder } };
        DockPanel.SetDock(adds, Dock.Right);
        status.Children.Add(adds);
        status.Children.Add(statusLine);

        var history = EditorKit.Button("History…", OpenHistory, "Every run on this PC, and one-time triggers that have run");
        history.Margin = new Thickness(8, 0, 0, 0);
        history.VerticalAlignment = VerticalAlignment.Top;
        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(history, Dock.Right);
        footer.Children.Add(history);
        footer.Children.Add(recent);

        list.SelectionChanged += (_, _) =>
        {
            if (!refreshing) ShowDetail(force: false);
        };
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && SelectedRow() is { } row)
            {
                if (row.IsFolder) DeleteFolder(row.Id);
                else DeleteTrigger(row.Id);
            }
        };

        var left = new DockPanel();
        foreach (var top in new FrameworkElement[] { title, status })
        {
            DockPanel.SetDock(top, Dock.Top);
            left.Children.Add(top);
        }
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(boardNote, Dock.Bottom);
        left.Children.Add(footer);
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
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(done, Dock.Right);
        bottom.Children.Add(done);
        bottom.Children.Add(EditorKit.Muted(
            "Times are this PC's clock. A fire missed by more than a minute (the PC was asleep or off) is logged as missed, never run late. Nothing fires twice after a restart.", 11, 0));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390), MinWidth = 300 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(rightPanel, 1);
        grid.Children.Add(leftPanel);
        grid.Children.Add(rightPanel);

        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(grid);
        // Padding (not a margin) so the window's own edge is the content's edge.
        Content = new Border { Padding = new Thickness(12), Child = root };

        model.Library.Changed += OnLibraryChanged;
        model.Schedule.Changed += OnScheduleChanged;
        // The "until 10:30" and "today/tomorrow" texts move on with the clock (this is display only, not show timing).
        tick.Tick += (_, _) => RefreshSoon();
        tick.Start();
        Closed += (_, _) =>
        {
            tick.Stop();
            model.Library.Changed -= OnLibraryChanged;
            model.Schedule.Changed -= OnScheduleChanged;
            if (openWindow == this) openWindow = null;
        };
        Activated += (_, _) => Editor?.RefreshChoices();
        Refresh(rebuildDetail: true);
        var first = list.Items.Cast<ListBoxItem>().ToList().FindIndex(i => i.Tag is Row { IsFolder: false });
        if (first >= 0) list.SelectedIndex = first;
    }

    private static TimeZoneInfo Zone => TimeZoneInfo.Local;

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>The scheduler's own clock.</summary>
    private static DateTimeOffset Now => ShowTimeProvider.Shared.GetUtcNow();

    private ScheduleTrigger? Trigger(string id) => model.Library.Get<ScheduleTrigger>(id);

    // MARK: Keeping up

    private void OnLibraryChanged(Type type, string id)
    {
        if (type != typeof(ScheduleTrigger) && type != typeof(SchedulerBoard)) return;
        if (ownSaves > 0 && Dispatcher.CheckAccess()) return;
        Dispatcher.BeginInvoke(() => Refresh(rebuildDetail: true));
    }

    /// <summary>After each sweep: the next times may have moved, a one-off may have been archived.</summary>
    private void OnScheduleChanged() => Dispatcher.BeginInvoke(RefreshSoon);

    /// <summary>
    /// A redraw nobody asked for (the clock moved on, the scheduler swept). It waits while a
    /// row's right-click menu is open, so the menu isn't pulled away from under the mouse.
    /// </summary>
    private void RefreshSoon()
    {
        if (menusOpen > 0) refreshWhenMenusClose = true;
        else Refresh(rebuildDetail: false);
    }

    private void Track(ContextMenu menu)
    {
        menu.Opened += (_, _) => menusOpen++;
        menu.Closed += (_, _) =>
        {
            menusOpen = Math.Max(0, menusOpen - 1);
            if (menusOpen == 0 && refreshWhenMenusClose)
            {
                refreshWhenMenusClose = false;
                Dispatcher.BeginInvoke(() => Refresh(rebuildDetail: false));
            }
        };
    }

    private void Save(Action save, bool rebuildDetail = false)
    {
        ownSaves++;
        try { save(); }
        catch (Exception ex)
        {
            Log.Error("Scheduler: a change couldn't be saved", ex);
            MessageBox.Show(this, $"That change couldn't be saved: {ex.Message}", Title);
        }
        finally { ownSaves--; }
        Refresh(rebuildDetail);
    }

    private void Edit(string triggerId, Action<ScheduleTrigger> change, bool rebuildDetail = false) =>
        Save(() => model.Automation.UpdateScheduleTrigger(triggerId, change), rebuildDetail);

    // MARK: The board (left)

    private Row? SelectedRow() => (list.SelectedItem as ListBoxItem)?.Tag as Row;

    private void Refresh(bool rebuildDetail)
    {
        var keep = SelectedRow();
        var schedulerOn = model.Schedule.Enabled;
        onChip.Content = schedulerOn ? "On" : "Off";
        onChip.IsChecked = schedulerOn;
        onChip.Foreground = schedulerOn ? EditorKit.Res<Brush>("Good") : EditorKit.Res<Brush>("MutedText");
        onChip.BorderBrush = schedulerOn ? EditorKit.Res<Brush>("Good") : EditorKit.Res<Brush>("Border");
        statusLine.Text = ScheduleEditing.StatusLine(schedulerOn, model.Schedule.NextFire, Zone, Culture);
        statusLine.Foreground = EditorKit.Res<Brush>(schedulerOn && model.Schedule.NextFire is not null ? "MutedText" : "FaintText");

        refreshing = true;
        try
        {
            list.Items.Clear();
            var board = model.Automation.SchedulerBoard();
            foreach (var node in board.Nodes)
            {
                if (board.Folder(node) is { } folder)
                {
                    AddFolderRow(folder, schedulerOn);
                    if (folder.Collapsed != true)
                        foreach (var id in folder.TriggerIds) AddTriggerRow(id, folder, schedulerOn);
                }
                else
                {
                    AddTriggerRow(node, null, schedulerOn);
                }
            }
            boardNote.Text = list.Items.Count == 0 ? "No triggers yet. Press + Trigger to make one." : "";
            if (keep is not null && list.Items.Cast<ListBoxItem>().FirstOrDefault(i => (Row)i.Tag == keep) is { } item) list.SelectedItem = item;
        }
        finally
        {
            refreshing = false;
        }
        ShowRecent();
        ShowDetail(force: rebuildDetail);
        UpdateNextFireLine();
    }

    private void ShowRecent()
    {
        recent.Children.Clear();
        var runs = model.Schedule.History.Take(3).ToList();
        if (runs.Count == 0) recent.Children.Add(EditorKit.Faint("No firings yet.", 11));
        foreach (var run in runs)
        {
            var row = new DockPanel();
            var at = EditorKit.Faint(ScheduleConditionFormat.LongTime(ScheduleConditionFormat.Wall(run.At, Zone), Culture), 10);
            DockPanel.SetDock(at, Dock.Right);
            row.Children.Add(at);
            if (ScheduleEditing.OutcomeTag(run.Outcome) is { } tag)
            {
                var label = new TextBlock { Text = tag, FontSize = 10, Foreground = EditorKit.Res<Brush>("Warn"), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(label, Dock.Right);
                row.Children.Add(label);
            }
            row.Children.Add(new TextBlock { Text = run.Name, FontSize = 11, Foreground = EditorKit.Res<Brush>("MutedText"), TextTrimming = TextTrimming.CharacterEllipsis });
            recent.Children.Add(row);
        }
    }

    private ToggleButton Chip(bool on, bool live, string tooltip, Action toggle)
    {
        var chip = new ToggleButton
        {
            Content = on ? "On" : "Paused", IsChecked = on, Style = (Style)EditorKit.Styles["ChipToggle"], ToolTip = tooltip,
            Foreground = live ? EditorKit.Res<Brush>("Good") : EditorKit.Res<Brush>("MutedText"),
            BorderBrush = live ? EditorKit.Res<Brush>("Good") : EditorKit.Res<Brush>("Border"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        chip.Click += (_, _) => toggle();
        return chip;
    }

    private void AddFolderRow(ScheduleFolder folder, bool schedulerOn)
    {
        var id = folder.Id;
        var enabled = folder.Enabled ?? true;
        var collapsed = folder.Collapsed == true;
        var chevron = EditorKit.Ghost(collapsed ? "\uE76C" : "\uE70D", collapsed ? "Show its triggers" : "Hide its triggers",
            () => Save(() => model.Automation.UpdateSchedulerBoard(b => b.SetFolderCollapsed(id, !collapsed))), 10);
        chevron.FontFamily = EditorKit.IconFont;
        var chip = Chip(enabled, enabled && schedulerOn, "Switch the whole folder on or off",
            () => Save(() => model.Automation.UpdateSchedulerBoard(b => b.SetFolderEnabled(id, !enabled))));
        var row = new DockPanel();
        DockPanel.SetDock(chip, Dock.Right);
        row.Children.Add(chip);
        row.Children.Add(chevron);
        row.Children.Add(EditorKit.Icon("folder", 12));
        row.Children.Add(new TextBlock
        {
            Text = folder.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = enabled ? EditorKit.Res<Brush>("Text") : EditorKit.Res<Brush>("FaintText"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        row.Children.Add(EditorKit.Faint(ScheduleEditing.ActiveTriggerCount(folder, Trigger).ToString(CultureInfo.InvariantCulture)));
        var item = new ListBoxItem { Content = row, Tag = new Row(id, true), Padding = new Thickness(4, 3, 6, 3) };
        var enable = new MenuItem { Header = $"Enabled: {(enabled ? "On" : "Off")}", Style = (Style)EditorKit.Styles["CascadingMenuItem"] };
        enable.Items.Add(EditorKit.Command("On", () => Save(() => model.Automation.UpdateSchedulerBoard(b => b.SetFolderEnabled(id, true)))));
        enable.Items.Add(EditorKit.Command("Off", () => Save(() => model.Automation.UpdateSchedulerBoard(b => b.SetFolderEnabled(id, false)))));
        item.ContextMenu = new ContextMenu
        {
            Items =
            {
                enable,
                EditorKit.Command("Rename…", () => SelectOnly(new Row(id, true), focusName: true)),
                EditorKit.Command("Duplicate", () => DuplicateFolder(id)),
                EditorKit.Separator(),
                EditorKit.Command("Delete Folder", () => DeleteFolder(id)),
            },
        };
        Track(item.ContextMenu);
        list.Items.Add(item);
    }

    private void AddTriggerRow(string id, ScheduleFolder? folder, bool schedulerOn)
    {
        if (Trigger(id) is not { } trigger || trigger.Archived == true) return;
        var folderOn = folder?.Enabled ?? true;
        var upcoming = model.Schedule.Upcoming.TryGetValue(id, out var next) ? next : (DateTimeOffset?)null;
        var state = ScheduleEditing.RowStatus(trigger, folderOn, schedulerOn, upcoming, model.Schedule.History, Now, Zone, Culture);

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 0, 0) };
        right.Children.Add(Chip(state.ChipOn, state.ChipLive, "On, or paused (off, snoozed or ended): click to switch",
            () => Edit(id, t => ScheduleEditing.ToggleFromChip(t, Now, Zone), rebuildDetail: true)));
        if (state.When is { } when)
        {
            var text = EditorKit.Faint(when);
            text.HorizontalAlignment = HorizontalAlignment.Right;
            if (state.WhenIsWarning) text.Foreground = EditorKit.Res<Brush>("Warn");
            right.Children.Add(text);
        }
        if (state.Ends is { } ends)
        {
            var text = EditorKit.Faint(ends);
            text.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(text);
        }
        var row = new DockPanel { Margin = new Thickness(folder is null ? 0 : 18, 0, 0, 0) };
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = trigger.Name, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = state.Dimmed ? EditorKit.Res<Brush>("FaintText") : EditorKit.Res<Brush>("Text"),
        });
        var summary = new TextBlock
        {
            Text = ScheduleEditing.ConditionSummary(trigger, TimerName, Zone, Culture), FontSize = 10.5,
            Foreground = EditorKit.Res<Brush>("MutedText"), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        summary.ToolTip = summary.Text;
        left.Children.Add(summary);
        row.Children.Add(left);
        var item = new ListBoxItem { Content = row, Tag = new Row(id, false), Padding = new Thickness(6, 5, 6, 5) };
        item.ContextMenu = TriggerMenu(trigger);
        Track(item.ContextMenu);
        list.Items.Add(item);
    }

    private string? TimerName(string id) => model.Timers.Snapshot(id)?.Name;

    /// <summary>The trigger's right-click menu (and the editor's More menu): the Mac's Run Now / Enabled ▸ / Duplicate / Delete.</summary>
    private ContextMenu TriggerMenu(ScheduleTrigger trigger)
    {
        var id = trigger.Id;
        var now = Now;
        var style = (Style)EditorKit.Styles["CascadingMenuItem"];
        var enabled = new MenuItem { Header = ScheduleEditing.EnabledTitle(trigger, now, Zone), Style = style };
        enabled.Items.Add(EditorKit.Command("On", () => Edit(id, t => ScheduleTriggers.SetEnabled(t, true), rebuildDetail: true)));
        enabled.Items.Add(EditorKit.Command("Off", () => Edit(id, t => ScheduleTriggers.SetEnabled(t, false), rebuildDetail: true)));
        enabled.Items.Add(EditorKit.Separator());
        if (ScheduleMath.NextDue(trigger, now, Zone) is { } due && ScheduleEditing.SkipNextFireUntil(trigger, now, Zone) is { } skipUntil)
            enabled.Items.Add(EditorKit.Command($"Skip Next Fire ({ScheduleConditionFormat.WeekdayTime(ScheduleConditionFormat.Wall(due, Zone), Culture)})",
                () => Edit(id, t => ScheduleEditing.Snooze(t, skipUntil), rebuildDetail: true)));
        enabled.Items.Add(EditorKit.Command("Off Until…", () => OffUntil(id)));
        if (ScheduleMath.Snoozed(trigger, now, Zone))
            enabled.Items.Add(EditorKit.Command("Resume Now", () => Edit(id, t => t.DisabledUntil = null, rebuildDetail: true)));
        enabled.Items.Add(EditorKit.Separator());
        enabled.Items.Add(EditorKit.Command("On Until…", () => OnUntil(id)));
        if (ScheduleMath.PlannedEnd(trigger, Zone) is { } end)
            enabled.Items.Add(EditorKit.Command($"Clear End Date ({ScheduleConditionFormat.ShortMonthDay(ScheduleConditionFormat.Wall(end, Zone), Culture)})",
                () => Edit(id, t => t.EnabledUntil = null, rebuildDetail: true)));
        return new ContextMenu
        {
            Items =
            {
                EditorKit.Command("Run Now", () => RunNow(id)),
                enabled,
                EditorKit.Command("Duplicate", () => DuplicateTrigger(id)),
                EditorKit.Separator(),
                EditorKit.Command("Delete Trigger", () => DeleteTrigger(id)),
            },
        };
    }

    private void SelectOnly(Row row, bool focusName = false)
    {
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

    /// <summary>Selects a trigger by id (the self-test uses this).</summary>
    internal void Select(string id) => SelectOnly(new Row(id, false));

    // MARK: The editor (right)

    private void ShowDetail(bool force)
    {
        var row = SelectedRow();
        var key = row is null ? "" : (row.IsFolder ? "f:" : "t:") + row.Id;
        if (!force && key == shownSelection) return;
        shownSelection = key;
        nameBox = null;
        nextFireLine = null;
        Editor = null;
        detail.Content = row switch
        {
            null => Placeholder(),
            { IsFolder: true } => FolderDetail(row.Id),
            _ => TriggerDetail(row.Id),
        };
    }

    private StackPanel Placeholder()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Schedule triggers", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(EditorKit.Muted("A trigger runs its actions by itself: every Sunday at 10:25, once on a date, or when the sermon timer reaches 2:00. "
            + "Select a trigger on the left, or press + Trigger to make one.", 12));
        return panel;
    }

    /// <summary>Re-reads the detail card's "Fires next" line (it moves on with the clock and after each sweep).</summary>
    private void UpdateNextFireLine()
    {
        if (nextFireLine is null || SelectedRow() is not { IsFolder: false } row || Trigger(row.Id) is not { } trigger) return;
        var board = model.Automation.SchedulerBoard();
        var text = ScheduleEditing.NextFireLine(trigger, board.FolderEnabled(trigger.Id), model.Schedule.Enabled, Now, Zone, Culture);
        nextFireLine.Text = text;
        nextFireLine.Foreground = EditorKit.Res<Brush>(text.Contains("Won't fire", StringComparison.Ordinal) ? "Warn" : "MutedText");
    }

    /// <summary>The "Fires next" line as shown (for the self-test).</summary>
    internal string? NextFireText => nextFireLine?.Text;

    private UIElement TriggerDetail(string id)
    {
        if (Trigger(id) is not { } trigger) return Placeholder();
        var panel = new StackPanel();

        nameBox = EditorKit.CommitBox(trigger.Name, typed =>
        {
            Edit(id, t => ScheduleEditing.Rename(t, typed));
            return Trigger(id)?.Name ?? typed;
        }, "Trigger name");
        nameBox.FontSize = 16;
        var run = EditorKit.Button("▶ Run now", () => RunNow(id), "Runs this trigger's actions now, whatever its schedule says", accent: true);
        run.Margin = new Thickness(10, 0, 0, 0);
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(run, Dock.Right);
        top.Children.Add(run);
        top.Children.Add(nameBox);
        panel.Children.Add(top);
        nextFireLine = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(nextFireLine);

        var notes = EditorKit.CommitBox(trigger.Notes ?? "", typed =>
        {
            Edit(id, t => ScheduleEditing.SetNotes(t, typed));
            return typed;
        }, "What this is for (optional)");
        panel.Children.Add(EditorKit.Row("Notes", notes));

        var ends = new CheckBox { Content = "Ends", IsChecked = trigger.EnabledUntil is not null, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        ends.Click += (_, _) => Edit(id, t => ScheduleEditing.SetEnds(t, ends.IsChecked == true, Now, Zone), rebuildDetail: true);
        var endRow = new StackPanel { Orientation = Orientation.Horizontal };
        endRow.Children.Add(ends);
        if (ScheduleMath.PlannedEnd(trigger, Zone) is { } plannedEnd)
            endRow.Children.Add(new DateTimeFields(ScheduleConditionFormat.Wall(plannedEnd, Zone), withDate: true,
                wall => Edit(id, t => t.EnabledUntil = ScheduleConditionFormat.LocalString(wall))));
        else
            endRow.Children.Add(EditorKit.Faint("Runs until you switch it off", 11));
        panel.Children.Add(EditorKit.Row("End date", endRow));

        panel.Children.Add(EditorKit.Row("Folder", PlaceRow(id)));
        var tools = new WrapPanel { Margin = new Thickness(96, 2, 0, 0) };
        tools.Children.Add(EditorKit.Button("Duplicate", () => DuplicateTrigger(id), "A copy (switched off) right after this one"));
        var more = EditorKit.Button("Enabled ▾", () => { }, "Switch it on or off, skip the next fire, pause it until a moment, or give it an end date");
        more.Click += (_, _) =>
        {
            if (Trigger(id) is not { } current) return;
            var menu = TriggerMenu(current);
            // Only the Enabled submenu's items: Run Now, Duplicate and Delete have their own buttons.
            var enabledItem = (MenuItem)menu.Items[1];
            var items = enabledItem.Items.Cast<object>().ToList();
            enabledItem.Items.Clear();
            var flat = new ContextMenu();
            foreach (var entry in items) flat.Items.Add(entry);
            EditorKit.Open(flat, more);
        };
        tools.Children.Add(more);
        tools.Children.Add(EditorKit.Button("Delete", () => DeleteTrigger(id)));
        panel.Children.Add(tools);

        panel.Children.Add(EditorKit.Section("CONDITIONS", 18));
        if (trigger.Conditions.Count > 1) panel.Children.Add(EditorKit.Row("Fire when", LogicRow(trigger)));
        foreach (var condition in trigger.Conditions) panel.Children.Add(ConditionCard(id, condition));
        if (trigger.Conditions.Count == 0) panel.Children.Add(EditorKit.Muted("No conditions yet, so this never runs by itself. Add one:", 12, 8));
        var addCondition = EditorKit.Button("+ Add condition", () => { }, "On some days at a time, once on a date, or when a timer reaches a value");
        addCondition.HorizontalAlignment = HorizontalAlignment.Left;
        addCondition.Click += (_, _) => EditorKit.Open(new ContextMenu
        {
            Items =
            {
                EditorKit.Command("Weekly", () => AddCondition(id, ScheduleConditionKind.Weekly)),
                EditorKit.Command("One Time", () => AddCondition(id, ScheduleConditionKind.OneTime)),
                EditorKit.Command("Timer Reaches", () => AddCondition(id, ScheduleConditionKind.TimerReaches)),
            },
        }, addCondition);
        panel.Children.Add(addCondition);

        panel.Children.Add(EditorKit.Section("ACTIONS", 18));
        var editor = new ActionListEditor(model.ActionChoices, emptyText: "No actions yet: press + Add action to say what it does.");
        editor.Load(trigger.Actions);
        editor.Changed += actions => Edit(id, t => t.Actions = [.. actions]);
        Editor = editor;
        // Windows only: the trigger's production steps (cameras, OBS, vMix, lights), kept beside the library.
        Production.ProductionStepsSection.Attach(editor, model, id);
        panel.Children.Add(editor);

        // Filled in now, and kept up to date by Refresh.
        Dispatcher.BeginInvoke(UpdateNextFireLine);
        return panel;
    }

    private UIElement LogicRow(ScheduleTrigger trigger)
    {
        var id = trigger.Id;
        var all = trigger.ConditionLogic == ScheduleConditionLogic.All;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (logic, label, tip) in new[]
                 {
                     (ScheduleConditionLogic.Any, "Any matches", "Fires whenever any one condition comes due"),
                     (ScheduleConditionLogic.All, "All match", "Fires when a condition comes due and every other one holds then"),
                 })
        {
            var button = new ToggleButton { Content = label, ToolTip = tip, Margin = new Thickness(0, 0, 4, 0), IsChecked = all == (logic == ScheduleConditionLogic.All) };
            button.Click += (_, _) => Edit(id, t => ScheduleEditing.SetLogic(t, logic), rebuildDetail: true);
            row.Children.Add(button);
        }
        return row;
    }

    private Border ConditionCard(string triggerId, ScheduleCondition condition)
    {
        var conditionId = condition.Id;
        void Change(Action<ScheduleCondition> change, bool rebuild = false) => Edit(triggerId, t =>
        {
            if (t.Conditions.FirstOrDefault(c => c.Id == conditionId) is { } stored) change(stored);
        }, rebuild);

        var body = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var remove = EditorKit.Ghost("\uE711", "Remove this condition",
            () => Edit(triggerId, t => t.Conditions.RemoveAll(c => c.Id == conditionId), rebuildDetail: true), 11);
        remove.FontFamily = EditorKit.IconFont;
        DockPanel.SetDock(remove, Dock.Right);
        header.Children.Add(remove);
        header.Children.Add(new TextBlock
        {
            Text = ScheduleEditing.KindLabel(condition.Kind).ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = EditorKit.Res<Brush>("MutedText"), VerticalAlignment = VerticalAlignment.Center,
        });
        body.Children.Add(header);

        switch (condition.Kind)
        {
            case ScheduleConditionKind.Weekly:
            {
                var days = new WrapPanel();
                for (var weekday = 1; weekday <= 7; weekday++)
                {
                    var day = weekday;
                    var button = new ToggleButton
                    {
                        Content = Culture.DateTimeFormat.AbbreviatedDayNames[day - 1], IsChecked = ScheduleEditing.HasDay(condition, day),
                        Width = 46, Padding = new Thickness(0, 3, 0, 3), Margin = new Thickness(0, 0, 4, 4), ToolTip = Culture.DateTimeFormat.DayNames[day - 1],
                    };
                    button.Click += (_, _) => Change(c => ScheduleEditing.ToggleDay(c, day), rebuild: true);
                    days.Children.Add(button);
                }
                body.Children.Add(EditorKit.Row("Days", days, 70));
                if (condition.Days is not { Count: > 0 }) body.Children.Add(EditorKit.Muted("No days picked, so it runs every day.", 11, 4));
                var time = ScheduleConditionFormat.ParseTime(condition.TimeOfDay ?? "09:00") is { Hour: >= 0 and <= 23, Minute: >= 0 and <= 59, Second: >= 0 and <= 59 } parsed
                    ? parsed : (9, 0, 0);
                body.Children.Add(EditorKit.Row("At", new DateTimeFields(new DateTime(2000, 1, 1, time.Item1, time.Item2, time.Item3), withDate: false,
                    wall => Change(c => c.TimeOfDay = ScheduleConditionFormat.TimeOfDay(wall.Hour, wall.Minute, wall.Second))), 70, 2));
                break;
            }
            case ScheduleConditionKind.OneTime:
            {
                var at = condition.Date is { } raw && ScheduleMath.ParseLocalDate(raw, Zone) is { } date
                    ? ScheduleConditionFormat.Wall(date, Zone)
                    : ScheduleConditionFormat.Wall(Now, Zone);
                body.Children.Add(EditorKit.Row("On", new DateTimeFields(at, withDate: true,
                    wall => Change(c => c.Date = ScheduleConditionFormat.LocalString(wall))), 70, 2));
                break;
            }
            default:
            {
                var timers = new List<EditorChoice> { new("", ActionEditing.Choose) };
                timers.AddRange(model.Timers.Timers.Select(t => new EditorChoice(t.Id, t.Name)));
                if (condition.TimerId is { Length: > 0 } current && timers.All(t => t.Id != current)) timers.Add(new EditorChoice(current, "Missing timer"));
                body.Children.Add(EditorKit.Row("Timer", EditorKit.Picker(timers, condition.TimerId,
                    picked => Change(c => c.TimerId = picked.Length == 0 ? null : picked, rebuild: true)), 70));
                var seconds = (int)(condition.TimerSeconds ?? 0);
                var at = new StackPanel { Orientation = Orientation.Horizontal };
                at.Children.Add(EditorKit.NumberBox(seconds / 60, minutes =>
                {
                    Change(c => ScheduleEditing.SetTimerMinutes(c, minutes));
                    return Math.Clamp(minutes, 0, 999);
                }, tooltip: "Minutes (0 to 999)"));
                at.Children.Add(new TextBlock { Text = "min", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText") });
                at.Children.Add(EditorKit.NumberBox(seconds % 60, secs =>
                {
                    Change(c => ScheduleEditing.SetTimerSeconds(c, secs));
                    return Math.Clamp(secs, 0, 59);
                }, tooltip: "Seconds (0 to 59)"));
                var hint = ScheduleEditing.TimerHint(condition.TimerId is { } timerId ? model.Timers.Snapshot(timerId)?.Mode : null);
                at.Children.Add(new TextBlock { Text = "sec " + hint, VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText") });
                body.Children.Add(EditorKit.Row("At", at, 70, 2));
                break;
            }
        }
        return EditorKit.Card(body);
    }

    private UIElement FolderDetail(string folderId)
    {
        if (model.Automation.SchedulerBoard().Folder(folderId) is not { } folder) return Placeholder();
        var panel = new StackPanel();
        nameBox = EditorKit.CommitBox(folder.Name, typed =>
        {
            Save(() => model.Automation.UpdateSchedulerBoard(b => b.RenameFolder(folderId, typed)));
            return model.Automation.SchedulerBoard().Folder(folderId)?.Name ?? typed;
        }, "Folder name");
        nameBox.FontSize = 16;
        nameBox.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(nameBox);
        var enabled = new CheckBox { Content = "On: its triggers can fire", IsChecked = folder.Enabled ?? true };
        enabled.Click += (_, _) => Save(() => model.Automation.UpdateSchedulerBoard(b => b.SetFolderEnabled(folderId, enabled.IsChecked == true)));
        panel.Children.Add(EditorKit.Row("", enabled));
        var moves = new StackPanel { Orientation = Orientation.Horizontal };
        moves.Children.Add(EditorKit.Button("Move up", () => Save(() => model.Automation.UpdateSchedulerBoard(b => b.StepFolder(folderId, -1)))));
        moves.Children.Add(EditorKit.Button("Move down", () => Save(() => model.Automation.UpdateSchedulerBoard(b => b.StepFolder(folderId, 1)))));
        panel.Children.Add(EditorKit.Row("Place", moves));
        var count = ScheduleEditing.ActiveTriggerCount(folder, Trigger);
        panel.Children.Add(EditorKit.Muted($"{count} trigger{(count == 1 ? "" : "s")}. Switching the folder off pauses them all. To put a trigger in, select it and choose this folder. Deleting a folder keeps its triggers.", 11, 8));
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(EditorKit.Button("Duplicate", () => DuplicateFolder(folderId), "A copy of the folder and every trigger in it (the copies start switched off)"));
        tools.Children.Add(EditorKit.Button("Delete Folder", () => DeleteFolder(folderId), "Its triggers go back to the top level"));
        panel.Children.Add(tools);
        return panel;
    }

    private UIElement PlaceRow(string id)
    {
        var board = model.Automation.SchedulerBoard();
        var choices = new List<EditorChoice> { new("", "Not in a folder") };
        choices.AddRange(board.Nodes.Select(board.Folder).OfType<ScheduleFolder>().Select(f => new EditorChoice(f.Id, f.Name)));
        var picker = EditorKit.Picker(choices, board.FolderContaining(id)?.Id ?? "",
            picked => Save(() => model.Automation.UpdateSchedulerBoard(b => b.PutInFolder(id, picked.Length == 0 ? null : picked))), minWidth: 170);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(picker);
        var up = EditorKit.Button("Move up", () => Save(() => model.Automation.UpdateSchedulerBoard(b => b.StepTrigger(id, -1))));
        up.Margin = new Thickness(8, 0, 6, 0);
        row.Children.Add(up);
        row.Children.Add(EditorKit.Button("Move down", () => Save(() => model.Automation.UpdateSchedulerBoard(b => b.StepTrigger(id, 1)))));
        return row;
    }

    // MARK: Commands

    /// <summary>Makes a trigger, selects it and puts the cursor in its name. Returns its id.</summary>
    internal string? NewTrigger(string name = "New Trigger")
    {
        string? id = null;
        Save(() => id = model.Automation.CreateScheduleTrigger(name));
        if (id is not null) SelectOnly(new Row(id, false), focusName: true);
        return id;
    }

    /// <summary>Adds a condition to a trigger, starting as the Mac's do (Sundays at 9:00, an hour from now, the first timer at 0:00).</summary>
    internal void AddCondition(string triggerId, ScheduleConditionKind kind)
    {
        var condition = kind switch
        {
            ScheduleConditionKind.Weekly => ScheduleEditing.NewWeekly(),
            ScheduleConditionKind.OneTime => ScheduleEditing.NewOneTime(Now, Zone),
            _ => ScheduleEditing.NewTimerReaches(model.Timers.Timers.FirstOrDefault()?.Id),
        };
        Edit(triggerId, t => t.Conditions.Add(condition), rebuildDetail: true);
    }

    private void NewFolder()
    {
        string? id = null;
        Save(() => model.Automation.UpdateSchedulerBoard(b => id = b.AddFolder("New Folder")));
        if (id is not null) SelectOnly(new Row(id, true), focusName: true);
    }

    private void RunNow(string id)
    {
        if (Trigger(id) is not { } trigger) return;
        model.Schedule.RunNow(id);
        model.Notice = $"Ran \"{trigger.Name}\"";
        Refresh(rebuildDetail: false);
    }

    private void DuplicateTrigger(string id)
    {
        string? copy = null;
        Save(() => copy = model.Automation.DuplicateScheduleTrigger(id));
        if (copy is not null) SelectOnly(new Row(copy, false));
    }

    private void DuplicateFolder(string id)
    {
        string? copy = null;
        Save(() => copy = model.Automation.DuplicateScheduleFolder(id));
        if (copy is not null) SelectOnly(new Row(copy, true));
    }

    private void DeleteTrigger(string id)
    {
        if (Trigger(id) is not { } trigger) return;
        if (MessageBox.Show(this, $"Delete the trigger \"{trigger.Name}\"? It goes to the library's Trash folder.", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        Save(() => model.Automation.DeleteScheduleTrigger(id), rebuildDetail: true);
    }

    private void DeleteFolder(string id) => Save(() => model.Automation.UpdateSchedulerBoard(b => b.RemoveFolder(id)), rebuildDetail: true);

    private void OffUntil(string id)
    {
        if (Trigger(id) is not { } trigger) return;
        if (UntilDialog.Ask(this, $"Off Until: {trigger.Name}", "Back on",
                "The trigger pauses now and comes back on by itself at this moment. Fires it sleeps through are skipped, never run late.",
                "Pause", ScheduleEditing.DefaultSnoozeUntil(Now, Zone)) is { } until)
            Edit(id, t => ScheduleEditing.Snooze(t, until), rebuildDetail: true);
    }

    private void OnUntil(string id)
    {
        if (Trigger(id) is not { } trigger) return;
        if (UntilDialog.Ask(this, $"On Until: {trigger.Name}", "Ends",
                "The trigger runs normally until this moment, then stops for good. Its last fire moves it to the History, where Duplicate to Active brings it back.",
                "Set End Date", ScheduleEditing.DefaultEndUntil(Now, Zone)) is { } until)
            Edit(id, t => t.EnabledUntil = ScheduleConditionFormat.LocalString(until), rebuildDetail: true);
    }

    private void OpenHistory()
    {
        var history = new SchedulerHistoryWindow(this, model);
        history.ShowDialog();
        Refresh(rebuildDetail: true);
        if (history.Duplicated is { } copy) Select(copy);
    }

    /// <summary>What the board shows, row by row (for the self-test's checks).</summary>
    internal IReadOnlyList<string> RowTexts() =>
        [.. list.Items.Cast<ListBoxItem>().Select(item => string.Join(" ", Texts(item)))];

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        if (root is TextBlock { Text.Length: > 0 } text && text.FontFamily != EditorKit.IconFont) yield return text.Text;
        if (root is ContentControl { Content: string content }) yield return content;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var found in Texts(child)) yield return found;
    }
}
