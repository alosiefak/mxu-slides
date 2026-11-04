using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The small badges on a slide tile for what the slide does when it goes live (fires a video, starts
/// a timer, clears a layer…): up to three icons, then "+N". Pointing at one for a moment names it,
/// in a label drawn inside the tile itself (MxU's 2026-11 update: "tile badges name their action on
/// hover again, as an in-tile overlay"), so the name never covers the next tile or gets cut off at
/// the window's edge the way a tooltip can. Sits over a tile's thumbnail; only the badges take the mouse.
/// </summary>
public sealed class TileActionBadges : Grid
{
    public static readonly DependencyProperty SlideProperty = DependencyProperty.Register(
        nameof(Slide), typeof(Slide), typeof(TileActionBadges), new PropertyMetadata(null, (d, _) => ((TileActionBadges)d).Rebuild()));

    /// <summary>The slide whose actions the badges show.</summary>
    public Slide? Slide
    {
        get => (Slide?)GetValue(SlideProperty);
        set => SetValue(SlideProperty, value);
    }

    private readonly StackPanel badges = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4) };
    private readonly Border tip = new()
    {
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4),
        Padding = new Thickness(7, 3, 7, 3), CornerRadius = new CornerRadius(4), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x1F, 0x1F, 0x1F)),
    };
    private readonly TextBlock tipText = new() { Foreground = Brushes.White, FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right };
    private readonly DispatcherTimer wait = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private string? pending;

    public TileActionBadges()
    {
        tip.Child = tipText;
        Children.Add(badges);
        Children.Add(tip);
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            if (pending is null) return;
            tipText.Text = pending;
            tip.Visibility = Visibility.Visible;
        };
    }

    private void Rebuild()
    {
        badges.Children.Clear();
        HideTip();
        var actions = Slide?.Actions ?? [];
        if (actions.Count == 0) return;
        var model = Window.GetWindow(this)?.DataContext as MainViewModel ?? Application.Current?.MainWindow?.DataContext as MainViewModel;
        var names = model?.ActionLabelNames();
        foreach (var action in actions.Take(3))
        {
            var label = names is null ? action.Kind.DisplayName() : SlideActions.Label(action, names);
            badges.Children.Add(Badge(EditorKit.Glyph(SlideActions.IconName(action)), label, isText: false));
        }
        if (actions.Count > 3)
            badges.Children.Add(Badge($"+{actions.Count - 3}", string.Join("\n", actions.Skip(3).Select(a => names is null ? a.Kind.DisplayName() : SlideActions.Label(a, names))), isText: true));
    }

    private Border Badge(string content, string label, bool isText)
    {
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x00, 0x00, 0x00)), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(3, 1, 3, 1), Margin = new Thickness(0, 0, 2, 0),
            Child = new TextBlock
            {
                Text = content, Foreground = Brushes.White, FontSize = isText ? 9 : 10,
                FontFamily = isText ? new FontFamily("Segoe UI") : EditorKit.IconFont, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        badge.MouseEnter += (_, _) => ShowTipSoon(label);
        badge.MouseLeave += (_, _) => HideTip();
        return badge;
    }

    private void ShowTipSoon(string label)
    {
        pending = label;
        wait.Stop();
        wait.Start();
    }

    private void HideTip()
    {
        pending = null;
        wait.Stop();
        tip.Visibility = Visibility.Collapsed;
    }

    /// <summary>For the self-test's picture: names the first badge now, as a pause over it would.</summary>
    internal bool ShowFirstTipNow()
    {
        if (badges.Children.Count == 0 || badges.Children[0] is not Border first) return false;
        first.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = MouseEnterEvent });
        wait.Stop();
        tipText.Text = pending ?? "";
        tip.Visibility = Visibility.Visible;
        return pending is not null;
    }

    internal string ShownTip => tip.Visibility == Visibility.Visible ? tipText.Text : "";
}
