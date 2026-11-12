using System.Text.Json.Serialization;

namespace MxU.Slides.Core.Control;

/// <summary>
/// One key with its modifiers: "F1", "Ctrl+Shift+N", "→". The key is a token: a lower-case
/// letter or digit, "f1"…"f24", "left", "right", "up", "down", "space", "return", "escape",
/// "delete" (Backspace or Delete), "tab", "pageup", "pagedown", "home", "end", or the
/// punctuation key's own character ("/", ",", "."…, as typed without Shift).
///
/// Port of KeyChord in PresenterCore/KeyboardControl.swift. The Mac's ⌘ is Ctrl here and its ⌥
/// is Alt; Windows has no use for the Mac's ⌃ (Control) as a separate modifier. A modifier that
/// isn't held is stored as nothing (null), like the Mac's, so saved maps stay small.
/// </summary>
public sealed record KeyChord
{
    public KeyChord(string key, bool ctrl = false, bool alt = false, bool shift = false)
    {
        Key = NormalizeToken(key);
        Ctrl = ctrl ? true : null;
        Alt = alt ? true : null;
        Shift = shift ? true : null;
    }

    [JsonConstructor]
    public KeyChord(string key, bool? ctrl, bool? alt, bool? shift) : this(key, ctrl == true, alt == true, shift == true) { }

    [JsonPropertyName("key")]
    public string Key { get; init; }

    [JsonPropertyName("ctrl"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Ctrl { get; init; }

    [JsonPropertyName("alt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Alt { get; init; }

    [JsonPropertyName("shift"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Shift { get; init; }

    [JsonIgnore] public bool HasCtrl => Ctrl ?? false;
    [JsonIgnore] public bool HasAlt => Alt ?? false;
    [JsonIgnore] public bool HasShift => Shift ?? false;

    /// <summary>No modifier held: a plain letter, digit, arrow or F-key.</summary>
    [JsonIgnore] public bool IsBare => !HasCtrl && !HasAlt && !HasShift;

    /// <summary>A function key (F1–F24): it types nothing, so it can fire even while a text box has focus.</summary>
    [JsonIgnore] public bool IsFunctionKey => Key.Length > 1 && Key[0] == 'f' && int.TryParse(Key.AsSpan(1), out var n) && n is >= 1 and <= 24;

    /// <summary>Same key and the same modifiers; "not held" and "false" are the same thing.</summary>
    public bool Matches(KeyChord other) =>
        NormalizeToken(Key) == NormalizeToken(other.Key)
        && HasCtrl == other.HasCtrl && HasAlt == other.HasAlt && HasShift == other.HasShift;

    /// <summary>Lower-cased, with the Mac's other spellings folded in (esc, enter, backspace).</summary>
    public static string NormalizeToken(string raw) => raw.ToLowerInvariant() switch
    {
        "esc" => "escape",
        "enter" => "return",
        "backspace" => "delete",
        var token => token,
    };

    /// <summary>The caps a chip shows: modifiers in Windows order, then the key ("Ctrl", "Shift", "N").</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Caps
    {
        get
        {
            // Shift with a punctuation key reads as the character it types: Shift+/ is "?".
            if (HasShift && !HasCtrl && !HasAlt && ShiftedPunctuation.TryGetValue(Key, out var typed)) return [typed];
            var caps = new List<string>();
            if (HasCtrl) caps.Add("Ctrl");
            if (HasAlt) caps.Add("Alt");
            if (HasShift) caps.Add("Shift");
            caps.Add(CapLabel(Key));
            return caps;
        }
    }

    /// <summary>"Ctrl+Shift+N", "F1", "→", "?".</summary>
    [JsonIgnore] public string Display => string.Join("+", Caps);

    public override string ToString() => Display;

    /// <summary>A key token's label on its cap ("→", "Space", "Enter", "F5", "N").</summary>
    public static string CapLabel(string token) => NormalizeToken(token) switch
    {
        "left" => "←",
        "right" => "→",
        "up" => "↑",
        "down" => "↓",
        "space" => "Space",
        "return" => "Enter",
        "escape" => "Esc",
        "delete" => "Delete",
        "tab" => "Tab",
        "pageup" => "PgUp",
        "pagedown" => "PgDn",
        "home" => "Home",
        "end" => "End",
        var t => t.ToUpperInvariant(),
    };

    /// <summary>What Shift turns a punctuation key into on a UK or US keyboard (Shift+/ is ?).</summary>
    private static readonly Dictionary<string, string> ShiftedPunctuation = new(StringComparer.Ordinal)
    {
        ["/"] = "?", [","] = "<", ["."] = ">", [";"] = ":", ["["] = "{", ["]"] = "}", ["-"] = "_", ["="] = "+", ["\\"] = "|",
    };

    public bool Equals(KeyChord? other) => other is not null && Matches(other);

    public override int GetHashCode() => HashCode.Combine(NormalizeToken(Key), HasCtrl, HasAlt, HasShift);
}

/// <summary>The shortcuts window's sections (Mac KeyCommandGroup; Windows has no Editor group yet).</summary>
public enum KeyCommandGroup { ShowControl, Clears, Video, CreateLibrary, Application }

/// <summary>
/// Where a shortcut works. Present: the show's keys (Next, the clears…), in the main window while
/// nothing is being typed. Anywhere: also in Edit mode. A narrower scope wins when the same chord
/// is used twice (the Mac's rule), so Ctrl+J can mean one thing while presenting and another elsewhere.
/// </summary>
public enum KeyCommandScope { Present, Editor, Anywhere }

/// <summary>
/// Every command a shortcut can run. The Mac's names (KeyCommand in KeyboardControl.swift) where
/// the Mac has the command; the rest are the Windows app's own keys, which it already had before
/// they could be changed: New Song, Import, Bible, Show / Edit, skip builds, and the cheat sheet.
/// </summary>
public enum KeyCommand
{
    NextSlide, PreviousSlide, NextSkippingBuilds, GoToSlide, NextServiceItem, PreviousServiceItem,
    ClearAll, ClearSlides, ClearMedia, ClearOverlays, ClearAudio, ClearAlerts, ClearInputs,
    VideoPlayPause, VideoGoToBeginning,
    NewSong, NewPresentation, NewService, ImportFiles, FindInLibrary, OpenBible,
    ModePresent, ModeEdit, ToggleEdit, ModeVolunteer, OpenKeyboardSettings, ShowShortcuts,
}

/// <summary>Each command's name, group, scope, defaults and whether a volunteer may use it.</summary>
public static class KeyCommands
{
    /// <summary>The name a command is saved under in settings.json (the Mac's rawValue: "nextSlide").</summary>
    public static string Raw(this KeyCommand command)
    {
        var name = command.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static KeyCommand? Parse(string raw) =>
        Enum.GetValues<KeyCommand>().Cast<KeyCommand?>().FirstOrDefault(c => c!.Value.Raw() == raw);

    /// <summary>
    /// The keys a command has until someone changes them: the Mac's (⌘ read as Ctrl), and the
    /// Windows app's own (PageDown and ↓ for Next, which clickers send; Ctrl+N for a new song).
    /// </summary>
    public static IReadOnlyList<KeyChord> DefaultChords(this KeyCommand command) => command switch
    {
        KeyCommand.NextSlide => [new("right"), new("space"), new("return"), new("pagedown"), new("down")],
        KeyCommand.PreviousSlide => [new("left"), new("pageup"), new("up")],
        KeyCommand.NextSkippingBuilds => [new("right", alt: true)],
        KeyCommand.ClearAll => [new("f1")],
        KeyCommand.ClearSlides => [new("f2")],
        KeyCommand.ClearMedia => [new("f3")],
        KeyCommand.ClearOverlays => [new("f4")],
        KeyCommand.ClearAudio => [new("f5")],
        KeyCommand.ClearAlerts => [new("f6")],
        KeyCommand.NewSong => [new("n", ctrl: true)],
        KeyCommand.NewPresentation => [new("n", ctrl: true, shift: true)],
        KeyCommand.ImportFiles => [new("i", ctrl: true)],
        KeyCommand.FindInLibrary => [new("f", ctrl: true)],
        KeyCommand.OpenBible => [new("b", ctrl: true)],
        KeyCommand.ModePresent => [new("1", ctrl: true)],
        KeyCommand.ModeEdit => [new("2", ctrl: true)],
        KeyCommand.ToggleEdit => [new("e", ctrl: true)],
        KeyCommand.OpenKeyboardSettings => [new("k", alt: true)],
        KeyCommand.ShowShortcuts => [new("/", shift: true)],
        _ => [],
    };

    public static KeyCommandGroup Group(this KeyCommand command) => command switch
    {
        KeyCommand.NextSlide or KeyCommand.PreviousSlide or KeyCommand.NextSkippingBuilds or KeyCommand.GoToSlide
            or KeyCommand.NextServiceItem or KeyCommand.PreviousServiceItem => KeyCommandGroup.ShowControl,
        KeyCommand.ClearAll or KeyCommand.ClearSlides or KeyCommand.ClearMedia or KeyCommand.ClearOverlays
            or KeyCommand.ClearAudio or KeyCommand.ClearAlerts or KeyCommand.ClearInputs => KeyCommandGroup.Clears,
        KeyCommand.VideoPlayPause or KeyCommand.VideoGoToBeginning => KeyCommandGroup.Video,
        KeyCommand.NewSong or KeyCommand.NewPresentation or KeyCommand.NewService or KeyCommand.ImportFiles
            or KeyCommand.FindInLibrary or KeyCommand.OpenBible => KeyCommandGroup.CreateLibrary,
        _ => KeyCommandGroup.Application,
    };

    public static KeyCommandScope Scope(this KeyCommand command) =>
        command.Group() is KeyCommandGroup.ShowControl or KeyCommandGroup.Clears or KeyCommandGroup.Video
            ? KeyCommandScope.Present
            : KeyCommandScope.Anywhere;

    /// <summary>
    /// A volunteer (run-only mode) may use it: running the show, the clears, video play/pause,
    /// Show mode, entering volunteer mode and the cheat sheet. Making, finding, editing and the
    /// shortcut settings themselves are locked. (The Mac also lets ⌥K open a locked Settings page;
    /// here the shortcuts window edits settings, so it stays shut.)
    /// </summary>
    public static bool RunsInRunOnly(this KeyCommand command) => command.Group() switch
    {
        KeyCommandGroup.ShowControl or KeyCommandGroup.Clears or KeyCommandGroup.Video => true,
        KeyCommandGroup.CreateLibrary => false,
        _ => command is KeyCommand.ModePresent or KeyCommand.ModeVolunteer or KeyCommand.ShowShortcuts,
    };

    /// <summary>A command whose keys are fixed: typing a number then Enter goes to that slide.</summary>
    public static string? FixedKeyDescription(this KeyCommand command) => command == KeyCommand.GoToSlide ? "0–9 then Enter" : null;

    public static string DisplayName(this KeyCommand command) => command switch
    {
        KeyCommand.NextSlide => "Next Slide",
        KeyCommand.PreviousSlide => "Previous Slide",
        KeyCommand.NextSkippingBuilds => "Next Slide, Skipping Builds",
        KeyCommand.GoToSlide => "Go to Slide",
        KeyCommand.NextServiceItem => "Next Service Item",
        KeyCommand.PreviousServiceItem => "Previous Service Item",
        KeyCommand.ClearAll => "Clear All",
        KeyCommand.ClearSlides => "Clear Slides",
        KeyCommand.ClearMedia => "Clear Media",
        KeyCommand.ClearOverlays => "Clear Overlays",
        KeyCommand.ClearAudio => "Clear Music",
        KeyCommand.ClearAlerts => "Clear Alerts",
        KeyCommand.ClearInputs => "Clear Inputs",
        KeyCommand.VideoPlayPause => "Play/Pause Video",
        KeyCommand.VideoGoToBeginning => "Video to Beginning",
        KeyCommand.NewSong => "New Song",
        KeyCommand.NewPresentation => "New Presentation",
        KeyCommand.NewService => "New Service",
        KeyCommand.ImportFiles => "Import",
        KeyCommand.FindInLibrary => "Find in Library",
        KeyCommand.OpenBible => "Bible",
        KeyCommand.ModePresent => "Show Mode",
        KeyCommand.ModeEdit => "Edit Mode",
        KeyCommand.ToggleEdit => "Switch Show / Edit",
        KeyCommand.ModeVolunteer => "Volunteer Mode",
        KeyCommand.OpenKeyboardSettings => "Keyboard Shortcuts",
        KeyCommand.ShowShortcuts => "Show the Cheat Sheet",
        _ => command.ToString(),
    };

    public static string DisplayName(this KeyCommandGroup group) => group switch
    {
        KeyCommandGroup.ShowControl => "Show Control",
        KeyCommandGroup.Clears => "Clear Layers",
        KeyCommandGroup.Video => "Video",
        KeyCommandGroup.CreateLibrary => "Create & Library",
        _ => "Application",
    };

    public static string DisplayName(this KeyCommandScope scope) => scope switch
    {
        KeyCommandScope.Present => "Show",
        KeyCommandScope.Editor => "Editor",
        _ => "Anywhere",
    };
}

/// <summary>
/// The things that get a shortcut only once someone gives them one: an action combo, an output
/// preset (a look), an overlay, a timer's start, pause and reset, and a preview window. Saved as
/// "combo.&lt;id&gt;", "preset.&lt;id&gt;"… beside the commands. Port of GeneratedKeyKind (the Mac's
/// Settings-page shortcuts aren't here: Windows has one Settings window).
/// </summary>
public enum GeneratedKeyKind { Combo, OutputPreset, OverlayToggle, TimerStart, TimerPause, TimerReset, Preview }

public static class GeneratedKeyKinds
{
    public static string Prefix(this GeneratedKeyKind kind) => kind switch
    {
        GeneratedKeyKind.Combo => "combo.",
        GeneratedKeyKind.OutputPreset => "preset.",
        GeneratedKeyKind.OverlayToggle => "overlay.",
        GeneratedKeyKind.TimerStart => "timerstart.",
        GeneratedKeyKind.TimerPause => "timerpause.",
        GeneratedKeyKind.TimerReset => "timerreset.",
        _ => "preview.",
    };

    /// <summary>A volunteer may fire these; switching the look (an output preset) is locked, as on the Mac.</summary>
    public static bool RunsInRunOnly(this GeneratedKeyKind kind) => kind != GeneratedKeyKind.OutputPreset;

    /// <summary>A preview window opens from anywhere; the rest are show keys.</summary>
    public static KeyCommandScope Scope(this GeneratedKeyKind kind) => kind == GeneratedKeyKind.Preview ? KeyCommandScope.Anywhere : KeyCommandScope.Present;
}

/// <summary>A shortcut for one thing: "combo.ABC" is the combo ABC.</summary>
public readonly record struct GeneratedKey(GeneratedKeyKind Kind, string Id)
{
    public string MapKey => Kind.Prefix() + Id;

    public static GeneratedKey? Parse(string mapKey)
    {
        foreach (var kind in Enum.GetValues<GeneratedKeyKind>())
            if (mapKey.StartsWith(kind.Prefix(), StringComparison.Ordinal)) return new GeneratedKey(kind, mapKey[kind.Prefix().Length..]);
        return null;
    }
}

/// <summary>What a pressed chord runs: a command, or one combo, look, overlay, timer or preview.</summary>
public abstract record KeyBindingTarget
{
    private KeyBindingTarget() { }

    public sealed record Command(KeyCommand Value) : KeyBindingTarget;

    public sealed record Generated(GeneratedKey Key) : KeyBindingTarget;

    public bool RunsInRunOnly => this switch
    {
        Command command => command.Value.RunsInRunOnly(),
        Generated generated => generated.Key.Kind.RunsInRunOnly(),
        _ => false,
    };
}

/// <summary>
/// This PC's shortcuts: only what was changed from the defaults is stored. A command that isn't
/// in <see cref="Chords"/> has its defaults; one stored as null has no key at all (unbound on
/// purpose). Combos, looks, overlays and timers have a key only when one was given. Saved in
/// settings.json, so each PC in the building can have its own (a clicker on one, a keyboard on
/// another). Port of KeyCommandMap in PresenterCore/KeyboardControl.swift.
/// </summary>
public sealed class KeyCommandMap
{
    [JsonPropertyName("chords")]
    public Dictionary<string, KeyChord?> Chords { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The command's keys now: its own if changed (none if unbound), else its defaults.</summary>
    public IReadOnlyList<KeyChord> ChordsFor(KeyCommand command) =>
        Chords.TryGetValue(command.Raw(), out var chord) ? chord is null ? [] : [chord] : command.DefaultChords();

    public bool IsCustomized(KeyCommand command) => Chords.ContainsKey(command.Raw());

    /// <summary>
    /// Gives a command one key (null: no key). Recording the command's only default again, or
    /// unbinding one that has no default, forgets the change instead of storing it.
    /// </summary>
    public void SetChord(KeyChord? chord, KeyCommand command)
    {
        var defaults = command.DefaultChords();
        if (chord is not null && defaults.Count == 1 && defaults[0].Matches(chord)) Chords.Remove(command.Raw());
        else if (chord is null && defaults.Count == 0) Chords.Remove(command.Raw());
        else Chords[command.Raw()] = chord;
    }

    public void ResetToDefault(KeyCommand command) => Chords.Remove(command.Raw());

    public KeyChord? ChordFor(GeneratedKey key) => Chords.GetValueOrDefault(key.MapKey);

    /// <summary>Gives a combo, look, overlay, timer or preview a key, or (null) takes it away.</summary>
    public void SetChord(KeyChord? chord, GeneratedKey key)
    {
        if (chord is null) Chords.Remove(key.MapKey);
        else Chords[key.MapKey] = chord;
    }

    /// <summary>The ids of one kind that have a key, sorted.</summary>
    public List<string> BoundIds(GeneratedKeyKind kind) =>
        [.. Chords.Keys.Where(k => k.StartsWith(kind.Prefix(), StringComparison.Ordinal)).Select(k => k[kind.Prefix().Length..]).Order(StringComparer.Ordinal)];

    /// <summary>Every combo, look, overlay, timer and preview that has a key, by map key.</summary>
    public List<GeneratedKey> BoundGeneratedKeys =>
        [.. Chords.Keys.Order(StringComparer.Ordinal)
            .Where(k => Chords[k] is not null)
            .Select(GeneratedKey.Parse)
            .OfType<GeneratedKey>()];

    /// <summary>"Restore Defaults": every command back to its defaults; combos and the rest keep their keys (the Mac's rule).</summary>
    public void RestoreCommandDefaults()
    {
        foreach (var key in Chords.Keys.Where(k => GeneratedKey.Parse(k) is null).ToList()) Chords.Remove(key);
    }

    /// <summary>Some command has been changed from its defaults (Restore Defaults has something to do).</summary>
    public bool HasCustomizedCommands => Chords.Keys.Any(k => GeneratedKey.Parse(k) is null);

    /// <summary>
    /// What a chord runs, given which scopes are active now. A command or key whose scope is active
    /// matches; one in a narrow scope (Show) beats one that works anywhere, so the same chord can
    /// have a meaning while presenting and another elsewhere.
    /// </summary>
    public KeyBindingTarget? Resolve(KeyChord chord, IReadOnlySet<KeyCommandScope> activeScopes)
    {
        KeyBindingTarget? anywhere = null;
        foreach (var command in Enum.GetValues<KeyCommand>())
        {
            var scope = command.Scope();
            if (scope != KeyCommandScope.Anywhere && !activeScopes.Contains(scope)) continue;
            if (!ChordsFor(command).Any(c => c.Matches(chord))) continue;
            if (scope != KeyCommandScope.Anywhere) return new KeyBindingTarget.Command(command);
            anywhere ??= new KeyBindingTarget.Command(command);
        }
        foreach (var key in BoundGeneratedKeys)
        {
            var scope = key.Kind.Scope();
            if (scope != KeyCommandScope.Anywhere && !activeScopes.Contains(scope)) continue;
            if (ChordFor(key)?.Matches(chord) != true) continue;
            if (scope != KeyCommandScope.Anywhere) return new KeyBindingTarget.Generated(key);
            anywhere ??= new KeyBindingTarget.Generated(key);
        }
        return anywhere;
    }

    private sealed record Bound(string MapKey, KeyChord Chord, KeyCommandScope Scope);

    private List<Bound> AllBound()
    {
        var bound = new List<Bound>();
        foreach (var command in Enum.GetValues<KeyCommand>())
            foreach (var chord in ChordsFor(command)) bound.Add(new Bound(command.Raw(), chord, command.Scope()));
        foreach (var key in BoundGeneratedKeys)
            if (ChordFor(key) is { } chord) bound.Add(new Bound(key.MapKey, chord, key.Kind.Scope()));
        return bound;
    }

    private static bool ScopesMeet(KeyCommandScope a, KeyCommandScope b) => a == b || a == KeyCommandScope.Anywhere || b == KeyCommandScope.Anywhere;

    /// <summary>
    /// Every map key whose chord is also used by something else that can be active at the same
    /// time (the same scope, or one of them works anywhere). The shortcuts window marks these.
    /// </summary>
    public HashSet<string> ConflictedKeys()
    {
        var bound = AllBound();
        var conflicted = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < bound.Count; i++)
            for (var j = i + 1; j < bound.Count; j++)
                if (bound[i].MapKey != bound[j].MapKey && bound[i].Chord.Matches(bound[j].Chord) && ScopesMeet(bound[i].Scope, bound[j].Scope))
                {
                    conflicted.Add(bound[i].MapKey);
                    conflicted.Add(bound[j].MapKey);
                }
        return conflicted;
    }

    /// <summary>The other map keys that use one of these chords where it can clash ("Also used by…").</summary>
    public List<string> OthersUsing(string mapKey, IReadOnlyList<KeyChord> chords, KeyCommandScope scope) =>
        [.. AllBound()
            .Where(b => b.MapKey != mapKey && ScopesMeet(b.Scope, scope) && chords.Any(c => c.Matches(b.Chord)))
            .Select(b => b.MapKey)
            .Distinct()];

    /// <summary>The map key a chord would clash with if given to <paramref name="mapKey"/>, or null when it's free there.</summary>
    public string? ClashFor(string mapKey, KeyChord chord, KeyCommandScope scope) => OthersUsing(mapKey, [chord], scope).FirstOrDefault();

    /// <summary>
    /// Why a chord can't be a shortcut on Windows, or null when it can: the text-editing keys
    /// (Ctrl+A, C, V, X, Y, Z) belong to every text box, and Windows keeps Alt+F4, Alt+Tab,
    /// Alt+Esc and Alt+Space for itself.
    /// </summary>
    public static string? ReservedReason(KeyChord chord)
    {
        if (chord.HasCtrl && !chord.HasAlt && !chord.HasShift && chord.Key is "a" or "c" or "v" or "x" or "y" or "z"
            || chord.HasCtrl && !chord.HasAlt && chord.HasShift && chord.Key == "z") // Ctrl+Shift+Z: redo
            return $"{chord.Display} is for editing text";
        if (chord.HasAlt && !chord.HasCtrl && chord.Key is "f4" or "tab" or "escape" or "space")
            return $"Windows keeps {chord.Display} for itself";
        return null;
    }

    /// <summary>
    /// A chord can fire while a text box has focus: it has Ctrl or Alt, or it's an F-key (none of
    /// those type anything), and it isn't one of the text-editing keys. Letters, digits, arrows,
    /// Space and Enter always go to the text box.
    /// </summary>
    public static bool FiresWhileTyping(KeyChord chord) =>
        (chord.HasCtrl || chord.HasAlt || chord.IsFunctionKey) && ReservedReason(chord) is null;
}
