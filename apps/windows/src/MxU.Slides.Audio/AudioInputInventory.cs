using System.Text.Json;
using System.Text.Json.Serialization;

namespace MxU.Slides.Audio;

/// <summary>
/// One named audio input: a sound device on this PC and which of its channels, e.g. "Mixing
/// desk" = channels 1–2 of the USB interface the desk is plugged into. Port of the Mac's
/// AudioInputInventory.Entry (same JSON field names).
/// </summary>
public sealed record AudioInputEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString().ToUpperInvariant();

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>The Windows sound device's id (null: not assigned to a device yet).</summary>
    [JsonPropertyName("uid")]
    public string? Uid { get; init; }

    /// <summary>The first channel of a stereo pair (0 = channels 1–2).</summary>
    [JsonPropertyName("channelOffset")]
    public int? ChannelOffset { get; init; }

    /// <summary>One channel (0 = channel 1), heard on both sides.</summary>
    [JsonPropertyName("monoChannel")]
    public int? MonoChannel { get; init; }

    /// <summary>This input's own delay, to line a late sound up with the picture.</summary>
    [JsonPropertyName("delayMs")]
    public int? DelayMs { get; init; }

    [JsonIgnore]
    public bool IsAssigned => !string.IsNullOrEmpty(Uid);

    [JsonIgnore]
    public InputChannelSelection Channels => MonoChannel is { } mono
        ? new InputChannelSelection.Mono(mono)
        : new InputChannelSelection.StereoPair(ChannelOffset ?? 0);

    /// <summary>"channels 1–2" or "channel 3".</summary>
    public static string ChannelsLabel(InputChannelSelection channels) => channels switch
    {
        InputChannelSelection.Mono mono => $"channel {mono.Channel + 1}",
        InputChannelSelection.StereoPair pair => $"channels {pair.Offset + 1}–{pair.Offset + 2}",
        _ => "",
    };
}

/// <summary>
/// The named audio inputs on this PC, kept in a JSON file (they name this PC's devices, so they
/// aren't in the library), with one <see cref="AudioInputSource"/> per input for whatever wants
/// to hear it. Port of the Mac's AudioInputInventory, which keeps them in its preferences. The
/// Mac starts every input when it launches; Windows opens a device only while something listens
/// (so the microphone-in-use light isn't on all service).
/// <para>Threading: thread-safe; <see cref="EntriesChanged"/> is raised on the calling thread.</para>
/// </summary>
public sealed class AudioInputInventory
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly Lock gate = new();
    private readonly string storePath;
    private readonly AudioInputHubPool pool;
    private readonly Dictionary<string, AudioInputSource> sources = new(StringComparer.Ordinal);
    private List<AudioInputEntry> entries;

    /// <param name="storePath">The JSON file, e.g. the app's data folder\audio-inputs.json.</param>
    public AudioInputInventory(string storePath, AudioInputHubPool? pool = null)
    {
        this.storePath = storePath;
        this.pool = pool ?? AudioInputHubPool.Shared;
        entries = Load(storePath);
    }

    public event Action? EntriesChanged;

    public IReadOnlyList<AudioInputEntry> Entries
    {
        get { lock (gate) return [.. entries]; }
    }

    public AudioInputEntry? Entry(string id)
    {
        lock (gate) return entries.FirstOrDefault(e => e.Id == id);
    }

    public string? NameFor(string id) => Entry(id)?.Name;

    /// <summary>The entry for a device (how older presets named an input): its channels 1–2 if there is one, else any.</summary>
    public AudioInputEntry? ForUid(string uid)
    {
        lock (gate)
        {
            var matches = entries.Where(e => e.Uid == uid).ToList();
            return matches.FirstOrDefault(e => e.Channels == new InputChannelSelection.StereoPair(0)) ?? matches.FirstOrDefault();
        }
    }

    /// <summary>The entry for exactly this device and these channels, made (and named <paramref name="name"/>) if there isn't one.</summary>
    public AudioInputEntry Ensure(string uid, string name, InputChannelSelection channels)
    {
        AudioInputEntry made;
        lock (gate)
        {
            if (entries.FirstOrDefault(e => e.Uid == uid && e.Channels == channels) is { } existing) return existing;
            made = new AudioInputEntry
            {
                Name = name,
                Uid = uid,
                ChannelOffset = channels is InputChannelSelection.StereoPair pair ? pair.Offset : null,
                MonoChannel = channels is InputChannelSelection.Mono mono ? mono.Channel : null,
            };
            entries.Add(made);
            SaveLocked();
        }
        EntriesChanged?.Invoke();
        return made;
    }

    public void Remove(string id)
    {
        AudioInputSource? source;
        lock (gate)
        {
            if (entries.RemoveAll(e => e.Id == id) == 0) return;
            sources.Remove(id, out source);
            SaveLocked();
        }
        source?.Stop();
        EntriesChanged?.Invoke();
    }

    /// <summary>The source for an entry (made the first time it's asked for); null for an unknown or unassigned entry.</summary>
    public AudioInputSource? Source(string id)
    {
        lock (gate)
        {
            if (entries.FirstOrDefault(e => e.Id == id) is not { IsAssigned: true } entry) return null;
            if (sources.TryGetValue(id, out var source) && source.DeviceUid == entry.Uid && source.Channels == entry.Channels) return source;
            source?.Stop();
            source = new AudioInputSource(entry.Uid!, entry.Channels, pool) { DelayMilliseconds = entry.DelayMs ?? 0 };
            sources[id] = source;
            return source;
        }
    }

    private static List<AudioInputEntry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<AudioInputEntry>>(File.ReadAllText(path), Json)?.Where(e => e.Id.Length > 0).ToList() ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged file is kept aside, not thrown away; the inputs can be chosen again.
            try { File.Move(path, path + ".damaged", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return [];
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!);
            var temp = storePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, Json));
            File.Move(temp, storePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not saving is no reason to stop a show; the entry still works until the app quits.
        }
    }
}

/// <summary>
/// One input's sound for everything that listens to it (a recording, a stream): a tap on the
/// shared <see cref="AudioInputHub"/> for its device, cut down to its channels (48 kHz stereo)
/// and passed through its own delay. On Windows the device is opened by the first listener and
/// let go by the last (<see cref="Open"/>). Port of the Mac's AudioInputSource.
/// <para>Threading: thread-safe. Consumers are called on the device's capture thread.</para>
/// </summary>
public sealed class AudioInputSource
{
    private readonly Lock gate = new();
    private readonly AudioInputHubPool pool;
    private readonly AudioDelayLine delayLine = new();
    private readonly Dictionary<Guid, Action<PcmBuffer, double>> consumers = [];
    private AudioInputHub? hub;
    private Guid tapToken;

    public AudioInputSource(string deviceUid, InputChannelSelection channels, AudioInputHubPool? pool = null)
    {
        DeviceUid = deviceUid;
        Channels = channels;
        this.pool = pool ?? AudioInputHubPool.Shared;
    }

    public string DeviceUid { get; }

    public InputChannelSelection Channels { get; }

    public bool IsRunning
    {
        get { lock (gate) return hub is not null; }
    }

    public int ConsumerCount
    {
        get { lock (gate) return consumers.Count; }
    }

    public double DelayMilliseconds
    {
        get => delayLine.DelayMilliseconds;
        set => delayLine.DelayMilliseconds = value;
    }

    /// <summary>Opens the device if it isn't already. Throws <see cref="AudioInputHub.CaptureException"/> when it can't.</summary>
    public void Start()
    {
        lock (gate)
        {
            if (hub is not null) return;
            var opened = pool.Acquire(DeviceUid);
            hub = opened;
            tapToken = opened.AddTap(Channels, Captured);
        }
    }

    private void Captured(PcmBuffer buffer, double hostSeconds)
    {
        // Each tap gets its own buffer from the hub, so it can be delayed in place.
        delayLine.Process(buffer);
        Action<PcmBuffer, double>[] sinks;
        lock (gate) sinks = [.. consumers.Values];
        foreach (var sink in sinks)
        {
            try { sink(buffer, hostSeconds); }
            catch { }
        }
    }

    /// <summary>Lets go of the device (consumers stay, and hear nothing until it starts again).</summary>
    public void Suspend()
    {
        AudioInputHub? letGo;
        Guid token;
        lock (gate)
        {
            letGo = hub;
            token = tapToken;
            hub = null;
        }
        if (letGo is null) return;
        // Outside the lock: closing the device waits for its capture thread, which may be waiting for the lock in Captured.
        letGo.RemoveTap(token);
        pool.Release(DeviceUid);
    }

    /// <summary>Lets go of the device and forgets every consumer.</summary>
    public void Stop()
    {
        Suspend();
        lock (gate) consumers.Clear();
    }

    public Guid AddConsumer(Action<PcmBuffer, double> consumer)
    {
        var token = Guid.NewGuid();
        lock (gate) consumers[token] = consumer;
        return token;
    }

    public void RemoveConsumer(Guid token)
    {
        lock (gate) consumers.Remove(token);
    }

    /// <summary>
    /// Listens, opening the device if needed; null (with the reason) when it can't be opened,
    /// e.g. it's unplugged or Windows' microphone privacy setting blocks desktop apps. Disposing
    /// the result stops listening, and the last listener to go lets go of the device.
    /// </summary>
    public IDisposable? Open(Action<PcmBuffer, double> consumer, out string? problem)
    {
        problem = null;
        try
        {
            Start();
        }
        catch (AudioInputHub.CaptureException e)
        {
            problem = e.Message;
            return null;
        }
        var token = AddConsumer(consumer);
        return new Listener(this, token);
    }

    private void Close(Guid token)
    {
        bool last;
        lock (gate)
        {
            consumers.Remove(token);
            last = consumers.Count == 0;
        }
        if (last) Suspend();
    }

    private sealed class Listener(AudioInputSource owner, Guid token) : IDisposable
    {
        private int closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref closed, 1) == 0) owner.Close(token);
        }
    }
}
