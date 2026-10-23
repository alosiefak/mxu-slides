using System.Text.Json;
using MxU.Slides.Core.Control;

namespace MxU.Slides.Core.Tests.Control;

// Ported from PresenterCoreTests/MIDIControlTests.swift (the command map and parser; device binding is app-side on Windows).
public class MidiControlTests
{
    [Fact]
    public void MapDefaultsWearProPresenterNumbers()
    {
        var map = new MidiCommandMap();
        Assert.Equal(0, map.NoteFor(MidiCommand.ClearAll));
        Assert.Equal(2, map.NoteFor(MidiCommand.ClearMedia));
        Assert.Equal(12, map.NoteFor(MidiCommand.NextSlide));
        Assert.Equal(19, map.NoteFor(MidiCommand.TriggerSlide));
        Assert.Equal(29, map.NoteFor(MidiCommand.TriggerActionCombo));
        Assert.Equal(28, map.NoteFor(MidiCommand.ClearAlerts));
        Assert.Equal(17, map.NoteFor(MidiCommand.SelectService));
        Assert.Equal(18, map.NoteFor(MidiCommand.SelectServiceItem));
        Assert.Equal(22, map.NoteFor(MidiCommand.SelectAudioPlaylist));
        Assert.Equal(23, map.NoteFor(MidiCommand.TriggerAudio));
        Assert.Equal(24, map.NoteFor(MidiCommand.ToggleOverlay));
        Assert.Equal(32, map.NoteFor(MidiCommand.SelectOverlayFolder));
        Assert.Equal(31, map.NoteFor(MidiCommand.SelectActionComboFolder));
        Assert.Equal(33, map.NoteFor(MidiCommand.FireAlert));
        Assert.Equal(35, map.NoteFor(MidiCommand.ClearInputs));
        var notes = MidiCommands.All.Select(c => c.DefaultNote()).ToList();
        Assert.Equal(notes.Count, notes.Distinct().Count());
        Assert.Equal(13, MidiCommands.All.Count(c => c.UsesVelocityIndex()));
    }

    [Fact]
    public void TimelineCommandsWearProPresenterNotes()
    {
        var map = new MidiCommandMap();
        Assert.Equal(MidiCommand.StartTimeline, map.CommandForNote(14));
        Assert.Equal(MidiCommand.StopTimeline, map.CommandForNote(15));
        Assert.Equal(MidiCommand.RewindTimeline, map.CommandForNote(16));
        foreach (var c in new[] { MidiCommand.StartTimeline, MidiCommand.StopTimeline, MidiCommand.RewindTimeline })
        {
            Assert.Equal(MidiCommandGroup.Presentation, c.Group());
            Assert.False(c.UsesVelocityIndex());
        }
        var moved = new MidiCommandMap();
        moved.SetNote(14, MidiCommand.ClearAll);
        Assert.Equal(MidiCommand.ClearAll, moved.CommandForNote(14));
    }

    [Fact]
    public void PlaybackAndProPresenterCueRecipesResolve()
    {
        var map = new MidiCommandMap();
        Assert.Equal("G-1", MidiCommandMap.NoteName(19));
        Assert.Equal((MidiCommand.TriggerSlide, 2), map.Resolve(0x90, 19, 2));
        Assert.Equal((MidiCommand.SelectService, 1), map.Resolve(0x90, 17, 1));
        Assert.Equal((MidiCommand.SelectServiceItem, 4), map.Resolve(0x90, 18, 4));
        Assert.Equal((MidiCommand.NextServiceItem, 127), map.Resolve(0x90, 10, 127));
        Assert.Null(map.Resolve(0x80, 19, 64));
    }

    [Fact]
    public void MapStoresOnlyOverridesAndResolvesByNote()
    {
        var map = new MidiCommandMap();
        map.SetNote(64, MidiCommand.ClearAll);
        Assert.Equal(new Dictionary<string, int> { ["clearAll"] = 64 }, map.Notes);
        Assert.Equal(MidiCommand.ClearAll, map.CommandForNote(64));
        Assert.Null(map.CommandForNote(0));
        map.SetNote(MidiCommand.ClearAll.DefaultNote(), MidiCommand.ClearAll);
        Assert.Empty(map.Notes);

        map.SetNote(100, MidiCommand.NextSlide);
        map.Channel = 5;
        var decoded = JsonSerializer.Deserialize<MidiCommandMap>(JsonSerializer.Serialize(map))!;
        Assert.Equal(map.Channel, decoded.Channel);
        Assert.Equal(map.Notes, decoded.Notes);
    }

    [Fact]
    public void ResolveFiresOnNoteOnOnly()
    {
        var map = new MidiCommandMap();
        Assert.Equal((MidiCommand.NextSlide, 100), map.Resolve(0x90, 12, 100));
        Assert.Null(map.Resolve(0x90, 12, 0));
        Assert.Null(map.Resolve(0x80, 12, 100));
        Assert.Null(map.Resolve(0xB0, 12, 100));
        Assert.Null(map.Resolve(0x90, 99, 100));
    }

    [Fact]
    public void ResolveHonorsTheChannelFilter()
    {
        var map = new MidiCommandMap { Channel = 3 };
        Assert.Equal(MidiCommand.NextSlide, map.Resolve(0x92, 12, 1)?.Command);
        Assert.Null(map.Resolve(0x90, 12, 1));
        map.Channel = null;
        Assert.Equal(MidiCommand.NextSlide, map.Resolve(0x9F, 12, 1)?.Command);
    }

    [Fact]
    public void NoteNamesMatchProPresenterOctaves()
    {
        Assert.Equal("C-2", MidiCommandMap.NoteName(0));
        Assert.Equal("C-1", MidiCommandMap.NoteName(12));
        Assert.Equal("G-1", MidiCommandMap.NoteName(19));
        Assert.Equal("F#0", MidiCommandMap.NoteName(30));
        Assert.Equal("G8", MidiCommandMap.NoteName(127));
    }

    [Fact]
    public void ChannelMessageParserWalksPacketsLikeAWire()
    {
        Assert.Equal([new(0x90, 12, 100), new(0x80, 12, 0)], MidiParse.ChannelMessages([0x90, 12, 100, 0x80, 12, 0]));
        var running = MidiParse.ChannelMessages([0x90, 12, 100, 13, 90]);
        Assert.Equal(2, running.Count);
        Assert.Equal(new MidiParse.ChannelMessage(0x90, 13, 90), running[1]);
        Assert.Equal([new(0x90, 12, 100)], MidiParse.ChannelMessages([0xF8, 0x90, 0xF8, 12, 100, 0xF8]));
        Assert.Equal([new(0xC0, 5, 0), new(0x90, 12, 100)], MidiParse.ChannelMessages([0xF0, 1, 2, 3, 0xF7, 0xC0, 5, 0x90, 12, 100]));
        Assert.Empty(MidiParse.ChannelMessages([0x90, 12]));
    }

    [Fact]
    public void RawNamesMatchTheMac()
    {
        Assert.Equal("clearAll", MidiCommand.ClearAll.Raw());
        Assert.Equal("selectServiceItem", MidiCommand.SelectServiceItem.Raw());
        Assert.Equal(MidiCommand.RewindTimeline, MidiCommands.Parse("rewindTimeline"));
    }
}
