using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Settings › Present: the two present settings the Mac has that Windows had no control for,
/// with the Mac's wording ("Show a repeated chord-only section once", "Confidence NEXT skips blank
/// slides"). Each saves and takes effect at once. Placed in the Settings window by one line of
/// its XAML; it finds the app's view model itself.
/// </summary>
public sealed class PresentSettingsSection : UserControl
{
    public PresentSettingsSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "PRESENT", Style = EditorKit.Res<Style>("SectionLabel"), Margin = new Thickness(0, 18, 0, 6) });
        var emptyRepeats = new CheckBox
        {
            Content = "Show a repeated chord-only section once",
            ToolTip = "When a song plays the same section twice in a row and it has only chords or nothing on it, it shows once. "
                + "Off, every repeat the order calls for is a slide to click. Sections with words, media or different chords always play.",
        };
        panel.Children.Add(emptyRepeats);
        panel.Children.Add(Caption("An Interlude played twice shows once."));
        var skipBlanks = new CheckBox
        {
            Content = "Confidence NEXT skips blank slides",
            ToolTip = "Blank slides parked as clears are ignored: NEXT shows the first upcoming slide that has words.",
        };
        panel.Children.Add(skipBlanks);
        panel.Children.Add(Caption("NEXT shows the next slide with words."));
        Content = panel;

        Loaded += (_, _) =>
        {
            if (Application.Current?.MainWindow?.DataContext is not MainViewModel model) return;
            emptyRepeats.IsChecked = model.EmptyRepeatsOnce;
            skipBlanks.IsChecked = model.NextSkipsBlanks;
            emptyRepeats.Click += (_, _) => model.EmptyRepeatsOnce = emptyRepeats.IsChecked == true;
            skipBlanks.Click += (_, _) => model.NextSkipsBlanks = skipBlanks.IsChecked == true;
        };
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text, Style = EditorKit.Res<Style>("Muted"), FontSize = 11, Margin = new Thickness(22, 0, 0, 6),
    };
}
