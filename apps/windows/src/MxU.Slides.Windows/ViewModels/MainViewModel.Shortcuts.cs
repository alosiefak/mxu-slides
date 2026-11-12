using MxU.Slides.Core.Control;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Keyboard shortcuts (Phase 8): this PC's key map (settings.json), changed in the shortcuts window
/// (Views/Keys/ShortcutsWindow.cs) and read by the main window's key handler (Views/MainWindow.Keys.cs).
/// The commands and their defaults are Core's (Core/Control/KeyboardControl.cs, a port of the
/// Mac's KeyboardControl.swift); this runs the shortcuts given to one combo, look, overlay, timer
/// or preview, and edits the group hot keys, which live in the library's group palette (so they
/// follow the songs to every PC and to the Mac).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Raised when a shortcut changes (the shortcuts window and the cheat sheet redraw).</summary>
    public event Action? KeyMapChanged;

    public KeyCommandMap KeyMap => Settings.KeyMap;

    /// <summary>Changes the key map and saves it. Locked in volunteer mode.</summary>
    public void UpdateKeyMap(Action<KeyCommandMap> change)
    {
        if (!Permit(OperatorAction.ChangeShortcuts)) return;
        change(Settings.KeyMap);
        Settings.Save();
        KeyMapChanged?.Invoke();
    }

    /// <summary>Runs the shortcut given to one combo, look, overlay, timer or preview window.</summary>
    public void RunShortcut(GeneratedKey key, System.Windows.Window? owner)
    {
        Log.Info($"Key: {key.MapKey}");
        switch (key.Kind)
        {
            case GeneratedKeyKind.Combo when Library.Get<ActionCombo>(key.Id) is not null:
                RunActionCombo(key.Id);
                break;
            case GeneratedKeyKind.OutputPreset when Library.Get<OutputPreset>(key.Id) is { } preset:
                ActivateOutputPreset(preset);
                break;
            case GeneratedKeyKind.OverlayToggle when Library.Get<Overlay>(key.Id) is { } overlay:
                SetOverlay(overlay, up: !IsOverlayLive(overlay.Id));
                break;
            case GeneratedKeyKind.TimerStart when Timers.Snapshot(key.Id) is not null:
                Timers.Start(key.Id);
                break;
            case GeneratedKeyKind.TimerPause when Timers.Snapshot(key.Id) is not null:
                Timers.Pause(key.Id);
                break;
            case GeneratedKeyKind.TimerReset when Timers.Snapshot(key.Id) is not null:
                Timers.Reset(key.Id);
                break;
            case GeneratedKeyKind.Preview:
                OpenPreviewWindow(key.Id, owner);
                break;
            default:
                // Its combo, look, overlay or timer has gone since the key was given.
                Notice = $"That shortcut's {ShortcutNoun(key.Kind)} isn't in the library any more.";
                break;
        }
    }

    private static string ShortcutNoun(GeneratedKeyKind kind) => kind switch
    {
        GeneratedKeyKind.Combo => "combo",
        GeneratedKeyKind.OutputPreset => "look",
        GeneratedKeyKind.OverlayToggle => "overlay",
        GeneratedKeyKind.Preview => "screen",
        _ => "timer",
    };

    /// <summary>Video play/pause and back to the start, from a key (the transport strip and MIDI do the same).</summary>
    public void VideoFromKey(bool toBeginning) =>
        HandleMidi(toBeginning ? Core.Control.MidiCommand.VideoGoToBeginning : Core.Control.MidiCommand.VideoPlayPause, 0);

    /// <summary>Backspace while a slide number is being typed takes its last digit off (the Mac's go-to buffer).</summary>
    public void TrimGoToDigit()
    {
        if (GoToBuffer.Length <= 1)
        {
            CancelGoTo();
            return;
        }
        GoToBuffer = GoToBuffer[..^1];
        goToExpiry?.Stop();
        goToExpiry?.Start();
    }

    // MARK: The rows the shortcuts window lists

    /// <summary>A shortcut row for one thing: its key and what the window calls it.</summary>
    public sealed record ShortcutRow(GeneratedKey Key, string Label);

    /// <summary>The combos, looks, timers, overlays and previews that can have a key, in the window's sections.</summary>
    public List<(string Title, string? Footer, List<ShortcutRow> Rows)> ShortcutSections()
    {
        var sections = new List<(string, string?, List<ShortcutRow>)>();
        var presets = Library.All<OutputPreset>().OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (presets.Count > 0)
            sections.Add(("Looks (Output Presets)", null, [.. presets.Select(p => new ShortcutRow(new(GeneratedKeyKind.OutputPreset, p.Id), $"Switch to “{p.Name}”"))]));
        var timers = Timers.Timers.Where(t => !Core.Stage.TimerController.IsPermanent(t.Id)).ToList();
        if (timers.Count > 0)
            sections.Add(("Timers", null, [.. timers.SelectMany(t => new[]
            {
                new ShortcutRow(new(GeneratedKeyKind.TimerStart, t.Id), $"Start “{t.Name}”"),
                new ShortcutRow(new(GeneratedKeyKind.TimerPause, t.Id), $"Pause “{t.Name}”"),
                new ShortcutRow(new(GeneratedKeyKind.TimerReset, t.Id), $"Reset “{t.Name}”"),
            })]));
        var overlays = Library.All<Overlay>().OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (overlays.Count > 0)
            sections.Add(("Overlays", "Toggle: puts the overlay up, or takes it down when it's up.",
                [.. overlays.Select(o => new ShortcutRow(new(GeneratedKeyKind.OverlayToggle, o.Id), $"Toggle “{o.Name}”"))]));
        var (screens, layouts) = PreviewChoices();
        if (screens.Count + layouts.Count > 0)
            sections.Add(("Preview Windows", null, [.. screens.Concat(layouts).Select(c => new ShortcutRow(new(GeneratedKeyKind.Preview, c.Id), $"Open Preview: {c.Name}"))]));
        return sections;
    }

    /// <summary>The combos in the order they're listed, with their names, for "Add Combo Shortcut…".</summary>
    public List<ActionCombo> ShortcutCombos() => ActionComboOrder();

    /// <summary>The name a map key goes by in the window ("Clear All", "Start “Sermon”", a combo's name).</summary>
    public string ShortcutLabel(string mapKey)
    {
        if (KeyCommands.Parse(mapKey) is { } command) return command.DisplayName();
        if (GeneratedKey.Parse(mapKey) is not { } key) return mapKey;
        if (key.Kind == GeneratedKeyKind.Combo) return Library.Get<ActionCombo>(key.Id)?.Name ?? "A combo";
        return ShortcutSections().SelectMany(s => s.Rows).FirstOrDefault(r => r.Key == key)?.Label ?? key.Id;
    }

    // MARK: Group hot keys

    public IReadOnlyList<GroupDefinition> GroupHotKeyGroups => GroupPalettes.Current(Library).Groups;

    /// <summary>
    /// Gives a group (Chorus, Verse 2…) its hot key letter, or "" for none. A letter equal to the
    /// group's default is stored as "unset", so a later change to the defaults reaches it (the
    /// Mac's GroupHotKeyChip.write). Saved in the library's group palette.
    /// </summary>
    public void SetGroupHotKey(string groupId, string? letter)
    {
        if (!Permit(OperatorAction.ChangeShortcuts)) return;
        var palette = Core.Json.MxuJson.Clone(GroupPalettes.Current(Library));
        if (palette.Groups.FirstOrDefault(g => g.Id == groupId) is not { } group) return;
        var normalized = HotKeyJump.NormalizedName(group.Name);
        group.HotKey = letter is not null && HotKeyJump.DefaultHotKeys.GetValueOrDefault(normalized) == letter ? null : letter;
        try
        {
            Library.Save(palette);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't save a group's hot key", ex);
            Problem = $"Couldn't save the hot key: {ex.Message}";
            return;
        }
        RefreshHotKeys();
        KeyMapChanged?.Invoke();
    }
}
