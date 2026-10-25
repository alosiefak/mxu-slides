using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The service picker beside SERVICE (Phase 8). It lists this week's service first: today's and
/// later ones in date order, then the undated ones, twelve at a time ("Show 12 more"); older
/// services wait under "Earlier", newest first. Each row has its date in a column. When the
/// service came from a Planning Center plan with several times, a chip beside it picks the time
/// this PC runs. Port of the Mac's servicePickerPopup (AppShell.swift); the grouping is Core's
/// (<see cref="ServicePicking"/>), and the plan-time chip is Windows-only (<see cref="ServicePlanTimes"/>).
/// </summary>
public sealed class ServicePicker : UserControl
{
    private readonly Button picker;
    private readonly TextBlock name = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock date = new() { FontSize = 10.5, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button time;
    private MainViewModel? model;
    private int shownCurrent = ServicePicking.Page;
    private int shownEarlier = ServicePicking.Page;
    private bool earlierOpen;
    private Popup? popup;

    public ServicePicker()
    {
        date.Foreground = EditorKit.Res<Brush>("FaintText");
        var label = new DockPanel();
        var chevron = new TextBlock { Text = "", FontFamily = EditorKit.IconFont, FontSize = 8, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("FaintText") };
        DockPanel.SetDock(chevron, Dock.Right);
        DockPanel.SetDock(date, Dock.Right);
        label.Children.Add(chevron);
        label.Children.Add(date);
        label.Children.Add(name);
        picker = new Button { Content = label, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 3, 8, 3), ToolTip = "Choose the service" };
        picker.Click += (_, _) => Open();
        time = OperatorKit.Chip("", "All times", "Which of the plan's times this computer runs: its rows and their order", chip => OpenTimes(chip));
        time.Margin = new Thickness(4, 0, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(time, Dock.Right);
        row.Children.Add(time);
        row.Children.Add(picker);
        Content = row;
        DataContextChanged += (_, _) => Attach(OperatorKit.Model(this));
    }

    private void Attach(MainViewModel? next)
    {
        if (model is not null) model.PropertyChanged -= OnModelChanged;
        model = next;
        if (model is not null) model.PropertyChanged += OnModelChanged;
        Refresh();
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedService) or nameof(MainViewModel.SelectedServicePlanTime)) Refresh();
    }

    /// <summary>The label and the time chip as they are now (the self-test reads these).</summary>
    internal string LabelText => $"{name.Text} {date.Text}".Trim();

    internal string TimeText { get; private set; } = "";

    private void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Refresh);
            return;
        }
        var service = model?.SelectedService;
        name.Text = service?.Name ?? "Choose a service…";
        date.Text = service is null ? "" : ServicePicking.DateColumnLabel(service.ServiceDate);
        var times = model?.SelectedServicePlanTimes ?? [];
        time.Visibility = times.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        TimeText = model?.SelectedServicePlanTime is { } chosen ? ServicePlanTimes.Label(chosen) : "All times";
        OperatorKit.SetChipText(time, TimeText);
    }

    // MARK: The list

    /// <summary>Opens the list as a click does (the self-test pictures it).</summary>
    internal Popup? Open()
    {
        if (model is null) return null;
        popup = OperatorKit.Popover(picker, Build(), width: Math.Max(300, picker.ActualWidth + 60));
        return popup;
    }

    private UIElement Build()
    {
        var rows = new StackPanel();
        if (model is null) return rows;
        var groups = model.ServicePickerGroups();
        foreach (var service in groups.Current.Take(shownCurrent)) rows.Children.Add(ServiceRow(service));
        if (groups.Current.Count > shownCurrent) rows.Children.Add(MoreRow(groups.Current.Count, shownCurrent, () => shownCurrent += ServicePicking.Page));
        if (groups.Earlier.Count > 0)
        {
            rows.Children.Add(EarlierRow(groups.Earlier.Count));
            if (earlierOpen)
            {
                foreach (var service in groups.Earlier.Take(shownEarlier)) rows.Children.Add(ServiceRow(service));
                if (groups.Earlier.Count > shownEarlier) rows.Children.Add(MoreRow(groups.Earlier.Count, shownEarlier, () => shownEarlier += ServicePicking.Page));
            }
        }
        if (groups.Current.Count + groups.Earlier.Count == 0) rows.Children.Add(EditorKit.Muted("No services yet.", 11, 4));
        var panel = new StackPanel();
        panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (!model.IsVolunteer)
        {
            panel.Children.Add(new Border { Height = 1, Background = EditorKit.Res<Brush>("Border"), Margin = new Thickness(0, 6, 0, 6) });
            panel.Children.Add(ActionRow("New Service", () =>
            {
                Close();
                model.NewServiceCommand.Execute(null);
            }));
        }
        return panel;
    }

    private void Close()
    {
        if (popup is not null) popup.IsOpen = false;
    }

    /// <summary>Rebuilds the open list in place (Show more, Earlier).</summary>
    private void Rebuild()
    {
        if (popup?.Child is Border { } frame) frame.Child = Build();
    }

    private Border RowFrame(UIElement content, Action click, string? tooltip = null)
    {
        var frame = new Border
        {
            Child = content, Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent,
            Cursor = Cursors.Hand, ToolTip = tooltip,
        };
        frame.MouseEnter += (_, _) => frame.Background = EditorKit.Res<Brush>("Hover");
        frame.MouseLeave += (_, _) => frame.Background = Brushes.Transparent;
        frame.MouseLeftButtonUp += (_, _) => click();
        return frame;
    }

    private Border ServiceRow(Service service)
    {
        var current = model?.SelectedService?.Id == service.Id;
        var row = new DockPanel();
        row.Children.Add(new TextBlock
        {
            Text = current ? "" : "", FontFamily = EditorKit.IconFont, FontSize = 10, Width = 16, VerticalAlignment = VerticalAlignment.Center,
            Foreground = EditorKit.Res<Brush>("Accent"),
        });
        var when = new TextBlock
        {
            Text = ServicePicking.DateColumnLabel(service.ServiceDate), Width = 92, TextAlignment = TextAlignment.Right, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = service.ServiceDate.Length == 0 ? EditorKit.Res<Brush>("FaintText") : EditorKit.Res<Brush>("MutedText"),
        };
        DockPanel.SetDock(when, Dock.Right);
        row.Children.Add(when);
        row.Children.Add(new TextBlock { Text = service.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal });
        var frame = RowFrame(row, () =>
        {
            Close();
            if (model is not null) model.SelectedService = service;
        }, service.Name);
        if (current) frame.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        return frame;
    }

    private Border MoreRow(int total, int shown, Action more)
    {
        var (adds, remaining) = ServicePicking.More(total, shown);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
        row.Children.Add(new TextBlock { Text = $"Show {adds} more", FontSize = 11, Foreground = EditorKit.Res<Brush>("MutedText") });
        row.Children.Add(new TextBlock { Text = $"  {remaining}", FontSize = 11, Foreground = EditorKit.Res<Brush>("FaintText") });
        return RowFrame(row, () =>
        {
            more();
            Rebuild();
        });
    }

    private Border EarlierRow(int count)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = earlierOpen ? "" : "", FontFamily = EditorKit.IconFont, FontSize = 8, Width = 16, VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("FaintText") });
        row.Children.Add(new TextBlock { Text = "Earlier", FontSize = 11, Foreground = EditorKit.Res<Brush>("MutedText") });
        row.Children.Add(new TextBlock { Text = $"  {count}", FontSize = 11, Foreground = EditorKit.Res<Brush>("FaintText") });
        return RowFrame(row, () =>
        {
            earlierOpen = !earlierOpen;
            Rebuild();
        }, "Services dated before today, newest first");
    }

    private Border ActionRow(string title, Action click) =>
        RowFrame(new TextBlock { Text = title, Margin = new Thickness(16, 0, 0, 0), Foreground = EditorKit.Res<Brush>("Text") }, click);

    /// <summary>Opens the list with Earlier open (the self-test pictures both groups).</summary>
    internal Popup? OpenWithEarlier()
    {
        earlierOpen = true;
        return Open();
    }

    // MARK: Plan times

    internal ContextMenu? OpenTimes(FrameworkElement under)
    {
        if (model?.SelectedService is not { } service) return null;
        var chosen = model.SelectedServicePlanTime?.HexId;
        List<object> items = [OperatorKit.Choice("All times (every row)", chosen is null, () => model.ChoosePlanTime(null))];
        items.Add(EditorKit.Separator());
        foreach (var planTime in ServicePlanTimes.Times(service))
        {
            var left = ServicePlanTimes.LeftOut(service, planTime.HexId);
            var title = ServicePlanTimes.Label(planTime) + (left > 0 ? $"  ·  {left} row{(left == 1 ? "" : "s")} not in it" : "");
            items.Add(OperatorKit.Choice(title, chosen == planTime.HexId, () => model.ChoosePlanTime(planTime.HexId)));
        }
        var menu = OperatorKit.Menu(items);
        OperatorKit.Open(menu, under);
        return menu;
    }

    internal ContextMenu? OpenTimes() => OpenTimes(time);
}
