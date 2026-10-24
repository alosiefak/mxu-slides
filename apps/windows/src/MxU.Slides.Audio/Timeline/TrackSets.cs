using MxU.Slides.Core.Automation;
using System.Globalization;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Audio.Timeline;

/// <summary>
/// Tracks sets: a song's stems bought as one download (MultiTracks.com and friends name
/// the folder "Song-Arrangement-Key-Tempo bpm"). Reading those names, naming clips like
/// titles, and finding which set a song already uses. Port of PresenterCore's TrackSets.swift.
/// </summary>
public static class TrackSets
{
    /// <summary>What a download's folder name says.</summary>
    public sealed record Download(string? Song, string? Key, double? Tempo)
    {
        public static readonly Download Unknown = new(null, null, null);
    }

    /// <summary>Reads "Run To The Father-Run to the Father-C-68.50bpm" as song, key and tempo.</summary>
    public static Download FromFolder(string name)
    {
        var parts = name.Split('-').Select(p => p.Trim()).ToList();
        if (parts.Count < 3) return Download.Unknown;
        var last = parts[^1];
        if (!last.EndsWith("bpm", StringComparison.OrdinalIgnoreCase)) return Download.Unknown;
        if (!double.TryParse(last[..^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var tempo)) return Download.Unknown;
        if (!IsKey(parts[^2])) return Download.Unknown;
        return new Download(parts[0], parts[^2], tempo);
    }

    internal static bool IsKey(string text)
    {
        if (text.Length == 0 || !"ABCDEFG".Contains(text[0])) return false;
        return text[1..] is "" or "#" or "b" or "m" or "#m" or "bm";
    }

    /// <summary>The download a set of files came from: the first of their folders (up to three levels up) that reads as one.</summary>
    public static Download ForFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var folder = path.TrimEnd('/', '\\');
            for (var level = 0; level < 3 && folder.Length > 0; level++)
            {
                var found = FromFolder(LastComponent(folder));
                if (found.Key is not null) return found;
                folder = Parent(folder);
            }
        }
        return Download.Unknown;
    }

    private static string LastComponent(string path)
    {
        var at = path.LastIndexOfAny(['/', '\\']);
        return at < 0 ? path : path[(at + 1)..];
    }

    private static string Parent(string path)
    {
        var at = path.LastIndexOfAny(['/', '\\']);
        return at <= 0 ? "" : path[..at];
    }

    /// <summary>"Run to the Father — C, 68.5 bpm" (whatever of key and tempo is known).</summary>
    public static string Name(string song, string? key, double? tempo)
    {
        string? tempoText = tempo is { } t
            ? Math.Round(t, MidpointRounding.AwayFromZero) == t
                ? $"{(long)t} bpm"
                : $"{t.ToString("G6", CultureInfo.InvariantCulture)} bpm"
            : null;
        var details = new[] { key, tempoText }.OfType<string>().ToList();
        return details.Count == 0 ? song : $"{song} — {string.Join(", ", details)}";
    }

    /// <summary>A file name as a title: underscores to spaces, no format suffix, no leading track number.</summary>
    public static string ClipName(string name)
    {
        var words = name.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[^1].ToLowerInvariant() is "mp3" or "wav" or "m4a" or "aif" or "aiff" or "flac" or "aac" or "caf")
            words.RemoveAt(words.Count - 1);
        if (words.Count > 1 && words[0].Trim('.', '-').All(char.IsNumber))
        {
            words.RemoveAt(0);
            if (words.Count > 1 && words[0] == "-") words.RemoveAt(0);
        }
        return words.Count == 0 ? name : string.Join(" ", words);
    }

    /// <summary>Audio items that belong to sets (they're hidden from the music list).</summary>
    public static HashSet<string> StemIds(IEnumerable<TrackSet> sets) =>
        sets.SelectMany(s => s.Stems.Select(stem => stem.AudioId)).ToHashSet(StringComparer.Ordinal);

    /// <summary>The set with exactly these stems (dropping the same download twice reuses it).</summary>
    public static TrackSet? Existing(IEnumerable<string> withStems, IEnumerable<TrackSet> sets)
    {
        var wanted = withStems.ToHashSet(StringComparer.Ordinal);
        return sets.FirstOrDefault(set => set.Stems.Select(s => s.AudioId).ToHashSet(StringComparer.Ordinal).SetEquals(wanted));
    }

    /// <summary>The set most of a song's stems come from (null when none do).</summary>
    public static TrackSet? UsedBy(IEnumerable<TimelineCue> cues, IEnumerable<TrackSet> sets)
    {
        var onSong = TimelineTracks.Stems(cues.ToList()).Select(c => c.MediaId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        TrackSet? best = null;
        var bestCount = 0;
        foreach (var set in sets)
        {
            var held = set.Stems.Count(s => onSong.Contains(s.AudioId));
            if (held > bestCount)
            {
                best = set;
                bestCount = held;
            }
        }
        return best;
    }

    /// <summary>A copy of <paramref name="set"/> with the stems it doesn't have yet added.</summary>
    public static TrackSet Joining(IEnumerable<TrackSetStem> stems, TrackSet set)
    {
        var joined = Core.Json.MxuJson.Clone(set);
        foreach (var stem in stems)
            if (!joined.Stems.Any(s => s.AudioId == stem.AudioId)) joined.Stems.Add(Core.Json.MxuJson.Clone(stem));
        return joined;
    }
}
