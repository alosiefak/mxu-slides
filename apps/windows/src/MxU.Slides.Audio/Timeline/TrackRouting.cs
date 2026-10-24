using System.Text.Json;
using System.Text.Json.Serialization;
using MxU.Slides.Audio.Multitrack;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Audio.Timeline;

/// <summary>
/// How this computer plays a song's stems: which audio interface, which of its outputs each
/// family (click, guide, drums, keys…) goes to, and each track's fader, pan, mute and solo.
/// They name this computer's devices, so they're kept beside settings.json, not in the
/// library (the Mac keeps them in its user defaults). Saved as tracks-setup.json.
/// </summary>
public sealed class TrackOutputSettings
{
    /// <summary>The interface the stems play on (a Windows device id); null = the Windows default output.</summary>
    [JsonPropertyName("deviceUid")] public string? DeviceUid { get; set; }

    /// <summary>The setup guide's "outputs to your sound board" (0 = everything on the main pair).</summary>
    [JsonPropertyName("outputs")] public int Outputs { get; set; }

    /// <summary>The first of those outputs (0 = output 1).</summary>
    [JsonPropertyName("start")] public int Start { get; set; }

    /// <summary>The music stems also play on outputs 1–2 (the house). Off once each family has its own output.</summary>
    [JsonPropertyName("musicOnMain")] public bool MusicOnMain { get; set; } = true;

    /// <summary>Family (as stored: "click", "electricGuitars"…) → the output(s) its mix lands on.</summary>
    [JsonPropertyName("families")] public Dictionary<string, FamilyOutput> Families { get; set; } = [];

    /// <summary>Track channel id → its fader, pan, mute and solo.</summary>
    [JsonPropertyName("channels")] public Dictionary<string, TrackChannelMix> Channels { get; set; } = [];

    /// <summary>The tracks' master fader.</summary>
    [JsonPropertyName("trim")] public float Trim { get; set; } = 1;

    [JsonPropertyName("muted")] public bool Muted { get; set; }

    /// <summary>A track's strip (made with the defaults when it has none yet).</summary>
    public TrackChannelMix Mix(string channelId)
    {
        if (!Channels.TryGetValue(channelId, out var mix)) Channels[channelId] = mix = new TrackChannelMix();
        return mix;
    }
}

/// <summary>Where a family's mix lands: one output (mono) or a pair starting there (stereo).</summary>
public sealed class FamilyOutput
{
    /// <summary>The device output, from 0; null = "not yet" (the mix is silent).</summary>
    [JsonPropertyName("channel")] public int? Channel { get; set; }

    [JsonPropertyName("stereo")] public bool Stereo { get; set; }
}

/// <summary>One track's strip on this computer.</summary>
public sealed class TrackChannelMix
{
    [JsonPropertyName("gain")] public float Gain { get; set; } = 1;

    [JsonPropertyName("pan")] public float Pan { get; set; }

    [JsonPropertyName("muted")] public bool Muted { get; set; }

    /// <summary>Soloing a music track quiets the other music tracks (click and guide always play).</summary>
    [JsonPropertyName("solo")] public bool Solo { get; set; }
}

/// <summary>
/// Turns the church's track channels and this computer's <see cref="TrackOutputSettings"/>
/// into the stems player's mixer (<see cref="MultitrackRouting"/>): each track sends to its
/// family's mix, and music tracks to the main pair too; each mix lands on the outputs the
/// setup gave it. Port of TracksController.routing, seedSends and familyBus, and the
/// TracksSetupGuide sheet's layout (Sources/TracksController.swift, TracksSetupView.swift),
/// without the Mac's full audio mixer: on Windows a family's mix goes straight to outputs.
/// </summary>
public static class TrackRouting
{
    /// <summary>The house: outputs 1–2 of the tracks' interface.</summary>
    public const string MainMix = "main";

    public const string FileName = "tracks-setup.json";

    /// <summary>The most outputs the setup guide offers.</summary>
    public const int MaxOutputs = 32;

    /// <summary>A family's stored name ("electricGuitars").</summary>
    public static string Raw(this TimelineTracks.Family family)
    {
        var name = family.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>The id of a family's mix.</summary>
    public static string BusId(TimelineTracks.Family family) => "bus:" + family.Raw();

    /// <summary>
    /// The mixer for these channels. <paramref name="extra"/> are channel ids the song has
    /// stems on that are no longer in the list (they still play, on the main pair).
    /// <paramref name="deviceChannels"/> is how many outputs the interface has.
    /// </summary>
    public static MultitrackRouting Build(
        IReadOnlyList<TrackChannel> channels, TrackOutputSettings settings, int deviceChannels, IEnumerable<string>? extra = null)
    {
        var known = channels.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var all = channels.Concat((extra ?? []).Where(id => !known.Contains(id)).Distinct().Order(StringComparer.Ordinal)
            .Select(id => new TrackChannel { Id = id, Name = id })).ToList();
        var soloed = all.Where(c => settings.Channels.TryGetValue(c.Id, out var mix) && mix.Solo).Select(c => c.Id).ToHashSet();
        var quiet = TimelineTracks.SilencedBySolo(all, soloed);
        var routed = all.Select(channel =>
        {
            settings.Channels.TryGetValue(channel.Id, out var mix);
            // Each track feeds its family's mix (a channel no longer in the list has no family mix).
            var sends = new Dictionary<string, float>(StringComparer.Ordinal);
            if (known.Contains(channel.Id)) sends[BusId(TimelineTracks.FamilyOf(channel))] = 1;
            // Click and guide are for the band's ears only: never the house.
            if (channel.TrackRole() == TimelineTracks.Role.Music && settings.MusicOnMain) sends[MainMix] = 1;
            return new MultitrackRouting.Channel(
                channel.Id, Math.Clamp(mix?.Gain ?? 1, 0, 1), Math.Clamp(mix?.Pan ?? 0, -1, 1),
                (mix?.Muted ?? false) || quiet.Contains(channel.Id), sends, channel.Mono == true);
        }).ToList();

        var mixes = new List<MultitrackRouting.Mix>
        {
            new(MainMix, outputs: deviceChannels >= 2 ? [new MultitrackRouting.Output([0, 1])] : deviceChannels == 1 ? [new MultitrackRouting.Output([0])] : []),
        };
        foreach (var family in TimelineTracks.Families)
        {
            var outputs = new List<MultitrackRouting.Output>();
            if (settings.Families.TryGetValue(family.Raw(), out var landing) && landing.Channel is { } first && first >= 0)
            {
                if (landing.Stereo && first + 1 < deviceChannels) outputs.Add(new MultitrackRouting.Output([first, first + 1]));
                else if (!landing.Stereo && first < deviceChannels) outputs.Add(new MultitrackRouting.Output([first]));
            }
            mixes.Add(new MultitrackRouting.Mix(BusId(family), outputs: outputs));
        }
        return new MultitrackRouting(routed, mixes, Math.Clamp(settings.Trim, 0, 1), settings.Muted);
    }

    /// <summary>
    /// The setup guide's "outputs to your sound board": with none, the music plays on the main
    /// pair; with some, each family gets its standard output from <paramref name="start"/>
    /// (Click on 1, Guide on 2, …) and the music comes off the main pair.
    /// </summary>
    public static void Lay(TrackOutputSettings settings, int count, int start, int deviceChannels)
    {
        settings.Outputs = Math.Clamp(count, 0, Math.Min(MaxOutputs, deviceChannels));
        settings.Start = Math.Clamp(start, 0, Math.Max(0, deviceChannels - settings.Outputs));
        if (settings.Outputs == 0)
        {
            settings.MusicOnMain = true;
            return;
        }
        var layout = TimelineTracks.LayoutFitting(settings.Outputs);
        foreach (var family in TimelineTracks.Families)
        {
            if (!layout.TryGetValue(family, out var channels))
            {
                Assign(settings, family, null, false);
                continue;
            }
            Assign(settings, family, settings.Start + channels[0], channels.Length == 2);
        }
        settings.MusicOnMain = false;
    }

    /// <summary>A family's output now: (null, stereo-by-default) when it has none; click and guide default to mono.</summary>
    public static (int? Channel, bool Stereo) Current(TrackOutputSettings settings, TimelineTracks.Family family) =>
        settings.Families.TryGetValue(family.Raw(), out var landing) && landing.Channel is not null
            ? (landing.Channel, landing.Stereo)
            : (null, family != TimelineTracks.Family.Click && family != TimelineTracks.Family.Guide);

    /// <summary>Sends a family's mix to an output (or, null, nowhere yet).</summary>
    public static void Assign(TrackOutputSettings settings, TimelineTracks.Family family, int? channel, bool stereo) =>
        settings.Families[family.Raw()] = new FamilyOutput { Channel = channel, Stereo = stereo };

    /// <summary>The setup guide's line under the outputs ("None: the tracks play from…").</summary>
    public static string Explanation(int count, string? deviceName) => count == 0
        ? "None: the tracks play from this computer's main output. Give click and guide outputs below for the band."
        : $"Each mix below goes to its own output on {deviceName ?? "the interface"}. Change any of them.";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Reads the settings (defaults when the file is missing or damaged; this never throws).</summary>
    public static TrackOutputSettings Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<TrackOutputSettings>(File.ReadAllBytes(path), Json) ?? new TrackOutputSettings();
        }
        catch (Exception)
        {
            // A damaged file means defaults: the stems still play on the main pair.
        }
        return new TrackOutputSettings();
    }

    /// <summary>Saves the settings safely (a half-written file can't replace a good one).</summary>
    public static void Save(TrackOutputSettings settings, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        LibraryStore.AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(settings, Json));
    }
}
