using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The version chip beside the service picker: "Main" or the version this computer runs, shown
/// once the service has versions. Click it to choose (Service › Versions has the same menu).
/// </summary>
public sealed class ServiceVersionChip : Button
{
    public ServiceVersionChip()
    {
        Style = (Style)Application.Current.FindResource("GhostButton");
        Padding = new Thickness(6, 1, 6, 1);
        Margin = new Thickness(0, 0, 4, 0);
        FontSize = 11;
        ToolTip = "Which version of this service this computer runs";
        // The version's name and a chevron (a StringFormat wouldn't apply: Content isn't a string).
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 90, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MainViewModel.ServiceVersionName)));
        var chevron = new TextBlock
        {
            Text = "", FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 8,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(name);
        content.Children.Add(chevron);
        Content = content;
        SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(MainViewModel.ServiceHasVersions)) { Converter = new BoolToVisibility() });
        Click += (_, _) =>
        {
            if (DataContext is MainViewModel model && Window.GetWindow(this) is { } owner) ServiceMenus.ShowVersionMenu(owner, model, this);
        };
    }
}
