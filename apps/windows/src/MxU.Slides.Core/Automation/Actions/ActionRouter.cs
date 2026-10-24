using System.Globalization;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// Runs action steps: a slide's actions when it fires, a combo when its button (or a MIDI
/// note, or the Local API) runs it, a schedule trigger's actions, a timeline's actions cue.
/// It looks each step's target up in the library, skips steps whose target is missing (and
/// says so in <see cref="Noted"/>), and holds back delayed steps until their time. A combo
/// can fire another combo, four deep at most and never itself again, so a loop can't hang
/// the show. Port of Sources/ActionRouter.swift (the theme "override looks" an output preset
/// can carry are not ported: Windows has no looks yet). Drive it from the UI thread.
/// </summary>
public sealed class ActionRouter
{
    /// <summary>How deep combos may fire combos (and presentations fire presentations).</summary>
    public const int MaxDepth = 4;

    private readonly IShowActions show;
    private readonly IActionLibrary library;
    private readonly TimeProvider clock;
    private readonly SynchronizationContext? ui;

    private int presentationFireDepth;
    private bool startupRan;

    // Delayed steps: each batch has a token; re-scheduling a step cancels the batch it was in.
    private readonly Dictionary<int, ShowTimer> pendingDelayed = [];
    private readonly Dictionary<string, int> pendingTokensByActionId = [];
    private int nextDelayToken;

    /// <param name="clock">Time for delayed steps (tests pass a fake).</param>
    /// <param name="ui">Where delayed steps run; pass the UI thread's <see cref="SynchronizationContext.Current"/>.</param>
    public ActionRouter(IShowActions show, IActionLibrary library, TimeProvider? clock = null, SynchronizationContext? ui = null)
    {
        this.show = show;
        this.library = library;
        this.clock = clock ?? TimeProvider.System;
        this.ui = ui;
    }

    /// <summary>The diagnostics trail: (crumb, detail), e.g. ("actions.skipped", "fireMedia missing mediaId").</summary>
    public event Action<string, string>? Noted;

    private void Note(string crumb, string detail) => Noted?.Invoke(crumb, detail);

    /// <summary>Delayed batches waiting to run.</summary>
    public int PendingDelayedBatches => pendingDelayed.Count;

    // MARK: Output presets

    /// <summary>
    /// Before a slide fires: switches to the output preset it asks for (its last Switch Output
    /// Preset step, looking into combos it fires), else the one its service item asks for.
    /// Done first so the slide lands on the right outputs; its preset steps are then skipped.
    /// </summary>
    public void ApplyPresetSwitches(Slide slide, string? contextId) => ApplyPreset(ResolvedPresetId(slide.Actions, contextId));

    public void ApplyPresetSwitches(MediaItem item, string? contextId) => ApplyPreset(ResolvedPresetId(item.Actions, contextId));

    private void ApplyPreset(string? presetId)
    {
        if (presetId is null || show.ActiveOutputPresetId == presetId) return;
        show.SwitchOutputPreset(presetId);
    }

    /// <summary>The preset a list of steps switches to (ignoring the service item).</summary>
    public string? DeclaredPresetId(IReadOnlyList<SlideAction>? actions) => ResolvedPresetId(actions, null);

    /// <summary>The preset showing once this slide fires (its own, its item's, else the current one).</summary>
    public string? PredictedPresetId(Slide slide, string? contextId) => ResolvedPresetId(slide.Actions, contextId) ?? show.ActiveOutputPresetId;

    private string? ResolvedPresetId(IReadOnlyList<SlideAction>? actions, string? contextId)
    {
        var result = contextId is null ? null : library.ServiceItemOutputPresetId(contextId);
        void Scan(IReadOnlyList<SlideAction> steps, int depth, IReadOnlySet<string> visited)
        {
            foreach (var action in steps.Where(a => !SlideActions.WaitsBeforeFiring(a)))
            {
                switch (action.Kind)
                {
                    case SlideActionKind.SwitchOutputPreset:
                        if (action.PresetId is { Length: > 0 } id && library.OutputPreset(id) is not null) result = id;
                        break;
                    case SlideActionKind.FireCombo:
                        if (action.ComboId is not { } comboId || depth >= MaxDepth || visited.Contains(comboId)
                            || library.ActionCombo(comboId) is not { } combo)
                            continue;
                        Scan(combo.Actions, depth + 1, new HashSet<string>(visited) { comboId });
                        break;
                }
            }
        }
        Scan(actions ?? [], 0, new HashSet<string>());
        return result;
    }

    // MARK: Firing

    /// <summary>
    /// After a slide fires: runs its actions, minus clears of the layers it just filled
    /// (and preset switches, already applied).
    /// </summary>
    public void SlideDidFire(Slide slide, string? contextId)
    {
        if (slide.Actions is not { Count: > 0 } actions) return;
        var protectedLayers = SlideActions.ProtectedLayers(slide, MediaLayer);
        var effective = SlideActions.FireActions(slide, protectedLayers);
        Note("actions.fire", $"{slide.Name} {effective.Count} step(s)");
        if (effective.Count < actions.Count) Note("actions.clear.skippedOwnLayer", slide.Name);
        if (effective.Count == 0) return;
        Execute(effective, 0, new HashSet<string>(), skippingPresetSwitches: true, protectedLayers);
    }

    private LayerKind? MediaLayer(string mediaId) => library.Media(mediaId) is { } item ? SlideActions.FiredLayer(item) : null;

    /// <summary>After a media item fires onto <paramref name="layer"/>: runs its actions, minus clears of that layer.</summary>
    public void MediaDidFire(MediaItem item, LayerKind layer)
    {
        if (item.Actions is not { Count: > 0 } actions) return;
        var effective = SlideActions.FireActions(item, layer);
        Note("actions.fire", $"{item.Name} {effective.Count} step(s)");
        if (effective.Count < actions.Count) Note("actions.clear.skippedOwnLayer", item.Name);
        if (effective.Count == 0) return;
        Execute(effective, 0, new HashSet<string>(), skippingPresetSwitches: true, new HashSet<LayerKind> { layer });
    }

    /// <summary>Runs a combo (its button, a MIDI note, the Local API).</summary>
    public void Fire(string comboId)
    {
        if (library.ActionCombo(comboId) is not { } combo)
        {
            Note("actions.combo.missing", comboId);
            return;
        }
        library.MarkUsed(comboId);
        Execute(combo.Actions, 1, new HashSet<string> { comboId });
    }

    /// <summary>Runs steps (a schedule trigger's, a timeline cue's).</summary>
    public void Execute(IReadOnlyList<SlideAction> actions) => Execute(actions, 0, new HashSet<string>());

    /// <summary>
    /// Runs the combos marked "Trigger on Startup", by name, once per launch. Call it when the
    /// library has loaded and the outputs are up (the Mac waits two seconds after launch).
    /// </summary>
    public void RunStartupCombos()
    {
        if (startupRan) return;
        startupRan = true;
        var startup = library.ActionCombos().Where(c => c.RunOnStartup == true)
            .OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        foreach (var combo in startup)
        {
            Note("actions.startup", combo.Name);
            Execute(combo.Actions, 1, new HashSet<string> { combo.Id });
        }
    }

    private void Execute(
        IReadOnlyList<SlideAction> actions, int depth, IReadOnlySet<string> visited,
        bool skippingPresetSwitches = false, IReadOnlySet<LayerKind>? protectedLayers = null)
    {
        var schedule = SlideActions.DelaySchedule(actions);
        foreach (var action in schedule.Immediate)
            Perform(action, depth, visited, skippingPresetSwitches, protectedLayers ?? new HashSet<LayerKind>());
        foreach (var batch in schedule.Delayed)
            ScheduleDelayed(batch.Actions, batch.Delay, depth, visited);
    }

    private void ScheduleDelayed(IReadOnlyList<SlideAction> batch, double delay, int depth, IReadOnlySet<string> visited)
    {
        foreach (var action in batch)
        {
            if (pendingTokensByActionId.TryGetValue(action.Id, out var old) && pendingDelayed.Remove(old, out var task))
                task.Dispose();
        }
        nextDelayToken++;
        var token = nextDelayToken;
        foreach (var action in batch) pendingTokensByActionId[action.Id] = token;

        var ripe = batch.Select(action =>
        {
            var step = MxuJson.Clone(action);
            step.DelaySeconds = null;
            return step;
        }).ToList();
        Note("actions.delayed.scheduled", string.Create(CultureInfo.InvariantCulture, $"{batch.Count} step(s) in {delay}s"));
        pendingDelayed[token] = ShowTimer.After(clock, ui, TimeSpan.FromSeconds(delay), () =>
        {
            if (!pendingDelayed.ContainsKey(token)) return;
            ForgetDelayed(token);
            Note("actions.delayed.fired", string.Join(",", ripe.Select(a => a.Kind.Raw())));
            Execute(ripe, depth, visited);
        });
    }

    private void ForgetDelayed(int token)
    {
        pendingDelayed.Remove(token);
        foreach (var id in pendingTokensByActionId.Where(p => p.Value == token).Select(p => p.Key).ToList())
            pendingTokensByActionId.Remove(id);
    }

    /// <summary>Drops every step still waiting (the Clear All button and panic clear call this).</summary>
    public void CancelPendingDelayedActions()
    {
        if (pendingDelayed.Count == 0) return;
        Note("actions.delayed.cancelled", $"{pendingDelayed.Count} batch(es)");
        foreach (var task in pendingDelayed.Values) task.Dispose();
        pendingDelayed.Clear();
        pendingTokensByActionId.Clear();
    }

    private string Label(SlideAction action)
    {
        var parts = new List<string> { action.Kind.Raw() };
        if (action.Layer is { } layer) parts.Add(layer);
        var id = action.ComboId ?? action.PresetId ?? action.ConfidenceLayoutId ?? action.OverlayId ?? action.AlertId
            ?? action.MediaId ?? action.PresentationId ?? action.AudioItemId ?? action.PlaylistId ?? action.TimerId;
        if (!string.IsNullOrEmpty(id)) parts.Add(library.Name(id) ?? id);
        if (action.Kind == SlideActionKind.MidiOut)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"ch{action.MidiChannel ?? 1} #{action.MidiNumber ?? 0}"));
        return string.Join(' ', parts);
    }

    private void Perform(SlideAction action, int depth, IReadOnlySet<string> visited, bool skippingPresetSwitches, IReadOnlySet<LayerKind> protectedLayers)
    {
        Note("actions.step", Label(action));
        switch (action.Kind)
        {
            case SlideActionKind.ClearLayer:
            {
                if (LayerKinds.Parse(action.Layer) is not { } layer)
                {
                    Miss(action, "layer");
                    return;
                }
                if (protectedLayers.Contains(layer))
                {
                    Note("actions.clear.skippedOwnLayer", layer.Raw());
                    return;
                }
                show.ClearLayer(layer);
                break;
            }
            case SlideActionKind.ClearAll:
                if (protectedLayers.Count > 0)
                    Note("actions.clear.allScoped", string.Join(",", protectedLayers.Select(l => l.Raw()).Order(StringComparer.Ordinal)));
                show.ClearAll(protectedLayers);
                break;
            case SlideActionKind.ClearAudio:
                show.ClearAudio();
                break;
            case SlideActionKind.SwitchOutputPreset:
            {
                if (skippingPresetSwitches) return;
                if (action.PresetId is not { Length: > 0 } presetId || library.OutputPreset(presetId) is null)
                {
                    Miss(action, "presetId");
                    return;
                }
                if (show.ActiveOutputPresetId != presetId) show.SwitchOutputPreset(presetId);
                break;
            }
            case SlideActionKind.FireMedia:
                if (action.MediaId is not { } mediaId || library.Media(mediaId) is not { } item)
                {
                    Miss(action, "mediaId");
                    return;
                }
                show.FireMedia(item);
                break;
            case SlideActionKind.FireAlert:
                if (action.AlertId is not { } alertId || library.AlertPreset(alertId) is not { } preset)
                {
                    Miss(action, "alertId");
                    return;
                }
                show.FireAlert(preset);
                break;
            case SlideActionKind.DismissAlert:
                show.DismissAlert();
                break;
            case SlideActionKind.TimerStart:
            case SlideActionKind.TimerPause:
            case SlideActionKind.TimerReset:
            case SlideActionKind.TimerConfigure:
                if (action.TimerId is not { } timerId)
                {
                    Miss(action, "timerId");
                    return;
                }
                switch (action.Kind)
                {
                    case SlideActionKind.TimerStart: show.StartTimer(timerId); break;
                    case SlideActionKind.TimerPause: show.PauseTimer(timerId); break;
                    case SlideActionKind.TimerReset: show.ResetTimer(timerId); break;
                    default:
                        show.ConfigureTimer(timerId, action.TimerMode, action.TimerDurationSeconds, action.TimerHour, action.TimerMinute);
                        break;
                }
                break;
            case SlideActionKind.MidiOut:
                switch (MidiOutTarget.Resolve(action.MidiDeviceId, show.MidiOutDevices))
                {
                    case MidiOutTarget.SettingsSelection:
                        show.SendMidi(MidiOutMessage.From(action), null);
                        break;
                    case MidiOutTarget.Device device:
                        show.SendMidi(MidiOutMessage.From(action), device.DestinationId);
                        break;
                    case MidiOutTarget.Skip skip:
                        Note("actions.skipped", $"midiOut {skip.Reason}");
                        break;
                }
                break;
            case SlideActionKind.FireCombo:
            {
                if (action.ComboId is not { Length: > 0 } comboId)
                {
                    Miss(action, "comboId");
                    return;
                }
                if (depth >= MaxDepth || visited.Contains(comboId))
                {
                    Note("actions.combo.cycle", string.Create(CultureInfo.InvariantCulture, $"{comboId} depth={depth}"));
                    return;
                }
                if (library.ActionCombo(comboId) is not { } combo)
                {
                    Miss(action, "comboId");
                    return;
                }
                Execute(combo.Actions, depth + 1, new HashSet<string>(visited) { comboId }, skippingPresetSwitches, protectedLayers);
                break;
            }
            case SlideActionKind.CaptureStart:
                if (action.CapturePresetId is not { Length: > 0 } capture || library.StreamPreset(capture) is null)
                {
                    Miss(action, "capturePresetId");
                    return;
                }
                show.StartCapture(capture);
                break;
            case SlideActionKind.CaptureStop:
                if (action.CapturePresetId is { Length: > 0 } stopping) show.StopCapture(stopping);
                else show.StopAllCapture();
                break;
            case SlideActionKind.FirePresentation:
                FirePresentation(action);
                break;
            case SlideActionKind.SetConfidenceLayout:
                if (!show.HasConfidenceMonitors)
                {
                    Miss(action, "confidenceMonitor");
                    return;
                }
                show.SetConfidenceLayout(action.ConfidenceLayoutId, Guid.TryParseExact(action.ConfidenceScreenId, "D", out var screen) ? screen : null);
                break;
            case SlideActionKind.FireOverlay:
                if (action.OverlayId is not { } overlayId || library.Overlay(overlayId) is not { } overlay)
                {
                    Miss(action, "overlayId");
                    return;
                }
                show.FireOverlay(overlay);
                break;
            case SlideActionKind.DismissOverlay:
                if (action.OverlayId is { Length: > 0 } dismissing) show.DismissOverlay(dismissing);
                else show.DismissAllOverlays();
                break;
            case SlideActionKind.FireLiveInput:
            {
                var layer = LayerKinds.Parse(action.Layer) ?? LayerKind.VideoInput;
                if (action.LiveInputId is { Length: > 0 } itemId) show.FireLiveInput(itemId, layer);
                else if (action.InputSourceId is { Length: > 0 } sourceId) show.FireLiveInputSource(action.InputSourceKind ?? CaptureSourceKind.Camera, sourceId, layer);
                else Miss(action, "liveInputId");
                break;
            }
            case SlideActionKind.EnableAudioInput:
            case SlideActionKind.DisableAudioInput:
                if (action.AudioInputId is not { Length: > 0 } inputId)
                {
                    Miss(action, "audioInputId");
                    return;
                }
                show.SetAudioInputEnabled(inputId, action.Kind == SlideActionKind.EnableAudioInput);
                break;
            case SlideActionKind.SetSignage:
            case SlideActionKind.SetScreenSource:
            case SlideActionKind.ClearSignage:
                Note("actions.retired", action.Kind.Raw());
                break;
            case SlideActionKind.FireAudio:
                if (action.AudioItemId is not { Length: > 0 } audioId || library.Audio(audioId) is not { } audio)
                {
                    Miss(action, "audioItemId");
                    return;
                }
                show.FireAudio(audio, action.AudioRepeat);
                break;
            case SlideActionKind.FireAudioPlaylist:
                if (action.PlaylistId is not { Length: > 0 } playlistId || library.Playlist(playlistId) is not { } playlist
                    || playlist.EffectiveKind() != PlaylistKind.Audio)
                {
                    Miss(action, "playlistId");
                    return;
                }
                show.FireAudioPlaylist(playlist, action.AudioEntryId, action.AudioRepeat);
                break;
        }
    }

    private void FirePresentation(SlideAction action)
    {
        if (action.PresentationId is not { } presentationId)
        {
            Miss(action, "presentationId");
            return;
        }
        if (library.Presentation(presentationId) is not { } presentation)
        {
            Miss(action, library.PresentationExists(presentationId) ? "presentation still decoding" : "presentationId");
            return;
        }
        var slides = SlideSceneBuilder.ArrangedSlides(presentation);
        var index = action.SlideIndex ?? 0;
        if (index < 0 || index >= slides.Count)
        {
            Miss(action, "slideIndex");
            return;
        }
        if (presentationFireDepth >= MaxDepth)
        {
            Note("actions.presentation.cycle", string.Create(CultureInfo.InvariantCulture, $"{presentationId} depth={presentationFireDepth}"));
            return;
        }
        presentationFireDepth++;
        try
        {
            show.FireSlide(slides[index], presentation);
        }
        finally
        {
            presentationFireDepth--;
        }
    }

    private void Miss(SlideAction action, string field) => Note("actions.skipped", $"{action.Kind.Raw()} missing {field}");
}
