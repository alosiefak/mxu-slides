using MxU.Slides.Audio.Decoding;
using MxU.Slides.Audio.Library;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Audio.Timeline;

/// <summary>
/// Bringing a song's stems in: each audio file is copied into the library as a music item
/// named "Song · File", and the files together become a <see cref="TrackSet"/> (the stems
/// of one download), so dropping the same folder again reuses them and the stems remember
/// their tracks. Port of AppModel.importTrackSet and importStems (Sources/AppModel.swift).
/// Slow (it copies and hashes files): run it off the UI thread.
/// </summary>
public sealed class StemImporter(LibraryStore store)
{
    /// <summary>
    /// Imports every audio file in <paramref name="paths"/> (folders are walked). The same
    /// file imported before is reused, not copied again. Files that can't be read are skipped.
    /// </summary>
    public List<TimelineStemImport> ImportStems(IEnumerable<string> paths, string song)
    {
        var importer = new AudioImporter(store);
        var stems = new List<TimelineStemImport>();
        foreach (var path in AudioImporter.Files(paths).Where(AudioImporter.IsAudioFile))
        {
            AudioItem? item;
            try
            {
                item = importer.Import(path, song + TimelineLabels.Dot + Path.GetFileNameWithoutExtension(path));
            }
            catch (Exception)
            {
                continue;
            }
            if (item is null) continue;
            stems.Add(new TimelineStemImport(Path.GetFileName(path), item.Id, item.DurationSeconds, Channels(path)));
        }
        return stems;
    }

    /// <summary>How many channels a file has (1 = mono, so its new track is made mono); null when it can't be read.</summary>
    public static int? Channels(string path)
    {
        try
        {
            using var decoder = AudioFileDecoder.Open(path, 48_000);
            return decoder.SourceChannels;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Imports the files as a song's stems and files them as a track set: the set that has
    /// exactly these stems already (with the tracks they went on last time), else the set
    /// named by <paramref name="joining"/> (one more file for a song's set), else a new set
    /// named from the download's folder ("Song — C, 68.5 bpm"). Null when no file imported.
    /// </summary>
    public (TrackSet Set, List<TimelineStemImport> Stems)? ImportTrackSet(IReadOnlyList<string> paths, string song, string? joining = null)
    {
        var stems = ImportStems(paths, song);
        if (stems.Count == 0) return null;
        var sets = store.All<TrackSet>();
        if (TrackSets.Existing(stems.Select(s => s.AudioId), sets) is { } existing)
        {
            var known = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var stem in existing.Stems) known.TryAdd(stem.AudioId, stem.ChannelId);
            return (existing, stems.Select(s => s with { ChannelId = known.GetValueOrDefault(s.AudioId) }).ToList());
        }
        var added = stems.Select(s => new TrackSetStem { AudioId = s.AudioId, Name = Path.GetFileNameWithoutExtension(s.FileName) }).ToList();
        if (joining is not null && store.Get<TrackSet>(joining) is { } set)
        {
            var joined = TrackSets.Joining(added, set);
            store.Save(joined);
            return (joined, stems);
        }
        var download = TrackSets.ForFiles(paths);
        var made = new TrackSet
        {
            Id = LibraryStore.NewId(),
            Name = TrackSets.Name(song, download.Key, download.Tempo),
            Stems = added,
            Key = download.Key,
            Tempo = download.Tempo,
            Source = download.Key is null ? null : "MultiTracks",
        };
        store.Save(made);
        return (made, stems);
    }

    /// <summary>One more file for a song that already has stems joins the song's set; a folder or several files make their own.</summary>
    public static string? JoiningSet(IReadOnlyList<string> paths, IReadOnlyList<TimelineCue> cues, IEnumerable<TrackSet> sets) =>
        paths.Count == 1 && !Directory.Exists(paths[0]) ? TrackSets.UsedBy(cues, sets)?.Id : null;

    /// <summary>
    /// After stems land on their tracks, each set remembers the track its stems went on (so
    /// the next song that uses the set puts them back there).
    /// </summary>
    public static bool Remember(TrackSet set, IReadOnlyDictionary<string, string> channelsByAudioId)
    {
        var changed = false;
        foreach (var stem in set.Stems)
        {
            if (!channelsByAudioId.TryGetValue(stem.AudioId, out var channel) || stem.ChannelId == channel) continue;
            stem.ChannelId = channel;
            changed = true;
        }
        return changed;
    }
}

/// <summary>
/// A song map found next to dropped stems: MultiTracks.com downloads carry an Ableton set
/// ("MultiTrack.als") whose locators mark where each section starts. Port of SongMapImport
/// in Sources/SongMapEditing.swift.
/// </summary>
public sealed record SongMapImport(SongMaps.AbletonSet Set, double Bpm)
{
    public const string FileName = "MultiTrack.als";

    /// <summary>Looks in the dropped folders (and the folders above the dropped files) for a set with locators.</summary>
    public static SongMapImport? Find(IReadOnlyList<string> paths)
    {
        var folders = paths.SelectMany(path => Directory.Exists(path)
            ? new[] { path, Path.GetDirectoryName(path.TrimEnd('\\', '/')) }
            : new[] { Path.GetDirectoryName(path), Path.GetDirectoryName(Path.GetDirectoryName(path) ?? "") }).OfType<string>();
        foreach (var folder in folders.Where(f => f.Length > 0).Distinct())
        {
            var file = Path.Combine(folder, FileName);
            try
            {
                if (!File.Exists(file) || SongMaps.ReadAbletonSet(File.ReadAllBytes(file)) is not { Locators.Count: > 0 } set) continue;
                var bpm = SongMaps.Bpm(Path.GetFileName(folder.TrimEnd('\\', '/'))) ?? set.Tempo.FirstOrDefault()?.Bpm ?? 120;
                return new SongMapImport(set, bpm);
            }
            catch (Exception)
            {
                // An unreadable set just means no map; the stems still import.
            }
        }
        return null;
    }
}
