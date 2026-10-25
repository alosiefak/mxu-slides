using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Core.Control;

namespace MxU.Slides.Windows.Views.Keys;

/// <summary>
/// A shortcut's keys drawn as keycaps ("Ctrl" "Shift" "N"), several chords side by side; "—" for
/// none, or a fixed description ("0–9 then Enter"). Clicking it starts recording (the shortcuts
/// window listens for the next key). Port of the Mac's KeyChordChip (KeyboardSettingsPane.swift).
/// </summary>
internal sealed class KeyChip : Button
{
    public KeyChip(IReadOnlyList<KeyChord> chords, string? fixedText, bool conflicted, Action record)
    {
        Style = EditorKit.Res<Style>("GhostButton");
        Padding = new Thickness(4, 1, 4, 1);
        MinHeight = 24;
        HorizontalAlignment = HorizontalAlignment.Right;
        IsEnabled = fixedText is null;
        ToolTip = fixedText is null ? "Click, then press the new shortcut (Esc cancels, Backspace removes it)" : "These keys are fixed";
        Content = fixedText is not null ? Faint(fixedText) : chords.Count == 0 ? Faint("—") : Caps(chords, conflicted);
        Click += (_, _) => record();
    }

    /// <summary>The chip while it waits for a key.</summary>
    public void ShowRecording() => Content = new Border
    {
        BorderBrush = EditorKit.Res<Brush>("Accent"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 1, 8, 1),
        Child = new TextBlock { Text = "Type the shortcut · Esc cancels", FontSize = 11, Foreground = EditorKit.Res<Brush>("Accent") },
    };

    private static TextBlock Faint(string text) => new() { Text = text, FontSize = 11, Foreground = EditorKit.Res<Brush>("FaintText") };

    /// <summary>Keycaps for one or more chords (the cheat sheet uses these too).</summary>
    public static StackPanel Caps(IReadOnlyList<KeyChord> chords, bool conflicted = false, double size = 11)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < chords.Count; i++)
        {
            if (i > 0) row.Children.Add(new TextBlock { Text = "or", FontSize = size - 1, Foreground = EditorKit.Res<Brush>("FaintText"), Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            foreach (var cap in chords[i].Caps)
            {
                row.Children.Add(new Border
                {
                    Background = EditorKit.Res<Brush>("Raised"), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(0, 0, 3, 0),
                    BorderBrush = conflicted ? new SolidColorBrush(Color.FromRgb(0xFF, 0x95, 0x00)) : EditorKit.Res<Brush>("Border"), BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = cap, FontSize = size, FontFamily = new FontFamily("Segoe UI"), Foreground = EditorKit.Res<Brush>("Text") },
                });
            }
        }
        return row;
    }
}
