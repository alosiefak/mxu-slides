using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Control;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Control;

// Port of the device tests in PresenterCoreTests/MIDIControlTests.swift (endpoint ids are strings on Windows).
public class MidiDeviceTests
{
    private static readonly Dictionary<string, MidiDeviceBinding> NoBindings = [];

    [Fact]
    public void ExplicitBindingWinsEvenWhenEndpointAbsent()
    {
        var resolved = MidiDeviceResolver.Resolve([new MIDIDevice { Id = "d1", Name = "Lyrics MIDI" }],
            new Dictionary<string, MidiDeviceBinding> { ["d1"] = new("99", "98") }, [new MidiEndpoint("5", "Lyrics MIDI")], []);
        Assert.Equal("99", resolved[0].DestinationId);
        Assert.Equal("98", resolved[0].SourceId);
    }

    [Fact]
    public void AutoBindMatchesEndpointNamesThenItemName()
    {
        var imported = new MIDIDevice { Id = "d1", Name = "Lyrics MIDI", SourceNames = ["Lyrics Strip"], DestinationNames = ["Bus 1"] };
        var handmade = new MIDIDevice { Id = "d2", Name = "Bus 1" };
        var resolved = MidiDeviceResolver.Resolve([imported, handmade], NoBindings,
            [new MidiEndpoint("1", "bus 1"), new MidiEndpoint("2", "Other")], [new MidiEndpoint("3", "Lyrics Strip")]);
        Assert.Equal("1", resolved[0].DestinationId);
        Assert.Equal("3", resolved[0].SourceId);
        Assert.Equal("1", resolved[1].DestinationId);
        Assert.Null(resolved[1].SourceId);
    }

    [Fact]
    public void InputSourcesAreEnabledDevicesThatListen()
    {
        Assert.Empty(MidiDeviceResolver.InputSources([new ResolvedMidiDevice(new MIDIDevice { Id = "d1", Name = "X" })]));
        var input = new MIDIDevice { Id = "d2", Name = "Pad", Direction = MIDIDeviceItemDirection.Input };
        var bound = MidiDeviceResolver.Resolve([input], new Dictionary<string, MidiDeviceBinding> { ["d2"] = new("7", "8") }, [], []);
        Assert.Equal(["8"], MidiDeviceResolver.InputSources(bound));
        Assert.False(bound[0].WantsOutput);
        var disabled = new MIDIDevice { Id = "d3", Name = "Off", Enabled = false };
        var off = MidiDeviceResolver.Resolve([disabled], new Dictionary<string, MidiDeviceBinding> { ["d3"] = new("9", "9") }, [], []);
        Assert.Empty(MidiDeviceResolver.InputSources(off));
    }

    [Fact]
    public void DiscoveredMergesEndpointPairsByName()
    {
        var merged = MidiDeviceResolver.Discovered(
            [new MidiEndpoint("1", "Lightkey Output"), new MidiEndpoint("2", "Console")],
            [new MidiEndpoint("3", "Console"), new MidiEndpoint("4", "Lightkey Input")]);
        Assert.Equal(["Lightkey Output", "Console", "Lightkey Input"], merged.Select(m => m.Name));
        Assert.Null(merged[0].Source);
        Assert.Equal("2", merged[1].Destination?.Id);
        Assert.Equal("3", merged[1].Source?.Id);
        Assert.Equal(MIDIDeviceItemDirection.Both, merged[1].Direction);
        Assert.Equal("4", merged[2].Source?.Id);
        Assert.Equal(MIDIDeviceItemDirection.Input, merged[2].Direction);
    }

    [Fact]
    public void ConnectedStateChecksTheEndpointsTheDirectionNeeds()
    {
        var both = new ResolvedMidiDevice(new MIDIDevice { Id = "k", Name = "Keys", Direction = MIDIDeviceItemDirection.Both }, "1", "11");
        Assert.True(both.IsConnected(new HashSet<string> { "1" }, new HashSet<string> { "11" }));
        Assert.False(both.IsConnected(new HashSet<string> { "1" }, new HashSet<string>()));
        var output = both with { Device = new MIDIDevice { Id = "k", Name = "Keys", Direction = MIDIDeviceItemDirection.Output } };
        Assert.True(output.IsConnected(new HashSet<string> { "1" }, new HashSet<string>()));
    }

    [Fact]
    public void MidiOutTargetResolvesThroughItemsAndNeverFallsBackToBroadcast()
    {
        var output = new MIDIDevice { Id = "lk", Name = "Lightkey", Direction = MIDIDeviceItemDirection.Output };
        var items = MidiDeviceResolver.Resolve([output], new Dictionary<string, MidiDeviceBinding> { ["lk"] = new("7") },
            [new MidiEndpoint("7", "Lightkey Output")], []).Select(i => i.ForActions()).ToList();
        Assert.Equal(new MidiOutTarget.Device("7"), MidiOutTarget.Resolve("lk", items));
        Assert.Equal(new MidiOutTarget.SettingsSelection(), MidiOutTarget.Resolve(null, items));
        Assert.Equal(new MidiOutTarget.Skip("device missing"), MidiOutTarget.Resolve("other", items));
        var unbound = new[] { new ResolvedMidiDevice(output).ForActions() };
        Assert.Equal(new MidiOutTarget.Skip("port not present"), MidiOutTarget.Resolve("lk", unbound));
    }

    [Fact]
    public void ChannelFilterMatchesEntrySemantics()
    {
        var resolved = new ResolvedMidiDevice(new MIDIDevice { Id = "d1", Name = "X", Channel = 2 });
        Assert.True(resolved.AcceptsChannel(0x91));
        Assert.False(resolved.AcceptsChannel(0x90));
        Assert.True(new ResolvedMidiDevice(new MIDIDevice { Id = "d2", Name = "Y" }).AcceptsChannel(0x9F));
    }
}
