using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// What the automation editors (Views/ActionCombosWindow, SlideActionsWindow, SchedulerWindow)
/// need from the app: the lists their pickers offer, and saving the actions of a slide or a
/// media item. Combos and triggers are saved through <see cref="Automation"/>, which keeps
/// their boards in step.
/// </summary>
public sealed partial class MainViewModel
{
    private AutomationLibrary? automation;

    /// <summary>Creating, changing and deleting combos and triggers, with their boards (Core/Automation/AutomationLibrary.cs).</summary>
    public AutomationLibrary Automation => automation ??= new AutomationLibrary(Library);

    /// <summary>
    /// Everything an action's pickers can offer right now: the library's presets, media,
    /// songs, music, alerts, overlays, layouts and combos; the timers; and this PC's video
    /// inputs, confidence screens and MIDI outputs.
    /// </summary>
    public ActionChoices ActionChoices()
    {
        static List<EditorChoice> Named<T>(IEnumerable<T> docs, Func<T, string?>? detail = null) where T : ILibraryDocument =>
            [.. docs.Select(d => new EditorChoice(d.Id, d.Name, detail?.Invoke(d)))];

        List<EditorChoice> inputs = [];
        try { inputs = [.. Inputs.Entries.Select(e => new EditorChoice(e.Id, e.Name))]; }
        catch (Exception ex) { Log.Warn($"Couldn't list the video inputs for the action editor: {ex.Message}"); }

        var screens = Outputs?.Config is { } config
            ? config.Screens.Where(s => config.Role(s.Id) == ScreenRole.Confidence)
                .Select(s => new EditorChoice(s.Id.ToString("D").ToUpperInvariant(), s.Name)).ToList()
            : [];

        return new ActionChoices
        {
            OutputPresets = Named(Library.All<OutputPreset>()),
            Media = Named(Library.All<MediaItem>(), m => m.MediaKind == MediaKind.Video
                ? (m.Classification == MediaClassification.Background ? "Video · background" : "Video")
                : "Image"),
            Presentations = Named(Library.All<Presentation>(), p => $"{p.PresentationKind} · {p.Slides.Count} slide{(p.Slides.Count == 1 ? "" : "s")}"),
            AudioItems = Named(Library.All<AudioItem>()),
            AudioPlaylists = Named(Library.All<Playlist>().Where(p => p.EffectiveKind() == PlaylistKind.Audio)),
            Alerts = Named(Library.All<AlertPreset>()),
            Overlays = Named(Library.All<Overlay>()),
            LiveInputs = inputs,
            // Windows can't switch audio inputs from a step yet (the router logs and skips it), so none are offered.
            AudioInputs = [],
            ConfidenceLayouts = Named(Library.All<ConfidenceLayout>()),
            ConfidenceScreens = screens,
            Timers = [.. Timers.Timers.Select(t => new EditorChoice(t.Id, t.Name))],
            Combos = Named(Library.All<ActionCombo>()),
            CapturePresets = Named(Library.All<StreamRecordPreset>()),
            MidiDevices = [.. midiOutPorts.Select(p => new EditorChoice(p.Id, p.Name))],
            SlideCount = id => Library.Get<Presentation>(id)?.Slides.Count,
            ComboIcon = id => Library.Get<ActionCombo>(id)?.IconName,
        };
    }

    /// <summary>The names a step's one-line label needs ("Fire Overlay: Lower Third").</summary>
    public SlideActions.LabelNames ActionLabelNames()
    {
        var library = new LibraryStoreActions(Library, () => SelectedService);
        var choices = ActionChoices();
        return new SlideActions.LabelNames(
            library.Name,
            [.. choices.Timers.Select(t => (t.Id, t.Name))],
            [.. choices.ConfidenceScreens.Select(s => (s.Id, s.Name))],
            action => action.LiveInputId is { Length: > 0 } id ? choices.LiveInputs.FirstOrDefault(i => i.Id == id)?.Name : null);
    }

    /// <summary>The presentation holding a slide: the open one if it's there, else any in the library.</summary>
    public Presentation? PresentationContaining(string slideId)
    {
        if (CurrentPresentation is { } open && open.Slides.Any(s => s.Id == slideId)) return open;
        return Library.All<Presentation>().FirstOrDefault(p => p.Slides.Any(s => s.Id == slideId));
    }

    /// <summary>
    /// Saves a slide's actions (none is stored as absent, as on the Mac). The open
    /// presentation is edited in place, as the slide editor does, so the live show and the
    /// slide grid see the change at once. False when the slide can't be found.
    /// </summary>
    public bool SaveSlideActions(string presentationId, string slideId, IReadOnlyList<SlideAction> actions)
    {
        var presentation = CurrentPresentation?.Id == presentationId ? CurrentPresentation : Library.Get<Presentation>(presentationId);
        if (presentation?.Slides.FirstOrDefault(s => s.Id == slideId) is not { } slide) return false;
        slide.Actions = ActionEditing.Stored(actions);
        try
        {
            SavePresentation(presentation);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't save the slide's actions", ex);
            Problem = $"Couldn't save the slide's actions: {ex.Message}";
            return false;
        }
    }

    /// <summary>Saves a media item's actions (they run each time it's fired). False when it's gone.</summary>
    public bool SaveMediaActions(string mediaId, IReadOnlyList<SlideAction> actions)
    {
        if (Library.Get<MediaItem>(mediaId) is not { } item) return false;
        item.Actions = ActionEditing.Stored(actions);
        try
        {
            Library.Save(item);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't save the media item's actions", ex);
            Problem = $"Couldn't save the media item's actions: {ex.Message}";
            return false;
        }
    }
}
