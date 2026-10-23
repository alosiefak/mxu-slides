using System.Windows;
using System.Windows.Controls;

namespace MxU.Slides.Windows.Views;

/// <summary>A small "type a value" prompt, built in code so it needs no XAML.</summary>
public static class InputDialog
{
    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 380, Margin = new Thickness(0, 6, 0, 14) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Style = (Style)Application.Current.FindResource("AccentButton") };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var window = new Window
        {
            Title = title,
            Owner = owner,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return window.ShowDialog() == true ? box.Text : null;
    }
}
