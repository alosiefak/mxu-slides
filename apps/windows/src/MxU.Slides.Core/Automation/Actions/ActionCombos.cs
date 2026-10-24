using MxU.Slides.Core.Json;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// The editing rules behind the Action Combos module and the action editors: making a
/// step, its defaults, the chips' titles, reordering, duplicating and pasting combos,
/// and what a drop onto a combo adds. The WPF views call these so they behave like the
/// Mac's. Port of the non-UI logic in Sources/Modules/ActionCombosModule.swift and
/// Sources/ActionEditors.swift.
/// </summary>
public static class ActionCombos
{
    /// <summary>The colours a combo can wear.</summary>
    public static readonly IReadOnlyList<(string Name, string Hex)> Palette =
    [
        ("Red", "#FF5F57FF"), ("Orange", "#FFA344FF"), ("Yellow", "#FFD60AFF"),
        ("Green", "#32D74BFF"), ("Blue", "#4B9BFFFF"), ("Purple", "#BF5AF2FF"),
    ];

    /// <summary>The icons a combo can wear (glyph names; "combos", the bolt, is the default and stored as absent).</summary>
    public static readonly IReadOnlyList<(string Glyph, string Name)> Icons =
    [
        ("combos", "Bolt"), ("timers", "Timer"), ("alerts", "Bell"),
        ("media", "Media"), ("audio", "Music"), ("screens", "Screen"),
        ("clear", "Clear"), ("midi", "MIDI"), ("broadcast", "Record"),
        ("overlays", "Overlay"), ("light", "Light"),
    ];

    /// <summary>A combo's icon: its stored one if known, else the bolt.</summary>
    public static string Icon(ActionCombo combo) =>
        combo.IconName is { } name && Icons.Any(i => i.Glyph == name) ? name : "combos";

    /// <summary>The stored value for an icon choice (the default is stored as absent).</summary>
    public static string? StoredIcon(string glyph) => glyph == "combos" ? null : glyph;

    /// <summary>"empty", "1 action", "3 actions".</summary>
    public static string StepCaption(ActionCombo combo) => combo.Actions.Count switch
    {
        0 => "empty",
        1 => "1 action",
        var count => $"{count} actions",
    };

    /// <summary>Run on startup is stored as true, or absent.</summary>
    public static void SetRunOnStartup(ActionCombo combo, bool on) => combo.RunOnStartup = on ? true : null;

    /// <summary>A rename: trimmed, and ignored when it trims to nothing.</summary>
    public static bool Rename(ActionCombo combo, string name)
    {
        var trimmed = name.TrimmingWhitespacesAndNewlines();
        if (trimmed.Length == 0) return false;
        combo.Name = trimmed;
        return true;
    }

    /// <summary>A new step of a kind, with a fresh id (the Add Action menu's make).</summary>
    public static SlideAction Make(SlideActionKind kind, Action<SlideAction>? fill = null, Func<string>? newId = null)
    {
        var action = new SlideAction { Id = (newId ?? TimelineClips.NewId)(), Kind = kind };
        fill?.Invoke(action);
        return action;
    }

    /// <summary>Configure Timer from the menu starts as a five-minute countdown.</summary>
    public static SlideAction MakeConfigureTimer(string timerId, Func<string>? newId = null) =>
        Make(SlideActionKind.TimerConfigure, a =>
        {
            a.TimerId = timerId;
            a.TimerMode = TimerMode.Countdown;
            a.TimerDurationSeconds = 300;
        }, newId);

    /// <summary>Changing a step's kind in the placement sheet keeps its id and delay.</summary>
    public static SlideAction Replacing(SlideAction draft, SlideAction replacement)
    {
        var next = MxuJson.Clone(replacement);
        next.Id = draft.Id;
        next.DelaySeconds = draft.DelaySeconds;
        return next;
    }

    /// <summary>A delay of zero is stored as absent.</summary>
    public static void SetDelay(SlideAction action, double seconds) => action.DelaySeconds = seconds > 0 ? seconds : null;

    /// <summary>A picker's empty choice is stored as absent.</summary>
    public static string? Stored(string value) => value.Length == 0 ? null : value;

    /// <summary>Steppers clamp into their range (MIDI channel 1–16, numbers and values 0–127, hours 0–23 …).</summary>
    public static int Clamped(int value, int lower, int upper) => Math.Min(upper, Math.Max(lower, value));

    /// <summary>The combo chip stores the first slide as absent; the editor's stepper stores what it shows, minus one.</summary>
    public static int? ChipSlideIndex(int index) => index == 0 ? null : index;

    public static int StepperSlideIndex(int shown) => Math.Max(0, shown - 1);

    /// <summary>The duration steppers: whole minutes and seconds of the stored total.</summary>
    public static double WithMinutes(double? total, int minutes) => minutes * 60 + (int)(total ?? 0) % 60;

    public static double WithSeconds(double? total, int seconds) => (int)(total ?? 0) / 60 * 60 + seconds;

    /// <summary>A music file's Repeat toggle stores on as true, off as absent.</summary>
    public static bool? FileRepeat(bool on) => on ? true : null;

    /// <summary>A playlist's repeat picker: "" (the playlist's own setting), "on" or "off".</summary>
    public static string RepeatToken(bool? audioRepeat) => audioRepeat switch
    {
        true => "on",
        false => "off",
        null => "",
    };

    public static bool? RepeatFromToken(string token) => token.Length == 0 ? null : token == "on";

    public static string RepeatTitle(bool? audioRepeat) => audioRepeat switch
    {
        true => "Repeat: On",
        false => "Repeat: Off",
        null => "Repeat: Playlist Setting",
    };

    public static string TimerModeTitle(TimerMode? mode) => mode switch
    {
        TimerMode.Countdown => "Countdown",
        TimerMode.CountdownToTime => "Countdown to Time",
        TimerMode.CountUp => "Count Up",
        _ => "Keep Mode",
    };

    /// <summary>The duration chip's choices, in seconds.</summary>
    public static readonly IReadOnlyList<int> DurationChoices = [60, 120, 180, 300, 450, 600, 900, 1200, 1800];

    public static string DurationTitle(double? seconds) =>
        seconds is > 0 and var value ? SlideActions.MinutesSeconds((int)value) : "Duration…";

    public static string ConfidenceLayoutTitle(string? id, Func<string, string?> name) =>
        string.IsNullOrEmpty(id) ? "Built-in Layout" : EntryTitle(id, name) ?? "Layout…";

    /// <summary>A chip's title for a library id: its name, "Missing" when gone, null when not set.</summary>
    public static string? EntryTitle(string? id, Func<string, string?> name) =>
        string.IsNullOrEmpty(id) ? null : name(id) ?? "Missing";

    /// <summary>Steps with nothing to choose show no chips.</summary>
    public static bool HasParameters(SlideActionKind kind) =>
        kind is not (SlideActionKind.ClearAll or SlideActionKind.ClearAudio or SlideActionKind.DismissAlert) && !kind.IsRetired();

    /// <summary>The layers a Clear step offers, top layer first (the Mac lists them reversed).</summary>
    public static IEnumerable<LayerKind> ClearableLayers => LayerKinds.All.Reverse();

    /// <summary>Swaps a step with its neighbour (offset -1 up, +1 down); false at the ends.</summary>
    public static bool Move(List<SlideAction> actions, string actionId, int offset)
    {
        var index = actions.FindIndex(a => a.Id == actionId);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= actions.Count) return false;
        (actions[index], actions[target]) = (actions[target], actions[index]);
        return true;
    }

    public static bool Remove(List<SlideAction> actions, string actionId) => actions.RemoveAll(a => a.Id == actionId) > 0;

    /// <summary>
    /// A library item dropped onto a combo: media adds Fire Media, another combo adds Fire
    /// Combo (a combo can't fire itself). Anything else adds nothing.
    /// </summary>
    public static SlideAction? Dropped(string kind, string entryId, string comboId, Func<string>? newId = null) => kind switch
    {
        "media" => Make(SlideActionKind.FireMedia, a => a.MediaId = entryId, newId),
        "actionCombo" when entryId != comboId => Make(SlideActionKind.FireCombo, a => a.ComboId = entryId, newId),
        _ => null,
    };

    /// <summary>The copy Duplicate makes: " Copy" on the name, its steps with new ids, its look (not its startup flag).</summary>
    public static ActionCombo Duplicate(ActionCombo combo, string id, Func<string>? newId = null) => Copied(combo, id, combo.Name + " Copy", newId);

    /// <summary>A pasted combo: the same name, its steps with new ids, its look.</summary>
    public static ActionCombo Pasted(ActionCombo combo, string id, Func<string>? newId = null) => Copied(combo, id, combo.Name, newId);

    private static ActionCombo Copied(ActionCombo combo, string id, string name, Func<string>? newId) => new()
    {
        Id = id,
        Name = name,
        Actions = combo.Actions.Select(action =>
        {
            var copy = MxuJson.Clone(action);
            copy.Id = (newId ?? TimelineClips.NewId)();
            return copy;
        }).ToList(),
        ColorHex = combo.ColorHex,
        IconName = combo.IconName,
    };

    /// <summary>The combos a Fire Combo step may pick: every combo but the one being edited.</summary>
    public static IEnumerable<ActionCombo> Choosable(IEnumerable<ActionCombo> combos, string? editingComboId) =>
        combos.Where(c => c.Id != editingComboId);
}
