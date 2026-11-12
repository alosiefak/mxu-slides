using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MxU.Slides.Windows.Views.Notes;

/// <summary>
/// The badges a slide tile wears while the note that made it has a change waiting (the Mac's
/// NoteUpdateBadge): an orange "Update" when the passage's words changed, a red "Remove" when the
/// passage is gone, and a blue "+ Add" when a new passage's slide would go in after this one.
/// Clicking a badge raises <see cref="Clicked"/> (the main window opens Changes from the Notes)
/// and never fires the slide underneath. Hidden while there is nothing to say, so the tile
/// template can always include it.
/// </summary>
public sealed class NoteTileBadges : StackPanel
{
    public static readonly DependencyProperty UpdateProperty = DependencyProperty.Register(
        nameof(Update), typeof(string), typeof(NoteTileBadges), new PropertyMetadata(null, (d, _) => ((NoteTileBadges)d).Redraw()));

    public static readonly DependencyProperty AddsProperty = DependencyProperty.Register(
        nameof(Adds), typeof(string), typeof(NoteTileBadges), new PropertyMetadata(null, (d, _) => ((NoteTileBadges)d).Redraw()));

    // The Mac's colours: orange for new words, red for a removal, the accent blue for an addition.
    private static readonly Brush Orange = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
    private static readonly Brush Red = Frozen(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush Blue = Frozen(Color.FromRgb(0x0A, 0x84, 0xFF));

    public NoteTileBadges()
    {
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(4);
        Redraw();
    }

    /// <summary>"Update" or "Remove", or null for neither.</summary>
    public string? Update
    {
        get => (string?)GetValue(UpdateProperty);
        set => SetValue(UpdateProperty, value);
    }

    /// <summary>"+ Add" (or "+ Add 2"…), or null when no new slide waits after this one.</summary>
    public string? Adds
    {
        get => (string?)GetValue(AddsProperty);
        set => SetValue(AddsProperty, value);
    }

    /// <summary>A badge was clicked.</summary>
    public event EventHandler? Clicked;

    private void Redraw()
    {
        Children.Clear();
        if (!string.IsNullOrEmpty(Update))
            Children.Add(Capsule(Update, Update == "Remove" ? Red : Orange,
                Update == "Remove"
                    ? "The notes no longer have this slide's passage. Click to review the change."
                    : "The notes have new words for this slide. Click to review the change."));
        if (!string.IsNullOrEmpty(Adds))
            Children.Add(Capsule(Adds, Blue, "The notes have a new passage whose slide would go in after this one. Click to review it."));
        Visibility = Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private Border Capsule(string text, Brush color, string help)
    {
        var capsule = new Border
        {
            Background = color, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 4, 0),
            Cursor = Cursors.Hand, ToolTip = help,
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 9, FontWeight = FontWeights.Bold },
        };
        // Handled here, so the click never reaches the tile (which would put the slide on screen).
        capsule.MouseLeftButtonDown += (_, e) => e.Handled = true;
        capsule.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Clicked?.Invoke(this, EventArgs.Empty);
        };
        return capsule;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
