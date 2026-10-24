using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Outputs;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Show automation's hands: what slide actions, action combos, schedule triggers and
/// timeline cues do to the live show. The <see cref="ActionRouter"/> (ported from the
/// Mac) decides what happens and when; this carries it out. Things this PC can't do yet
/// are logged and skipped, never thrown, so one missing piece doesn't stop the rest of a combo.
/// </summary>
public sealed partial class MainViewModel : IShowActions
{
    private ActionRouter? router;

    /// <summary>Runs slide actions and action combos (created on the UI thread, so delayed steps come back to it).</summary>
    public ActionRouter Actions => router ??= CreateRouter();

    private ActionRouter CreateRouter()
    {
        var created = new ActionRouter(this, new LibraryStoreActions(Library, () => SelectedService), ui: SynchronizationContext.Current);
        created.Noted += (crumb, detail) => Log.Info($"Actions: {crumb} {detail}");
        return created;
    }

    /// <summary>The action combos, for the bin's Combos tab (click one to run it).</summary>
    public System.Collections.ObjectModel.ObservableCollection<ActionCombo> Combos { get; } = [];

    public void RefreshCombos()
    {
        Combos.Clear();
        foreach (var combo in ActionComboOrder()) Combos.Add(combo);
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void RunCombo(ActionCombo? combo)
    {
        if (combo is null) return;
        RunActionCombo(combo.Id);
        Notice = $"Ran \"{combo.Name}\"";
    }

    /// <summary>Runs an action combo by id (the toolbar, MIDI and the Local API use this).</summary>
    public void RunActionCombo(string comboId) => Actions.Fire(comboId);

    /// <summary>Action combos in the order MIDI "Run Action Combo" numbers them (velocity 1 = the first): by name.</summary>
    private List<ActionCombo> ActionComboOrder() =>
        [.. Library.All<ActionCombo>().OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];

    private void Skipped(string what) => Log.Info($"Actions: skipped \"{what}\" (not available on this PC yet)");

    // MARK: Clearing

    void IShowActions.ClearLayer(LayerKind layer) => Clear(() => Show.Clear(layer, ShowState.HostClock()));

    void IShowActions.ClearAll(IReadOnlySet<LayerKind> protecting) => Clear(() =>
    {
        foreach (var layer in LayerKinds.All.Where(l => !protecting.Contains(l))) Show.Clear(layer);
    });

    void IShowActions.ClearAudio() => ClearAudioCommand.Execute(null);

    // MARK: Output presets

    string? IShowActions.ActiveOutputPresetId => Settings.ActiveOutputPresetId;

    void IShowActions.SwitchOutputPreset(string presetId)
    {
        if (Library.Get<OutputPreset>(presetId) is not { } preset || Outputs is null) return;
        // Looks the new preset drops play their Outs first (MainViewModel.Live.cs).
        ActivateOutputPreset(preset);
        Notice = $"Output preset: {preset.Name}";
    }

    // MARK: Media, alerts and overlays

    void IShowActions.FireMedia(MediaItem item) => FireMedia(item);

    void IShowActions.FireAlert(AlertPreset preset) => ShowAlertPreset(preset);

    /// <summary>The alert preset up now (a timeline's alert cue takes down only its own alert).</summary>
    private string? liveAlertPresetId;

    /// <summary>Puts an alert preset up: its message with timer tokens filled, on the screens it names.</summary>
    private void ShowAlertPreset(AlertPreset preset, IReadOnlyDictionary<string, string>? tokens = null, string? message = null, string? target = null)
    {
        var values = tokens?.ToDictionary() ?? [];
        foreach (var name in AlertTokens.TokenNames(preset.Message)
                     .Where(n => Timers.Timers.Any(t => string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase))))
            values.TryAdd(name, $"{{{name}}}");
        var text = message ?? AlertTokens.Compose(preset.Message, values);
        var audience = target is "audience" or "both";
        var stage = target is null or "confidence" or "both";
        if (target is null && preset.Target is { } presetTarget)
        {
            audience = presetTarget is AlertTarget.Audience or AlertTarget.Both;
            stage = presetTarget is AlertTarget.Confidence or AlertTarget.Both;
        }
        Show.FireAlert(text, audience, stage);
        liveAlertPresetId = preset.Id;
        RenderOutputs(DefaultTransition());
    }

    void IShowActions.DismissAlert() => ClearAlertsCommand.Execute(null);

    void IShowActions.FireOverlay(Overlay overlay) => SetOverlay(overlay, up: true);

    void IShowActions.DismissOverlay(string overlayId)
    {
        if (Show.LiveOverlays.FirstOrDefault(o => o.Id == overlayId) is { } overlay) SetOverlay(overlay, up: false);
    }

    void IShowActions.DismissAllOverlays() => ClearOverlaysCommand.Execute(null);

    // MARK: Live inputs

    void IShowActions.FireLiveInput(string liveInputId, LayerKind layer) => FireLiveInput(liveInputId, layer);

    /// <summary>An older-style action naming a device directly: use (or make) the inventory entry for it.</summary>
    void IShowActions.FireLiveInputSource(CaptureSourceKind kind, string sourceId, LayerKind layer)
    {
        var entry = Inputs.Entries.FirstOrDefault(e => e.Kind == kind && e.SourceId == sourceId) ?? Inputs.Create(sourceId, kind, sourceId);
        FireLiveInput(entry.Id, layer);
    }

    /// <summary>Puts a camera or NDI input on a media layer (cue id <c>input::…</c>, as on the Mac).</summary>
    private void FireLiveInput(string inputId, LayerKind layer)
    {
        CueMediaLayer? cueLayer = layer switch
        {
            LayerKind.LoopingVideos => CueMediaLayer.LoopingVideos,
            LayerKind.StillGraphics => CueMediaLayer.StillGraphics,
            LayerKind.Videos => CueMediaLayer.Videos,
            _ => null,
        };
        Inputs.StartIfNeeded(LiveInputCueId(inputId));
        Show.Fire(new CueMedia { MediaId = LiveInputCueId(inputId), Layer = cueLayer, Loops = true }, layer);
        RenderOutputs(DefaultTransition());
        MarkLiveTiles();
        MarkLiveInputs();
    }

    void IShowActions.SetAudioInputEnabled(string audioInputId, bool enabled) => Skipped($"audio input {audioInputId} {(enabled ? "on" : "off")}");

    // MARK: Presentations

    void IShowActions.FireSlide(Slide slide, Presentation presentation)
    {
        var arrangement = presentation.DefaultArrangementId;
        var index = SlideSceneBuilder.ArrangedSlides(presentation, arrangement).FindIndex(s => s.Id == slide.Id);
        if (index >= 0) Fire(presentation, arrangement, index, null);
    }

    // MARK: Music (filled in by the audio engine)

    void IShowActions.FireAudio(AudioItem item, bool? repeatOverride) => PlayAudio(item, repeatOverride);

    void IShowActions.FireAudioPlaylist(Playlist playlist, string? startAtEntryId, bool? repeatOverride) =>
        PlayAudioPlaylist(playlist, startAtEntryId, repeatOverride);

    // MARK: Timers

    void IShowActions.StartTimer(string timerId) => Timers.Start(timerId);

    void IShowActions.PauseTimer(string timerId) => Timers.Pause(timerId);

    void IShowActions.ResetTimer(string timerId) => Timers.Reset(timerId);

    void IShowActions.ConfigureTimer(string timerId, TimerMode? mode, double? durationSeconds, int? hour, int? minute) =>
        Timers.Configure(timerId, mode, durationSeconds, hour, minute);

    // MARK: MIDI out

    private readonly MidiOutputService midiOut = new();
    private IReadOnlyList<(string Id, string Name)> midiOutPorts = [];

    /// <summary>The MIDI outputs connected to this PC (listed at start-up and when Settings opens).</summary>
    public async Task RefreshMidiOutputsAsync() => midiOutPorts = await MidiOutputService.ListAsync();

    /// <summary>A MIDI output on this PC, as the router sees a library MIDI device: id = destination = the Windows port id.</summary>
    IReadOnlyList<MidiOutDevice> IShowActions.MidiOutDevices => [.. MidiDevices.Select(d => d.ForActions())];

    /// <summary>To one port, or (null) the output chosen in Settings; skipped when none is chosen.</summary>
    void IShowActions.SendMidi(MidiOutMessage message, string? destinationId)
    {
        var port = destinationId ?? Settings.MidiOutputId;
        if (port is null)
        {
            Skipped("send MIDI (choose a MIDI output in Screens & Settings › MIDI)");
            return;
        }
        _ = midiOut.SendAsync(port, message.Bytes());
    }

    // MARK: Capture (recording and streaming)

    void IShowActions.StartCapture(string presetId) => StartBroadcast(presetId);

    void IShowActions.StopCapture(string presetId) => StopBroadcast(presetId);

    void IShowActions.StopAllCapture() => StopBroadcast(null);

    // MARK: Confidence monitors

    bool IShowActions.HasConfidenceMonitors => Outputs?.Config.Screens.Any(s => Outputs.Config.Role(s.Id) == ScreenRole.Confidence) == true;

    void IShowActions.SetConfidenceLayout(string? layoutId, Guid? screenId)
    {
        var layout = layoutId is null ? StageLayouts.FirstOrDefault() : StageLayouts.FirstOrDefault(l => l.Id == layoutId);
        if (layout is not null) SelectedStageLayout = layout;
    }
}
