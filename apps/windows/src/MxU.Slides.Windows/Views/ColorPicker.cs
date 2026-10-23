using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// A swatch button that opens a palette and a hex box. <see cref="Hex"/> is the
/// colour in the Mac's "#RRGGBBAA" form (empty means "no colour set").
/// </summary>
public sealed class ColorPicker : ContentControl
{
    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(
        nameof(Hex), typeof(string), typeof(ColorPicker),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorPicker)d).Refresh()));

    public string Hex
    {
        get => (string)GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    private static readonly string[] Palette =
    [
        "#FFFFFFFF", "#EDEDEDFF", "#B8B8B8FF", "#6E6E6EFF", "#2E2E2EFF", "#000000FF",
        "#3B82F6FF", "#60A5FAFF", "#22D3EEFF", "#7ED957FF", "#24B34CFF", "#0E3B33FF",
        "#FACC15FF", "#FFB826FF", "#F97316FF", "#EF4444FF", "#CC004EFF", "#F472B6FF",
        "#7600CCFF", "#C8A96AFF", "#E6D3A3FF", "#0F172AF2", "#1C1A17CC", "#00000000",
    ];

    private readonly Border swatch = new() { Width = 22, Height = 16, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1) };
    private readonly TextBlock label = new() { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
    private readonly Popup popup;
    private readonly TextBox hexBox = new() { Width = 110, Margin = new Thickness(0, 6, 0, 0) };

    public ColorPicker()
    {
        var button = new ToggleButton { Padding = new Thickness(6, 2, 8, 2), HorizontalContentAlignment = HorizontalAlignment.Left };
        button.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, label } };
        swatch.BorderBrush = (Brush)Application.Current.FindResource("Border");

        var grid = new UniformGrid { Columns = 6 };
        foreach (var hex in Palette)
        {
            var cell = new Button
            {
                Width = 26, Height = 22, Margin = new Thickness(2), Padding = new Thickness(0), ToolTip = hex,
                Background = hex == "#00000000" ? Checker() : Painting.Brush(hex, SceneColor.Black),
                BorderBrush = (Brush)Application.Current.FindResource("Border"),
            };
            cell.Click += (_, _) =>
            {
                Hex = hex;
                popup!.IsOpen = false;
            };
            grid.Children.Add(cell);
        }
        hexBox.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            ApplyHexBox();
            popup!.IsOpen = false;
        };
        hexBox.LostFocus += (_, _) => ApplyHexBox();
        popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = (Brush)Application.Current.FindResource("Card"),
                BorderBrush = (Brush)Application.Current.FindResource("Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8),
                Child = new StackPanel { Children = { grid, hexBox } },
            },
        };
        button.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(Popup.IsOpen)) { Source = popup, Mode = BindingMode.TwoWay });
        popup.Opened += (_, _) => hexBox.Text = Hex;
        Content = new Grid { Children = { button, popup } };
        Refresh();
    }

    private void ApplyHexBox()
    {
        var text = hexBox.Text.Trim();
        if (text.Length == 0) return;
        if (!text.StartsWith('#')) text = "#" + text;
        if (ColorHex.Color(text) is { } color) Hex = ColorHex.Hex(color);
    }

    private void Refresh()
    {
        var color = ColorHex.Color(Hex);
        swatch.Background = color is null ? Checker() : Painting.Brush(color.Value);
        label.Text = color is null ? "None" : Hex.Length >= 7 ? Hex[..7] : Hex;
    }

    private static Brush Checker()
    {
        var brush = new DrawingBrush
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 8),
            ViewportUnits = BrushMappingMode.Absolute,
            Drawing = new GeometryDrawing(Brushes.Gray, null, new GeometryGroup
            {
                Children = { new RectangleGeometry(new Rect(0, 0, 4, 4)), new RectangleGeometry(new Rect(4, 4, 4, 4)) },
            }),
        };
        brush.Freeze();
        return brush;
    }
}
