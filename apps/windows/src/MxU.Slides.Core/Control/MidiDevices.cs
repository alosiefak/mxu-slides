using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Control;

/// <summary>A MIDI port connected to this computer: its Windows device id and its name.</summary>
public sealed record MidiEndpoint(string Id, string Name);

/// <summary>Which ports a library MIDI device was tied to by hand on this computer (kept in the settings, not the library).</summary>
public sealed record MidiDeviceBinding(string? DestinationId = null, string? SourceId = null);

/// <summary>
/// A library <see cref="MIDIDevice"/> ("Lighting desk", "Keys") with the ports it uses on this
/// computer. Port of ResolvedMIDIDevice in PresenterCore/MIDIControl.swift.
/// </summary>
public sealed record ResolvedMidiDevice(MIDIDevice Device, string? DestinationId = null, string? SourceId = null)
{
    public string Id => Device.Id;

    public bool IsEnabled => Device.Enabled ?? true;

    public MIDIDeviceItemDirection Direction => Device.Direction ?? MIDIDeviceItemDirection.Both;

    public bool WantsOutput => Direction != MIDIDeviceItemDirection.Input;

    public bool WantsInput => Direction != MIDIDeviceItemDirection.Output;

    /// <summary>True unless the device listens on one channel (1–16) and the message is on another.</summary>
    public bool AcceptsChannel(byte status) => Device.Channel is not { } channel || channel == (status & 0x0F) + 1;

    /// <summary>True when the ports its direction needs are plugged in now.</summary>
    public bool IsConnected(IReadOnlySet<string> destinations, IReadOnlySet<string> sources) =>
        (!WantsOutput || DestinationId is { } destination && destinations.Contains(destination))
        && (!WantsInput || SourceId is { } source && sources.Contains(source));

    /// <summary>As the action router sees it, for MIDI Out steps.</summary>
    public MidiOutDevice ForActions() => MidiOutDevice.From(Device, DestinationId);

    public static string DirectionName(MIDIDeviceItemDirection direction) => direction switch
    {
        MIDIDeviceItemDirection.Output => "Output",
        MIDIDeviceItemDirection.Input => "Input",
        _ => "In + Out",
    };
}

/// <summary>
/// Ties library MIDI devices to this computer's ports: a binding made by hand wins (even when
/// its port is unplugged), otherwise a port whose name matches one the device remembers (from a
/// ProPresenter import), otherwise one named like the device. Port of MIDIDeviceResolver.
/// </summary>
public static class MidiDeviceResolver
{
    public static List<ResolvedMidiDevice> Resolve(
        IEnumerable<MIDIDevice> devices, IReadOnlyDictionary<string, MidiDeviceBinding> bindings,
        IReadOnlyList<MidiEndpoint> destinations, IReadOnlyList<MidiEndpoint> sources) =>
        [.. devices.Select(device =>
        {
            var binding = bindings.GetValueOrDefault(device.Id);
            return new ResolvedMidiDevice(device,
                binding?.DestinationId ?? AutoBind(device.DestinationNames, device.Name, destinations),
                binding?.SourceId ?? AutoBind(device.SourceNames, device.Name, sources));
        })];

    /// <summary>
    /// The ports plugged in now as devices to add: an output and an input with the same name are
    /// one device; inputs left over are input-only devices.
    /// </summary>
    public static List<(MidiEndpoint? Destination, MidiEndpoint? Source, string Name, MIDIDeviceItemDirection Direction)> Discovered(
        IReadOnlyList<MidiEndpoint> destinations, IReadOnlyList<MidiEndpoint> sources)
    {
        var unclaimed = sources.ToList();
        var found = new List<(MidiEndpoint?, MidiEndpoint?, string, MIDIDeviceItemDirection)>();
        foreach (var destination in destinations)
        {
            var match = unclaimed.FindIndex(s => s.Name == destination.Name);
            MidiEndpoint? source = null;
            if (match >= 0)
            {
                source = unclaimed[match];
                unclaimed.RemoveAt(match);
            }
            found.Add((destination, source, destination.Name, source is null ? MIDIDeviceItemDirection.Output : MIDIDeviceItemDirection.Both));
        }
        found.AddRange(unclaimed.Select(s => ((MidiEndpoint?)null, (MidiEndpoint?)s, s.Name, MIDIDeviceItemDirection.Input)));
        return found;
    }

    /// <summary>The ports to listen to: enabled devices that want input. Empty when none do.</summary>
    public static HashSet<string> InputSources(IEnumerable<ResolvedMidiDevice> devices) =>
        [.. devices.Where(d => d.IsEnabled && d.WantsInput && d.SourceId is not null).Select(d => d.SourceId!)];

    private static string? AutoBind(IReadOnlyList<string>? names, string fallback, IReadOnlyList<MidiEndpoint> endpoints)
    {
        foreach (var name in names is { Count: > 0 } ? names : [fallback])
            if (endpoints.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) is { } match) return match.Id;
        return null;
    }
}
