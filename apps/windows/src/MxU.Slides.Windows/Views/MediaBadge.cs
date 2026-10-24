using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The little orange warning on a tile: "FILE MISSING" or "NEEDS TRANSCODE" on a media tile,
/// a warning triangle (<see cref="Compact"/>) on a slide that shows missing media, as the Mac
/// draws them. <see cref="Detail"/> is the tooltip saying why. Hidden while <see cref="Text"/>
/// is empty, so a template can always include it.
/// </summary>
public sealed class MediaBadge : Border
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MediaBadge), new PropertyMetadata(null, (d, _) => ((MediaBadge)d).Update()));

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail), typeof(string), typeof(MediaBadge), new PropertyMetadata(null, (d, _) => ((MediaBadge)d).Update()));

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(MediaBadge), new PropertyMetadata(false, (d, _) => ((MediaBadge)d).Update()));

    private static readonly Brush Orange = Frozen(Color.FromRgb(0xF0, 0x8A, 0x24));
    private static readonly Brush Red = Frozen(Color.FromRgb(0xDC, 0x26, 0x26));
    private readonly TextBlock label = new() { Foreground = Brushes.White, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };

    public MediaBadge()
    {
        Child = label;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(4);
        Update();
    }

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Detail
    {
        get => (string?)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    /// <summary>Just the warning triangle, in a circle (for slide tiles).</summary>
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    private void Update()
    {
        Visibility = string.IsNullOrEmpty(Text) ? Visibility.Collapsed : Visibility.Visible;
        ToolTip = string.IsNullOrEmpty(Detail) ? Text : Detail;
        Background = Text?.Contains("FAILED", StringComparison.Ordinal) == true ? Red : Orange;
        if (Compact)
        {
            label.Text = "";
            label.FontFamily = EditorKit.IconFont;
            label.FontSize = 10;
            CornerRadius = new CornerRadius(9);
            Padding = new Thickness(4);
        }
        else
        {
            label.Text = Text;
            label.FontFamily = SystemFonts.MessageFontFamily;
            label.FontSize = 9;
            CornerRadius = new CornerRadius(3);
            Padding = new Thickness(5, 1, 5, 1);
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
