using System.Globalization;
using MxU.Slides.Core.Control;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Automation;

/// <summary>One thing an action editor can pick: a library item, a timer, a screen, a MIDI output.</summary>
/// <param name="Id">What gets stored in the step (an empty id is the "nothing chosen" entry).</param>
/// <param name="Name">What the list shows.</param>
/// <param name="Detail">An optional second line ("Video", "Song").</param>
public sealed record EditorChoice(string Id, string Name, string? Detail = null)
{
    /// <summary>WPF lists show <see cref="object.ToString"/> when they have no template, so this is the visible text.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// Everything the action editors can offer, gathered by the app when an editor opens: the
/// library's presets, alerts, overlays …, the timers, and this computer's inputs, screens and
/// MIDI outputs. On the Mac the editors read these straight from the AppModel, the timers
/// controller and the device inventories; here the app hands them over in one record so the
/// editing rules below don't need the app.
/// </summary>
public sealed record ActionChoices
{
    public IReadOnlyList<EditorChoice> OutputPresets { get; init; } = [];
    public IReadOnlyList<EditorChoice> Media { get; init; } = [];
    public IReadOnlyList<EditorChoice> Presentations { get; init; } = [];
    public IReadOnlyList<EditorChoice> AudioItems { get; init; } = [];

    /// <summary>Music playlists only (media playlists can't be played by a step).</summary>
    public IReadOnlyList<EditorChoice> AudioPlaylists { get; init; } = [];
    public IReadOnlyList<EditorChoice> Alerts { get; init; } = [];
    public IReadOnlyList<EditorChoice> Overlays { get; init; } = [];

    /// <summary>The named video inputs ("Pulpit camera").</summary>
    public IReadOnlyList<EditorChoice> LiveInputs { get; init; } = [];
    public IReadOnlyList<EditorChoice> AudioInputs { get; init; } = [];
    public IReadOnlyList<EditorChoice> ConfidenceLayouts { get; init; } = [];

    /// <summary>The confidence (stage) screens; ids as the Mac stores them (an upper-case UUID).</summary>
    public IReadOnlyList<EditorChoice> ConfidenceScreens { get; init; } = [];
    public IReadOnlyList<EditorChoice> Timers { get; init; } = [];
    public IReadOnlyList<EditorChoice> Combos { get; init; } = [];
    public IReadOnlyList<EditorChoice> CapturePresets { get; init; } = [];
    public IReadOnlyList<EditorChoice> MidiDevices { get; init; } = [];

    /// <summary>How many slides a presentation has (shown beside the Slide number); null when unknown.</summary>
    public Func<string, int?>? SlideCount { get; init; }

    /// <summary>A combo's icon name, so a Fire Combo step can wear its combo's icon.</summary>
    public Func<string, string?>? ComboIcon { get; init; }
}

/// <summary>Where a picker's entries come from.</summary>
public enum ActionPickSource
{
    Layer,
    LiveInputLayer,
    OutputPreset,
    Media,
    Alert,
    Timer,
    Combo,
    MidiDevice,
    CapturePreset,
    Presentation,
    ConfidenceLayout,
    ConfidenceScreen,
    Overlay,
    LiveInput,
    AudioInput,
    AudioItem,
    AudioPlaylist,
}

/// <summary>
/// One setting of a step, as the editor shows it: a picker, a number, a choice of options,
/// a tick box, or a note. Each knows how to read and write its value on a
/// <see cref="SlideAction"/>, storing it the way the Mac does (an empty choice is stored as absent).
/// </summary>
public abstract record ActionField(string Label);

/// <summary>A choice from a list. The first entry (id "") reads <paramref name="EmptyLabel"/> and is stored as absent.</summary>
public sealed record PickField(
    string Label, ActionPickSource Source, string EmptyLabel, string MissingLabel,
    Func<SlideAction, string?> Get, Action<SlideAction, string?> Set) : ActionField(Label)
{
    /// <summary>Lists that can run to hundreds (media, songs, music) open a search window instead of a drop-down.</summary>
    public bool Searchable => Source is ActionPickSource.Media or ActionPickSource.Presentation or ActionPickSource.AudioItem;

    /// <summary>Stores a picked id ("" stores absent).</summary>
    public void Apply(SlideAction action, string? id) => Set(action, string.IsNullOrEmpty(id) ? null : id);
}

/// <summary>A whole number with − and + buttons, kept inside <paramref name="Min"/>…<paramref name="Max"/>.</summary>
public sealed record NumberField(
    string Label, int Min, int Max, Func<SlideAction, int> Get, Action<SlideAction, int> Set,
    Func<int, string>? Display = null) : ActionField(Label)
{
    /// <summary>Stores a value, clamped into the range (the Mac's steppers clamp the same way).</summary>
    public void Apply(SlideAction action, int value) => Set(action, ActionCombos.Clamped(value, Min, Max));
}

/// <summary>
/// A pick from fixed options (a timer's mode, a MIDI message kind, a playlist's repeat).
/// <paramref name="Reshapes"/> is true when changing it changes which other settings show.
/// </summary>
public sealed record OptionField(
    string Label, IReadOnlyList<EditorChoice> Options, Func<SlideAction, string> Get, Action<SlideAction, string> Set,
    bool Reshapes = false) : ActionField(Label);

/// <summary>A tick box.</summary>
public sealed record ToggleField(string Label, Func<SlideAction, bool> Get, Action<SlideAction, bool> Set) : ActionField(Label);

/// <summary>A line of explanation instead of settings.</summary>
public sealed record NoteField(string Text) : ActionField("");

/// <summary>A node of the Add Action menu: a submenu (with <see cref="Children"/>), a command (with <see cref="Make"/>) or a separator.</summary>
public sealed record ActionMenuItem(string Title, Func<SlideAction>? Make = null, IReadOnlyList<ActionMenuItem>? Children = null)
{
    public static readonly ActionMenuItem Separator = new("-");

    public bool IsSeparator => Make is null && Children is null && Title == "-";

    public bool IsSubmenu => Children is not null;
}

/// <summary>
/// The rules behind the action editors: which settings each kind of step has, what each
/// picker offers, what the Add Action menu holds, and how a delay is typed. Port of the
/// non-UI half of Sources/ActionEditors.swift (ActionParameterFields, AddActionMenuItems,
/// ActionsMenu) and Sources/LiveInputCatalog.swift. <see cref="ActionCombos"/> holds the
/// rest (titles, defaults, moving and removing steps).
/// </summary>
public static class ActionEditing
{
    /// <summary>The "nothing chosen yet" label on pickers.</summary>
    public const string Choose = "Choose…";

    /// <summary>The Mac's Delay row: 0 to 3600 seconds, one decimal place.</summary>
    public const double MaxDelaySeconds = 3600;

    /// <summary>A card's header: the step's kind in capitals ("FIRE MEDIA").</summary>
    public static string Header(SlideAction action) => action.Kind.DisplayName().ToUpperInvariant();

    // MARK: Settings per kind

    /// <summary>
    /// The settings a step shows, in order (the Delay row comes after them, for every kind).
    /// Port of ActionParameterFields.parameters: the same fields, labels, defaults and ranges.
    /// </summary>
    public static IReadOnlyList<ActionField> Fields(SlideAction action)
    {
        switch (action.Kind)
        {
            case SlideActionKind.ClearLayer:
                return [Pick("Layer", ActionPickSource.Layer, a => a.Layer, (a, v) => a.Layer = v)];
            case SlideActionKind.SwitchOutputPreset:
                return [Pick("Preset", ActionPickSource.OutputPreset, a => a.PresetId, (a, v) => a.PresetId = v)];
            case SlideActionKind.FireMedia:
                return [Pick("Media", ActionPickSource.Media, a => a.MediaId, (a, v) => a.MediaId = v, missing: "Missing media")];
            case SlideActionKind.FireAlert:
                return [Pick("Alert", ActionPickSource.Alert, a => a.AlertId, (a, v) => a.AlertId = v)];
            case SlideActionKind.TimerStart or SlideActionKind.TimerPause or SlideActionKind.TimerReset:
                return [TimerPick()];
            case SlideActionKind.TimerConfigure:
            {
                List<ActionField> fields =
                [
                    TimerPick(),
                    new OptionField("Mode",
                        [new("", "Keep Mode"), new("countdown", "Countdown"), new("countdownToTime", "Countdown to Time"), new("countUp", "Count Up")],
                        a => a.TimerMode is { } mode ? TimerController.ModeKey(mode) : "",
                        (a, token) => a.TimerMode = TimerController.ModeForKey(token), Reshapes: true),
                ];
                if (action.TimerMode == TimerMode.CountdownToTime)
                {
                    fields.Add(Number("Hour", a => a.TimerHour, (a, v) => a.TimerHour = v, fallback: 9, 0, 23));
                    fields.Add(Number("Minute", a => a.TimerMinute, (a, v) => a.TimerMinute = v, fallback: 0, 0, 59));
                }
                else
                {
                    fields.Add(new NumberField("Minutes", 0, 600, a => (int)(a.TimerDurationSeconds ?? 0) / 60,
                        (a, minutes) => a.TimerDurationSeconds = ActionCombos.WithMinutes(a.TimerDurationSeconds, minutes)));
                    fields.Add(new NumberField("Seconds", 0, 59, a => (int)(a.TimerDurationSeconds ?? 0) % 60,
                        (a, seconds) => a.TimerDurationSeconds = ActionCombos.WithSeconds(a.TimerDurationSeconds, seconds)));
                }
                return fields;
            }
            case SlideActionKind.FireCombo:
                return [Pick("Combo", ActionPickSource.Combo, a => a.ComboId, (a, v) => a.ComboId = v)];
            case SlideActionKind.MidiOut:
            {
                List<ActionField> fields =
                [
                    Pick("Device", ActionPickSource.MidiDevice, a => a.MidiDeviceId, (a, v) => a.MidiDeviceId = v, empty: "All Enabled Devices"),
                    new OptionField("Message", [new("noteOn", "Note On"), new("controlChange", "Control Change")],
                        a => (a.MidiKind ?? MIDIMessageKind.NoteOn) == MIDIMessageKind.ControlChange ? "controlChange" : "noteOn",
                        (a, token) => a.MidiKind = token == "controlChange" ? MIDIMessageKind.ControlChange : MIDIMessageKind.NoteOn, Reshapes: true),
                    Number("Channel", a => a.MidiChannel, (a, v) => a.MidiChannel = v, fallback: 1, 1, 16),
                ];
                if (action.MidiKind == MIDIMessageKind.ControlChange)
                {
                    fields.Add(Number("Controller", a => a.MidiNumber, (a, v) => a.MidiNumber = v, fallback: 0, 0, 127));
                    fields.Add(Number("Value", a => a.MidiValue, (a, v) => a.MidiValue = v, fallback: 127, 0, 127));
                }
                else
                {
                    fields.Add(Number("Note", a => a.MidiNumber, (a, v) => a.MidiNumber = v, fallback: 0, 0, 127, NoteDisplay));
                    fields.Add(Number("Velocity", a => a.MidiValue, (a, v) => a.MidiValue = v, fallback: 127, 0, 127));
                }
                return fields;
            }
            case SlideActionKind.CaptureStart:
                return [Pick("Preset", ActionPickSource.CapturePreset, a => a.CapturePresetId, (a, v) => a.CapturePresetId = v)];
            case SlideActionKind.CaptureStop:
                return [Pick("Preset", ActionPickSource.CapturePreset, a => a.CapturePresetId, (a, v) => a.CapturePresetId = v, empty: "All Capture")];
            case SlideActionKind.FirePresentation:
                return
                [
                    Pick("Presentation", ActionPickSource.Presentation, a => a.PresentationId, (a, v) => a.PresentationId = v),
                    // The stepper shows the slide's number (1 = the first) and stores it minus one.
                    new NumberField("Slide", 1, 999, a => (a.SlideIndex ?? 0) + 1, (a, shown) => a.SlideIndex = ActionCombos.StepperSlideIndex(shown)),
                ];
            case SlideActionKind.SetConfidenceLayout:
                return
                [
                    Pick("Layout", ActionPickSource.ConfidenceLayout, a => a.ConfidenceLayoutId, (a, v) => a.ConfidenceLayoutId = v, empty: "Built-in Layout"),
                    Pick("Screen", ActionPickSource.ConfidenceScreen, a => a.ConfidenceScreenId, (a, v) => a.ConfidenceScreenId = v,
                        empty: "All Confidence Monitors", missing: "Missing screen"),
                ];
            case SlideActionKind.FireOverlay:
                return [Pick("Overlay", ActionPickSource.Overlay, a => a.OverlayId, (a, v) => a.OverlayId = v)];
            case SlideActionKind.DismissOverlay:
                return [Pick("Overlay", ActionPickSource.Overlay, a => a.OverlayId, (a, v) => a.OverlayId = v, empty: "All Overlays")];
            case SlideActionKind.FireLiveInput:
                return
                [
                    new PickField("Source", ActionPickSource.LiveInput, Choose, "Missing input",
                        a => LiveInputTokens.SelectionToken(a.LiveInputId, a.InputSourceKind, a.InputSourceId), LiveInputTokens.Apply),
                    Pick("Layer", ActionPickSource.LiveInputLayer, a => a.Layer, (a, v) => a.Layer = v, empty: "Video Input"),
                ];
            case SlideActionKind.SetSignage or SlideActionKind.SetScreenSource or SlideActionKind.ClearSignage:
                return [new NoteField("Digital Signage was removed. This step does nothing.")];
            case SlideActionKind.EnableAudioInput or SlideActionKind.DisableAudioInput:
                return [Pick("Input", ActionPickSource.AudioInput, a => a.AudioInputId, (a, v) => a.AudioInputId = v, missing: "Missing input")];
            case SlideActionKind.FireAudio:
                return
                [
                    Pick("File", ActionPickSource.AudioItem, a => a.AudioItemId, (a, v) => a.AudioItemId = v, missing: "Missing music"),
                    new ToggleField("Repeat", a => a.AudioRepeat ?? false, (a, on) => a.AudioRepeat = ActionCombos.FileRepeat(on)),
                ];
            case SlideActionKind.FireAudioPlaylist:
                return
                [
                    Pick("Playlist", ActionPickSource.AudioPlaylist, a => a.PlaylistId, (a, v) => a.PlaylistId = v),
                    new OptionField("Repeat", [new("", "Playlist Setting"), new("on", "On"), new("off", "Off")],
                        a => ActionCombos.RepeatToken(a.AudioRepeat), (a, token) => a.AudioRepeat = ActionCombos.RepeatFromToken(token)),
                ];
            default:
                // Clear All, Clear Music and Dismiss Alert have nothing to choose.
                return [];
        }
    }

    private static PickField Pick(
        string label, ActionPickSource source, Func<SlideAction, string?> get, Action<SlideAction, string?> set,
        string empty = Choose, string missing = "Missing") => new(label, source, empty, missing, get, set);

    private static PickField TimerPick() =>
        Pick("Timer", ActionPickSource.Timer, a => a.TimerId, (a, v) => a.TimerId = v, missing: "Missing timer");

    private static NumberField Number(
        string label, Func<SlideAction, int?> get, Action<SlideAction, int> set, int fallback, int min, int max, Func<int, string>? display = null) =>
        new(label, min, max, a => get(a) ?? fallback, set, display);

    /// <summary>"C3 · 60": the Note stepper shows the note's name and number.</summary>
    public static string NoteDisplay(int number) => $"{MidiCommandMap.NoteName(number)} · {number.ToString(CultureInfo.InvariantCulture)}";

    // MARK: Picker entries

    /// <summary>
    /// What a picker lists: its empty entry first (id ""), then the choices. A step that points
    /// at something no longer there gets one more entry, so the editor can show it as missing
    /// (the Mac shows a blank picker). A Fire Combo picker never offers the combo being edited.
    /// </summary>
    public static IReadOnlyList<EditorChoice> Entries(PickField field, SlideAction action, ActionChoices choices, string? editingComboId = null)
    {
        var list = new List<EditorChoice> { new("", field.EmptyLabel) };
        list.AddRange(field.Source switch
        {
            ActionPickSource.Layer => ActionCombos.ClearableLayers.Select(l => new EditorChoice(l.Raw(), l.DisplayName())),
            ActionPickSource.LiveInputLayer => ActionCombos.ClearableLayers.Where(l => l != LayerKind.VideoInput)
                .Select(l => new EditorChoice(l.Raw(), l.DisplayName())),
            ActionPickSource.LiveInput => choices.LiveInputs.Select(c => c with { Id = LiveInputTokens.ItemToken(c.Id) }),
            ActionPickSource.Combo => choices.Combos.Where(c => c.Id != editingComboId),
            var source => Source(source, choices),
        });
        var current = field.Get(action);
        if (!string.IsNullOrEmpty(current) && list.All(c => c.Id != current))
        {
            var label = field.Source == ActionPickSource.LiveInput && LiveInputTokens.LiveInputId(current) is null
                ? LiveInputTokens.LegacyLabel(action.InputSourceKind, action.InputSourceId)
                : field.MissingLabel;
            list.Add(new EditorChoice(current, label));
        }
        return list;
    }

    private static IReadOnlyList<EditorChoice> Source(ActionPickSource source, ActionChoices choices) => source switch
    {
        ActionPickSource.OutputPreset => choices.OutputPresets,
        ActionPickSource.Media => choices.Media,
        ActionPickSource.Alert => choices.Alerts,
        ActionPickSource.Timer => choices.Timers,
        ActionPickSource.MidiDevice => choices.MidiDevices,
        ActionPickSource.CapturePreset => choices.CapturePresets,
        ActionPickSource.Presentation => choices.Presentations,
        ActionPickSource.ConfidenceLayout => choices.ConfidenceLayouts,
        ActionPickSource.ConfidenceScreen => choices.ConfidenceScreens,
        ActionPickSource.Overlay => choices.Overlays,
        ActionPickSource.AudioInput => choices.AudioInputs,
        ActionPickSource.AudioItem => choices.AudioItems,
        ActionPickSource.AudioPlaylist => choices.AudioPlaylists,
        _ => [],
    };

    /// <summary>The name a picker button shows for the step's current value: the item's name, "Choose…", or the missing label.</summary>
    public static string CurrentTitle(PickField field, SlideAction action, ActionChoices choices, string? editingComboId = null)
    {
        var current = field.Get(action) ?? "";
        return Entries(field, action, choices, editingComboId).FirstOrDefault(c => c.Id == current)?.Name ?? field.EmptyLabel;
    }

    /// <summary>
    /// True when a step still needs something chosen before it can do anything: a picker whose
    /// empty entry is "Choose…" is still on it (Fire Media with no media, Start Timer with no timer …).
    /// </summary>
    public static bool NeedsChoice(SlideAction action) =>
        Fields(action).OfType<PickField>().Any(f => f.EmptyLabel == Choose && string.IsNullOrEmpty(f.Get(action)));

    // MARK: Delay

    /// <summary>The Delay box's text: "0", "2", "2.5".</summary>
    public static string DelayText(SlideAction action) => (action.DelaySeconds ?? 0).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a typed delay ("2", "2.5", "2,5"), rounds it to a tenth of a second and keeps it
    /// within 0…3600. Null when the text isn't a number (the box then shows the old value again).
    /// </summary>
    public static double? ParseDelay(string text)
    {
        var trimmed = text.Trim().Replace(',', '.');
        if (trimmed.EndsWith('s')) trimmed = trimmed[..^1].TrimEnd();
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds)) return null;
        return Math.Clamp(Math.Round(seconds, 1, MidpointRounding.AwayFromZero), 0, MaxDelaySeconds);
    }

    /// <summary>The Delay stepper: one second up or down, kept within 0…3600 (zero is stored as absent).</summary>
    public static void StepDelay(SlideAction action, int by) =>
        ActionCombos.SetDelay(action, Math.Clamp((action.DelaySeconds ?? 0) + by, 0, MaxDelaySeconds));

    // MARK: Lists

    /// <summary>A slide's or media item's list as stored: none is stored as absent (the Mac's <c>actions.isEmpty ? nil : actions</c>).</summary>
    public static List<SlideAction>? Stored(IReadOnlyList<SlideAction> actions) => actions.Count == 0 ? null : [.. actions];

    /// <summary>The slide menu's title: "Actions: None" or "Actions: 3".</summary>
    public static string ActionsMenuTitle(int count) => count == 0 ? "Actions: None" : $"Actions: {count.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The slide menu offers Edit All… and Remove All only for two or more steps (as on the Mac).</summary>
    public static bool OffersEditAll(int count) => count > 1;

    // MARK: The Add Action menu

    /// <summary>
    /// The Add Action menu, in the Mac's order. A submenu with nothing in it is left out
    /// (no output presets, no timers, no other combos, no live inputs …), and the combo being
    /// edited is never offered to itself. Each command makes a fresh step with a new id.
    /// Port of AddActionMenuItems.
    /// </summary>
    public static IReadOnlyList<ActionMenuItem> AddMenu(ActionChoices choices, string? excludeComboId = null, Func<string>? newId = null)
    {
        ActionMenuItem Command(string title, SlideActionKind kind, Action<SlideAction>? fill = null) =>
            new(title, () => ActionCombos.Make(kind, fill, newId));

        var menu = new List<ActionMenuItem>();
        if (choices.OutputPresets.Count > 0)
            menu.Add(new("Output Preset", Children: [.. choices.OutputPresets.Select(p => Command(p.Name, SlideActionKind.SwitchOutputPreset, a => a.PresetId = p.Id))]));

        menu.Add(new("Clear", Children:
        [
            Command("Clear All", SlideActionKind.ClearAll),
            ActionMenuItem.Separator,
            .. ActionCombos.ClearableLayers.Select(layer => Command(layer.DisplayName(), SlideActionKind.ClearLayer, a => a.Layer = layer.Raw())),
            ActionMenuItem.Separator,
            Command("Clear Music", SlideActionKind.ClearAudio),
        ]));
        menu.Add(Command("Fire Media…", SlideActionKind.FireMedia));
        menu.Add(Command("Fire Presentation", SlideActionKind.FirePresentation));

        var music = new List<ActionMenuItem>();
        if (choices.AudioPlaylists.Count > 0)
            music.Add(new("Playlist", Children: [.. choices.AudioPlaylists.Select(p => Command(p.Name, SlideActionKind.FireAudioPlaylist, a => a.PlaylistId = p.Id))]));
        music.Add(Command("File…", SlideActionKind.FireAudio));
        menu.Add(new("Music", Children: music));

        menu.Add(new("Alert", Children: WithTail(
            choices.Alerts.Select(alert => Command(alert.Name, SlideActionKind.FireAlert, a => a.AlertId = alert.Id)),
            Command("Dismiss Alert", SlideActionKind.DismissAlert))));
        menu.Add(new("Overlay", Children: WithTail(
            choices.Overlays.Select(overlay => Command(overlay.Name, SlideActionKind.FireOverlay, a => a.OverlayId = overlay.Id)),
            Command("Take Down Overlays", SlideActionKind.DismissOverlay))));

        if (choices.LiveInputs.Count > 0)
            menu.Add(new("Live Input", Children: [.. choices.LiveInputs.Select(input => Command(input.Name, SlideActionKind.FireLiveInput, a => a.LiveInputId = input.Id))]));
        if (choices.AudioInputs.Count > 0)
            menu.Add(new("Audio Input", Children:
            [
                .. choices.AudioInputs.SelectMany(input => new[]
                {
                    Command($"{input.Name} On", SlideActionKind.EnableAudioInput, a => a.AudioInputId = input.Id),
                    Command($"{input.Name} Off", SlideActionKind.DisableAudioInput, a => a.AudioInputId = input.Id),
                }),
            ]));

        var layouts = new List<ActionMenuItem> { Command("Built-in Layout", SlideActionKind.SetConfidenceLayout) };
        if (choices.ConfidenceLayouts.Count > 0) layouts.Add(ActionMenuItem.Separator);
        layouts.AddRange(choices.ConfidenceLayouts.Select(layout => Command(layout.Name, SlideActionKind.SetConfidenceLayout, a => a.ConfidenceLayoutId = layout.Id)));
        menu.Add(new("Confidence Layout", Children: layouts));

        if (choices.Timers.Count > 0)
        {
            ActionMenuItem TimerOps(string title, SlideActionKind kind) =>
                new(title, Children: [.. choices.Timers.Select(timer => Command(timer.Name, kind, a => a.TimerId = timer.Id))]);
            menu.Add(new("Timer", Children:
            [
                TimerOps("Start", SlideActionKind.TimerStart),
                TimerOps("Pause", SlideActionKind.TimerPause),
                TimerOps("Reset", SlideActionKind.TimerReset),
                new("Configure", Children: [.. choices.Timers.Select(timer => new ActionMenuItem(timer.Name, () => ActionCombos.MakeConfigureTimer(timer.Id, newId)))]),
            ]));
        }

        var combos = choices.Combos.Where(c => c.Id != excludeComboId).ToList();
        if (combos.Count > 0)
            menu.Add(new("Action Combo", Children: [.. combos.Select(combo => Command(combo.Name, SlideActionKind.FireCombo, a => a.ComboId = combo.Id))]));

        var capture = new List<ActionMenuItem>();
        if (choices.CapturePresets.Count > 0)
        {
            capture.Add(new("Start", Children: [.. choices.CapturePresets.Select(p => Command(p.Name, SlideActionKind.CaptureStart, a => a.CapturePresetId = p.Id))]));
            capture.Add(new("Stop", Children: [.. choices.CapturePresets.Select(p => Command(p.Name, SlideActionKind.CaptureStop, a => a.CapturePresetId = p.Id))]));
            capture.Add(ActionMenuItem.Separator);
        }
        capture.Add(Command("Stop All Capture", SlideActionKind.CaptureStop));
        menu.Add(new("Capture", Children: capture));
        menu.Add(Command("MIDI Out", SlideActionKind.MidiOut));
        return menu;
    }

    /// <summary>The items, then (after a separator when there are any) the closing command.</summary>
    private static List<ActionMenuItem> WithTail(IEnumerable<ActionMenuItem> items, ActionMenuItem tail)
    {
        var list = items.ToList();
        if (list.Count > 0) list.Add(ActionMenuItem.Separator);
        list.Add(tail);
        return list;
    }

    /// <summary>Finds a command by its titles from the top ("Timer", "Start", "Sermon"); null when there's no such path.</summary>
    public static ActionMenuItem? Find(IReadOnlyList<ActionMenuItem> menu, params string[] path)
    {
        ActionMenuItem? found = null;
        IReadOnlyList<ActionMenuItem>? level = menu;
        foreach (var title in path)
        {
            found = level?.FirstOrDefault(item => item.Title == title);
            if (found is null) return null;
            level = found.Children;
        }
        return found;
    }
}

/// <summary>
/// How a Fire Live Input step's Source picker stores its choice. A step names a video input
/// ("item::" + its id); an older step names a device directly ("camera::…", "ndi::…"),
/// which still works and shows as its own entry. Port of Sources/LiveInputCatalog.swift.
/// </summary>
public static class LiveInputTokens
{
    private const string ItemPrefix = "item::";

    public static string ItemToken(string liveInputId) => ItemPrefix + liveInputId;

    /// <summary>The picker's token for a step: its input's, else its older device's, else "".</summary>
    public static string SelectionToken(string? liveInputId, CaptureSourceKind? kind, string? id)
    {
        if (!string.IsNullOrEmpty(liveInputId)) return ItemToken(liveInputId);
        if (kind is not { } source || string.IsNullOrEmpty(id)) return "";
        return $"{CaptureSourceKindJsonConverter.ToRaw(source)}::{id}";
    }

    /// <summary>The input id in an "item::" token; null for anything else.</summary>
    public static string? LiveInputId(string token)
    {
        if (!token.StartsWith(ItemPrefix, StringComparison.Ordinal)) return null;
        var id = token[ItemPrefix.Length..];
        return id.Length == 0 ? null : id;
    }

    /// <summary>The device an older token names ("ndi::BOOTH (Cam 1)"); null when it isn't one.</summary>
    public static (CaptureSourceKind Kind, string Id)? Choice(string token)
    {
        if (token.Length == 0) return null;
        var parts = token.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !CaptureSourceKindJsonConverter.TryParse(parts[0], out var kind)) return null;
        return (kind, string.Join(':', parts.Skip(1)));
    }

    /// <summary>
    /// Stores a picked token on a step: an input clears the older device fields; an older
    /// device token keeps it as it was; "" clears everything.
    /// </summary>
    public static void Apply(SlideAction action, string? token)
    {
        action.InputSourceKind = null;
        action.InputSourceId = null;
        token ??= "";
        if (LiveInputId(token) is { } itemId)
        {
            action.LiveInputId = itemId;
        }
        else if (Choice(token) is { } device)
        {
            action.LiveInputId = null;
            action.InputSourceKind = device.Kind;
            action.InputSourceId = device.Id;
        }
        else
        {
            action.LiveInputId = null;
        }
    }

    /// <summary>
    /// How an older step's device reads ("NDI: BOOTH (Cam 1)"). The Mac asks the camera for
    /// its name; Windows shows its id.
    /// </summary>
    public static string LegacyLabel(CaptureSourceKind? kind, string? id)
    {
        if (string.IsNullOrEmpty(id)) return "None";
        return kind == CaptureSourceKind.Ndi ? $"NDI: {id}" : $"Camera: {id}";
    }
}
