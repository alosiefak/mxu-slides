namespace MxU.Slides.Core.Control;

// MIDI control: the commands a note-on can trigger, their default notes (the
// same numbers ProPresenter uses) and raw-byte parsing. Port of
// PresenterCore/MIDIControl.swift; device discovery is the app's job.

public enum MidiCommandGroup { Clears, Video, Presentation, SelectByIndex }

public enum MidiCommand
{
    ClearAll, ClearSlides, ClearMedia, ClearOverlays, ClearAudio, ClearAlerts, ClearInputs,
    VideoGoToBeginning, VideoPlayPause, VideoPlay, VideoPause,
    NextServiceItem, PreviousServiceItem, NextSlide, PreviousSlide,
    SelectService, SelectServiceItem, TriggerSlide, SelectAudioPlaylist, TriggerAudio,
    ToggleOverlay, SelectOverlayFolder, SelectActionComboFolder,
    StartTimer, StopTimer, ResetTimer, TriggerActionCombo, FireAlert,
    StartTimeline, StopTimeline, RewindTimeline,
}

public static class MidiCommands
{
    public static readonly MidiCommand[] All = Enum.GetValues<MidiCommand>();

    /// <summary>The raw name stored in settings ("clearAll"), as on the Mac.</summary>
    public static string Raw(this MidiCommand c)
    {
        var name = c.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static MidiCommand? Parse(string raw) => All.Cast<MidiCommand?>().FirstOrDefault(c => c!.Value.Raw() == raw);

    public static int DefaultNote(this MidiCommand c) => c switch
    {
        MidiCommand.ClearAll => 0,
        MidiCommand.ClearSlides => 1,
        MidiCommand.ClearMedia => 2,
        MidiCommand.ClearOverlays => 3,
        MidiCommand.ClearAudio => 4,
        MidiCommand.ClearAlerts => 28,
        MidiCommand.ClearInputs => 35,
        MidiCommand.VideoGoToBeginning => 6,
        MidiCommand.VideoPlayPause => 7,
        MidiCommand.VideoPlay => 8,
        MidiCommand.VideoPause => 9,
        MidiCommand.NextServiceItem => 10,
        MidiCommand.PreviousServiceItem => 11,
        MidiCommand.NextSlide => 12,
        MidiCommand.PreviousSlide => 13,
        MidiCommand.SelectService => 17,
        MidiCommand.SelectServiceItem => 18,
        MidiCommand.TriggerSlide => 19,
        MidiCommand.SelectAudioPlaylist => 22,
        MidiCommand.TriggerAudio => 23,
        MidiCommand.ToggleOverlay => 24,
        MidiCommand.SelectOverlayFolder => 32,
        MidiCommand.SelectActionComboFolder => 31,
        MidiCommand.StartTimer => 25,
        MidiCommand.StopTimer => 26,
        MidiCommand.ResetTimer => 27,
        MidiCommand.TriggerActionCombo => 29,
        MidiCommand.FireAlert => 33,
        MidiCommand.StartTimeline => 14,
        MidiCommand.StopTimeline => 15,
        _ => 16,
    };

    public static MidiCommandGroup Group(this MidiCommand c) => c switch
    {
        MidiCommand.ClearAll or MidiCommand.ClearSlides or MidiCommand.ClearMedia or MidiCommand.ClearOverlays
            or MidiCommand.ClearAudio or MidiCommand.ClearAlerts or MidiCommand.ClearInputs => MidiCommandGroup.Clears,
        MidiCommand.VideoGoToBeginning or MidiCommand.VideoPlayPause or MidiCommand.VideoPlay or MidiCommand.VideoPause => MidiCommandGroup.Video,
        MidiCommand.NextServiceItem or MidiCommand.PreviousServiceItem or MidiCommand.NextSlide or MidiCommand.PreviousSlide
            or MidiCommand.StartTimeline or MidiCommand.StopTimeline or MidiCommand.RewindTimeline => MidiCommandGroup.Presentation,
        _ => MidiCommandGroup.SelectByIndex,
    };

    /// <summary>"Select by index" commands use the velocity as a 1-based index (velocity 3 = the third item).</summary>
    public static bool UsesVelocityIndex(this MidiCommand c) => c.Group() == MidiCommandGroup.SelectByIndex;

    public static string DisplayName(this MidiCommand c) => c switch
    {
        MidiCommand.ClearAll => "Clear All",
        MidiCommand.ClearSlides => "Clear Slides",
        MidiCommand.ClearMedia => "Clear Media",
        MidiCommand.ClearOverlays => "Clear Overlays",
        MidiCommand.ClearAudio => "Clear Music",
        MidiCommand.ClearAlerts => "Clear Alerts",
        MidiCommand.ClearInputs => "Clear Inputs",
        MidiCommand.VideoGoToBeginning => "Go to Beginning",
        MidiCommand.VideoPlayPause => "Play/Pause",
        MidiCommand.VideoPlay => "Play",
        MidiCommand.VideoPause => "Pause",
        MidiCommand.NextServiceItem => "Next Service Item",
        MidiCommand.PreviousServiceItem => "Previous Service Item",
        MidiCommand.NextSlide => "Next Slide",
        MidiCommand.PreviousSlide => "Previous Slide",
        MidiCommand.SelectService => "Select Service",
        MidiCommand.SelectServiceItem => "Select Service Item",
        MidiCommand.TriggerSlide => "Trigger Slide",
        MidiCommand.SelectAudioPlaylist => "Select Music Playlist",
        MidiCommand.TriggerAudio => "Trigger Music",
        MidiCommand.ToggleOverlay => "Toggle Overlay",
        MidiCommand.SelectOverlayFolder => "Select Overlay Folder",
        MidiCommand.SelectActionComboFolder => "Select Combo Folder",
        MidiCommand.StartTimer => "Start Timer",
        MidiCommand.StopTimer => "Stop Timer",
        MidiCommand.ResetTimer => "Reset Timer",
        MidiCommand.TriggerActionCombo => "Run Action Combo",
        MidiCommand.FireAlert => "Fire Alert",
        MidiCommand.StartTimeline => "Start Timeline",
        MidiCommand.StopTimeline => "Stop Timeline",
        _ => "Rewind Timeline",
    };
}

/// <summary>Which note triggers which command. Stores only the notes changed from the defaults.</summary>
public sealed class MidiCommandMap
{
    /// <summary>1–16 to listen on one channel only; null listens on all (omni).</summary>
    public int? Channel { get; set; }

    public Dictionary<string, int> Notes { get; set; } = [];

    public int NoteFor(MidiCommand command) => Notes.TryGetValue(command.Raw(), out var note) ? note : command.DefaultNote();

    public void SetNote(int note, MidiCommand command)
    {
        var clamped = Math.Clamp(note, 0, 127);
        if (clamped == command.DefaultNote()) Notes.Remove(command.Raw());
        else Notes[command.Raw()] = clamped;
    }

    public MidiCommand? CommandForNote(int note) => MidiCommands.All.Cast<MidiCommand?>().FirstOrDefault(c => NoteFor(c!.Value) == note);

    /// <summary>The command a channel message triggers: note-on with non-zero velocity only, on the chosen channel.</summary>
    public (MidiCommand Command, int Velocity)? Resolve(byte status, byte data1, byte data2)
    {
        if ((status & 0xF0) != 0x90 || data2 == 0) return null;
        if (Channel is { } channel && channel != (status & 0x0F) + 1) return null;
        return CommandForNote(data1) is { } command ? (command, data2) : null;
    }

    public static string NoteName(int note)
    {
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        var clamped = Math.Clamp(note, 0, 127);
        return $"{names[clamped % 12]}{clamped / 12 - 2}";
    }
}

public static class MidiParse
{
    public readonly record struct ChannelMessage(byte Status, byte Data1, byte Data2);

    /// <summary>Channel messages in a byte stream: honours running status, skips realtime bytes, SysEx and system common messages.</summary>
    public static List<ChannelMessage> ChannelMessages(IReadOnlyList<byte> bytes)
    {
        var result = new List<ChannelMessage>();
        var index = 0;
        byte? running = null;
        while (index < bytes.Count)
        {
            var b = bytes[index];
            if (b >= 0xF8)
            {
                index++;
                continue;
            }
            byte status;
            if ((b & 0x80) != 0)
            {
                index++;
                if (b >= 0xF0)
                {
                    running = null;
                    switch (b)
                    {
                        case 0xF0:
                            while (index < bytes.Count && bytes[index] != 0xF7) index++;
                            index++;
                            break;
                        case 0xF1 or 0xF3:
                            index++;
                            break;
                        case 0xF2:
                            index += 2;
                            break;
                    }
                    continue;
                }
                status = b;
                running = b;
            }
            else if (running is { } current)
            {
                status = current;
            }
            else
            {
                index++;
                continue;
            }
            var dataCount = status is >= 0xC0 and <= 0xDF ? 1 : 2;
            var data = new List<byte>(2);
            while (data.Count < dataCount && index < bytes.Count)
            {
                var next = bytes[index];
                if (next >= 0xF8)
                {
                    index++;
                    continue;
                }
                if ((next & 0x80) != 0) break;
                data.Add(next);
                index++;
            }
            if (data.Count != dataCount) continue;
            result.Add(new ChannelMessage(status, data[0], dataCount == 2 ? data[1] : (byte)0));
        }
        return result;
    }
}
