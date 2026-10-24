using System.Globalization;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// What a slide's (or media item's) actions do when it fires, before the router runs them:
/// which steps wait (a delay), and which clears are skipped because they would clear the
/// very layer the slide just put up. Plus the names and labels the editors show. Port of
/// the action half of SlideScene/SlideSceneBuilder.swift, SlideActionKind.displayName
/// (ActionEditors.swift), slideActionLabel (PresentGridView.swift) and GlyphKind(action:).
/// </summary>
public static class SlideActions
{
    /// <summary>A step with a delay waits; zero or no delay runs at once.</summary>
    public static bool WaitsBeforeFiring(SlideAction action) => (action.DelaySeconds ?? 0) > 0;

    /// <summary>Steps that wait the same time, run together.</summary>
    public sealed record DelayedBatch(double Delay, IReadOnlyList<SlideAction> Actions);

    /// <summary>A list split into the steps that run now and batches by delay (shortest first).</summary>
    public sealed record DelayPlan(IReadOnlyList<SlideAction> Immediate, IReadOnlyList<DelayedBatch> Delayed);

    public static DelayPlan DelaySchedule(IEnumerable<SlideAction> actions)
    {
        var immediate = new List<SlideAction>();
        var byDelay = new SortedDictionary<double, List<SlideAction>>();
        foreach (var action in actions)
        {
            if (WaitsBeforeFiring(action))
            {
                var delay = action.DelaySeconds ?? 0;
                if (!byDelay.TryGetValue(delay, out var batch)) byDelay[delay] = batch = [];
                batch.Add(action);
            }
            else
            {
                immediate.Add(action);
            }
        }
        return new DelayPlan(immediate, byDelay.Select(pair => new DelayedBatch(pair.Key, pair.Value)).ToList());
    }

    /// <summary>
    /// The layers a slide fills when it fires: the slide layer, its background's layer, and
    /// the layers its Fire Media steps land on (<paramref name="mediaLayer"/> says where a media id goes).
    /// </summary>
    public static HashSet<LayerKind> ProtectedLayers(Slide slide, Func<string, LayerKind?>? mediaLayer = null)
    {
        var layers = new HashSet<LayerKind> { LayerKind.Slide };
        if (slide.Background is { } background && background.MediaId.Length > 0)
            layers.Add(SlideSceneBuilder.LayerKindOf(background.Layer));
        foreach (var action in slide.Actions ?? [])
        {
            if (action.Kind != SlideActionKind.FireMedia) continue;
            if (action.MediaId is { } id && mediaLayer?.Invoke(id) is { } layer) layers.Add(layer);
        }
        return layers;
    }

    /// <summary>The layer a media item lands on when fired (port of the router's mediaLayer).</summary>
    public static LayerKind FiredLayer(MediaItem item) => SlideSceneBuilder.LayerKindOf(MediaCueRouting.Dropped(item).Layer);

    public static List<SlideAction> FireActions(Slide slide) => FireActions(slide, ProtectedLayers(slide));

    /// <summary>The slide's steps minus immediate clears of a protected layer (a delayed clear still runs).</summary>
    public static List<SlideAction> FireActions(Slide slide, IReadOnlySet<LayerKind> protectedLayers)
    {
        if (slide.Actions is not { } actions) return [];
        return actions.Where(action =>
            action.Kind != SlideActionKind.ClearLayer || WaitsBeforeFiring(action)
            || LayerKinds.Parse(action.Layer) is not { } layer || !protectedLayers.Contains(layer)).ToList();
    }

    /// <summary>A media item's steps minus immediate clears of the layer it just went up on.</summary>
    public static List<SlideAction> FireActions(MediaItem item, LayerKind layer)
    {
        if (item.Actions is not { } actions) return [];
        return actions.Where(action =>
            action.Kind != SlideActionKind.ClearLayer || WaitsBeforeFiring(action)
            || LayerKinds.Parse(action.Layer) is not { } target || target != layer).ToList();
    }

    /// <summary>The raw kind name stored in documents ("fireMedia").</summary>
    public static string Raw(this SlideActionKind kind) => SlideActionKindJsonConverter.ToRaw(kind);

    public static string DisplayName(this SlideActionKind kind) => kind switch
    {
        SlideActionKind.ClearLayer => "Clear Layer",
        SlideActionKind.ClearAll => "Clear All",
        SlideActionKind.ClearAudio => "Clear Music",
        SlideActionKind.SwitchOutputPreset => "Switch Output Preset",
        SlideActionKind.FireMedia => "Fire Media",
        SlideActionKind.FireAlert => "Fire Alert",
        SlideActionKind.DismissAlert => "Dismiss Alert",
        SlideActionKind.TimerStart => "Start Timer",
        SlideActionKind.TimerPause => "Pause Timer",
        SlideActionKind.TimerReset => "Reset Timer",
        SlideActionKind.TimerConfigure => "Configure Timer",
        SlideActionKind.MidiOut => "MIDI Out",
        SlideActionKind.FireCombo => "Fire Action Combo",
        SlideActionKind.CaptureStart => "Start Capture",
        SlideActionKind.CaptureStop => "Stop Capture",
        SlideActionKind.FirePresentation => "Fire Presentation",
        SlideActionKind.SetConfidenceLayout => "Set Confidence Layout",
        SlideActionKind.FireOverlay => "Fire Overlay",
        SlideActionKind.DismissOverlay => "Take Down Overlay",
        SlideActionKind.FireLiveInput => "Fire Live Input",
        SlideActionKind.SetSignage or SlideActionKind.SetScreenSource or SlideActionKind.ClearSignage => "Signage (removed)",
        SlideActionKind.EnableAudioInput => "Audio Input On",
        SlideActionKind.DisableAudioInput => "Audio Input Off",
        SlideActionKind.FireAudio => "Music File",
        SlideActionKind.FireAudioPlaylist => "Music Playlist",
        _ => kind.ToString(),
    };

    /// <summary>Digital Signage was removed; these steps still load but do nothing.</summary>
    public static bool IsRetired(this SlideActionKind kind) =>
        kind is SlideActionKind.SetSignage or SlideActionKind.SetScreenSource or SlideActionKind.ClearSignage;

    /// <summary>What the labels need to name a step's targets.</summary>
    public sealed record LabelNames(
        Func<string, string?> Entry,
        IReadOnlyList<(string Id, string Name)>? Timers = null,
        IReadOnlyList<(string Id, string Name)>? ConfidenceScreens = null,
        Func<SlideAction, string?>? LiveInput = null,
        Func<string, string?>? AudioInput = null);

    /// <summary>
    /// The one-line label a step shows in the Actions menu and the slide's tooltip
    /// ("Fire Overlay: Lower Third", "Configure Timer: Sermon (Countdown 5:00)").
    /// Ids that aren't set read "not set"; ids no longer in the library read "missing".
    /// </summary>
    public static string Label(SlideAction action, LabelNames names)
    {
        string EntryName(string? id) => string.IsNullOrEmpty(id) ? "not set" : names.Entry(id) ?? "missing";
        string TimerName(string? id) =>
            string.IsNullOrEmpty(id) ? "not set" : (names.Timers ?? []).FirstOrDefault(t => t.Id == id).Name ?? "missing";
        string? AudioInputName()
        {
            if (string.IsNullOrEmpty(action.AudioInputId)) return null;
            return names.AudioInput?.Invoke(action.AudioInputId) ?? "missing input";
        }
        string ConfigureDetail()
        {
            var parts = new List<string>();
            switch (action.TimerMode)
            {
                case TimerMode.Countdown: parts.Add("Countdown"); break;
                case TimerMode.CountdownToTime: parts.Add("Until " + TimerController.WallLabel(action.TimerHour ?? 0, action.TimerMinute ?? 0)); break;
                case TimerMode.CountUp: parts.Add("Count Up"); break;
            }
            if (action.TimerMode != TimerMode.CountdownToTime && action.TimerDurationSeconds is > 0 and var seconds)
                parts.Add(MinutesSeconds((int)seconds));
            return parts.Count == 0 ? "" : $" ({string.Join(' ', parts)})";
        }
        string ConfidenceLabel()
        {
            var layout = string.IsNullOrEmpty(action.ConfidenceLayoutId) ? "Built-in Layout" : EntryName(action.ConfidenceLayoutId);
            if (string.IsNullOrEmpty(action.ConfidenceScreenId)) return $"Confidence Monitor: {layout}";
            var screen = (names.ConfidenceScreens ?? [])
                .FirstOrDefault(s => string.Equals(s.Id, action.ConfidenceScreenId, StringComparison.OrdinalIgnoreCase)).Name ?? "missing screen";
            return $"Confidence Monitor: {layout} on {screen}";
        }
        return action.Kind switch
        {
            SlideActionKind.SwitchOutputPreset => $"Switch Output Preset: {EntryName(action.PresetId)}",
            SlideActionKind.ClearLayer => $"Clear {LayerKinds.Parse(action.Layer)?.DisplayName() ?? "Layer"}",
            SlideActionKind.ClearAll => "Clear All",
            SlideActionKind.ClearAudio => "Clear Music",
            SlideActionKind.FireMedia => $"Fire Media: {EntryName(action.MediaId)}",
            SlideActionKind.FireAlert => $"Fire Alert: {EntryName(action.AlertId)}",
            SlideActionKind.DismissAlert => "Dismiss Alert",
            SlideActionKind.TimerStart => $"Start Timer: {TimerName(action.TimerId)}",
            SlideActionKind.TimerPause => $"Pause Timer: {TimerName(action.TimerId)}",
            SlideActionKind.TimerReset => $"Reset Timer: {TimerName(action.TimerId)}",
            SlideActionKind.TimerConfigure => $"Configure Timer: {TimerName(action.TimerId)}{ConfigureDetail()}",
            SlideActionKind.MidiOut => string.Create(CultureInfo.InvariantCulture, $"MIDI Out: ch {action.MidiChannel ?? 1}"),
            SlideActionKind.FireCombo => $"Run Combo: {EntryName(action.ComboId)}",
            SlideActionKind.CaptureStart => $"Start Capture: {EntryName(action.CapturePresetId)}",
            SlideActionKind.CaptureStop => action.CapturePresetId is null ? "Stop All Capture" : $"Stop Capture: {EntryName(action.CapturePresetId)}",
            SlideActionKind.FirePresentation => $"Fire Presentation: {EntryName(action.PresentationId)}",
            SlideActionKind.SetConfidenceLayout => ConfidenceLabel(),
            SlideActionKind.FireOverlay => $"Fire Overlay: {EntryName(action.OverlayId)}",
            SlideActionKind.DismissOverlay => string.IsNullOrEmpty(action.OverlayId) ? "Take Down Overlays" : $"Take Down Overlay: {EntryName(action.OverlayId)}",
            SlideActionKind.FireLiveInput => $"Fire Live Input: {LiveInputName(action, names) ?? "not set"}",
            SlideActionKind.EnableAudioInput => $"Audio Input On: {AudioInputName() ?? "not set"}",
            SlideActionKind.DisableAudioInput => $"Audio Input Off: {AudioInputName() ?? "not set"}",
            SlideActionKind.FireAudio => $"Music: {EntryName(action.AudioItemId)}",
            SlideActionKind.FireAudioPlaylist => $"Music Playlist: {EntryName(action.PlaylistId)}",
            _ => action.Kind.DisplayName(),
        };
    }

    private static string? LiveInputName(SlideAction action, LabelNames names)
    {
        if (!string.IsNullOrEmpty(action.LiveInputId) || !string.IsNullOrEmpty(action.InputSourceId))
            return names.LiveInput?.Invoke(action) ?? (string.IsNullOrEmpty(action.LiveInputId) ? action.InputSourceId : "missing input");
        return null;
    }

    /// <summary>"5:00" for 300 seconds.</summary>
    public static string MinutesSeconds(int seconds) => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");

    /// <summary>
    /// The icon a step wears (the Mac's GlyphKind raw names: "screens", "clear", "media", …).
    /// A Fire Combo step wears its combo's icon (<paramref name="comboIcon"/> looks it up).
    /// </summary>
    public static string IconName(SlideAction action, Func<string, string?>? comboIcon = null) => action.Kind switch
    {
        SlideActionKind.SwitchOutputPreset or SlideActionKind.FireLiveInput => "screens",
        SlideActionKind.ClearLayer or SlideActionKind.ClearAll or SlideActionKind.ClearAudio => "clear",
        SlideActionKind.SetSignage or SlideActionKind.SetScreenSource or SlideActionKind.ClearSignage => "clear",
        SlideActionKind.FireMedia => "media",
        SlideActionKind.FireAlert or SlideActionKind.DismissAlert => "alerts",
        SlideActionKind.TimerStart or SlideActionKind.TimerPause or SlideActionKind.TimerReset or SlideActionKind.TimerConfigure => "timers",
        SlideActionKind.MidiOut => "midi",
        SlideActionKind.FireCombo => (action.ComboId is { } id ? comboIcon?.Invoke(id) : null) is { } icon && GlyphNames.Contains(icon) ? icon : "combos",
        SlideActionKind.CaptureStart or SlideActionKind.CaptureStop => "broadcast",
        SlideActionKind.FirePresentation => "presentations",
        SlideActionKind.SetConfidenceLayout => "confidence",
        SlideActionKind.FireOverlay or SlideActionKind.DismissOverlay => "overlays",
        _ => "audio",
    };

    /// <summary>Every icon name the Mac's GlyphKind knows (a combo's stored icon must be one of these).</summary>
    public static readonly IReadOnlySet<string> GlyphNames = new HashSet<string>
    {
        "services", "presentations", "overlays", "media", "audio", "themes", "libraries", "folder", "viewOptions",
        "timers", "alerts", "broadcast", "combos", "screens", "confidence", "clear", "midi", "mixer", "sync",
        "notes", "fade", "crossfade", "light",
    };

    /// <summary>A playlist's kind; an old playlist without one is judged by its first entry.</summary>
    public static PlaylistKind EffectiveKind(this Playlist playlist) =>
        playlist.PlaylistKind ?? (playlist.Entries.FirstOrDefault()?.RefKind == PlaylistRefKind.Media ? PlaylistKind.Media : PlaylistKind.Audio);
}
