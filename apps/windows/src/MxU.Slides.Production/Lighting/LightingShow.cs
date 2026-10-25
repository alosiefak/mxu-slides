using System.Globalization;
using System.Text.Json.Serialization;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;

namespace MxU.Slides.Production.Lighting;

/// <summary>
/// The lighting a library holds: named <see cref="LightingLook"/>s (channel levels with a fade
/// time), a <see cref="LightingCue"/> list to step through with GO, and channel groups to set
/// many channels with one slider. It belongs to the show, not to this PC, so it's kept in the
/// library (in "Windows extras/lighting/show.json", where the Mac never looks); which network
/// node each universe goes out of is this PC's business (Settings › Production).
/// </summary>
public sealed class LightingShow
{
    public const string ExtrasKind = "lighting";
    public const string ExtrasOwner = "show";

    [JsonPropertyName("groups")]
    public List<ChannelGroup> Groups { get; set; } = [];

    [JsonPropertyName("looks")]
    public List<LightingLook> Looks { get; set; } = [];

    [JsonPropertyName("cues")]
    public List<LightingCue> Cues { get; set; } = [];

    public LightingLook? Look(string? id) => id is null ? null : Looks.FirstOrDefault(l => l.Id == id);

    /// <summary>
    /// The channels that are attributes (colour, position, gobo: groups marked so) rather
    /// than brightness. They merge latest-takes-precedence and ignore the grand master;
    /// everything else is brightness, merged highest-takes-precedence.
    /// </summary>
    public HashSet<(int Universe, int Channel)> AttributeChannels() =>
        [.. Groups.Where(g => g.Attribute).SelectMany(g => g.Channels.Select(c => (g.Universe, c)))];

    public static LightingShow Load(WindowsExtras extras) =>
        extras.Get<LightingShow>(ExtrasKind, ExtrasOwner) ?? new LightingShow();

    public void Save(WindowsExtras extras) => extras.Save(ExtrasKind, ExtrasOwner, this);

    public LightingShow Copy() => MxuJson.Clone(this);
}

/// <summary>A named set of channels in one universe: "Front wash" = universe 1, channels 1-6.</summary>
public sealed class ChannelGroup
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("D").ToUpperInvariant();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("universe")]
    public int Universe { get; set; } = 1;

    [JsonPropertyName("channels")]
    public List<int> Channels { get; set; } = [];

    /// <summary>Colour, pan, tilt…: latest-takes-precedence, not dimmed by the grand master.</summary>
    [JsonPropertyName("attribute"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Attribute { get; set; }
}

/// <summary>A lighting state: some channels' levels (0-255), and how long it takes to fade in.</summary>
public sealed class LightingLook
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("D").ToUpperInvariant();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>The fade when a step or cue doesn't give its own, in seconds.</summary>
    [JsonPropertyName("fadeSeconds")]
    public double FadeSeconds { get; set; } = 3;

    /// <summary>The channels this look sets. Channels it doesn't mention: off, or (attributes) left as they are.</summary>
    [JsonPropertyName("levels")]
    public List<LookLevel> Levels { get; set; } = [];

    public int? Level(int universe, int channel) =>
        Levels.FirstOrDefault(l => l.Universe == universe && l.Channel == channel)?.Value;

    /// <summary>Sets (or, with null, forgets) one channel's level.</summary>
    public void SetLevel(int universe, int channel, int? value)
    {
        Levels.RemoveAll(l => l.Universe == universe && l.Channel == channel);
        if (value is { } level)
            Levels.Add(new LookLevel(universe, Math.Clamp(channel, 1, 512), Math.Clamp(level, 0, 255)));
        Levels.Sort((a, b) => a.Universe != b.Universe ? a.Universe.CompareTo(b.Universe) : a.Channel.CompareTo(b.Channel));
    }
}

/// <summary>One channel's level in a look.</summary>
public sealed record LookLevel(
    [property: JsonPropertyName("universe")] int Universe,
    [property: JsonPropertyName("channel")] int Channel,
    [property: JsonPropertyName("value")] int Value);

/// <summary>A step in the cue list: a look, with its own fade or the look's.</summary>
public sealed class LightingCue
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("D").ToUpperInvariant();

    /// <summary>What the cue list shows ("Walk-in", "Sermon"); empty: the look's name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("lookId")]
    public string LookId { get; set; } = "";

    [JsonPropertyName("fadeSeconds")]
    public double? FadeSeconds { get; set; }
}

/// <summary>Reading and writing channel lists the way lighting people type them: "1-6, 9, 12-13".</summary>
public static class ChannelList
{
    /// <summary>The channels in a typed list (1 to 512, each once, in order). Anything unreadable is left out.</summary>
    public static List<int> Parse(string text)
    {
        var channels = new SortedSet<int>();
        // "1 - 6" is a range too: close up the spaces round a dash first.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*[-–]\s*", "-");
        foreach (var part in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split(['-', '–'], 2);
            if (!int.TryParse(range[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var from)) continue;
            var to = from;
            if (range.Length == 2 && !int.TryParse(range[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out to)) continue;
            if (to < from) (from, to) = (to, from);
            for (var c = Math.Max(1, from); c <= Math.Min(512, to); c++) channels.Add(c);
        }
        return [.. channels];
    }

    /// <summary>A list written short: [1,2,3,4,5,6,9] is "1-6, 9".</summary>
    public static string Format(IEnumerable<int> channels)
    {
        var sorted = channels.Distinct().Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var start = i;
            while (i + 1 < sorted.Count && sorted[i + 1] == sorted[i] + 1) i++;
            parts.Add(start == i
                ? sorted[start].ToString(CultureInfo.InvariantCulture)
                : $"{sorted[start].ToString(CultureInfo.InvariantCulture)}-{sorted[i].ToString(CultureInfo.InvariantCulture)}");
            i++;
        }
        return string.Join(", ", parts);
    }
}
