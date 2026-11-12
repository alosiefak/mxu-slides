using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Control;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Volunteer;

/// <summary>
/// Settings › Present's Phase 8 part: Volunteer Mode (on or off, the panels volunteers keep, and an
/// optional PIN to leave it) and Keyboard Shortcuts. All of it is this PC's own (settings.json).
/// The Mac's rows are "Volunteer Mode" and "Tabs volunteers keep" (SettingsView.swift).
/// </summary>
public sealed class OperatorSettingsSection : UserControl
{
    private readonly TextBlock pinStatus = new() { Style = EditorKit.Res<Style>("Muted"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button pinButton = new() { Padding = new Thickness(10, 2, 10, 2) };
    private readonly Button removePin = new() { Content = "Remove PIN", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
    private MainViewModel? model;

    public OperatorSettingsSection()
    {
        var panel = new StackPanel();
        var enabled = new CheckBox
        {
            Content = "Volunteer Mode", Margin = new Thickness(0, 6, 0, 0),
            ToolTip = "Volunteers run slides, the clears and the panels below, with editing hidden. Leaving asks for the PIN when one is set.",
        };
        panel.Children.Add(enabled);
        panel.Children.Add(Caption("Adds Volunteer beside Show and Edit: run the show with editing locked."));
        var panels = new WrapPanel { Margin = new Thickness(22, 0, 0, 6) };
        panel.Children.Add(new TextBlock { Text = "Panels volunteers keep", Margin = new Thickness(22, 2, 0, 4), FontSize = 12 });
        panel.Children.Add(panels);
        var pinRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 0, 0, 6) };
        pinRow.Children.Add(pinButton);
        pinRow.Children.Add(removePin);
        pinRow.Children.Add(pinStatus);
        panel.Children.Add(pinRow);

        panel.Children.Add(new TextBlock { Text = "KEYBOARD", Style = EditorKit.Res<Style>("SectionLabel"), Margin = new Thickness(0, 18, 0, 6) });
        var keys = new Button { Content = "Keyboard Shortcuts…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 3, 10, 3) };
        panel.Children.Add(keys);
        panel.Children.Add(Caption("See and change every shortcut on this computer. Press ? in the main window for the cheat sheet.", left: 0));
        Content = panel;

        Loaded += (_, _) =>
        {
            if (model is not null || Application.Current?.MainWindow?.DataContext is not MainViewModel found) return;
            model = found;
            enabled.IsChecked = model.VolunteerModeEnabled;
            enabled.Click += (_, _) => model.VolunteerModeEnabled = enabled.IsChecked == true;
            foreach (var choice in VolunteerPanels.All)
            {
                var box = new CheckBox { Content = choice.Title(), IsChecked = model.VolunteerKeeps(choice), Margin = new Thickness(0, 0, 12, 4) };
                box.Click += (_, _) => model.SetVolunteerPanel(choice, box.IsChecked == true);
                panels.Children.Add(box);
            }
            pinButton.Click += (_, _) => SetPin();
            removePin.Click += (_, _) =>
            {
                model.SetVolunteerPin(null);
                ShowPin();
            };
            keys.Click += (_, _) => Keys.ShortcutsWindow.Open(Window.GetWindow(this), model);
            ShowPin();
        };
    }

    private void ShowPin()
    {
        var has = model?.HasVolunteerPin == true;
        pinButton.Content = has ? "Change PIN…" : "Set a PIN…";
        removePin.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        pinStatus.Text = has ? "Leaving Volunteer Mode asks for the PIN." : "No PIN: anyone can leave Volunteer Mode.";
    }

    private void SetPin()
    {
        if (model is null) return;
        var owner = Window.GetWindow(this);
        if (PinDialog.Ask(owner, "Volunteer Mode PIN", "A PIN of 4 to 8 digits. It's kept on this computer only.") is not { } pin) return;
        if (PinDialog.Ask(owner, "Volunteer Mode PIN", "Type the PIN again.") != pin)
        {
            MessageBox.Show(owner, "The two PINs didn't match, so nothing changed.", "Volunteer Mode PIN");
            return;
        }
        if (model.SetVolunteerPin(pin) is { } problem) MessageBox.Show(owner, problem, "Volunteer Mode PIN");
        ShowPin();
    }

    private static TextBlock Caption(string text, double left = 22) => new()
    {
        Text = text, Style = EditorKit.Res<Style>("Muted"), FontSize = 11, Margin = new Thickness(left, 0, 0, 6), TextWrapping = TextWrapping.Wrap,
    };
}
