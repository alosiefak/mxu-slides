using MxU.Slides.Core.Control;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.LocalApi;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The MIDI device list, as on the Mac: named devices in the library ("Lighting desk",
/// "Keys"), each on or off, in, out or both, on one channel or all. On this PC each is tied to
/// the ports with matching names, or to ports picked by hand (kept in the settings). MIDI
/// control then listens only to the devices that are on; with no devices listed it listens to
/// every input, as before. ProPresenter's MIDI devices import into this list.
/// </summary>
public sealed partial class MainViewModel
{
    private List<ResolvedMidiDevice> midiDevices = [];
    private IReadOnlyList<MidiEndpoint> midiInPorts = [];
    private IReadOnlyList<MidiEndpoint> midiOutEndpoints = [];

    /// <summary>The library's MIDI devices with their ports on this PC.</summary>
    public IReadOnlyList<ResolvedMidiDevice> MidiDevices => midiDevices;

    /// <summary>MIDI inputs and outputs connected now (for adding devices).</summary>
    public (IReadOnlyList<MidiEndpoint> Inputs, IReadOnlyList<MidiEndpoint> Outputs) MidiPorts => (midiInPorts, midiOutEndpoints);

    /// <summary>Raised when the device list or its connections change (the Settings window listens).</summary>
    public event Action? MidiDevicesChanged;

    /// <summary>Looks for MIDI ports again and ties the library's devices to them.</summary>
    public async Task RefreshMidiDevicesAsync()
    {
        midiInPorts = [.. (await MidiInputService.ListAsync()).Select(p => new MidiEndpoint(p.Id, p.Name))];
        midiOutEndpoints = [.. (await MidiOutputService.ListAsync()).Select(p => new MidiEndpoint(p.Id, p.Name))];
        ResolveMidiDevices();
    }

    private void ResolveMidiDevices()
    {
        midiDevices = MidiDeviceResolver.Resolve(
            Library.All<MIDIDevice>().OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase),
            Settings.MidiDeviceBindings ?? [], midiOutEndpoints, midiInPorts);
        if (midi is not null) midi.Accepts = AcceptsMidiFrom;
        MidiDevicesChanged?.Invoke();
    }

    /// <summary>
    /// Whether a note-on from this port counts: from an enabled input device (on its channel),
    /// or from anywhere while no listed device takes input.
    /// </summary>
    private bool AcceptsMidiFrom(string portId, byte status)
    {
        var listening = midiDevices.Where(d => d.IsEnabled && d.WantsInput).ToList();
        if (listening.Count == 0) return !midiDevices.Any(d => d.WantsInput);
        return listening.Any(d => d.SourceId == portId && d.AcceptsChannel(status));
    }

    /// <summary>For the self-test: whether a note-on from this port would count.</summary>
    internal bool AcceptsMidiForTest(string portId, byte status) => AcceptsMidiFrom(portId, status);

    /// <summary>Adds a device for ports plugged in now (an output and input with one name become one device).</summary>
    public MIDIDevice AddMidiDevice(string name, MIDIDeviceItemDirection direction, string? destinationId, string? sourceId)
    {
        var device = new MIDIDevice
        {
            Id = LibraryStore.NewId(), Name = name, Direction = direction, Enabled = true,
            DestinationNames = destinationId is not null ? [name] : null,
            SourceNames = sourceId is not null ? [name] : null,
        };
        Library.Save(device);
        SetMidiDeviceBinding(device.Id, new MidiDeviceBinding(destinationId, sourceId));
        return device;
    }

    public void RemoveMidiDevice(string id)
    {
        Library.Delete<MIDIDevice>(id);
        if (Settings.MidiDeviceBindings?.Remove(id) == true) Settings.Save();
        ResolveMidiDevices();
    }

    /// <summary>Changes a device's settings and saves it.</summary>
    public void UpdateMidiDevice(string id, Action<MIDIDevice> change)
    {
        if (Library.Get<MIDIDevice>(id) is not { } device) return;
        change(device);
        Library.Save(device);
        ResolveMidiDevices();
    }

    private void SetMidiDeviceBinding(string id, MidiDeviceBinding binding)
    {
        Settings.MidiDeviceBindings ??= [];
        Settings.MidiDeviceBindings[id] = binding;
        Settings.Save();
        ResolveMidiDevices();
    }

    /// <summary>ProPresenter's MIDI devices (its "communication devices"); ones already listed are kept as they are.</summary>
    private int SaveImportedMidiDevices(IEnumerable<MIDIDevice> devices)
    {
        var added = 0;
        foreach (var device in devices.Where(d => Library.Get<MIDIDevice>(d.Id) is null))
        {
            Library.Save(device);
            added++;
        }
        if (added > 0) ResolveMidiDevices();
        return added;
    }

    // MARK: Local API

    IReadOnlyList<ApiMidiDeviceStatus> IApiBridge.MidiDevices()
    {
        var destinations = midiOutEndpoints.Select(e => e.Id).ToHashSet();
        var sources = midiInPorts.Select(e => e.Id).ToHashSet();
        return [.. midiDevices.Select(d => new ApiMidiDeviceStatus(
            d.Id, d.Device.Name, d.IsEnabled, MIDIDeviceItemDirectionJsonConverter.ToRaw(d.Direction), d.IsConnected(destinations, sources), d.Device.Channel))];
    }

    void IApiBridge.SetMidiDeviceEnabled(string id, bool enabled)
    {
        if (Library.Get<MIDIDevice>(id) is null) throw ApiError.NotFound($"No MIDI device '{id}'. GET /v1/midi/devices lists them.");
        UpdateMidiDevice(id, device => device.Enabled = enabled);
    }
}
