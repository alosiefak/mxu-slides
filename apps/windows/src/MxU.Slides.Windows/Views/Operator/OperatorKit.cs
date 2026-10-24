using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The building blocks the operator panels share (the right-hand column of the main window and
/// the live header's chips), built in code like <see cref="EditorKit"/>: small chips, menu items
/// with a tick, the Mac's tiny field labels and the HH:MM:SS boxes.
/// </summary>
internal static class OperatorKit
{
    /// <summary>The main window's view model, for panels placed in XAML (their DataContext).</summary>
    public static MainViewModel? Model(FrameworkElement element) => element.DataContext as MainViewModel;

    /// <summary>The panel's header: a section label on the left, its buttons on the right.</summary>
    public static DockPanel Header(string title, params UIElement[] trailing)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var item in trailing) buttons.Children.Add(item);
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(new TextBlock { Text = title, Style = EditorKit.Res<Style>("SectionLabel"), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
        return header;
    }

    /// <summary>A small rounded button with an icon (Segoe Fluent Icons glyph).</summary>
    public static Button IconButton(string glyph, string tooltip, Action click, double size = 12)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = EditorKit.IconFont, FontSize = size },
            ToolTip = tooltip, Style = EditorKit.Res<Style>("GhostButton"), MinHeight = 22, Padding = new Thickness(6, 2, 6, 2),
            Foreground = EditorKit.Res<Brush>("MutedText"), Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A compact text button ("Go Live", "Send Update", "Live").</summary>
    public static Button SmallButton(string text, string? tooltip, Action click, Brush? foreground = null, Brush? border = null)
    {
        var button = new Button
        {
            Content = text, ToolTip = tooltip, FontSize = 11, MinHeight = 20, Padding = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (foreground is not null) button.Foreground = foreground;
        if (border is not null) button.BorderBrush = border;
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A chip: a small button with an optional icon, a label and a ▾, that opens a menu or a popup.</summary>
    public static Button Chip(string? glyph, string text, string tooltip, Action<Button> click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph is not null)
            content.Children.Add(new TextBlock { Text = glyph, FontFamily = EditorKit.IconFont, FontSize = 11, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText") });
        content.Children.Add(new TextBlock { Text = text, Tag = "label", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        content.Children.Add(new TextBlock { Text = "", FontFamily = EditorKit.IconFont, FontSize = 7, Margin = new Thickness(5, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("FaintText") });
        var button = new Button
        {
            Content = content, ToolTip = tooltip, MinHeight = 22, Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) => click(button);
        return button;
    }

    /// <summary>The chip's label, to change in place.</summary>
    public static void SetChipText(Button chip, string text)
    {
        if (chip.Content is StackPanel { Children: var children } && children.OfType<TextBlock>().FirstOrDefault(t => t.Tag as string == "label") is { } label)
            label.Text = text;
    }

    /// <summary>A menu item with a tick column (the main window's menus don't draw IsChecked).</summary>
    public static MenuItem Choice(string title, bool chosen, Action pick, bool enabled = true)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = chosen ? "" : "", FontFamily = EditorKit.IconFont, FontSize = 11, Width = 18, VerticalAlignment = VerticalAlignment.Center,
            Foreground = EditorKit.Res<Brush>("Accent"),
        });
        header.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        var item = new MenuItem { Header = header, Style = (Style)EditorKit.Styles["CascadingMenuItem"], IsEnabled = enabled, Tag = title };
        item.Click += (_, _) => pick();
        return item;
    }

    /// <summary>A submenu (Folder ›, Duration ›) in the menus' style.</summary>
    public static MenuItem Submenu(string title, IEnumerable<object> items)
    {
        var menu = new MenuItem { Header = title, Style = (Style)EditorKit.Styles["CascadingMenuItem"], Tag = title };
        foreach (var item in items) menu.Items.Add(item);
        if (menu.Items.Count == 0) menu.IsEnabled = false;
        return menu;
    }

    /// <summary>The Mac's tiny upper-case field label ("NAME", "OUTPUT LOCATION").</summary>
    public static TextBlock FieldLabel(string text, double width = double.NaN) => new()
    {
        Text = text, FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = EditorKit.Res<Brush>("FaintText"),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Width = width,
    };

    /// <summary>A whole-number box clamped to 0…<paramref name="max"/> (the Mac's TimerTimeField); Enter applies.</summary>
    public static TextBox NumberField(int value, int max, Action<int> changed, Action? enter = null, double width = 38, string? hint = null)
    {
        var box = new TextBox
        {
            Text = value.ToString(CultureInfo.InvariantCulture), Width = width, TextAlignment = TextAlignment.Center, Tag = hint,
            Padding = new Thickness(2, 2, 2, 2), FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center,
        };
        void Read()
        {
            var parsed = int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var number) ? Math.Clamp(number, 0, max) : 0;
            changed(parsed);
        }
        box.TextChanged += (_, _) => Read();
        box.LostKeyboardFocus += (_, _) =>
        {
            var parsed = int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var number) ? Math.Clamp(number, 0, max) : 0;
            box.Text = parsed.ToString(CultureInfo.InvariantCulture);
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Read();
            enter?.Invoke();
            e.Handled = true;
        };
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
        return box;
    }

    /// <summary>":" between time boxes.</summary>
    public static TextBlock Colon() => new() { Text = ":", Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("FaintText") };

    /// <summary>A colour swatch (a timer warning's colour).</summary>
    public static Border Swatch(string hex, double width = 22, double height = 14) => new()
    {
        Width = width, Height = height, CornerRadius = new CornerRadius(3), Background = Painting.Brush(hex, SceneColor.White),
        BorderBrush = EditorKit.Res<Brush>("Border"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A rounded row background, tinted when live (the Mac's 6% wash).</summary>
    public static Border RowCard(UIElement child, Color? wash = null) => new()
    {
        Child = child, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 4),
        Background = new SolidColorBrush(wash ?? Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
    };

    public static readonly Color OrangeWash = Color.FromArgb(0x16, 0xFF, 0x95, 0x00);
    public static readonly Color GreenWash = Color.FromArgb(0x16, 0x34, 0xC7, 0x59);
    public static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(0xFF, 0x95, 0x00));
    public static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));

    /// <summary>Opens a menu under an element.</summary>
    public static void Open(ContextMenu menu, FrameworkElement under) => EditorKit.Open(menu, under);

    /// <summary>A menu in the app's style from items (choices, separators, submenus).</summary>
    public static ContextMenu Menu(IEnumerable<object> items)
    {
        var menu = new ContextMenu();
        foreach (var item in items) menu.Items.Add(item);
        return menu;
    }

    /// <summary>A popup under an element that closes when the operator clicks elsewhere (the Mac's popovers).</summary>
    public static Popup Popover(FrameworkElement under, UIElement content, double width = 240)
    {
        var popup = new Popup
        {
            PlacementTarget = under, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border
            {
                Child = content, Width = width, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(8),
                Background = EditorKit.Res<Brush>("Card"), BorderBrush = EditorKit.Res<Brush>("Border"), BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 4, 0, 0),
            },
        };
        popup.IsOpen = true;
        return popup;
    }
}
