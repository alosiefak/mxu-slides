using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MxU.Slides.Windows.Views.Volunteer;

/// <summary>A small box that asks for volunteer mode's PIN; the digits show as dots.</summary>
public sealed class PinDialog : Window
{
    private readonly PasswordBox box = new() { MaxLength = 8, FontSize = 18, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 8, 0, 12) };
    private string? result;

    private PinDialog(Window? owner, string title, string message)
    {
        EditorKit.Dress(this, owner, title, double.NaN, double.NaN);
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        var ok = new Button { Content = "OK", IsDefault = true, Style = EditorKit.Res<Style>("AccentButton"), MinWidth = 80, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) =>
        {
            result = box.Password;
            DialogResult = true;
        };
        // Digits only (a PIN is 4 to 8 of them).
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !text.All(char.IsAsciiDigit)) e.CancelCommand();
        });
        Content = new StackPanel
        {
            Margin = new Thickness(18), Width = 300,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        Loaded += (_, _) => Keyboard.Focus(box);
    }

    /// <summary>Asks for a PIN; null when cancelled.</summary>
    public static string? Ask(Window? owner, string title, string message)
    {
        var dialog = new PinDialog(owner, title, message);
        return dialog.ShowDialog() == true ? dialog.result : null;
    }
}
