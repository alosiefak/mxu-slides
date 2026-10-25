using System.Windows.Input;
using MxU.Slides.Core.Control;

namespace MxU.Slides.Windows.Views.Keys;

/// <summary>
/// Turns a key Windows reports (WPF's <see cref="Key"/> and the modifiers held) into the Core's
/// <see cref="KeyChord"/>: the token a shortcut is saved and matched by. Keys a shortcut can't use
/// (Shift or Ctrl alone, the Windows key, media keys) give null. The Mac does the same from its
/// key events in KeyboardShortcuts.swift (KeyChord.from).
/// </summary>
public static class KeyTokens
{
    /// <summary>The chord for a key press, or null when that key can't be a shortcut.</summary>
    public static KeyChord? Chord(Key key, ModifierKeys modifiers)
    {
        if (Token(key) is not { } token) return null;
        return new KeyChord(token,
            ctrl: modifiers.HasFlag(ModifierKeys.Control),
            alt: modifiers.HasFlag(ModifierKeys.Alt),
            shift: modifiers.HasFlag(ModifierKeys.Shift));
    }

    /// <summary>The key's token ("a", "7", "f5", "left", "return", "/"…), or null.</summary>
    public static string? Token(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('a' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        >= Key.F1 and <= Key.F24 => $"f{key - Key.F1 + 1}",
        Key.Left => "left",
        Key.Right => "right",
        Key.Up => "up",
        Key.Down => "down",
        Key.Space => "space",
        Key.Return => "return",
        Key.Escape => "escape",
        Key.Back or Key.Delete => "delete",
        Key.Tab => "tab",
        Key.PageUp => "pageup",
        Key.PageDown => "pagedown",
        Key.Home => "home",
        Key.End => "end",
        // Punctuation: the character the key types without Shift on a UK or US keyboard.
        Key.OemQuestion => "/",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe or Key.OemBackslash => "\\",
        Key.OemMinus or Key.Subtract => "-",
        Key.OemPlus => "=",
        Key.Add => "+",
        Key.Multiply => "*",
        Key.Divide => "/",
        Key.Decimal => ".",
        Key.OemTilde => "`",
        _ => null,
    };

    /// <summary>Shift, Ctrl, Alt and the Windows key on their own: a recorder waits for the key that goes with them.</summary>
    public static bool IsModifierOnly(Key key) => key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System or Key.ImeProcessed or Key.DeadCharProcessed;

    /// <summary>What the key really was: Alt makes WPF report <see cref="Key.System"/>, and an input method <see cref="Key.ImeProcessed"/>.</summary>
    public static Key RealKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        _ => e.Key,
    };
}
