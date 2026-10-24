using MxU.Slides.Audio.Library;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Audio.Timeline;

/// <summary>
/// Editing the church's track list: adding a channel by name, reordering by drag, the
/// list's subtitles, and what a rename does to the mixer sends. Port of PresenterCore's
/// TrackChannels.swift (the parts with no UI or library writes). The naming and family
/// rules it builds on are Core's <see cref="TimelineTracks"/>.
/// </summary>
public static class TrackChannelEditing
{
    /// <summary>The track channels a song's timelines have cues on.</summary>
    public static HashSet<string> ChannelIds(Presentation presentation) =>
        (presentation.Timelines ?? []).SelectMany(t => t.Cues).Select(c => c.TrackChannelId()).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>The second line under a channel in the track list: its width, and its family mix unless the name already says it.</summary>
    public static string ListSubtitle(TrackChannel channel)
    {
        var width = channel.Mono == true ? "Mono" : "Stereo";
        var family = TimelineTracks.FamilyOf(channel).Name();
        var name = string.Join(" ", NameWords(channel.Name));
        var said = name.Length > 0 && family.ToLowerInvariant().StartsWith(name, StringComparison.Ordinal);
        return said ? width : $"{family} mix · {width}";
    }

    /// <summary>A drop onto the tracks area is welcome if it holds a folder or an audio file.</summary>
    public static bool TakesDrop(IEnumerable<string> paths) =>
        paths.Any(path => path.EndsWith('\\') || path.EndsWith('/') || Directory.Exists(path) || AudioImporter.IsAudioFile(path));

    /// <summary>Adds a channel by name in playback order; null for a blank name.</summary>
    public static TrackChannel? Adding(string named, List<TrackChannel> channels, string? id = null)
    {
        var trimmed = named.Trim();
        if (trimmed.Length == 0) return null;
        var role = TimelineTracks.RoleForName(trimmed);
        var made = new TrackChannel
        {
            Id = id ?? Guid.NewGuid().ToString().ToUpperInvariant(),
            Name = trimmed,
            Role = role == TimelineTracks.Role.Music ? null : role.Raw(),
        };
        channels.Insert(TimelineTracks.InsertionIndex(made, channels), made);
        return made;
    }

    /// <summary>Moving a channel before another (null = to the end): the old index, the target index, and the new order. Null when nothing moves.</summary>
    public static (int From, int? Before, List<TrackChannel> Next)? Move(string id, string? before, IReadOnlyList<TrackChannel> channels)
    {
        var from = IndexOf(channels, id);
        if (from < 0) return null;
        int? to = null;
        if (before is not null)
        {
            var index = IndexOf(channels, before);
            if (index < 0) return null;
            to = index;
        }
        var target = to ?? channels.Count;
        if (target == from || target == from + 1) return null;
        var next = channels.ToList();
        var moved = next[from];
        next.RemoveAt(from);
        next.Insert(target > from ? target - 1 : target, moved);
        return (from, to, next);
    }

    /// <summary>Where a dragged row lands: before this channel (null = at the end).</summary>
    public sealed record DropTarget(string? Before);

    /// <summary>Dropping a dragged row above or below <paramref name="target"/>. Null when it wouldn't move.</summary>
    public static DropTarget? Dropping(string id, string target, bool above, IReadOnlyList<TrackChannel> channels)
    {
        var to = IndexOf(channels, target);
        if (to < 0) return null;
        var before = above ? target : to + 1 < channels.Count ? channels[to + 1].Id : null;
        return Move(id, before, channels) is null ? null : new DropTarget(before);
    }

    /// <summary>
    /// A channel was renamed or its role changed: its send to its old family bus moves to
    /// the new family's, and a channel that stops being music is taken off the main mix.
    /// </summary>
    public static (Dictionary<string, float> Sends, string? Route) Edited(
        IReadOnlyDictionary<string, float> sends, string? route, TrackChannel old, TrackChannel @new,
        Func<TimelineTracks.Family, string?> bus, string main)
    {
        var next = new Dictionary<string, float>(sends, StringComparer.Ordinal);
        var (was, now) = (TimelineTracks.FamilyOf(old), TimelineTracks.FamilyOf(@new));
        if (was != now && bus(was) is { } from && next.TryGetValue(from, out var level) && bus(now) is { } to)
        {
            next.Remove(from);
            next[to] = Math.Max(next.GetValueOrDefault(to), level);
        }
        if (old.TrackRole() == TimelineTracks.Role.Music && @new.TrackRole() != TimelineTracks.Role.Music)
        {
            next.TryAdd(main, 0);
            if (route == main) route = "";
        }
        return (next, route);
    }

    private static int IndexOf(IReadOnlyList<TrackChannel> channels, string id)
    {
        for (var index = 0; index < channels.Count; index++)
            if (channels[index].Id == id) return index;
        return -1;
    }

    // A channel name's words without a trailing channel number ("Electric Guitar 2" →
    // electric guitar), as Core's TimelineTracks reads them (its own helper is internal).
    private static readonly HashSet<string> StemWords =
    [
        "eg", "egtr", "ag", "agtr", "gtr", "bgv", "bgvs", "perc", "percs", "clk",
        "click", "guide", "keys", "key", "piano", "organ", "synth", "synths", "pad", "pads", "bass",
        "drums", "drum", "strings", "track", "tracks", "vox", "loop", "loops",
    ];

    internal static List<string> NameWords(string name)
    {
        var dot = name.LastIndexOf('.');
        var slash = name.LastIndexOf('/');
        var stem = (dot > 0 && dot > slash + 1 ? name[..dot] : name).ToLowerInvariant();
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var c in stem + " ")
        {
            if (char.IsLetter(c) || char.IsNumber(c))
            {
                current.Append(c);
                continue;
            }
            if (current.Length > 0) words.Add(current.ToString());
            current.Clear();
        }
        if (words.Count > 0 && words[0].All(char.IsNumber)) words.RemoveAt(0);
        if (words.Count == 0) return words;
        var last = words[^1];
        var digits = last.Length - last.TrimEnd("0123456789".ToCharArray()).Length;
        if (digits == 0) return words;
        if (digits == last.Length)
        {
            if (words.Count > 1) words.RemoveAt(words.Count - 1);
        }
        else if (StemWords.Contains(last[..^digits]))
        {
            words[^1] = last[..^digits];
        }
        return words;
    }
}
