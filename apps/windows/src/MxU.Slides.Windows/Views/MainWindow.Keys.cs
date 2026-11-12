using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MxU.Slides.Core.Control;
using MxU.Slides.Windows.Views.Keys;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The main window's keyboard (Phase 8). Every shortcut goes through this PC's key map
/// (<see cref="KeyCommandMap"/>, in settings.json): the defaults are the keys the window always
/// had, which are the Mac's (F1–F6 clear, → or Space for Next…), and the shortcuts window
/// (Keys/ShortcutsWindow.cs) changes them. Typing a number then Enter goes to that slide, and a
/// letter jumps to its section (C = Chorus); those two are fixed, as on the Mac. In volunteer mode
/// only the keys that run the show work. Port of KeyInController in the Mac's KeyboardShortcuts.swift.
/// </summary>
public partial class MainWindow
{
    // Show keys work in the main window whichever mode it's in (in Edit mode, ← and → still move
    // the show on unless the slide canvas has the keyboard), so the Show scope is always active here.
    private static readonly HashSet<KeyCommandScope> ShowScope = [KeyCommandScope.Present];

    /// <summary>A key went down in the main window: runs its shortcut. True when it was used.</summary>
    private bool HandleKey(KeyEventArgs e)
    {
        var key = KeyTokens.RealKey(e);
        if (KeyTokens.IsModifierOnly(key)) return false;
        var typing = Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox { IsEditable: true };
        // While typing, Esc leaves the box (and does nothing else).
        if (typing && key == Key.Escape)
        {
            Keyboard.ClearFocus();
            return true;
        }
        if (KeyTokens.Chord(key, Keyboard.Modifiers) is not { } chord) return false;
        // While typing, or while the canvas has the keyboard (arrows nudge, Delete removes), only
        // chords that type nothing fire: F-keys and ones with Ctrl or Alt.
        var quiet = typing || Keyboard.FocusedElement is SlideCanvasEditor;
        if (quiet && !KeyCommandMap.FiresWhileTyping(chord)) return false;

        // A slide number being typed: Enter goes there, Esc forgets it, Backspace takes a digit off.
        if (!quiet && model.GoToBuffer.Length > 0 && chord.IsBare)
        {
            switch (chord.Key)
            {
                case "return":
                    model.CommitGoTo();
                    return true;
                case "escape":
                    model.CancelGoTo();
                    return true;
                case "delete":
                    model.TrimGoToDigit();
                    return true;
                case [var digit] when char.IsAsciiDigit(digit):
                    model.AppendGoToDigit(digit);
                    return true;
                default:
                    model.CancelGoTo();
                    break;
            }
        }

        if (model.Settings.KeyMap.Resolve(chord, ShowScope) is { } target)
        {
            if (model.IsVolunteer && !target.RunsInRunOnly)
            {
                model.Notice = $"{chord.Display} is locked in Volunteer Mode.";
                return true;
            }
            switch (target)
            {
                case KeyBindingTarget.Command command: RunCommand(command.Value); break;
                case KeyBindingTarget.Generated generated: model.RunShortcut(generated.Key, this); break;
            }
            return true;
        }

        // The fixed keys: digits build a slide number, letters are the groups' hot keys.
        if (quiet || !chord.IsBare) return false;
        if (chord.Key is [var number] && char.IsAsciiDigit(number))
        {
            model.AppendGoToDigit(number);
            return true;
        }
        return chord.Key is [var letter] && char.IsAsciiLetter(letter) && model.FireHotKey(chord.Key);
    }

    /// <summary>Runs one of the commands a shortcut can have.</summary>
    internal void RunCommand(KeyCommand command)
    {
        switch (command)
        {
            case KeyCommand.NextSlide: model.NextCommand.Execute(null); break;
            case KeyCommand.PreviousSlide: model.PreviousCommand.Execute(null); break;
            case KeyCommand.NextSkippingBuilds: model.NextSkippingBuildsCommand.Execute(null); break;
            case KeyCommand.NextServiceItem: model.StepServiceItem(+1); break;
            case KeyCommand.PreviousServiceItem: model.StepServiceItem(-1); break;
            case KeyCommand.ClearAll: model.ClearAllCommand.Execute(null); break;
            case KeyCommand.ClearSlides: model.ClearSlideCommand.Execute(null); break;
            case KeyCommand.ClearMedia: model.ClearMediaCommand.Execute(null); break;
            case KeyCommand.ClearOverlays: model.ClearOverlaysCommand.Execute(null); break;
            case KeyCommand.ClearAudio: model.ClearAudioCommand.Execute(null); break;
            case KeyCommand.ClearAlerts: model.ClearAlertsCommand.Execute(null); break;
            case KeyCommand.ClearInputs: model.ClearInputsNow(); break;
            case KeyCommand.VideoPlayPause: model.VideoFromKey(toBeginning: false); break;
            case KeyCommand.VideoGoToBeginning: model.VideoFromKey(toBeginning: true); break;
            case KeyCommand.NewSong: NewSong_Click(this, new System.Windows.RoutedEventArgs()); break;
            case KeyCommand.NewPresentation: model.NewPresentationCommand.Execute(null); break;
            case KeyCommand.NewService: model.NewServiceCommand.Execute(null); break;
            case KeyCommand.ImportFiles: Import_Click(this, new System.Windows.RoutedEventArgs()); break;
            case KeyCommand.FindInLibrary:
                LibrarySearchBox.Focus();
                LibrarySearchBox.SelectAll();
                break;
            case KeyCommand.OpenBible: model.OpenBibleCommand.Execute(null); break;
            case KeyCommand.ModePresent: model.IsEditing = false; break;
            case KeyCommand.ModeEdit: model.IsEditing = true; break;
            case KeyCommand.ToggleEdit: model.IsEditing = !model.IsEditing; break;
            case KeyCommand.ModeVolunteer:
                if (model.VolunteerModeEnabled) model.EnterVolunteer();
                else model.Notice = "Turn on Volunteer Mode in Screens & Settings › Present first.";
                break;
            case KeyCommand.OpenKeyboardSettings: ShortcutsWindow.Open(this, model); break;
            case KeyCommand.ShowShortcuts: CheatSheet.Toggle(this, model); break;
            case KeyCommand.GoToSlide: break; // typed digits, handled above
        }
    }
}
