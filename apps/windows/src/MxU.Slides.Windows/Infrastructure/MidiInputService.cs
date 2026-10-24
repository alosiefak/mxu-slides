using System.Windows.Threading;
using Windows.Devices.Enumeration;
using Windows.Devices.Midi;
using Windows.Storage.Streams;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Listens to every MIDI input (keyboards, Stream Deck MIDI, lighting desks,
/// loopMIDI from Companion) through Windows' own MIDI API, following devices
/// as they are plugged in and out. Note-on messages are raised on the UI thread.
/// </summary>
public sealed class MidiInputService : IDisposable
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<string, (MidiInPort Port, string Name)> ports = [];
    private DeviceWatcher? watcher;
    private bool running;

    /// <summary>Raw (status, note, velocity) of each note-on.</summary>
    public event Action<byte, byte, byte>? NoteOn;

    public event Action? DevicesChanged;

    /// <summary>
    /// Every message's bytes, raised on the MIDI thread the moment it arrives (timecode
    /// needs the exact time): the device id, the bytes and the <see cref="ShowState.HostClock"/> time.
    /// </summary>
    public event Action<string, byte[], double>? Message;

    /// <summary>The MIDI inputs connected now: (Windows device id, name).</summary>
    public static async Task<IReadOnlyList<(string Id, string Name)>> ListAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(MidiInPort.GetDeviceSelector());
            return [.. devices.Select(d => (d.Id, d.Name))];
        }
        catch (Exception ex)
        {
            Log.Warn($"Listing MIDI inputs failed: {ex.Message}");
            return [];
        }
    }

    public IReadOnlyList<string> DeviceNames
    {
        get { lock (ports) return ports.Values.Select(p => p.Name).OrderBy(n => n).ToList(); }
    }

    public void Start()
    {
        if (running) return;
        running = true;
        try
        {
            watcher = DeviceInformation.CreateWatcher(MidiInPort.GetDeviceSelector());
            watcher.Added += async (_, info) => await Open(info);
            watcher.Removed += (_, update) => Close(update.Id);
            watcher.Start();
            Log.Info("Listening for MIDI input");
        }
        catch (Exception ex)
        {
            Log.Warn($"MIDI input unavailable: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (!running) return;
        running = false;
        try { watcher?.Stop(); }
        catch { }
        watcher = null;
        lock (ports)
        {
            foreach (var (port, _) in ports.Values) port.Dispose();
            ports.Clear();
        }
        dispatcher.BeginInvoke(() => DevicesChanged?.Invoke());
    }

    private async Task Open(DeviceInformation info)
    {
        try
        {
            var port = await MidiInPort.FromIdAsync(info.Id);
            if (port is null) return;
            port.MessageReceived += (_, args) =>
            {
                if (Message is { } raw) raw(info.Id, Bytes(args.Message.RawData), ShowState.HostClock());
                if (args.Message is MidiNoteOnMessage on)
                    dispatcher.BeginInvoke(() => NoteOn?.Invoke((byte)(0x90 | on.Channel), on.Note, on.Velocity));
            };
            lock (ports) ports[info.Id] = (port, info.Name);
            Log.Info($"MIDI input connected: {info.Name}");
            await dispatcher.InvokeAsync(() => DevicesChanged?.Invoke());
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open MIDI input {info.Name}: {ex.Message}");
        }
    }

    private static byte[] Bytes(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private void Close(string id)
    {
        lock (ports)
        {
            if (!ports.Remove(id, out var entry)) return;
            entry.Port.Dispose();
            Log.Info($"MIDI input disconnected: {entry.Name}");
        }
        dispatcher.BeginInvoke(() => DevicesChanged?.Invoke());
    }

    public void Dispose() => Stop();
}
