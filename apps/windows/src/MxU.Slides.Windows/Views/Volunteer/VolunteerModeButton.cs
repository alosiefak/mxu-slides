using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Volunteer;

/// <summary>
/// "Volunteer" beside Show and Edit in the toolbar: it appears once Settings › Present turns
/// volunteer mode on, and switches the window into it (the Mac's mode switcher, VolunteerModeLogic).
/// </summary>
public sealed class VolunteerModeButton : UserControl
{
    private readonly ToggleButton button;
    private MainViewModel? model;

    public VolunteerModeButton()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = "", FontFamily = EditorKit.IconFont, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = "Volunteer" });
        button = new ToggleButton
        {
            Content = content, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Volunteer Mode: run the show with editing locked (leaving it asks for the PIN when one is set)",
        };
        button.Click += (_, _) =>
        {
            button.IsChecked = false;
            model?.EnterVolunteer();
        };
        Content = button;
        DataContextChanged += (_, _) => Attach(OperatorKit.Model(this));
    }

    private void Attach(MainViewModel? next)
    {
        if (model is not null) model.VolunteerChanged -= Refresh;
        model = next;
        if (model is not null) model.VolunteerChanged += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Refresh);
            return;
        }
        // The control itself stays (its margin spaces the toolbar); only the button comes and goes.
        button.Visibility = model?.VolunteerModeEnabled == true ? Visibility.Visible : Visibility.Collapsed;
    }
}
