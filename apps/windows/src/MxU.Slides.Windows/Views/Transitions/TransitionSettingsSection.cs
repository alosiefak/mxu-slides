using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Transitions;

/// <summary>
/// Settings › Transitions: the two room defaults (slides; backgrounds and videos), each playing in
/// a small preview, with Change… opening the same popover as the live header's chips. Changes save
/// and take effect at once, as Settings › Present's do. Placed in the Settings window by one line of
/// its XAML; it finds the app's view model itself.
/// </summary>
public sealed class TransitionSettingsSection : UserControl
{
    private readonly List<(bool Media, TransitionPreview Preview, TextBlock Caption)> rows = [];
    private MainViewModel? model;

    public TransitionSettingsSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "TRANSITIONS", Style = EditorKit.Res<Style>("SectionLabel"), Margin = new Thickness(0, 18, 0, 6) });
        foreach (var (media, label, help) in new[]
                 {
                     (false, "Slides", "When a slide and its presentation don't choose a transition"),
                     (true, "Media", "When a background or video doesn't choose a transition"),
                 })
        {
            var preview = new TransitionPreview(new Core.Animation.WindowsTransition { Kind = Core.Animation.WindowsTransitionKind.Dissolve }, 112, 63);
            var caption = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), MinWidth = 150 };
            var change = new Button { Content = "Change…", VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 2, 10, 2) };
            change.Click += (_, _) => RoomTransitionPopover.Open(change, model, media);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8), ToolTip = help };
            var name = new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);
            row.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { new Border { Child = preview, CornerRadius = new CornerRadius(4), ClipToBounds = true }, caption, change },
            });
            panel.Children.Add(row);
            rows.Add((media, preview, caption));
        }
        panel.Children.Add(new TextBlock
        {
            Text = "Slides, presentations and media items can have their own: right-click one and choose Transition.",
            Style = EditorKit.Res<Style>("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap,
        });
        Content = panel;

        Loaded += (_, _) =>
        {
            if (Application.Current?.MainWindow?.DataContext is not MainViewModel found) return;
            model = found;
            model.TransitionsChanged += Refresh;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (model is not null) model.TransitionsChanged -= Refresh;
        };
    }

    private void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Refresh);
            return;
        }
        if (model is null) return;
        foreach (var (media, preview, caption) in rows)
        {
            var choice = model.RoomTransitionChoice(media);
            preview.Transition = choice with
            {
                DurationSeconds = Infrastructure.OutputTransitions.PlayedSeconds(choice.Kind, choice.DurationSeconds),
            };
            caption.Text = Core.Animation.WindowsTransitions.Describe(choice);
        }
    }
}
