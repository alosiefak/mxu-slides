using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// A date box and a time box that save together, as a wall-clock time. What's typed is read
/// by <see cref="ScheduleEditing.ParseDateInput"/> and <see cref="ScheduleEditing.ParseTimeInput"/>
/// ("25/10/2026", "tomorrow", "9:30 pm"); the date is written out in full beside them so a
/// mistyped day is easy to spot. A box that can't be read turns red and saves nothing.
/// (The Mac uses its date picker; WPF's is a light-coloured control that doesn't suit the dark editor.)
/// </summary>
internal sealed class DateTimeFields : StackPanel
{
    private readonly TextBox? dateBox;
    private readonly TextBox timeBox;
    private readonly TextBlock confirmation = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
    private readonly Action<DateTime>? commit;
    private DateTime value;

    public DateTimeFields(DateTime wall, bool withDate, Action<DateTime>? commit)
    {
        Orientation = Orientation.Horizontal;
        value = wall;
        this.commit = commit;
        var culture = CultureInfo.CurrentCulture;
        confirmation.Foreground = EditorKit.Res<Brush>("MutedText");
        if (withDate)
        {
            dateBox = new TextBox
            {
                Text = ScheduleEditing.DateInputText(wall, culture), Width = 104, Margin = new Thickness(0, 0, 6, 0),
                ToolTip = $"A date, like {ScheduleEditing.DateInputText(new DateTime(2026, 12, 24), culture)} (or \"today\", \"tomorrow\")",
            };
            Hook(dateBox);
            Children.Add(dateBox);
        }
        timeBox = new TextBox
        {
            Text = ScheduleEditing.TimeInputText(wall.Hour, wall.Minute, wall.Second, culture), Width = 92,
            ToolTip = "A time, like 9:30, 18:45 or 9:30 pm (seconds too: 9:30:15)",
        };
        Hook(timeBox);
        Children.Add(timeBox);
        Children.Add(confirmation);
        Show();
    }

    /// <summary>The time the boxes hold now (the last one that could be read).</summary>
    public DateTime Value => value;

    /// <summary>True when both boxes can be read.</summary>
    public bool IsValid => Read() is not null;

    private void Hook(TextBox box)
    {
        box.LostKeyboardFocus += (_, _) => Apply();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            Apply();
            box.SelectAll();
        };
        box.TextChanged += (_, _) => Mark();
    }

    private DateTime? Read()
    {
        var culture = CultureInfo.CurrentCulture;
        var date = dateBox is null ? value.Date : ScheduleEditing.ParseDateInput(dateBox.Text, DateTime.Today, culture);
        if (date is null || ScheduleEditing.ParseTimeInput(timeBox.Text) is not { } time) return null;
        return date.Value.Date.AddHours(time.Hour).AddMinutes(time.Minute).AddSeconds(time.Second);
    }

    private void Mark()
    {
        var culture = CultureInfo.CurrentCulture;
        if (dateBox is not null)
            dateBox.BorderBrush = ScheduleEditing.ParseDateInput(dateBox.Text, DateTime.Today, culture) is null ? EditorKit.Res<Brush>("Live") : EditorKit.Res<Brush>("Border");
        timeBox.BorderBrush = ScheduleEditing.ParseTimeInput(timeBox.Text) is null ? EditorKit.Res<Brush>("Live") : EditorKit.Res<Brush>("Border");
    }

    private void Apply()
    {
        if (Read() is not { } read)
        {
            confirmation.Text = dateBox is null ? "Type a time like 9:30" : "Type a date and a time";
            confirmation.Foreground = EditorKit.Res<Brush>("Warn");
            return;
        }
        var changed = read != value;
        value = read;
        Show();
        if (changed) commit?.Invoke(read);
    }

    private void Show()
    {
        var culture = CultureInfo.CurrentCulture;
        if (dateBox is not null) dateBox.Text = ScheduleEditing.DateInputText(value, culture);
        timeBox.Text = ScheduleEditing.TimeInputText(value.Hour, value.Minute, value.Second, culture);
        confirmation.Text = dateBox is null ? "" : ScheduleEditing.DateConfirmation(value, culture);
        confirmation.Foreground = EditorKit.Res<Brush>("MutedText");
        Mark();
    }
}

/// <summary>Off Until… and On Until…: pick a moment that is still to come. Ports of the Mac's SnoozeSheet and EndDateSheet.</summary>
internal static class UntilDialog
{
    public static DateTime? Ask(Window owner, string title, string label, string explanation, string button, DateTime start)
    {
        var fields = new DateTimeFields(start, withDate: true, commit: null);
        var problem = new TextBlock { Foreground = EditorKit.Res<Brush>("Warn"), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        var ok = EditorKit.Button(button, () => { }, accent: true);
        ok.IsDefault = true;
        ok.Margin = new Thickness(0);
        var cancel = EditorKit.Button("Cancel", () => { });
        cancel.IsCancel = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(18), Width = 420 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(EditorKit.Row(label, fields, 70));
        panel.Children.Add(problem);
        panel.Children.Add(EditorKit.Muted(explanation, 11, 0));
        panel.Children.Add(buttons);
        var window = new Window { Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize };
        EditorKit.Dress(window, owner, title, double.NaN, double.NaN);
        DateTime? result = null;
        ok.Click += (_, _) =>
        {
            // Read whatever is typed, even if the box still has the cursor.
            ok.Focus();
            var now = Core.Threading.ShowTimeProvider.Shared.GetUtcNow();
            if (!fields.IsValid) problem.Text = "Type a date and a time.";
            else if (!ScheduleEditing.IsFuture(fields.Value, now, TimeZoneInfo.Local)) problem.Text = "Pick a moment that is still to come.";
            else
            {
                result = fields.Value;
                window.DialogResult = true;
            }
        };
        window.ShowDialog();
        return result;
    }
}

/// <summary>
/// Scheduler History: one-time triggers that have run (Duplicate to Active brings one back,
/// switched off) and every run on this PC, newest first. Port of SchedulerHistorySheet.
/// </summary>
internal sealed class SchedulerHistoryWindow : Window
{
    private readonly MainViewModel model;
    private readonly StackPanel body = new() { Margin = new Thickness(16) };

    /// <summary>The copy made by Duplicate to Active (the scheduler window selects it).</summary>
    public string? Duplicated { get; private set; }

    public SchedulerHistoryWindow(Window owner, MainViewModel model)
    {
        this.model = model;
        EditorKit.Dress(this, owner, "Scheduler History", 520, 560);
        var done = EditorKit.Button("Done", Close, accent: true);
        done.IsDefault = true;
        done.IsCancel = true;
        done.MinWidth = 90;
        done.Margin = new Thickness(0);
        var footer = new DockPanel { Margin = new Thickness(16, 0, 16, 14), LastChildFill = false };
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Build();
    }

    private void Build()
    {
        var zone = TimeZoneInfo.Local;
        var culture = CultureInfo.CurrentCulture;
        body.Children.Clear();
        body.Children.Add(EditorKit.Section("ARCHIVED", 0));
        var archived = ScheduleEditing.Archived(model.Library.All<ScheduleTrigger>(), zone);
        if (archived.Count == 0) body.Children.Add(EditorKit.Muted("One-time triggers land here after they run."));
        foreach (var trigger in archived)
        {
            var id = trigger.Id;
            var spent = ScheduleEditing.SpentDate(trigger, zone);
            var when = spent == DateTimeOffset.MinValue ? "" : ScheduleConditionFormat.Wall(spent, zone).ToString("g", culture);
            var duplicate = EditorKit.Button("Duplicate to Active", () =>
            {
                Duplicated = model.Automation.DuplicateScheduleTrigger(id);
                Close();
            }, "A copy back on the board, switched off, to set a new date for");
            duplicate.Margin = new Thickness(0);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(duplicate, Dock.Right);
            row.Children.Add(duplicate);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = trigger.Name, FontWeight = FontWeights.Medium });
            text.Children.Add(EditorKit.Faint(when));
            row.Children.Add(text);
            row.ContextMenu = new ContextMenu
            {
                Items =
                {
                    EditorKit.Command("Delete Trigger", () =>
                    {
                        model.Automation.DeleteScheduleTrigger(id);
                        Build();
                    }),
                },
            };
            body.Children.Add(row);
        }

        body.Children.Add(EditorKit.Section("RUNS ON THIS PC", 16));
        var log = model.Schedule.History;
        if (log.Count == 0) body.Children.Add(EditorKit.Muted("No firings yet."));
        foreach (var record in log)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var at = EditorKit.Faint(ScheduleConditionFormat.MonthDayTime(ScheduleConditionFormat.Wall(record.At, zone), culture), 11);
            DockPanel.SetDock(at, Dock.Right);
            row.Children.Add(at);
            if (ScheduleEditing.OutcomeTag(record.Outcome) is { } tag)
            {
                var label = new TextBlock { Text = tag, Foreground = EditorKit.Res<Brush>("Warn"), FontSize = 10, Margin = new Thickness(6, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(label, Dock.Right);
                row.Children.Add(label);
            }
            var name = new StackPanel { Orientation = Orientation.Horizontal };
            name.Children.Add(new TextBlock { Text = record.Name, Foreground = EditorKit.Res<Brush>("MutedText") });
            if (record.Source == Scheduler.RunSource.Manual) name.Children.Add(new TextBlock { Text = "run now", FontSize = 10, Margin = new Thickness(6, 0, 0, 0), Foreground = EditorKit.Res<Brush>("FaintText"), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(name);
            body.Children.Add(row);
        }
    }
}
