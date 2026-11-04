using System.Windows.Input;
using MxU.Slides.Core.Control;
using MxU.Slides.Windows.Views;
using MxU.Slides.Windows.Views.Keys;

namespace MxU.Slides.Windows.Tests.Views;

// The keys Windows reports become the Core's chords (Views/Keys/KeyTokens.cs), and the main
// preview's height for a target's shape (Views/Operator/PreviewShape.cs).
public class KeyTokensTests
{
    [Fact]
    public void KeysBecomeTheTokensShortcutsAreSavedBy()
    {
        Assert.Equal("a", KeyTokens.Token(Key.A));
        Assert.Equal("7", KeyTokens.Token(Key.D7));
        Assert.Equal("7", KeyTokens.Token(Key.NumPad7));
        Assert.Equal("f12", KeyTokens.Token(Key.F12));
        Assert.Equal("return", KeyTokens.Token(Key.Enter));
        Assert.Equal("pagedown", KeyTokens.Token(Key.Next)); // a clicker's "next" key
        Assert.Equal("delete", KeyTokens.Token(Key.Back));
        Assert.Equal("/", KeyTokens.Token(Key.OemQuestion));
        Assert.Null(KeyTokens.Token(Key.VolumeUp));
    }

    [Fact]
    public void ModifiersRideAlong()
    {
        Assert.Equal(new KeyChord("n", ctrl: true, shift: true), KeyTokens.Chord(Key.N, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Equal(new KeyChord("right", alt: true), KeyTokens.Chord(Key.Right, ModifierKeys.Alt));
        Assert.Equal("?", KeyTokens.Chord(Key.OemQuestion, ModifierKeys.Shift)!.Display);
        Assert.Null(KeyTokens.Chord(Key.LeftShift, ModifierKeys.Shift));
        Assert.True(KeyTokens.IsModifierOnly(Key.LeftCtrl));
        Assert.False(KeyTokens.IsModifierOnly(Key.F1));
    }

    [Fact]
    public void TheDefaultKeysStillDoWhatTheyDid()
    {
        // The keys the main window had before they could be changed (MainWindow.xaml.cs, Phase 2).
        var map = new KeyCommandMap();
        HashSet<KeyCommandScope> show = [KeyCommandScope.Present];
        KeyCommand? Run(Key key, ModifierKeys modifiers = ModifierKeys.None) =>
            map.Resolve(KeyTokens.Chord(key, modifiers)!, show) is KeyBindingTarget.Command command ? command.Value : null;
        Assert.Equal(KeyCommand.ClearAll, Run(Key.F1));
        Assert.Equal(KeyCommand.ClearAlerts, Run(Key.F6));
        Assert.Equal(KeyCommand.NextSlide, Run(Key.Space));
        Assert.Equal(KeyCommand.NextSlide, Run(Key.PageDown));
        Assert.Equal(KeyCommand.PreviousSlide, Run(Key.Up));
        Assert.Equal(KeyCommand.NextSkippingBuilds, Run(Key.Right, ModifierKeys.Alt));
        Assert.Equal(KeyCommand.NewSong, Run(Key.N, ModifierKeys.Control));
        Assert.Equal(KeyCommand.NewPresentation, Run(Key.N, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Equal(KeyCommand.ImportFiles, Run(Key.I, ModifierKeys.Control));
        Assert.Equal(KeyCommand.ToggleEdit, Run(Key.E, ModifierKeys.Control));
        Assert.Equal(KeyCommand.FindInLibrary, Run(Key.F, ModifierKeys.Control));
        Assert.Equal(KeyCommand.OpenBible, Run(Key.B, ModifierKeys.Control));
        Assert.Equal(KeyCommand.ShowShortcuts, Run(Key.OemQuestion, ModifierKeys.Shift));
        Assert.Null(Run(Key.C)); // letters are the groups' hot keys, not commands
    }

    [Fact]
    public void ThePreviewTakesItsTargetsShape()
    {
        Assert.Equal(225, PreviewShape.HeightFor(400, 16.0 / 9.0), 3);
        Assert.Equal(300, PreviewShape.HeightFor(400, 4.0 / 3.0), 3);
        // A tall stage layout is capped, so the live column stays usable.
        Assert.Equal(400 * PreviewShape.TallestRatio, PreviewShape.HeightFor(400, 9.0 / 32.0), 3);
        Assert.Equal(225, PreviewShape.HeightFor(400, 0), 3); // unknown: 16:9
        Assert.Equal(0, PreviewShape.HeightFor(0, 16.0 / 9.0));
    }
}
