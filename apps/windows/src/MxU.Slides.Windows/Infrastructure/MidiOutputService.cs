using Windows.Devices.Enumeration;
using Windows.Devices.Midi;
using Windows.Storage.Streams;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Sends MIDI out of this PC (slide actions and action combos can cue a lighting desk, a
/// mixer or another app). Uses Windows' own MIDI API; ports open on first use and stay open.
/// </summary>
public sealed class MidiOutputService : IDisposable
{
    private readonly Dictionary<string, IMidiOutPort> open = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    /// <summary>The MIDI outputs connected now: (Windows device id, name).</summary>
    public static async Task<IReadOnlyList<(string Id, string Name)>> ListAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(MidiOutPort.GetDeviceSelector());
            return [.. devices.Select(d => (d.Id, d.Name))];
        }
        catch (Exception ex)
        {
            Log.Warn($"Listing MIDI outputs failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>Sends raw MIDI bytes (one message) to an output port by its Windows device id.</summary>
    public async Task SendAsync(string deviceId, byte[] message)
    {
        try
        {
            IMidiOutPort? port;
            lock (gate) open.TryGetValue(deviceId, out port);
            if (port is null)
            {
                port = await MidiOutPort.FromIdAsync(deviceId);
                if (port is null)
                {
                    Log.Warn($"MIDI output {deviceId} couldn't be opened (unplugged or in use)");
                    return;
                }
                lock (gate) open[deviceId] = port;
            }
            using var writer = new DataWriter();
            writer.WriteBytes(message);
            port.SendBuffer(writer.DetachBuffer());
        }
        catch (Exception ex)
        {
            Log.Warn($"Sending MIDI failed: {ex.Message}");
            lock (gate) open.Remove(deviceId);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var port in open.Values) port.Dispose();
            open.Clear();
        }
    }
}
