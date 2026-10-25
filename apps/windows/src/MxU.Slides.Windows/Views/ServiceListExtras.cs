using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The service list's Phase 8 extras, attached to the main window's list in code so its XAML
/// stays as it was: choosing several rows (Ctrl-click and Shift-click, as in Explorer), and
/// collapsing a header's rows by clicking its chevron or double-clicking it. The right-click
/// menu's bulk items are added as it opens (<see cref="ServiceMenus.AddItemOptions"/>).
/// Port of the selection and collapsing in the Mac's ServicePlannerView.swift (RunOrderList).
/// </summary>
public static class ServiceListExtras
{
    public static void Attach(ListBox list, MainViewModel model)
    {
        list.SelectionMode = SelectionMode.Extended;
        // A row under a collapsed header is folded away: still in the list (Next walks it), not shown.
        list.Loaded += (_, _) =>
        {
            if (list.ItemContainerStyle is not null) return;
            var style = new Style(typeof(ListBoxItem), list.TryFindResource(typeof(ListBoxItem)) as Style);
            var folded = new DataTrigger { Binding = new Binding(nameof(ServiceItemViewModel.IsCollapsedAway)), Value = true };
            folded.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
            style.Triggers.Add(folded);
            list.ItemContainerStyle = style;
        };
        list.SelectionChanged += (_, _) => model.ChosenServiceRows = [.. list.SelectedItems.OfType<ServiceItemViewModel>()];
        list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // The chevron in front of a header's name folds its rows away.
            if (e.OriginalSource is TextBlock { Name: "Icon", DataContext: ServiceItemViewModel { IsHeader: true } header })
            {
                model.ToggleHeaderCollapsed(header.Item.Id);
                e.Handled = true;
            }
        };
        list.MouseDoubleClick += (_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is ServiceItemViewModel { IsHeader: true } header)
                model.ToggleHeaderCollapsed(header.Item.Id);
        };
    }

    /// <summary>The row under a point in the list (the self-test clicks headers this way).</summary>
    internal static ServiceItemViewModel? RowAt(ListBox list, Point point) =>
        (list.InputHitTest(point) as DependencyObject) is { } hit ? FindRow(hit) : null;

    private static ServiceItemViewModel? FindRow(DependencyObject node)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { DataContext: ServiceItemViewModel row }) return row;
        return null;
    }
}
