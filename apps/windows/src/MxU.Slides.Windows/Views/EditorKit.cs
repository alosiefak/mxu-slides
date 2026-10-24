using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Small building blocks the automation editors share (built in code, like
/// <see cref="BroadcastWindow"/>): labels, cards, number boxes, text boxes that save when you
/// leave them, the icons, and turning the Core's Add Action menu into a real WPF menu.
/// </summary>
internal static class EditorKit
{
    /// <summary>Windows' own icon font (Segoe Fluent Icons on Windows 11, MDL2 Assets on 10).</summary>
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static ResourceDictionary? styles;

    /// <summary>Views/EditorStyles.xaml, loaded once.</summary>
    public static ResourceDictionary Styles =>
        styles ??= (ResourceDictionary)Application.LoadComponent(new Uri("/MxUSlides;component/Views/EditorStyles.xaml", UriKind.Relative));

    /// <summary>A brush or style from Themes/Dark.xaml.</summary>
    public static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    /// <summary>
    /// The icon for one of the Mac's glyph names (the names a combo's icon and a step's
    /// icon are stored under: "combos", "timers", "alerts" …), as a Segoe icon character.
    /// </summary>
    public static string Glyph(string name) => name switch
    {
        "combos" => "\uE945",
        "timers" => "\uE916",
        "alerts" => "\uEA8F",
        "media" => "\uE8B9",
        "audio" => "\uE8D6",
        "screens" => "\uE7F4",
        "clear" => "\uE894",
        "midi" => "\uE765",
        "broadcast" => "\uE7C8",
        "overlays" => "\uE81E",
        "light" => "\uE82F",
        "presentations" => "\uE8A5",
        "confidence" => "\uE7F8",
        "folder" => "\uE8B7",
        "services" => "\uE8FD",
        _ => "\uE945",
    };

    public static TextBlock Icon(string glyphName, double size = 14, Brush? brush = null) => new()
    {
        Text = Glyph(glyphName), FontFamily = IconFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center,
        Foreground = brush ?? Res<Brush>("MutedText"),
    };

    /// <summary>A combo's colour (the Mac's "#RRGGBBAA"), or null when it has none.</summary>
    public static Brush? Tint(string? hex) => string.IsNullOrEmpty(hex) ? null : Painting.Brush(hex, SceneColor.White);

    // MARK: Text

    public static TextBlock Section(string text, double top = 14) => new()
    {
        Text = text, Style = Res<Style>("SectionLabel"), Margin = new Thickness(0, top, 0, 6),
    };

    public static TextBlock Muted(string text, double size = 11, double bottom = 6) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, Style = Res<Style>("Muted"), Margin = new Thickness(0, 0, 0, bottom),
    };

    public static TextBlock Faint(string text, double size = 10) => new()
    {
        Text = text, FontSize = size, Foreground = Res<Brush>("FaintText"), VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>A label on the left and its control on the right, one line of a form.</summary>
    public static DockPanel Row(string label, UIElement control, double labelWidth = 96, double bottom = 6)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, bottom) };
        var text = new TextBlock
        {
            Text = label, Width = labelWidth, VerticalAlignment = VerticalAlignment.Center, Foreground = Res<Brush>("MutedText"), FontSize = 12,
        };
        DockPanel.SetDock(text, Dock.Left);
        row.Children.Add(text);
        row.Children.Add(control);
        return row;
    }

    /// <summary>The rounded box each step, condition or combo sits in.</summary>
    public static Border Card(UIElement child, double bottom = 6) => new()
    {
        Child = child, Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)), BorderBrush = Res<Brush>("Border"),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 6),
        Margin = new Thickness(0, 0, 0, bottom),
    };

    // MARK: Buttons

    /// <summary>A small borderless button ("↑", "✕").</summary>
    public static Button Ghost(object content, string tooltip, Action click, double size = 12)
    {
        var button = new Button
        {
            Content = content, ToolTip = tooltip, Style = Res<Style>("GhostButton"), MinHeight = 22, FontSize = size,
            Padding = new Thickness(6, 0, 6, 0), Foreground = Res<Brush>("MutedText"),
        };
        button.Click += (_, _) => click();
        return button;
    }

    public static Button Button(string content, Action click, string? tooltip = null, bool accent = false)
    {
        var button = new Button { Content = content, ToolTip = tooltip, Margin = new Thickness(0, 0, 6, 0) };
        if (accent) button.Style = Res<Style>("AccentButton");
        button.Click += (_, _) => click();
        return button;
    }

    // MARK: Inputs

    /// <summary>
    /// A text box that saves when you press Enter or leave it, not on every key (the Mac's
    /// CommittedTextField). <paramref name="commit"/> returns the text to show afterwards
    /// (the tidied value, or the old one when the typing wasn't accepted). Esc puts the old text back.
    /// </summary>
    public static TextBox CommitBox(string text, Func<string, string> commit, string? hint = null, double width = double.NaN)
    {
        var box = new TextBox { Text = text, Tag = hint, Width = width, VerticalContentAlignment = VerticalAlignment.Center };
        var shown = text;
        void Commit()
        {
            if (box.Text == shown) return;
            shown = commit(box.Text);
            box.Text = shown;
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                box.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && box.Text != shown)
            {
                box.Text = shown;
                box.SelectAll();
                e.Handled = true;
            }
        };
        return box;
    }

    /// <summary>
    /// A whole-number box with − and + buttons. <paramref name="commit"/> stores a value and
    /// returns what was stored (the Core clamps it into range), which the box then shows.
    /// </summary>
    public static StackPanel NumberBox(int value, Func<int, int> commit, Func<int, string>? display = null, double width = 48, string? tooltip = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var detail = new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Res<Brush>("MutedText") };
        var current = value;
        TextBox? box = null;
        void Show(int shown)
        {
            current = shown;
            box!.Text = shown.ToString(CultureInfo.InvariantCulture);
            detail.Text = display?.Invoke(shown) ?? "";
        }
        box = CommitBox(value.ToString(CultureInfo.InvariantCulture), typed =>
        {
            if (int.TryParse(typed.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed)) current = commit(parsed);
            detail.Text = display?.Invoke(current) ?? "";
            return current.ToString(CultureInfo.InvariantCulture);
        }, width: width);
        box.TextAlignment = TextAlignment.Center;
        box.ToolTip = tooltip;
        panel.Children.Add(box);
        panel.Children.Add(Ghost("−", "Less", () => Show(commit(current - 1)), 13));
        panel.Children.Add(Ghost("+", "More", () => Show(commit(current + 1)), 13));
        panel.Children.Add(detail);
        detail.Text = display?.Invoke(value) ?? "";
        return panel;
    }

    /// <summary>A drop-down of choices that calls <paramref name="picked"/> when the person picks a different one.</summary>
    public static ComboBox Picker(IReadOnlyList<EditorChoice> choices, string? selectedId, Action<string> picked, double minWidth = 200)
    {
        var box = new ComboBox { ItemsSource = choices, MinWidth = minWidth, HorizontalAlignment = HorizontalAlignment.Left, MaxDropDownHeight = 420 };
        box.SelectedItem = choices.FirstOrDefault(c => c.Id == (selectedId ?? "")) ?? choices.FirstOrDefault();
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is EditorChoice choice) picked(choice.Id);
        };
        return box;
    }

    // MARK: Menus

    /// <summary>Builds the Add Action menu from the Core's menu tree; choosing a command calls <paramref name="picked"/> with a fresh step.</summary>
    public static ContextMenu ActionMenu(IReadOnlyList<ActionMenuItem> items, Action<SlideAction> picked)
    {
        var menu = new ContextMenu();
        foreach (var entry in MenuEntries(items, picked)) menu.Items.Add(entry);
        return menu;
    }

    /// <summary>The same menu as items to add to another menu (a slide's Actions › Add Action).</summary>
    public static IEnumerable<object> MenuEntries(IReadOnlyList<ActionMenuItem> items, Action<SlideAction> picked) =>
        items.Select(item => MenuEntry(item, picked));

    private static object MenuEntry(ActionMenuItem item, Action<SlideAction> picked)
    {
        if (item.IsSeparator) return new Separator();
        var entry = new MenuItem { Header = item.Title, Style = (Style)Styles["CascadingMenuItem"] };
        if (item.Children is { } children)
            foreach (var child in children) entry.Items.Add(MenuEntry(child, picked));
        else if (item.Make is { } make)
            entry.Click += (_, _) => picked(make());
        return entry;
    }

    /// <summary>A plain menu item for the editors' own menus.</summary>
    public static MenuItem Command(string title, Action click, bool enabled = true)
    {
        var item = new MenuItem { Header = title, Style = (Style)Styles["CascadingMenuItem"], IsEnabled = enabled };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>Opens a menu under a button.</summary>
    public static void Open(ContextMenu menu, FrameworkElement under)
    {
        menu.PlacementTarget = under;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // MARK: Look

    /// <summary>A window in the app's dark style, owned by <paramref name="owner"/>, centred over it.</summary>
    public static void Dress(Window window, Window? owner, string title, double width, double height)
    {
        window.Title = title;
        // NaN means "as big as its content" (a small dialog).
        if (!double.IsNaN(width))
        {
            window.Width = width;
            window.MinWidth = Math.Min(width, 560);
        }
        if (!double.IsNaN(height))
        {
            window.Height = height;
            window.MinHeight = Math.Min(height, 420);
        }
        window.ShowInTaskbar = false;
        window.Background = Res<Brush>("BasePlane");
        window.WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        if (owner is not null && owner.IsLoaded) window.Owner = owner;
        // Keep it on a small laptop screen.
        var area = SystemParameters.WorkArea;
        if (window.Width > area.Width - 24) window.Width = Math.Max(window.MinWidth, area.Width - 24);
        if (window.Height > area.Height - 24) window.Height = Math.Max(window.MinHeight, area.Height - 24);
    }

    /// <summary>Text an editor's "Run" or "Fire" button wears when there's nothing to run.</summary>
    public static string RunTooltip(int steps) => steps == 0 ? "Add an action first" : "Runs the actions now, in order";

    /// <summary>The label for a layer stored by its raw name, for summaries.</summary>
    public static string LayerName(string? raw) => LayerKinds.Parse(raw)?.DisplayName() ?? "Layer";
}
