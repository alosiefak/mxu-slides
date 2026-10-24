using System.Windows;
using System.Windows.Controls;

namespace MxU.Slides.Windows.Views;

/// <summary>Service › Change date…: a calendar to pick the service's date (the Mac's graphical date picker). Built in code, like InputDialog.</summary>
public static class ServiceDateWindow
{
    public static DateOnly? Ask(Window owner, string serviceName, DateOnly? current)
    {
        var start = current ?? DateOnly.FromDateTime(DateTime.Today);
        var calendar = new Calendar
        {
            SelectedDate = start.ToDateTime(TimeOnly.MinValue),
            DisplayDate = start.ToDateTime(TimeOnly.MinValue),
            Margin = new Thickness(0, 6, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Style = (Style)Application.Current.FindResource("AccentButton") };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = serviceName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 });
        panel.Children.Add(calendar);
        panel.Children.Add(buttons);
        var window = new Window
        {
            Title = "Service date",
            Owner = owner,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true && calendar.SelectedDate is { } picked ? DateOnly.FromDateTime(picked) : null;
    }
}
