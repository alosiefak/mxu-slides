using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Control;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Keys;

/// <summary>
/// The cheat sheet: press ? in the main window for every shortcut this PC has, at a glance, over
/// the window. Any key or a click closes it. In volunteer mode it lists only the keys a volunteer
/// can use. Windows-only (the Mac's keys are in its menus).
/// </summary>
public sealed class CheatSheet : Window
{
    private static CheatSheet? open;

    /// <summary>Opens the sheet over the window, or closes it when it's open.</summary>
    public static CheatSheet? Toggle(Window owner, MainViewModel model)
    {
        if (open is not null)
        {
            open.Close();
            return null;
        }
        open = new CheatSheet(owner, model);
        open.Closed += (_, _) => open = null;
        open.Show();
        return open;
    }

    /// <summary>The lines it shows (the self-test reads these).</summary>
    internal List<string> Lines { get; } = [];

    private CheatSheet(Window owner, MainViewModel model)
    {
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var columns = new WrapPanel { Orientation = Orientation.Vertical, MaxHeight = Math.Max(420, owner.ActualHeight - 160) };
        var map = model.KeyMap;
        foreach (var group in Enum.GetValues<KeyCommandGroup>())
        {
            var commands = Enum.GetValues<KeyCommand>()
                .Where(c => c.Group() == group && (map.ChordsFor(c).Count > 0 || c.FixedKeyDescription() is not null) && (!model.IsVolunteer || c.RunsInRunOnly()))
                .ToList();
            if (commands.Count == 0) continue;
            var block = Block(group.DisplayName());
            foreach (var command in commands)
                block.Children.Add(Line(command.DisplayName(), command.FixedKeyDescription() is { } fixedKeys ? Text(fixedKeys) : KeyChip.Caps(map.ChordsFor(command), size: 12)));
            columns.Children.Add(block);
        }
        var generated = map.BoundGeneratedKeys.Where(k => !model.IsVolunteer || k.Kind.RunsInRunOnly()).ToList();
        if (generated.Count > 0)
        {
            var block = Block("Combos, Looks, Overlays and Timers");
            foreach (var key in generated) block.Children.Add(Line(model.ShortcutLabel(key.MapKey), KeyChip.Caps([map.ChordFor(key)!], size: 12)));
            columns.Children.Add(block);
        }
        // One line per letter, in the palette's order ("Verse / Verse 1" share A).
        var letters = model.GroupHotKeyGroups
            .Select(g => (Letter: HotKeyJump.EffectiveHotKey(g), g.Name))
            .Where(g => g.Letter is not null)
            .GroupBy(g => g.Letter!)
            .ToList();
        if (letters.Count > 0)
        {
            var block = Block("Jump to a Section (the live song)");
            foreach (var letter in letters.Take(16))
                block.Children.Add(Line(string.Join(" / ", letter.Select(g => g.Name).Take(2)), KeyChip.Caps([new KeyChord(letter.Key)], size: 12)));
            columns.Children.Add(block);
        }

        var title = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var close = new TextBlock { Text = "Press any key or click to close", Foreground = EditorKit.Res<Brush>("FaintText"), FontSize = 11, VerticalAlignment = VerticalAlignment.Bottom };
        DockPanel.SetDock(close, Dock.Right);
        title.Children.Add(close);
        title.Children.Add(new TextBlock { Text = model.IsVolunteer ? "Keyboard Shortcuts · Volunteer Mode" : "Keyboard Shortcuts", FontSize = 17, FontWeight = FontWeights.SemiBold });
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1F, 0x1F, 0x1E)), BorderBrush = EditorKit.Res<Brush>("Border"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(22, 16, 22, 16), Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, Opacity = 0.5, ShadowDepth = 4 },
            Child = new StackPanel { Children = { title, columns } },
        };
        PreviewKeyDown += (_, e) =>
        {
            if (!KeyTokens.IsModifierOnly(KeyTokens.RealKey(e)))
            {
                e.Handled = true;
                CloseOnce();
            }
        };
        MouseLeftButtonUp += (_, _) => CloseOnce();
        // Clicking another window closes it too. Closing makes it lose the focus, which would close
        // it a second time, and WPF refuses that: hence the flag.
        Closing += (_, _) => closing = true;
        Deactivated += (_, _) =>
        {
            if (IsLoaded) CloseOnce();
        };
    }

    private bool closing;

    private void CloseOnce()
    {
        if (closing) return;
        closing = true;
        Close();
    }

    private static StackPanel Block(string title) => new()
    {
        Width = 380, Margin = new Thickness(0, 0, 24, 14),
        Children = { new TextBlock { Text = title.ToUpperInvariant(), Style = EditorKit.Res<Style>("SectionLabel"), Margin = new Thickness(0, 0, 0, 6) } },
    };

    private DockPanel Line(string name, UIElement keys)
    {
        Lines.Add(name);
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(keys, Dock.Right);
        line.Children.Add(keys);
        line.Children.Add(new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0) });
        return line;
    }

    private static TextBlock Text(string text) => new() { Text = text, FontSize = 12, Foreground = EditorKit.Res<Brush>("MutedText"), VerticalAlignment = VerticalAlignment.Center };
}
