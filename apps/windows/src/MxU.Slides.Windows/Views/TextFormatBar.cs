using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Styling the selected words of a text box: Bold, Italic, Underline, Strikethrough, a colour, a
/// size and letter spacing, as the Mac's canvas text editor offers on its selection. Select words in
/// the inspector's text box (<see cref="Target"/>), then press a button: only those words change,
/// as a style run (ObjectInspectorViewModel.TextTools.cs). With nothing selected, the buttons
/// style every word. The bar's buttons don't take the keyboard, so the selection stays put.
/// </summary>
public sealed class TextFormatBar : WrapPanel
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(TextBox), typeof(TextFormatBar), new PropertyMetadata(null, (d, e) => ((TextFormatBar)d).OnTarget(e.OldValue as TextBox)));

    /// <summary>The text box whose selected words the bar styles.</summary>
    public TextBox? Target
    {
        get => (TextBox?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    private readonly Button bold;
    private readonly Button italic;
    private readonly Button underline;
    private readonly Button strike;
    private readonly Button caps;
    private readonly ColorPicker colour = new() { ToolTip = "Colour of the selected words" };
    private readonly TextBox size = new() { Width = 42, ToolTip = "Size of the selected words (Enter)" };
    private readonly TextBox spacing = new() { Width = 42, ToolTip = "Letter spacing of the selected words (Enter)" };
    private bool showing;

    public TextFormatBar()
    {
        Margin = new Thickness(0, 0, 0, 6);
        bold = Toggle("B", "Bold the selected words", i => i.ToggleBold);
        bold.FontWeight = FontWeights.Bold;
        italic = Toggle("I", "Italicise the selected words", i => i.ToggleItalic);
        italic.FontStyle = FontStyles.Italic;
        underline = Toggle("U", "Underline the selected words", i => i.ToggleUnderline);
        strike = Toggle("S", "Strike through the selected words", i => i.ToggleStrikethrough);
        caps = Toggle("AA", "All Caps for the selected words", i => i.ToggleCaps);
        caps.Width = 30;
        caps.FontSize = 10;
        Children.Add(new TextBlock { Text = "Words:", Style = EditorKit.Res<Style>("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Select words in the text above, then style just them" });
        Children.Add(bold);
        Children.Add(italic);
        Children.Add(underline);
        Children.Add(strike);
        Children.Add(caps);
        colour.Margin = new Thickness(4, 0, 6, 0);
        DependencyPropertyDescriptor.FromProperty(ColorPicker.HexProperty, typeof(ColorPicker)).AddValueChanged(colour, (_, _) =>
        {
            if (showing || Inspector is not { } inspector || Target is not { } box) return;
            var hex = colour.Hex;
            inspector.StyleSelection(box.SelectionStart, box.SelectionLength, r => r.ColorHex = string.IsNullOrEmpty(hex) ? null : hex);
        });
        Children.Add(colour);
        Children.Add(Label("Size"));
        Children.Add(size);
        Children.Add(Label("Spacing"));
        Children.Add(spacing);
        size.KeyDown += (_, e) => Commit(e, size, (r, v) => r.FontSize = v is > 0 ? Math.Min(v.Value, 1000) : null);
        spacing.KeyDown += (_, e) => Commit(e, spacing, (r, v) => r.Tracking = v is { } t && t != 0 ? t : null);
        var clear = new Button { Content = "Plain", ToolTip = "Take the selected words' own styling away", Focusable = false, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(6, 0, 0, 0) };
        clear.Click += (_, _) =>
        {
            if (Inspector is { } inspector && Target is { } box) inspector.ClearSelectionStyle(box.SelectionStart, box.SelectionLength);
            ShowSelection();
        };
        Children.Add(clear);
    }

    private ObjectInspectorViewModel? Inspector => DataContext as ObjectInspectorViewModel;

    private static TextBlock Label(string text) =>
        new() { Text = text, Style = EditorKit.Res<Style>("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };

    private Button Toggle(string text, string tip, Func<ObjectInspectorViewModel, Action<int, int>> action)
    {
        var button = new Button { Content = text, ToolTip = tip, Width = 26, Focusable = false, Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 0) };
        button.Click += (_, _) =>
        {
            if (Inspector is not { } inspector || Target is not { } box) return;
            action(inspector)(box.SelectionStart, box.SelectionLength);
            ShowSelection();
        };
        return button;
    }

    private void Commit(KeyEventArgs e, TextBox from, Action<MxU.Slides.Core.Model.TextStyleRun, double?> apply)
    {
        if (e.Key != Key.Enter || Inspector is not { } inspector || Target is not { } box) return;
        double? value = double.TryParse(from.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ? v : null;
        inspector.StyleSelection(box.SelectionStart, box.SelectionLength, r => apply(r, value));
        e.Handled = true;
        box.Focus(); // back to the words, the selection still there
    }

    private void OnTarget(TextBox? old)
    {
        if (old is not null) old.SelectionChanged -= OnSelectionChanged;
        if (Target is { } box)
        {
            // The selection stays visible while a button or a box of the bar is used.
            box.IsInactiveSelectionHighlightEnabled = true;
            box.SelectionChanged += OnSelectionChanged;
        }
        ShowSelection();
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e) => ShowSelection();

    /// <summary>The bar shows the style under the selection: B lit when it's bold, its colour and size.</summary>
    private void ShowSelection()
    {
        if (Inspector is not { } inspector || Target is not { } box) return;
        showing = true;
        try
        {
            var run = inspector.RunAt(box.SelectionStart, box.SelectionLength);
            Light(bold, inspector.IsBoldAt(box.SelectionStart, box.SelectionLength));
            Light(italic, inspector.IsItalicAt(box.SelectionStart, box.SelectionLength));
            Light(underline, run?.Underline == true);
            Light(strike, run?.Strikethrough == true);
            Light(caps, inspector.IsCapsAt(box.SelectionStart, box.SelectionLength));
            colour.Hex = run?.ColorHex ?? "";
            size.Text = run?.FontSize is { } s ? Math.Round(s, 1).ToString(CultureInfo.CurrentCulture) : "";
            spacing.Text = run?.Tracking is { } t ? Math.Round(t, 1).ToString(CultureInfo.CurrentCulture) : "";
        }
        finally
        {
            showing = false;
        }
    }

    private static void Light(Button button, bool on) =>
        button.BorderBrush = on ? EditorKit.Res<System.Windows.Media.Brush>("Accent") : EditorKit.Res<System.Windows.Media.Brush>("Border");
}
