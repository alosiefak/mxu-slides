using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Animation;

namespace MxU.Slides.Windows.Views.Transitions;

/// <summary>
/// Choosing a transition: every kind as a small tile that plays itself, grouped as the catalogue
/// groups them (<see cref="WindowsTransitions.Group"/>), with the length and, for the kinds that
/// have them, the direction, softness and colour underneath. Tiles that have a direction or a
/// softness play with the ones chosen, so you can see what each would look like.
/// <para>
/// Used by the transition window (a slide, a presentation, a media item), the live header's chips
/// and Settings › Transitions. It only reports choices (<see cref="Changed"/>); whoever shows it
/// saves them.
/// </para>
/// </summary>
public sealed class TransitionPicker : UserControl
{
    private sealed record Tile(WindowsTransitionKind Kind, Border Frame, TransitionPreview Preview);

    private readonly List<Tile> tiles = [];
    private readonly Border? useDefault;
    private readonly StackPanel settings = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly Slider length = new() { Minimum = 0.1, Maximum = TransitionEdit.SliderMaximum, TickFrequency = TransitionEdit.DurationStep, IsSnapToTickEnabled = true, Width = 180 };
    private readonly TextBlock lengthLabel = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Width = 44 };
    private readonly Slider softness = new() { Minimum = 0, Maximum = 1, Width = 180, SmallChange = 0.05, LargeChange = 0.1 };
    private readonly TextBlock softnessLabel = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Width = 44 };
    private readonly Dictionary<TransitionDirection, ToggleButton> directions = [];
    private readonly ColorPicker color = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly FrameworkElement lengthRow, directionRow, softnessRow, colorRow;
    private bool updating;

    /// <param name="current">What's chosen now; null = the default.</param>
    /// <param name="defaultLabel">The default's words ("Presentation default (Dissolve 0.5 s)"), or null when there's no default to go back to (a room default).</param>
    /// <param name="compact">Smaller tiles, for the live header's popover.</param>
    public TransitionPicker(WindowsTransition? current, string? defaultLabel, bool compact = false)
    {
        Value = current;
        var tileWidth = compact ? 92.0 : 112.0;
        var panel = new StackPanel();

        if (defaultLabel is not null)
        {
            useDefault = new Border
            {
                Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = $"No transition of its own: {defaultLabel}", FontSize = 12 },
                ToolTip = "It uses the default, and follows it if the default changes",
            };
            useDefault.MouseLeftButtonUp += (_, _) => Choose(null);
            panel.Children.Add(useDefault);
        }

        var delay = 0.0;
        foreach (var group in WindowsTransitions.All.GroupBy(WindowsTransitions.Group))
        {
            panel.Children.Add(EditorKit.Section(group.Key.ToUpperInvariant(), top: 8));
            var row = new WrapPanel();
            foreach (var kind in group)
            {
                // Each tile starts a little after the last, so they don't all move at once.
                var preview = new TransitionPreview(TilePlays(kind), tileWidth, tileWidth * 9 / 16, delay);
                delay = (delay + 0.13) % 1.2;
                var name = new TextBlock
                {
                    Text = WindowsTransitions.Name(kind), FontSize = compact ? 10.5 : 11.5, Margin = new Thickness(2, 3, 2, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center,
                };
                var frame = new Border
                {
                    Child = new StackPanel { Children = { new Border { Child = preview, CornerRadius = new CornerRadius(3), ClipToBounds = true }, name } },
                    Padding = new Thickness(3), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(5),
                    BorderThickness = new Thickness(2), Cursor = Cursors.Hand,
                    ToolTip = WindowsTransitions.Describe(kind) + (WindowsTransitions.IsShared(kind) ? "" : "\nWindows only: a Mac plays " + TransitionEdit.MenuName(WindowsTransitions.Nearest(kind)) + "."),
                };
                frame.MouseLeftButtonUp += (_, _) => Choose(Value.WithKind(kind));
                AutomationName(frame, WindowsTransitions.Name(kind));
                tiles.Add(new Tile(kind, frame, preview));
                row.Children.Add(frame);
            }
            panel.Children.Add(row);
        }

        // The settings under the tiles: length always (but a cut), the rest when the kind has them.
        lengthRow = EditorKit.Row("Length", new StackPanel { Orientation = Orientation.Horizontal, Children = { length, lengthLabel } });
        var arrows = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var direction in Enum.GetValues<TransitionDirection>())
        {
            var button = new ToggleButton
            {
                Content = WindowsTransitions.Arrow(direction), Width = 34, Height = 26, Margin = new Thickness(0, 0, 4, 0), FontSize = 14,
                ToolTip = $"{direction}: the pictures move {direction.ToString().ToLowerInvariant()}",
            };
            button.Click += (_, _) => Choose(Value is { } v ? v with { Direction = direction } : null);
            directions[direction] = button;
            arrows.Children.Add(button);
        }
        directionRow = EditorKit.Row("Direction", arrows);
        softnessRow = EditorKit.Row("Softness", new StackPanel { Orientation = Orientation.Horizontal, Children = { softness, softnessLabel } });
        colorRow = EditorKit.Row("Colour", color);
        settings.Children.Add(lengthRow);
        settings.Children.Add(directionRow);
        settings.Children.Add(softnessRow);
        settings.Children.Add(colorRow);
        panel.Children.Add(settings);

        length.ValueChanged += (_, e) => lengthLabel.Text = TransitionEdit.Label(TransitionChips.Snapped(e.NewValue));
        softness.ValueChanged += (_, e) => softnessLabel.Text = $"{e.NewValue * 100:0} %";
        // Sliders report every step of a drag; the choice is made when the drag ends (or a key or click lands), as the Mac stores it.
        foreach (var slider in new[] { length, softness })
        {
            slider.LostMouseCapture += (_, _) => CommitSliders();
            slider.PreviewKeyUp += (_, _) => CommitSliders();
        }
        DependencyPropertyDescriptor.FromProperty(ColorPicker.HexProperty, typeof(ColorPicker)).AddValueChanged(color, (_, _) =>
        {
            if (!updating && Value is { } v && !string.IsNullOrEmpty(color.Hex)) Choose(v with { ColorHex = color.Hex.Length >= 7 ? color.Hex[..7] : color.Hex });
        });

        Content = panel;
        Show();
    }

    /// <summary>The choice now; null = the default (no transition of its own).</summary>
    public WindowsTransition? Value { get; private set; }

    /// <summary>The choice changed (a tile, a direction, a colour, or the end of a slider's drag).</summary>
    public event Action<WindowsTransition?>? Changed;

    /// <summary>Picks a kind as a click on its tile would (the self-test uses this).</summary>
    internal void Pick(WindowsTransitionKind kind) => Choose(Value.WithKind(kind));

    /// <summary>The tiles' previews, by kind (the self-test pictures them).</summary>
    internal IReadOnlyDictionary<WindowsTransitionKind, TransitionPreview> Previews => tiles.ToDictionary(t => t.Kind, t => t.Preview);

    private void Choose(WindowsTransition? choice)
    {
        if (Value == choice) return;
        Value = choice;
        Show();
        Changed?.Invoke(choice);
    }

    private void CommitSliders()
    {
        if (updating || Value is not { } v) return;
        var seconds = TransitionChips.Snapped(length.Value);
        var soft = Math.Round(softness.Value, 2);
        if (Math.Abs(seconds - v.DurationSeconds) > 1e-9 || Math.Abs(soft - v.Softness) > 1e-9)
            Choose(v with { DurationSeconds = seconds, Softness = soft });
    }

    /// <summary>What a tile plays: its kind with the chosen direction and softness, at a length that suits a thumbnail.</summary>
    private WindowsTransition TilePlays(WindowsTransitionKind kind)
    {
        var seconds = Math.Clamp(Value?.DurationSeconds ?? WindowsTransitions.DefaultSeconds, 0.5, 1.1);
        return Value.WithKind(kind) with { DurationSeconds = seconds };
    }

    /// <summary>Shows the choice: the chosen tile ringed, its settings, and every tile playing with them.</summary>
    private void Show()
    {
        updating = true;
        var accent = EditorKit.Res<Brush>("Accent");
        foreach (var tile in tiles)
        {
            var chosen = Value?.Kind == tile.Kind;
            tile.Frame.BorderBrush = chosen ? accent : Brushes.Transparent;
            tile.Frame.Background = chosen ? EditorKit.Res<Brush>("AccentWash") : Brushes.Transparent;
            tile.Preview.Transition = TilePlays(tile.Kind);
        }
        if (useDefault is not null)
        {
            useDefault.BorderBrush = Value is null ? accent : EditorKit.Res<Brush>("Border");
            useDefault.Background = Value is null ? EditorKit.Res<Brush>("AccentWash") : Brushes.Transparent;
        }
        var kind = Value?.Kind;
        settings.Visibility = kind is null ? Visibility.Collapsed : Visibility.Visible;
        lengthRow.Visibility = kind is { } k && WindowsTransitions.HasDuration(k) ? Visibility.Visible : Visibility.Collapsed;
        directionRow.Visibility = kind is { } d && WindowsTransitions.HasDirection(d) ? Visibility.Visible : Visibility.Collapsed;
        softnessRow.Visibility = kind is { } s && WindowsTransitions.HasSoftness(s) ? Visibility.Visible : Visibility.Collapsed;
        colorRow.Visibility = kind is { } c && WindowsTransitions.HasColor(c) ? Visibility.Visible : Visibility.Collapsed;
        if (Value is { } v)
        {
            length.Value = Math.Clamp(v.DurationSeconds, length.Minimum, length.Maximum);
            lengthLabel.Text = TransitionEdit.Label(v.DurationSeconds);
            softness.Value = v.Softness;
            softnessLabel.Text = $"{v.Softness * 100:0} %";
            foreach (var (direction, button) in directions) button.IsChecked = direction == v.Direction;
            color.Hex = WindowsTransitions.Color(v);
        }
        updating = false;
    }

    private static void AutomationName(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);
}
