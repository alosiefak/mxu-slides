using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Core.Control;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>MIDI note-on control, with the same note numbers as the Mac app and ProPresenter.</summary>
public sealed partial class MainViewModel
{
    private MidiInputService? midi;

    /// <summary>Timecode reads MTC from a MIDI input, so the listener runs while it does, even with MIDI control off.</summary>
    private bool timecodeWantsMidi;

    private MidiInputService MidiIn
    {
        get
        {
            if (midi is not null) return midi;
            midi = new MidiInputService();
            midi.NoteOn += OnMidiNoteOn;
            midi.DevicesChanged += UpdateMidiStatus;
            return midi;
        }
    }

    [ObservableProperty]
    public partial string MidiStatus { get; set; } = "Off";

    /// <summary>Starts or stops listening to MIDI to match Settings.</summary>
    public void ApplyMidiSettings()
    {
        if (Settings.MidiInputEnabled || timecodeWantsMidi) MidiIn.Start();
        else midi?.Stop();
        UpdateMidiStatus();
    }

    private void UpdateMidiStatus()
    {
        MidiStatus = !Settings.MidiInputEnabled ? "Off"
            : midi?.DeviceNames is { Count: > 0 } names ? string.Join(", ", names)
            : "On — no MIDI device connected";
    }

    private void OnMidiNoteOn(byte status, byte note, byte velocity)
    {
        if (!Settings.MidiInputEnabled) return;
        if (Settings.MidiMap.Resolve(status, note, velocity) is not { } hit) return;
        Log.Info($"MIDI {MidiCommandMap.NoteName(note)} v{velocity} → {hit.Command.DisplayName()}");
        try
        {
            HandleMidi(hit.Command, hit.Velocity);
        }
        catch (Exception ex)
        {
            Log.Error($"MIDI command {hit.Command} failed", ex);
        }
    }

    /// <summary>Runs a command. "Select by index" commands use the velocity as a 1-based position.</summary>
    public void HandleMidi(MidiCommand command, int velocity)
    {
        var index = velocity - 1;
        switch (command)
        {
            case MidiCommand.ClearAll: ClearAllCommand.Execute(null); break;
            case MidiCommand.ClearSlides: ClearSlideCommand.Execute(null); break;
            case MidiCommand.ClearMedia: ClearMediaCommand.Execute(null); break;
            case MidiCommand.ClearOverlays: ClearOverlaysCommand.Execute(null); break;
            case MidiCommand.ClearAudio: ClearAudioCommand.Execute(null); break;
            case MidiCommand.ClearAlerts: ClearAlertsCommand.Execute(null); break;
            case MidiCommand.ClearInputs: Clear(() => Show.Clear(ShowFunction.Inputs)); break;
            case MidiCommand.NextSlide: NextCommand.Execute(null); break;
            case MidiCommand.PreviousSlide: PreviousCommand.Execute(null); break;
            case MidiCommand.NextServiceItem: StepServiceItem(+1); break;
            case MidiCommand.PreviousServiceItem: StepServiceItem(-1); break;
            case MidiCommand.VideoGoToBeginning: WithLiveVideo(Media.Restart); break;
            case MidiCommand.VideoPlay: WithLiveVideo(Media.Play); break;
            case MidiCommand.VideoPause: WithLiveVideo(Media.Pause); break;
            case MidiCommand.VideoPlayPause:
                WithLiveVideo(key =>
                {
                    if (Media.Transport(key) is { Playing: true }) Media.Pause(key);
                    else Media.Play(key);
                });
                break;
            case MidiCommand.SelectService when index >= 0 && index < Services.Count:
                SelectedService = Services[index];
                break;
            case MidiCommand.SelectServiceItem when FireableRows() is var rows && index >= 0 && index < rows.Count:
                SelectedServiceItem = rows[index];
                break;
            case MidiCommand.TriggerSlide when index >= 0 && index < Slides.Count:
                GoLiveCommand.Execute(Slides[index]);
                break;
            case MidiCommand.ToggleOverlay when index >= 0 && index < Overlays.Count:
                ToggleOverlayCommand.Execute(Overlays[index]);
                break;
            case MidiCommand.SelectOverlayFolder when index >= 0 && index < OverlayPacks.Count:
                SelectedOverlayPack = OverlayPacks[index];
                break;
            case MidiCommand.StartTimer when index >= 0 && index < TimerRows.Count:
                Timers.Start(TimerRows[index].Id);
                break;
            case MidiCommand.StopTimer when index >= 0 && index < TimerRows.Count:
                Timers.Pause(TimerRows[index].Id);
                break;
            case MidiCommand.ResetTimer when index >= 0 && index < TimerRows.Count:
                Timers.Reset(TimerRows[index].Id);
                break;
            case MidiCommand.TriggerActionCombo when ActionComboOrder() is var combos && index >= 0 && index < combos.Count:
                RunActionCombo(combos[index].Id);
                break;
            case MidiCommand.FireAlert when Library.All<AlertPreset>().OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList() is var alerts
                                            && index >= 0 && index < alerts.Count:
                ShowAlertPreset(alerts[index]);
                break;
            default:
                Log.Info($"MIDI {command.DisplayName()} ({velocity}) has nothing to do here yet");
                break;
        }
    }

    private List<ServiceItemViewModel> FireableRows() => ServiceItems.Where(r => !r.IsHeader && !r.IsMissing).ToList();

    /// <summary>Takes the next (or previous) service item live from its start.</summary>
    public void StepServiceItem(int direction)
    {
        var rows = FireableRows();
        if (rows.Count == 0) return;
        var current = rows.FindIndex(r => r.Item.Id == Live?.ItemId);
        var target = current < 0 ? (direction > 0 ? 0 : rows.Count - 1) : current + direction;
        if (target < 0 || target >= rows.Count) return;
        FireServiceItemCommand.Execute(rows[target]);
    }

    private void WithLiveVideo(Action<string> action)
    {
        foreach (var layer in new[] { LayerKind.Videos, LayerKind.LoopingVideos })
        {
            if (!Show.LiveMedia.TryGetValue(layer, out var cue)) continue;
            action($"live:live-media-{layer.Raw()}:{cue.MediaId}");
            return;
        }
    }
}
