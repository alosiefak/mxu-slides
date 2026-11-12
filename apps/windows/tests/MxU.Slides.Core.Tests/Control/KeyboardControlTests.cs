using System.Text.Json;
using MxU.Slides.Core.Control;

namespace MxU.Slides.Core.Tests.Control;

// Ported from PresenterCoreTests/KeyboardControlTests.swift. Windows differences: ⌘ is Ctrl and
// ⌥ is Alt, chords read the Windows way ("Ctrl+Shift+N"), the defaults are the ones the Windows
// app had (Ctrl+N new song, PageDown for clickers…), and a few keys are reserved for typing and
// for Windows itself. The group hot-key tests are in Scene/GroupPaletteTests.cs.
public class KeyboardControlTests
{
    private static readonly HashSet<KeyCommandScope> Present = [KeyCommandScope.Present];
    private static readonly HashSet<KeyCommandScope> Editor = [KeyCommandScope.Editor];
    private static readonly HashSet<KeyCommandScope> None = [];

    [Fact]
    public void ChordDisplayAndNormalization()
    {
        Assert.Equal("Ctrl+B", new KeyChord("B", ctrl: true).Display);
        Assert.Equal("F1", new KeyChord("f1").Display);
        Assert.Equal("←", new KeyChord("left").Display);
        Assert.Equal("Ctrl+Alt+Shift+S", new KeyChord("s", ctrl: true, alt: true, shift: true).Display);
        Assert.Equal("?", new KeyChord("/", shift: true).Display);
        Assert.Equal(["Ctrl", "Shift", "N"], new KeyChord("n", ctrl: true, shift: true).Caps);

        Assert.True(new KeyChord("esc").Matches(new KeyChord("escape")));
        Assert.True(new KeyChord("enter").Matches(new KeyChord("return")));
        Assert.True(new KeyChord("backspace").Matches(new KeyChord("delete")));
    }

    [Fact]
    public void ChordMatchingIgnoresNullVersusFalseModifiers()
    {
        var stored = new KeyChord("c") with { Ctrl = false };
        Assert.True(stored.Matches(new KeyChord("c")));
        Assert.False(new KeyChord("c", ctrl: true).Matches(new KeyChord("c")));
        Assert.Equal(new KeyChord("c"), stored);
    }

    [Fact]
    public void ChordRoundTripsThroughJson()
    {
        var chord = new KeyChord("n", ctrl: true, shift: true);
        var json = JsonSerializer.Serialize(chord);
        Assert.DoesNotContain("alt", json); // a modifier not held isn't written
        var decoded = JsonSerializer.Deserialize<KeyChord>(json)!;
        Assert.True(decoded.Matches(chord));
    }

    [Fact]
    public void DefaultsApplyWithAnEmptyMap()
    {
        var map = new KeyCommandMap();
        Assert.Equal(["f1"], map.ChordsFor(KeyCommand.ClearAll).Select(c => c.Key));
        Assert.Equal(5, map.ChordsFor(KeyCommand.NextSlide).Count);
        Assert.False(map.IsCustomized(KeyCommand.ClearAll));
    }

    [Fact]
    public void OverrideUnbindAndReset()
    {
        var map = new KeyCommandMap();
        map.SetChord(new KeyChord("f8"), KeyCommand.ClearMedia);
        Assert.Equal(["f8"], map.ChordsFor(KeyCommand.ClearMedia).Select(c => c.Key));
        Assert.True(map.IsCustomized(KeyCommand.ClearMedia));

        map.SetChord(null, KeyCommand.ClearAll);
        Assert.Empty(map.ChordsFor(KeyCommand.ClearAll));
        Assert.True(map.IsCustomized(KeyCommand.ClearAll));

        map.ResetToDefault(KeyCommand.ClearAll);
        map.ResetToDefault(KeyCommand.ClearMedia);
        Assert.Empty(map.Chords);
    }

    [Fact]
    public void RecordingTheSingleDefaultClearsTheOverride()
    {
        var map = new KeyCommandMap();
        map.SetChord(new KeyChord("F2"), KeyCommand.ClearSlides);
        Assert.False(map.IsCustomized(KeyCommand.ClearSlides));

        map.SetChord(null, KeyCommand.NewService);
        Assert.False(map.IsCustomized(KeyCommand.NewService));
    }

    [Fact]
    public void UnbindSurvivesJsonRoundTrip()
    {
        var map = new KeyCommandMap();
        map.SetChord(null, KeyCommand.ClearAll);
        map.SetChord(new KeyChord("f9"), new GeneratedKey(GeneratedKeyKind.Combo, "combo-1"));
        var decoded = JsonSerializer.Deserialize<KeyCommandMap>(JsonSerializer.Serialize(map))!;
        Assert.Empty(decoded.ChordsFor(KeyCommand.ClearAll));
        Assert.True(decoded.IsCustomized(KeyCommand.ClearAll));
        Assert.Equal(new KeyChord("f9"), decoded.ChordFor(new GeneratedKey(GeneratedKeyKind.Combo, "combo-1")));
    }

    [Fact]
    public void ResolveHonorsScopes()
    {
        var map = new KeyCommandMap();
        Assert.Equal(new KeyBindingTarget.Command(KeyCommand.ClearAll), map.Resolve(new KeyChord("f1"), Present));
        Assert.Null(map.Resolve(new KeyChord("f1"), Editor));
        Assert.Equal(new KeyBindingTarget.Command(KeyCommand.NewSong), map.Resolve(new KeyChord("n", ctrl: true), Editor));
    }

    [Fact]
    public void NarrowScopeBeatsAnywhereOnASharedChord()
    {
        var map = new KeyCommandMap();
        map.SetChord(new KeyChord("j", ctrl: true), KeyCommand.NextSlide);
        map.SetChord(new KeyChord("j", ctrl: true), KeyCommand.NewService);
        Assert.Equal(new KeyBindingTarget.Command(KeyCommand.NextSlide), map.Resolve(new KeyChord("j", ctrl: true), Present));
        Assert.Equal(new KeyBindingTarget.Command(KeyCommand.NewService), map.Resolve(new KeyChord("j", ctrl: true), Editor));
    }

    [Fact]
    public void ComboBindingsResolveInPresent()
    {
        var map = new KeyCommandMap();
        var combo = new GeneratedKey(GeneratedKeyKind.Combo, "combo-1");
        map.SetChord(new KeyChord("f9"), combo);
        Assert.Equal(["combo-1"], map.BoundIds(GeneratedKeyKind.Combo));
        Assert.Equal(new KeyBindingTarget.Generated(combo), map.Resolve(new KeyChord("f9"), Present));
        Assert.Null(map.Resolve(new KeyChord("f9"), Editor));
        map.SetChord(null, combo);
        Assert.Empty(map.BoundIds(GeneratedKeyKind.Combo));
    }

    [Fact]
    public void GeneratedKeyParsingRoundTrips()
    {
        var key = new GeneratedKey(GeneratedKeyKind.OutputPreset, "preset-1");
        Assert.Equal("preset.preset-1", key.MapKey);
        Assert.Equal(key, GeneratedKey.Parse("preset.preset-1"));
        Assert.Equal(new GeneratedKey(GeneratedKeyKind.TimerReset, "t.1"), GeneratedKey.Parse("timerreset.t.1"));
        Assert.Null(GeneratedKey.Parse(KeyCommand.NextSlide.Raw()));
        Assert.Null(GeneratedKey.Parse("dmx.fixture-9"));
    }

    [Fact]
    public void GeneratedScopesFollowTheirKind()
    {
        var map = new KeyCommandMap();
        var sunday = new GeneratedKey(GeneratedKeyKind.OutputPreset, "sunday");
        map.SetChord(new KeyChord("f8"), sunday);
        Assert.Equal(new KeyBindingTarget.Generated(sunday), map.Resolve(new KeyChord("f8"), Present));
        Assert.Null(map.Resolve(new KeyChord("f8"), None));

        var preview = new GeneratedKey(GeneratedKeyKind.Preview, "stage");
        map.SetChord(new KeyChord("m", ctrl: true, alt: true), preview);
        Assert.Equal(new KeyBindingTarget.Generated(preview), map.Resolve(new KeyChord("m", ctrl: true, alt: true), None));

        var start = new GeneratedKey(GeneratedKeyKind.TimerStart, "t1");
        map.SetChord(new KeyChord("m", ctrl: true, alt: true), start);
        Assert.Equal(new KeyBindingTarget.Generated(start), map.Resolve(new KeyChord("m", ctrl: true, alt: true), Present));

        Assert.Contains("preview.stage", map.ConflictedKeys());
        Assert.Contains("timerstart.t1", map.ConflictedKeys());
    }

    [Fact]
    public void ConflictsAreScopeAware()
    {
        var map = new KeyCommandMap();
        map.SetChord(new KeyChord("f1"), KeyCommand.ClearMedia);
        var conflicted = map.ConflictedKeys();
        Assert.Contains(KeyCommand.ClearAll.Raw(), conflicted);
        Assert.Contains(KeyCommand.ClearMedia.Raw(), conflicted);
        Assert.Equal([KeyCommand.ClearAll.Raw()], map.OthersUsing(KeyCommand.ClearMedia.Raw(), [new KeyChord("f1")], KeyCommandScope.Present));

        // A show key and an editor-only key on the same chord never meet. (No Windows command is
        // editor-only yet, so ask about one directly.)
        map = new KeyCommandMap();
        map.SetChord(new KeyChord("b", ctrl: true, shift: true), KeyCommand.PreviousSlide);
        Assert.Empty(map.OthersUsing("someEditorKey", [new KeyChord("b", ctrl: true, shift: true)], KeyCommandScope.Editor));
        // Bible works anywhere, so on Ctrl+B it does clash with a show key.
        map.SetChord(new KeyChord("b", ctrl: true), KeyCommand.PreviousSlide);
        Assert.Contains(KeyCommand.OpenBible.Raw(), map.ConflictedKeys());

        map = new KeyCommandMap();
        map.SetChord(new KeyChord("1", ctrl: true), KeyCommand.NextSlide);
        conflicted = map.ConflictedKeys();
        Assert.Contains(KeyCommand.ModePresent.Raw(), conflicted);
        Assert.Contains(KeyCommand.NextSlide.Raw(), conflicted);
        Assert.Equal(KeyCommand.ModePresent.Raw(), map.ClashFor(KeyCommand.NextSlide.Raw(), new KeyChord("1", ctrl: true), KeyCommandScope.Present));
        Assert.Null(map.ClashFor(KeyCommand.NextSlide.Raw(), new KeyChord("9", ctrl: true), KeyCommandScope.Present));
    }

    [Fact]
    public void StockDefaultsCarryNoConflicts() => Assert.Empty(new KeyCommandMap().ConflictedKeys());

    [Fact]
    public void RestoreDefaultsKeepsTheCombosKeys()
    {
        var map = new KeyCommandMap();
        map.SetChord(new KeyChord("f8"), KeyCommand.ClearMedia);
        map.SetChord(new KeyChord("f9"), new GeneratedKey(GeneratedKeyKind.Combo, "c"));
        Assert.True(map.HasCustomizedCommands);
        map.RestoreCommandDefaults();
        Assert.False(map.HasCustomizedCommands);
        Assert.Equal(["combo.c"], map.Chords.Keys);
    }

    [Fact]
    public void TypingKeysAndWindowsKeysAreReserved()
    {
        Assert.NotNull(KeyCommandMap.ReservedReason(new KeyChord("c", ctrl: true)));
        Assert.NotNull(KeyCommandMap.ReservedReason(new KeyChord("z", ctrl: true)));
        Assert.NotNull(KeyCommandMap.ReservedReason(new KeyChord("f4", alt: true)));
        Assert.Null(KeyCommandMap.ReservedReason(new KeyChord("c", ctrl: true, shift: true)));
        Assert.Null(KeyCommandMap.ReservedReason(new KeyChord("f4")));

        // While a text box has focus only chords that type nothing fire.
        Assert.True(KeyCommandMap.FiresWhileTyping(new KeyChord("f1")));
        Assert.True(KeyCommandMap.FiresWhileTyping(new KeyChord("n", ctrl: true)));
        Assert.False(KeyCommandMap.FiresWhileTyping(new KeyChord("c", ctrl: true)));
        Assert.False(KeyCommandMap.FiresWhileTyping(new KeyChord("space")));
        Assert.False(KeyCommandMap.FiresWhileTyping(new KeyChord("/", shift: true)));
    }

    [Fact]
    public void AVolunteerKeepsTheShowKeysOnly()
    {
        Assert.True(KeyCommand.NextSlide.RunsInRunOnly());
        Assert.True(KeyCommand.ClearAll.RunsInRunOnly());
        Assert.True(KeyCommand.VideoPlayPause.RunsInRunOnly());
        Assert.True(KeyCommand.ShowShortcuts.RunsInRunOnly());
        Assert.False(KeyCommand.NewSong.RunsInRunOnly());
        Assert.False(KeyCommand.ToggleEdit.RunsInRunOnly());
        Assert.False(KeyCommand.OpenKeyboardSettings.RunsInRunOnly());
        Assert.True(new KeyBindingTarget.Generated(new GeneratedKey(GeneratedKeyKind.Combo, "c")).RunsInRunOnly);
        Assert.False(new KeyBindingTarget.Generated(new GeneratedKey(GeneratedKeyKind.OutputPreset, "p")).RunsInRunOnly);
    }

    [Fact]
    public void CommandsSaveUnderTheMacsNames()
    {
        Assert.Equal("nextSlide", KeyCommand.NextSlide.Raw());
        Assert.Equal("clearAudio", KeyCommand.ClearAudio.Raw());
        Assert.Equal(KeyCommand.VideoGoToBeginning, KeyCommands.Parse("videoGoToBeginning"));
        Assert.Null(KeyCommands.Parse("boldSelection"));
        Assert.Equal("0–9 then Enter", KeyCommand.GoToSlide.FixedKeyDescription());
    }
}
