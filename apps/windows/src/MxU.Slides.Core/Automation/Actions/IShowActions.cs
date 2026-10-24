using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// Everything a slide action, action combo, schedule trigger or timeline cue can do to
/// the show. The <see cref="ActionRouter"/> decides what to do; the app does it. The WPF
/// app implements this over its live show (outputs, media, timers, MIDI, capture).
/// On the Mac these are ServiceControls, the output presets controller, the confidence
/// monitor and MIDIOutService. Calls arrive on the thread that drives the router (the UI thread).
/// </summary>
public interface IShowActions
{
    // MARK: Clearing

    /// <summary>Clears one layer.</summary>
    void ClearLayer(LayerKind layer);

    /// <summary>
    /// Clear All from an action: clears every layer except <paramref name="protecting"/> (the
    /// layers the firing slide just filled). Unlike the Clear All button it must not cancel
    /// the router's pending delayed steps.
    /// </summary>
    void ClearAll(IReadOnlySet<LayerKind> protecting);

    /// <summary>Stops the music (the Audio function).</summary>
    void ClearAudio();

    // MARK: Output presets

    /// <summary>The output preset in use, or null.</summary>
    string? ActiveOutputPresetId { get; }

    /// <summary>Switches to an output preset (the router has checked it exists and isn't already active).</summary>
    void SwitchOutputPreset(string presetId);

    // MARK: Media, alerts and overlays

    /// <summary>Fires a media item onto its classification's layer.</summary>
    void FireMedia(MediaItem item);

    void FireAlert(AlertPreset preset);

    void DismissAlert();

    void FireOverlay(Overlay overlay);

    void DismissOverlay(string overlayId);

    void DismissAllOverlays();

    // MARK: Live inputs

    /// <summary>Fires a live input (a video input library item) onto a layer.</summary>
    void FireLiveInput(string liveInputId, LayerKind layer);

    /// <summary>Fires a capture source named the older way (camera, NDI or DeckLink id) onto a layer.</summary>
    void FireLiveInputSource(CaptureSourceKind kind, string sourceId, LayerKind layer);

    /// <summary>Turns an audio input's channel on or off in the mixer.</summary>
    void SetAudioInputEnabled(string audioInputId, bool enabled);

    // MARK: Presentations

    /// <summary>Fires a slide of a presentation, as clicking it would (its own actions then reach <see cref="ActionRouter.SlideDidFire"/>).</summary>
    void FireSlide(Slide slide, Presentation presentation);

    // MARK: Music

    /// <summary>Plays a music file; <paramref name="repeatOverride"/> true repeats it.</summary>
    void FireAudio(AudioItem item, bool? repeatOverride);

    /// <summary>Plays a music playlist from an entry (null: its first); <paramref name="repeatOverride"/> null keeps its own setting.</summary>
    void FireAudioPlaylist(Playlist playlist, string? startAtEntryId, bool? repeatOverride);

    // MARK: Timers

    void StartTimer(string timerId);

    void PauseTimer(string timerId);

    void ResetTimer(string timerId);

    /// <summary>Port of TimersController.configure; the WPF app can pass these straight to <c>TimerController.Configure</c>.</summary>
    void ConfigureTimer(string timerId, TimerMode? mode, double? durationSeconds, int? hour, int? minute);

    // MARK: MIDI

    /// <summary>The library's MIDI devices with their output ports on this computer.</summary>
    IReadOnlyList<MidiOutDevice> MidiOutDevices { get; }

    /// <summary>Sends a message to one output port, or (<paramref name="destinationId"/> null) to the outputs chosen in Settings.</summary>
    void SendMidi(MidiOutMessage message, string? destinationId);

    // MARK: Capture

    void StartCapture(string presetId);

    void StopCapture(string presetId);

    void StopAllCapture();

    // MARK: Confidence monitors

    /// <summary>False when this computer has no confidence monitor controller (the step is skipped).</summary>
    bool HasConfidenceMonitors { get; }

    /// <summary>Applies a confidence layout (null: the built-in one) to one screen, or (null) every confidence screen.</summary>
    void SetConfidenceLayout(string? layoutId, Guid? screenId);
}

/// <summary>
/// The library documents the router looks up. <see cref="LibraryStoreActions"/> implements
/// it over the Windows <see cref="LibraryStore"/>; tests use a dictionary.
/// </summary>
public interface IActionLibrary
{
    ActionCombo? ActionCombo(string id);

    IReadOnlyList<ActionCombo> ActionCombos();

    MediaItem? Media(string id);

    AlertPreset? AlertPreset(string id);

    Presentation? Presentation(string id);

    /// <summary>The presentation exists even if it can't be read yet (the Mac decodes lazily; "still decoding").</summary>
    bool PresentationExists(string id) => Presentation(id) is not null;

    Overlay? Overlay(string id);

    AudioItem? Audio(string id);

    Playlist? Playlist(string id);

    StreamRecordPreset? StreamPreset(string id);

    OutputPreset? OutputPreset(string id);

    /// <summary>Any library item's name, for the diagnostics trail; null when there is none.</summary>
    string? Name(string id);

    /// <summary>The output preset the current service gives this service item (its "context"), if any.</summary>
    string? ServiceItemOutputPresetId(string contextId);

    /// <summary>Notes that a combo was run (for "recently used" lists).</summary>
    void MarkUsed(string id) { }
}

/// <summary>The router's library over the Windows <see cref="LibraryStore"/>.</summary>
public sealed class LibraryStoreActions(LibraryStore store, Func<Service?> currentService) : IActionLibrary
{
    public ActionCombo? ActionCombo(string id) => store.Get<ActionCombo>(id);

    public IReadOnlyList<ActionCombo> ActionCombos() => store.All<ActionCombo>();

    public MediaItem? Media(string id) => store.Get<MediaItem>(id);

    public AlertPreset? AlertPreset(string id) => store.Get<AlertPreset>(id);

    public Presentation? Presentation(string id) => store.Get<Presentation>(id);

    public Overlay? Overlay(string id) => store.Get<Overlay>(id);

    public AudioItem? Audio(string id) => store.Get<AudioItem>(id);

    public Playlist? Playlist(string id) => store.Get<Playlist>(id);

    public StreamRecordPreset? StreamPreset(string id) => store.Get<StreamRecordPreset>(id);

    public OutputPreset? OutputPreset(string id) => store.Get<OutputPreset>(id);

    public string? Name(string id)
    {
        Func<string, ILibraryDocument?>[] kinds =
        [
            store.Get<Presentation>, store.Get<MediaItem>, store.Get<Overlay>, store.Get<AlertPreset>, store.Get<AudioItem>,
            store.Get<Playlist>, store.Get<ActionCombo>, store.Get<OutputPreset>, store.Get<StreamRecordPreset>,
            store.Get<ConfidenceLayout>, store.Get<ScheduleTrigger>, store.Get<Service>, store.Get<Theme>,
        ];
        return kinds.Select(get => get(id)).FirstOrDefault(doc => doc is not null)?.Name;
    }

    public string? ServiceItemOutputPresetId(string contextId) =>
        currentService()?.Items.FirstOrDefault(i => i.Id == contextId)?.OutputPresetId is { Length: > 0 } preset ? preset : null;
}
