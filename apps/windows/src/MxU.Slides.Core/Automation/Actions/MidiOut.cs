using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Automation;

/// <summary>A MIDI device as the action router sees it: whether it is on, which way it goes, and its output port if present.</summary>
public sealed record MidiOutDevice(string Id, bool IsEnabled, MIDIDeviceItemDirection Direction, string? DestinationId)
{
    public bool WantsOutput => Direction != MIDIDeviceItemDirection.Input;

    /// <summary>From a library <see cref="MIDIDevice"/> and the output port it is bound to on this computer.</summary>
    public static MidiOutDevice From(MIDIDevice device, string? destinationId) =>
        new(device.Id, device.Enabled ?? true, device.Direction ?? MIDIDeviceItemDirection.Both, destinationId);
}

/// <summary>
/// Where a MIDI Out step sends: to the outputs chosen in Settings (no device on the
/// step), to the step's device, or nowhere (and why). It never falls back to sending
/// everywhere. Port of MIDIOutTarget in PresenterCore/MIDIControl.swift.
/// </summary>
public abstract record MidiOutTarget
{
    private MidiOutTarget() { }

    public sealed record SettingsSelection : MidiOutTarget;

    public sealed record Device(string DestinationId) : MidiOutTarget;

    public sealed record Skip(string Reason) : MidiOutTarget;

    public static MidiOutTarget Resolve(string? deviceId, IReadOnlyList<MidiOutDevice> items)
    {
        if (string.IsNullOrEmpty(deviceId)) return new SettingsSelection();
        if (items.FirstOrDefault(i => i.Id == deviceId) is not { } item) return new Skip("device missing");
        if (!item.IsEnabled) return new Skip("device off");
        if (!item.WantsOutput) return new Skip("device is input-only");
        if (item.DestinationId is not { } destination) return new Skip("port not present");
        return new Device(destination);
    }
}

/// <summary>A MIDI Out step's message, with the step's defaults filled in (note on, channel 1, number 0, value 127).</summary>
public sealed record MidiOutMessage(MIDIMessageKind Kind, int Channel, int Number, int Value)
{
    public static MidiOutMessage From(SlideAction action) =>
        new(action.MidiKind ?? MIDIMessageKind.NoteOn, action.MidiChannel ?? 1, action.MidiNumber ?? 0, action.MidiValue ?? 127);

    /// <summary>The three bytes on the wire: status (note on 0x90 or control change 0xB0, plus the channel), number, value.</summary>
    public byte[] Bytes()
    {
        var status = (byte)((Kind == MIDIMessageKind.ControlChange ? 0xB0 : 0x90) | ((Math.Clamp(Channel, 1, 16) - 1) & 0x0F));
        return [status, (byte)(Number & 0x7F), (byte)(Value & 0x7F)];
    }
}
