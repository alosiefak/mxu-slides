using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Grid or Continuous, under the slides: one presentation at a time, or the whole service as one
/// scroll (Views/ContinuousSlidesView.xaml). A per-PC choice, kept in its settings (the Mac's
/// "present.continuous").
/// </summary>
public sealed class SlideViewSwitch : UserControl
{
    private readonly ToggleButton grid;
    private readonly ToggleButton continuous;
    private MainViewModel? model;

    public SlideViewSwitch()
    {
        grid = Segment("", "Grid: one song or presentation at a time");
        continuous = Segment("", "Continuous: the whole service as one scroll, each song's slides in its sections");
        grid.Click += (_, _) => Pick(false);
        continuous.Click += (_, _) => Pick(true);
        Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { grid, continuous } };
        Opacity = 0.8;
        DataContextChanged += (_, _) => Attach(OperatorKit.Model(this));
    }

    private static ToggleButton Segment(string glyph, string tooltip) => new()
    {
        Content = new TextBlock { Text = glyph, FontFamily = EditorKit.IconFont, FontSize = 11 },
        ToolTip = tooltip, Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 0, 2, 0), MinHeight = 22,
    };

    private void Attach(MainViewModel? next)
    {
        if (model is not null) model.PropertyChanged -= OnModelChanged;
        model = next;
        if (model is not null) model.PropertyChanged += OnModelChanged;
        Show();
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ShowsContinuous) or nameof(MainViewModel.IsEditing)) Show();
    }

    private void Pick(bool on)
    {
        if (model is not null) model.ShowsContinuous = on;
        Show();
    }

    private void Show()
    {
        var on = model?.ShowsContinuous == true;
        grid.IsChecked = !on;
        continuous.IsChecked = on;
        // The canvas editor fills the slide area in Edit mode: there's no grid to switch.
        Visibility = model?.IsEditing == true ? Visibility.Collapsed : Visibility.Visible;
    }
}
