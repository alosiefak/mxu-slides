using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Core.PlanningCenter;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Import from Planning Center: the church's service types on the left, the chosen one's plans on
/// the right (upcoming first, then the last two), and Import. A plan imported before says so, and
/// importing it again updates its service. Every request is async and the window can be closed at
/// any moment, which cancels whatever is still waiting. Windows only.
/// </summary>
public sealed class PlanningCenterWindow : Window
{
    private readonly MainViewModel model;
    private readonly CancellationTokenSource closing = new();
    private readonly ListBox types = new() { MinWidth = 200 };
    private readonly ListBox plans = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly Button import;
    private readonly Button close;
    private PlanningCenterClient? client;
    private Dictionary<string, string> imported = [];
    private bool busy;

    private PlanningCenterWindow(Window? owner, MainViewModel model)
    {
        this.model = model;
        EditorKit.Dress(this, owner, "Import from Planning Center", 860, 560);

        var root = new DockPanel();
        var heading = new TextBlock
        {
            Text = "Pick a plan to bring in as a service. Songs your library has are used; others are made from Planning Center's lyrics.",
            TextWrapping = TextWrapping.Wrap, Foreground = EditorKit.Res<Brush>("MutedText"), Margin = new Thickness(0, 0, 0, 10),
        };
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);

        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        import = EditorKit.Button("Import", () => _ = ImportSelectedAsync(), accent: true);
        import.IsEnabled = false;
        import.MinWidth = 120;
        close = EditorKit.Button("Close", Close);
        close.MinWidth = 90;
        DockPanel.SetDock(import, Dock.Right);
        DockPanel.SetDock(close, Dock.Right);
        bottom.Children.Add(import);
        bottom.Children.Add(close);
        bottom.Children.Add(status);
        root.Children.Add(bottom);

        var lists = new Grid();
        lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lists.Children.Add(Labelled("SERVICE TYPES", types));
        var right = Labelled("PLANS", plans);
        Grid.SetColumn(right, 2);
        lists.Children.Add(right);
        root.Children.Add(lists);
        // Padding on a border rather than a margin on the panel: the window's own content then starts at its corner.
        Content = new Border { Padding = new Thickness(16), Child = root };

        types.SelectionChanged += (_, _) =>
        {
            if (types.SelectedItem is ListBoxItem { Tag: PcoServiceType type }) _ = LoadPlansAsync(type);
        };
        plans.SelectionChanged += (_, _) => UpdateImportButton();
        plans.MouseDoubleClick += (_, _) =>
        {
            if (import.IsEnabled) _ = ImportSelectedAsync();
        };
        Loaded += (_, _) => Loading = LoadTypesAsync();
        Closed += (_, _) =>
        {
            closing.Cancel();
            client?.Dispose();
        };
    }

    /// <summary>The service types being read (the self-test waits for it).</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The selected type's plans being read.</summary>
    internal Task LoadingPlans { get; private set; } = Task.CompletedTask;

    public static PlanningCenterWindow Open(Window? owner, MainViewModel model)
    {
        var window = new PlanningCenterWindow(owner, model);
        window.Show();
        return window;
    }

    private static DockPanel Labelled(string label, UIElement list)
    {
        var panel = new DockPanel();
        var text = EditorKit.Section(label, 0);
        DockPanel.SetDock(text, Dock.Top);
        panel.Children.Add(text);
        panel.Children.Add(list);
        return panel;
    }

    private void Say(string text, bool problem = false)
    {
        status.Text = text;
        status.Foreground = EditorKit.Res<Brush>(problem ? "Warn" : "MutedText");
    }

    // MARK: Reading

    private async Task LoadTypesAsync()
    {
        client = model.PlanningCenterApi(new Progress<string>(text => Say(text)));
        if (client is null)
        {
            Say("Planning Center isn't connected yet. Add the church's Personal Access Token in Screens & Settings › Planning Center.", problem: true);
            return;
        }
        Say("Reading service types…");
        try
        {
            var found = await client.ServiceTypesAsync(closing.Token);
            types.Items.Clear();
            foreach (var type in found)
                types.Items.Add(new ListBoxItem { Content = Row(type.Name, type.Frequency), Tag = type, Padding = new Thickness(8, 6, 8, 6) });
            Say(found.Count == 0 ? "This Planning Center login can't see any service types." : "Pick a service type.");
            if (types.Items.Count > 0) types.SelectedIndex = 0;
        }
        catch (PlanningCenterException e)
        {
            Say(e.Message, problem: true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task LoadPlansAsync(PcoServiceType type) => LoadingPlans = LoadPlansCoreAsync(type);

    private async Task LoadPlansCoreAsync(PcoServiceType type)
    {
        if (client is null) return;
        plans.Items.Clear();
        UpdateImportButton();
        Say($"Reading {type.Name}'s plans…");
        try
        {
            var (upcoming, recent) = await client.PlansAsync(type.Id, closing.Token);
            if (!ReferenceEquals((types.SelectedItem as ListBoxItem)?.Tag, type)) return; // another type was picked meanwhile
            imported = model.ImportedPlans();
            plans.Items.Clear();
            foreach (var plan in upcoming) plans.Items.Add(PlanItem(type, plan));
            if (recent.Count > 0)
            {
                plans.Items.Add(new ListBoxItem { Content = EditorKit.Section("RECENT", 8), IsEnabled = false, Focusable = false });
                foreach (var plan in recent) plans.Items.Add(PlanItem(type, plan));
            }
            Say(upcoming.Count + recent.Count == 0 ? $"{type.Name} has no plans yet." : "Pick a plan, then Import.");
            var first = plans.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is not null);
            if (first is not null) plans.SelectedItem = first;
        }
        catch (PlanningCenterException e)
        {
            Say(e.Message, problem: true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record PlanChoice(PcoServiceType Type, PcoPlan Plan);

    private ListBoxItem PlanItem(PcoServiceType type, PcoPlan plan)
    {
        var date = plan.SortDate is { } when ? when.ToLocalTime().ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture) : plan.Dates ?? "No date";
        var detail = $"{date} · {plan.ItemsCount} item{(plan.ItemsCount == 1 ? "" : "s")}";
        if (imported.ContainsKey(plan.Id)) detail += " · imported (importing again updates it)";
        return new ListBoxItem { Content = Row(plan.DisplayTitle ?? "Untitled plan", detail), Tag = new PlanChoice(type, plan), Padding = new Thickness(8, 6, 8, 6) };
    }

    private static StackPanel Row(string title, string? detail)
    {
        var row = new StackPanel();
        row.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        if (detail is not null) row.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = EditorKit.Res<Brush>("MutedText") });
        return row;
    }

    private void UpdateImportButton()
    {
        var choice = (plans.SelectedItem as ListBoxItem)?.Tag as PlanChoice;
        import.IsEnabled = choice is not null && !busy;
        import.Content = choice is not null && imported.ContainsKey(choice.Plan.Id) ? "Update service" : "Import";
    }

    // MARK: Importing

    /// <summary>Selects a plan by its id (the self-test drives the window with this).</summary>
    internal bool Select(string planId)
    {
        var item = plans.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is PlanChoice c && c.Plan.Id == planId);
        if (item is null) return false;
        plans.SelectedItem = item;
        return true;
    }

    /// <summary>Imports (or updates) the selected plan; on success the service opens in the main window and this closes.</summary>
    internal async Task<PlanImportSummary?> ImportSelectedAsync()
    {
        if ((plans.SelectedItem as ListBoxItem)?.Tag is not PlanChoice choice || busy) return null;
        busy = true;
        UpdateImportButton();
        types.IsEnabled = plans.IsEnabled = false;
        try
        {
            var summary = await model.ImportPlanAsync(choice.Type.Id, choice.Plan.Id, choice.Type.Name, new Progress<string>(text => Say(text)), closing.Token);
            Say(summary.Text);
            Close();
            return summary;
        }
        catch (PlanningCenterException e)
        {
            Say(e.Message, problem: true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            busy = false;
            types.IsEnabled = plans.IsEnabled = true;
            UpdateImportButton();
        }
        return null;
    }
}
