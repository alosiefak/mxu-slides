using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Core.Follow;
using MxU.Slides.Windows.Follow;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Follow;

/// <summary>
/// The bar across the top of the main window while this computer backs up another (or was taken
/// over from), as the Mac's FollowBanner: a coloured dot (green live, blue connecting, red lost,
/// grey the booth quit), what is going on, and the buttons for it: Take Over, Stop Backing Up and
/// Try Again on a backup; Hand Back on a backup in charge; Take Back on a booth that stepped down;
/// Hide when a backup stopped following during a service. Placed by one line of MainWindow.xaml.
/// </summary>
public sealed class FollowBanner : UserControl
{
    private MainViewModel? model;

    public FollowBanner()
    {
        Visibility = Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            if (Application.Current?.MainWindow?.DataContext is not MainViewModel found || model is not null) return;
            model = found;
            model.Follow.Changed += Draw;
            Draw();
        };
    }

    private void Draw()
    {
        if (model is null) return;
        var follow = model.Follow;
        if (follow.SteppedDownTo is { } down)
        {
            var line = FollowLogic.SteppedDownBanner(down.BackupName);
            Bar(Colors.Orange, line.Title, follow.Problem ?? line.Detail, ("Take Back", () => follow.TakeBack()));
        }
        else if (follow.Paired is null && follow.DroppedBackup is { } dropped)
        {
            var line = FollowLogic.BackupDroppedBanner(dropped);
            Bar(Colors.Orange, line.Title, line.Detail, ("Hide", follow.HideDroppedBackup));
        }
        else if (follow.Paired is { } pairing)
        {
            if (follow.InCharge)
            {
                var line = FollowLogic.InChargeBanner(pairing.Name, pairing.TookOverAt ?? DateTimeOffset.Now, pairing.TookOverOnItsOwn ?? false);
                Bar(Color.FromRgb(0x3B, 0x82, 0xF6), line.Title, follow.Problem ?? line.Detail, ($"Hand Back to {pairing.Name}", () => _ = follow.HandBackAsync()));
            }
            else
            {
                var line = FollowLogic.Banner(pairing.Name, follow.State, follow.LastFire, follow.BoothQuit, follow.StandingBy);
                var buttons = new List<(string, Action)>();
                if (follow.State is FollowLogic.State.GaveUp) buttons.Add(("Try Again", follow.TryAgain));
                buttons.Add(("Take Over", () => _ = follow.TakeOverAsync()));
                buttons.Add(("Stop Backing Up", () => _ = follow.UnpairAsync()));
                Bar(Dot(follow), line.Title, follow.Problem ?? follow.CopyProblem ?? follow.CameBack ?? line.Detail, [.. buttons]);
            }
        }
        else
        {
            Content = null;
            Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The dot's colour, as the Mac's: grey once the booth quit, else by how the link stands.</summary>
    public static Color Dot(FollowController follow)
    {
        if (follow.BoothQuit && follow.State is not FollowLogic.State.Live) return Colors.Gray;
        return follow.State switch
        {
            FollowLogic.State.Live => Color.FromRgb(0x22, 0xC5, 0x5E),
            FollowLogic.State.Connecting => Color.FromRgb(0x3B, 0x82, 0xF6),
            _ => Color.FromRgb(0xEF, 0x44, 0x44),
        };
    }

    private void Bar(Color color, string title, string detail, params (string Label, Action Click)[] buttons)
    {
        var row = new DockPanel { LastChildFill = true };
        foreach (var (label, click) in buttons.Reverse())
        {
            var button = EditorKit.Button(label, click);
            button.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
        }
        var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(color), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = detail, FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(text);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x2E, color.R, color.G, color.B)),
            Padding = new Thickness(14, 8, 14, 8),
            Child = row,
        };
        Visibility = Visibility.Visible;
    }
}
