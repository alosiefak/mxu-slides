using System.Text.Json.Serialization;
using MxU.Slides.Core.Json;

namespace MxU.Slides.Audio.Multitrack;

/// <summary>
/// The tracks' mixer: each track channel's fader, pan, mute and sends to mixes; each mix's
/// fader, mute, the device channels it lands on and (for streams) a share slot. The
/// renderer turns this into one gain table. Mutable like the Swift struct it ports, so the
/// engine keeps its own <see cref="Clone"/>. Port of MultitrackRouting.swift.
/// </summary>
public sealed class MultitrackRouting : IEquatable<MultitrackRouting>
{
    /// <summary>One track channel's strip.</summary>
    public sealed class Channel : IEquatable<Channel>
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("gain")] public float Gain { get; set; } = 1;
        [JsonPropertyName("pan")] public float Pan { get; set; }
        [JsonPropertyName("muted")] public bool Muted { get; set; }

        /// <summary>Mix id → send level.</summary>
        [JsonPropertyName("sends")] public Dictionary<string, float> Sends { get; set; } = [];

        /// <summary>Fold the stem to mono before the pan (a mono source recorded on two channels).</summary>
        [JsonPropertyName("mono")] public bool Mono { get; set; }

        public Channel() { }

        public Channel(string id, float gain = 1, float pan = 0, bool muted = false, Dictionary<string, float>? sends = null, bool mono = false)
        {
            Id = id;
            Gain = gain;
            Pan = pan;
            Muted = muted;
            Sends = sends ?? [];
            Mono = mono;
        }

        public bool Equals(Channel? other) =>
            other is not null && Id == other.Id && Gain == other.Gain && Pan == other.Pan && Muted == other.Muted
            && Mono == other.Mono && Sends.Count == other.Sends.Count
            && Sends.All(s => other.Sends.TryGetValue(s.Key, out var level) && level == s.Value);

        public override bool Equals(object? obj) => Equals(obj as Channel);

        public override int GetHashCode() => HashCode.Combine(Id, Gain, Pan, Muted, Mono);
    }

    /// <summary>One mix: where it lands, and whether a stream takes it (a share slot).</summary>
    public sealed class Mix : IEquatable<Mix>
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("gain")] public float Gain { get; set; } = 1;
        [JsonPropertyName("muted")] public bool Muted { get; set; }
        [JsonPropertyName("outputs")] public List<Output> Outputs { get; set; } = [];

        /// <summary>The <see cref="TracksShare"/> slot a stream reads this mix from; null = none.</summary>
        [JsonPropertyName("feedSlot")] public int? FeedSlot { get; set; }

        public Mix() { }

        public Mix(string id, float gain = 1, bool muted = false, List<Output>? outputs = null, int? feedSlot = null)
        {
            Id = id;
            Gain = gain;
            Muted = muted;
            Outputs = outputs ?? [];
            FeedSlot = feedSlot;
        }

        public bool Equals(Mix? other) =>
            other is not null && Id == other.Id && Gain == other.Gain && Muted == other.Muted
            && FeedSlot == other.FeedSlot && Outputs.SequenceEqual(other.Outputs);

        public override bool Equals(object? obj) => Equals(obj as Mix);

        public override int GetHashCode() => HashCode.Combine(Id, Gain, Muted, FeedSlot);
    }

    /// <summary>Device channels a mix lands on (one = mono, two = a pair), at a level, with a delay.</summary>
    public sealed class Output : IEquatable<Output>
    {
        [JsonPropertyName("channels")] public List<int> Channels { get; set; } = [];
        [JsonPropertyName("level")] public float Level { get; set; } = 1;
        [JsonPropertyName("delayMs")] public double DelayMs { get; set; }

        public Output() { }

        public Output(List<int> channels, float level = 1, double delayMs = 0)
        {
            Channels = channels;
            Level = level;
            DelayMs = delayMs;
        }

        public bool Equals(Output? other) =>
            other is not null && Level == other.Level && DelayMs == other.DelayMs && Channels.SequenceEqual(other.Channels);

        public override bool Equals(object? obj) => Equals(obj as Output);

        public override int GetHashCode() => HashCode.Combine(Level, DelayMs, Channels.Count);
    }

    [JsonPropertyName("channels")] public List<Channel> Channels { get; set; } = [];
    [JsonPropertyName("mixes")] public List<Mix> Mixes { get; set; } = [];

    /// <summary>The tracks' master fader.</summary>
    [JsonPropertyName("trim")] public float Trim { get; set; } = 1;
    [JsonPropertyName("muted")] public bool Muted { get; set; }

    public MultitrackRouting() { }

    public MultitrackRouting(List<Channel>? channels = null, List<Mix>? mixes = null, float trim = 1, bool muted = false)
    {
        Channels = channels ?? [];
        Mixes = mixes ?? [];
        Trim = trim;
        Muted = muted;
    }

    /// <summary>A deep copy (the engine keeps one so later edits to yours don't change its own).</summary>
    public MultitrackRouting Clone() => MxuJson.Clone(this);

    /// <summary>The mixes a stream takes, in order (each has a share slot).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> FeedMixIds => Mixes.Where(m => m.FeedSlot is not null).Select(m => m.Id).ToList();

    /// <summary>Per device channel, the longest delay any output on it asks for, in frames.</summary>
    internal int[] DelayFrames(int deviceChannels, double sampleRate)
    {
        var frames = new int[deviceChannels];
        foreach (var output in Mixes.SelectMany(m => m.Outputs).Where(o => o.DelayMs > 0))
        {
            var delay = (int)Math.Round(Math.Min(output.DelayMs, 2000) / 1000 * sampleRate, MidpointRounding.AwayFromZero);
            foreach (var channel in output.Channels.Where(c => c >= 0 && c < deviceChannels))
                frames[channel] = Math.Max(frames[channel], delay);
        }
        return frames;
    }

    /// <summary>A mix as the console sees it: it may land on several devices.</summary>
    public sealed record ConsoleMix(string Id, IReadOnlyList<(string DeviceUid, Output Output)> Landings, float Gain = 1, bool Muted = false);

    /// <summary>
    /// The tracks play on one device: the one most of their live mixes land on (ties go to
    /// the earlier name). Returns the routing with only that device's outputs.
    /// </summary>
    public static (MultitrackRouting Routing, string DeviceUid) Resolve(
        IReadOnlyList<Channel> channels, IReadOnlyList<ConsoleMix> mixes, float trim, bool muted, string fallbackDevice)
    {
        var reached = channels.Where(c => !c.Muted).SelectMany(c => c.Sends.Where(s => s.Value > 0).Select(s => s.Key)).ToHashSet(StringComparer.Ordinal);
        var tally = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var mix in mixes.Where(m => reached.Contains(m.Id) && !m.Muted))
            foreach (var landing in mix.Landings)
                tally[landing.DeviceUid] = tally.GetValueOrDefault(landing.DeviceUid) + 1;
        string? device = null;
        foreach (var (uid, count) in tally)
            if (device is null || count > tally[device] || count == tally[device] && string.CompareOrdinal(uid, device) < 0) device = uid;
        device ??= fallbackDevice;
        var routing = new MultitrackRouting(
            channels.ToList(),
            mixes.Select(mix => new Mix(mix.Id, mix.Gain, mix.Muted,
                mix.Landings.Where(l => l.DeviceUid == device).Select(l => l.Output).ToList())).ToList(),
            trim, muted);
        return (routing, device);
    }

    /// <summary>Mixes some unmuted channel actually sends to.</summary>
    [JsonIgnore]
    public HashSet<string> ReachedMixIds => Muted
        ? []
        : Channels.Where(c => !c.Muted).SelectMany(c => c.Sends.Where(s => s.Value > 0).Select(s => s.Key)).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Gives each wanted mix a share slot, keeping the slots mixes already hold (a stream
    /// reading slot 3 keeps reading the same mix); when all are taken, an unwanted mix's
    /// slot is reused.
    /// </summary>
    public static Dictionary<string, int> AssignSlots(IReadOnlySet<string> wanted, IReadOnlyDictionary<string, int> held)
    {
        var slots = new Dictionary<string, int>(held, StringComparer.Ordinal);
        foreach (var mix in wanted.OrderBy(m => m, StringComparer.Ordinal))
        {
            if (slots.ContainsKey(mix)) continue;
            var taken = slots.Values.ToHashSet();
            var free = Enumerable.Range(0, TracksShare.Slots).Where(s => !taken.Contains(s)).Select(s => (int?)s).FirstOrDefault();
            if (free is { } slot)
            {
                slots[mix] = slot;
            }
            else if (slots.Where(s => !wanted.Contains(s.Key)).OrderBy(s => s.Value).Select(s => (KeyValuePair<string, int>?)s).FirstOrDefault() is { } idle)
            {
                slots.Remove(idle.Key);
                slots[mix] = idle.Value;
            }
        }
        return slots;
    }

    /// <summary>A copy where exactly the wanted mixes carry their share slot.</summary>
    public MultitrackRouting Feeding(IReadOnlySet<string> wanted, IReadOnlyDictionary<string, int> slots)
    {
        var routing = Clone();
        foreach (var mix in routing.Mixes)
            mix.FeedSlot = wanted.Contains(mix.Id) && slots.TryGetValue(mix.Id, out var slot) ? slot : null;
        return routing;
    }

    public bool Equals(MultitrackRouting? other) =>
        other is not null && Trim == other.Trim && Muted == other.Muted
        && Channels.SequenceEqual(other.Channels) && Mixes.SequenceEqual(other.Mixes);

    public override bool Equals(object? obj) => Equals(obj as MultitrackRouting);

    public override int GetHashCode() => HashCode.Combine(Trim, Muted, Channels.Count, Mixes.Count);
}

/// <summary>
/// The routing as one table of gains: for each stem's left and right, how much reaches
/// each destination (the device channels, then two per stream feed). Port of MultitrackMatrix.
/// </summary>
internal sealed class MultitrackMatrix
{
    public MultitrackMatrix(MultitrackRouting routing, IReadOnlyList<string> stemChannels, int deviceChannels)
    {
        Stems = stemChannels.Count;
        DeviceChannels = deviceChannels;
        FeedMixIds = routing.FeedMixIds;
        FeedSlots = routing.Mixes.Where(m => m.FeedSlot is not null).Select(m => m.FeedSlot!.Value).ToArray();
        Destinations = deviceChannels + 2 * FeedMixIds.Count;
        Gains = new float[stemChannels.Count * 2 * Destinations];
        if (routing.Muted) return;
        var channels = new Dictionary<string, MultitrackRouting.Channel>(StringComparer.Ordinal);
        foreach (var channel in routing.Channels) channels.TryAdd(channel.Id, channel);
        var mixes = new Dictionary<string, MultitrackRouting.Mix>(StringComparer.Ordinal);
        foreach (var mix in routing.Mixes) mixes.TryAdd(mix.Id, mix);
        for (var stem = 0; stem < stemChannels.Count; stem++)
        {
            if (!channels.TryGetValue(stemChannels[stem], out var channel) || channel.Muted) continue;
            var gain = routing.Trim * channel.Gain;
            var pan = Math.Clamp(channel.Pan, -1, 1);
            float[] sides = [gain * MathF.Min(1, 1 - pan), gain * MathF.Min(1, 1 + pan)];
            // A mono channel sends half of each side to both, before the pan.
            float[][] fold = channel.Mono ? [[0.5f, 0.5f], [0.5f, 0.5f]] : [[1, 0], [0, 1]];
            void Land(int side, int destination, float level)
            {
                for (var from = 0; from < 2; from++)
                    if (fold[from][side] != 0) Gains[(stem * 2 + from) * Destinations + destination] += fold[from][side] * sides[side] * level;
            }
            foreach (var (mixId, level) in channel.Sends)
            {
                if (!mixes.TryGetValue(mixId, out var mix) || mix.Muted) continue;
                var reach = level * mix.Gain;
                foreach (var output in mix.Outputs)
                {
                    if (output.Channels.Count == 1 && output.Channels[0] < deviceChannels)
                    {
                        // Both sides onto one channel, each at half.
                        for (var side = 0; side < 2; side++) Land(side, output.Channels[0], reach * output.Level * 0.5f);
                    }
                    else if (output.Channels.Count == 2 && output.Channels.All(c => c < deviceChannels))
                    {
                        for (var side = 0; side < 2; side++) Land(side, output.Channels[side], reach * output.Level);
                    }
                }
                var feed = IndexOf(FeedMixIds, mixId);
                if (feed >= 0)
                    for (var side = 0; side < 2; side++) Land(side, deviceChannels + feed * 2 + side, reach);
            }
        }
    }

    public int Stems { get; }

    public int DeviceChannels { get; }

    public IReadOnlyList<string> FeedMixIds { get; }

    /// <summary>The share slot of each fed mix, in <see cref="FeedMixIds"/> order.</summary>
    public int[] FeedSlots { get; }

    public int Destinations { get; }

    public float[] Gains { get; }

    public float Gain(int stem, int side, int destination) => Gains[(stem * 2 + side) * Destinations + destination];

    private static int IndexOf(IReadOnlyList<string> ids, string id)
    {
        for (var index = 0; index < ids.Count; index++)
            if (ids[index] == id) return index;
        return -1;
    }
}
