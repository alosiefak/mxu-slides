using System.Text.Json.Serialization;

namespace MxU.Slides.Audio.Routing;

/// <summary>
/// One named place sound can go: a device on this PC and which of its channels, plus a
/// playout delay. "Main Output" = the Windows default; "Foldback" = channels 3–4 of the USB
/// interface, 20 ms late. A mix (the music, the video sound) is sent to an output by its id.
/// Port of the Mac's AudioOutputInventory.Entry, with the same JSON field names.
/// </summary>
public sealed record AudioOutputEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString().ToUpperInvariant();

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>The Windows endpoint id; null = the system default device; "none" = no device at all.</summary>
    [JsonPropertyName("deviceUID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceUid { get; init; }

    /// <summary>The first channel of the stereo pair (0 = channels 1–2).</summary>
    [JsonPropertyName("channelOffset")]
    public int ChannelOffset { get; init; }

    /// <summary>One channel (0 = channel 1) carrying the sound folded to mono; null = a stereo pair.</summary>
    [JsonPropertyName("monoChannel"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MonoChannel { get; init; }

    /// <summary>Holds this output's sound back (1–2000 ms) to line up with a late screen; null = none.</summary>
    [JsonPropertyName("delayMs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DelayMs { get; init; }

    /// <summary>"No device": the sound reaches streams and recordings but no speaker.</summary>
    [JsonIgnore]
    public bool IsDeviceless => DeviceUid == AudioOutputInventory.NoDeviceUid;

    /// <summary>
    /// A shorter name for tight spaces: "Out 3" for an output called "Focusrite 3", "Out 3–4"
    /// for "Focusrite 3-4" (the names <see cref="AudioOutputInventory"/> gives the outputs it makes).
    /// </summary>
    [JsonIgnore]
    public string ShortName
    {
        get
        {
            if (MonoChannel is { } mono && Name.EndsWith($" {mono + 1}", StringComparison.Ordinal)) return $"Out {mono + 1}";
            if (MonoChannel is null && Name.EndsWith($" {ChannelOffset + 1}-{ChannelOffset + 2}", StringComparison.Ordinal))
                return $"Out {ChannelOffset + 1}–{ChannelOffset + 2}";
            return Name;
        }
    }
}

/// <summary>
/// The audio outputs on this PC. They name this PC's devices, so they live in its own
/// settings, not the library. There is always at least one ("Main Output", on the system
/// default device, until someone changes it). Port of the Mac's AudioOutputInventory,
/// which keeps them in its preferences; the app here saves <see cref="Entries"/> into
/// settings.json whenever <see cref="Changed"/> fires.
/// <para>Threading: thread-safe; <see cref="Changed"/> is raised on the calling thread.</para>
/// </summary>
public sealed class AudioOutputInventory
{
    /// <summary>The device id that means "no device" (the Mac's noDeviceUID).</summary>
    public const string NoDeviceUid = "none";

    public const int MaxDelayMilliseconds = 2000;

    private readonly Lock gate = new();
    private readonly Func<IReadOnlyList<AudioOutputDevice>> devices;
    private List<AudioOutputEntry> entries;

    /// <param name="stored">What settings.json held; nothing (a first run) starts with one "Main Output".</param>
    /// <param name="devices">This PC's output devices, for naming outputs made for a device (default: ask Windows).</param>
    public AudioOutputInventory(IEnumerable<AudioOutputEntry>? stored = null, Func<IReadOnlyList<AudioOutputDevice>>? devices = null)
    {
        this.devices = devices ?? AudioDeviceList.OutputDevices;
        entries = stored?.Where(e => e.Id.Length > 0).ToList() ?? [];
        if (entries.Count == 0) entries = [new AudioOutputEntry { Name = "Main Output" }];
    }

    /// <summary>An output was added, changed or removed: save the entries and route again.</summary>
    public event Action? Changed;

    public IReadOnlyList<AudioOutputEntry> Entries
    {
        get { lock (gate) return [.. entries]; }
    }

    public AudioOutputEntry? Entry(string? id)
    {
        if (id is null) return null;
        lock (gate) return entries.FirstOrDefault(e => e.Id == id);
    }

    public bool IsDeviceless(string? id) => Entry(id)?.IsDeviceless == true;

    /// <summary>The output on exactly this device and pair, if there is one.</summary>
    public AudioOutputEntry? Find(string? deviceUid, int channelOffset)
    {
        lock (gate) return entries.FirstOrDefault(e => e.DeviceUid == deviceUid && e.ChannelOffset == channelOffset);
    }

    /// <summary>The output for a device and pair, made (named after the device) when there isn't one.</summary>
    public AudioOutputEntry Ensure(string? deviceUid, int channelOffset)
    {
        if (Find(deviceUid, channelOffset) is { } existing) return existing;
        var deviceName = deviceUid is null ? null : devices().FirstOrDefault(d => d.Uid == deviceUid)?.Name;
        var name = deviceName ?? (deviceUid is null ? "Main Output" : "Output");
        if (channelOffset > 0) name = $"{name} {channelOffset + 1}-{channelOffset + 2}";
        return Add(new AudioOutputEntry { Name = name, DeviceUid = deviceUid, ChannelOffset = channelOffset });
    }

    /// <summary>A new output with no device yet (the default), named "Output N" unless given a name.</summary>
    public AudioOutputEntry Create(string? name = null)
    {
        string made;
        lock (gate)
        {
            var taken = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            var number = entries.Count + 1;
            while (taken.Contains($"Output {number}")) number++;
            made = name ?? $"Output {number}";
        }
        return Add(new AudioOutputEntry { Name = made });
    }

    /// <summary>Renames an output; a blank name is ignored.</summary>
    public void Rename(string id, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return;
        Change(id, e => e with { Name = trimmed });
    }

    /// <summary>Points an output at a device (null = the default, <see cref="NoDeviceUid"/> = none) and a pair or one channel.</summary>
    public void AssignDevice(string id, string? deviceUid, int channelOffset, int? monoChannel = null) =>
        Change(id, e => e with { DeviceUid = deviceUid, ChannelOffset = Math.Max(0, channelOffset), MonoChannel = monoChannel is { } mono ? Math.Max(0, mono) : null });

    /// <summary>One mono output per channel of a multi-channel interface ("Focusrite 1", "Focusrite 2"…), skipping channels it already has.</summary>
    public void AddOutputPerChannel(AudioOutputDevice device)
    {
        lock (gate)
        {
            var taken = entries.Where(e => e.DeviceUid == device.Uid && e.MonoChannel is not null).Select(e => e.MonoChannel!.Value).ToHashSet();
            for (var channel = 0; channel < device.ChannelCount; channel++)
            {
                if (taken.Contains(channel)) continue;
                entries.Add(new AudioOutputEntry { Name = $"{device.Name} {channel + 1}", DeviceUid = device.Uid, MonoChannel = channel });
            }
        }
        Changed?.Invoke();
    }

    /// <summary>The mono output for one channel of a device, made when there isn't one.</summary>
    public AudioOutputEntry EnsureMono(AudioOutputDevice device, int channel)
    {
        lock (gate)
            if (entries.FirstOrDefault(e => e.DeviceUid == device.Uid && e.MonoChannel == channel) is { } found) return found;
        return Add(new AudioOutputEntry { Name = $"{device.Name} {channel + 1}", DeviceUid = device.Uid, MonoChannel = channel });
    }

    /// <summary>The stereo output for one pair of a device, made when there isn't one.</summary>
    public AudioOutputEntry EnsurePair(AudioOutputDevice device, int offset)
    {
        lock (gate)
            if (entries.FirstOrDefault(e => e.DeviceUid == device.Uid && e.MonoChannel is null && e.ChannelOffset == offset) is { } found) return found;
        return Add(new AudioOutputEntry { Name = $"{device.Name} {offset + 1}-{offset + 2}", DeviceUid = device.Uid, ChannelOffset = offset });
    }

    /// <summary>"3" for a mono output on channel 3, "1-2" for a pair.</summary>
    public static string ChannelWords(AudioOutputEntry entry) =>
        entry.MonoChannel is { } mono ? $"{mono + 1}" : $"{entry.ChannelOffset + 1}-{entry.ChannelOffset + 2}";

    /// <summary>Sets the playout delay, clamped to 0–2000 ms (0 is stored as none).</summary>
    public void SetDelay(string id, int milliseconds)
    {
        var clamped = Math.Clamp(milliseconds, 0, MaxDelayMilliseconds);
        Change(id, e => e with { DelayMs = clamped == 0 ? null : clamped });
    }

    /// <summary>Removes an output, except the last one (there is always somewhere to play).</summary>
    public void Remove(string id)
    {
        lock (gate)
        {
            if (entries.Count <= 1 || entries.RemoveAll(e => e.Id == id) == 0) return;
        }
        Changed?.Invoke();
    }

    private AudioOutputEntry Add(AudioOutputEntry entry)
    {
        lock (gate) entries.Add(entry);
        Changed?.Invoke();
        return entry;
    }

    private void Change(string id, Func<AudioOutputEntry, AudioOutputEntry> change)
    {
        lock (gate)
        {
            var index = entries.FindIndex(e => e.Id == id);
            if (index < 0) return;
            var changed = change(entries[index]);
            if (changed == entries[index]) return;
            entries[index] = changed;
        }
        Changed?.Invoke();
    }
}

/// <summary>The words the Sound settings show for an output. Port of the Mac's AudioOutputWords.</summary>
public static class AudioOutputWords
{
    /// <summary>"Focusrite USB 3-4 · 20 ms delay", "System default", "No device"…</summary>
    public static string Subtitle(AudioOutputEntry entry, IReadOnlyList<AudioOutputDevice> devices)
    {
        var parts = new List<string>();
        if (entry.IsDeviceless) parts.Add("No device");
        else if (entry.DeviceUid is { } uid)
        {
            if (devices.FirstOrDefault(d => d.Uid == uid) is { } device)
                parts.Add(device.ChannelCount > 2 || entry.MonoChannel is not null ? $"{device.Name} {AudioOutputInventory.ChannelWords(entry)}" : device.Name);
            else
                parts.Add($"Channels {AudioOutputInventory.ChannelWords(entry)}");
        }
        else parts.Add("System default");
        if (entry.DelayMs is > 0 and var ms) parts.Add($"{ms} ms delay");
        return string.Join(" · ", parts);
    }

    /// <summary>Whether to flag something "Not connected": a device is assigned and it wasn't found. Port of SettingsDeviceStatus.showsNotConnected.</summary>
    public static bool ShowsNotConnected(string? assigned, bool found) => !string.IsNullOrEmpty(assigned) && !found;

    /// <summary>
    /// The same for an output: it names a device (not "no device"), and the device list, once it
    /// has been read (<paramref name="devices"/> not null), doesn't have it.
    /// </summary>
    public static bool ShowsNotConnected(AudioOutputEntry entry, IReadOnlyList<AudioOutputDevice>? devices) =>
        ShowsNotConnected(entry.IsDeviceless ? null : entry.DeviceUid, devices is null || devices.Any(d => d.Uid == entry.DeviceUid));

    public const string NotConnected = "Not connected";
}
