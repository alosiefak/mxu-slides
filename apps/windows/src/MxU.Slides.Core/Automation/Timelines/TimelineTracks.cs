using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Automation;

/// <summary>A dropped audio file on its way onto a song's track rows.</summary>
public sealed record TimelineStemImport(string FileName, string AudioId, double? Seconds, int? Channels = null, string? ChannelId = null);

/// <summary>
/// Multitrack stems on a presentation timeline. A stem is a cue on the lane
/// "track:&lt;channel id&gt;"; the church's <see cref="TrackChannel"/> list names the rows.
/// This file matches dropped files to channels by name ("EG 2.wav" → Electric Guitar 2),
/// makes channels for the rest, and groups channels into families for the mixer and
/// output patching. Port of PresenterCore/Timeline/TimelineTracks.swift. (The track
/// list editing in TrackChannels.swift is ported separately and adds to this class.)
/// </summary>
public static partial class TimelineTracks
{
    public const string LanePrefix = "track:";

    public static string Lane(string channelId) => LanePrefix + channelId;

    /// <summary>What a channel is for: music, or the click and guide only the band hears.</summary>
    public enum Role { Music, Click, Guide }

    public static string Raw(this Role role) => role.ToString().ToLowerInvariant();

    public static Role? RoleNamed(string? raw) => raw switch
    {
        "music" => Role.Music,
        "click" => Role.Click,
        "guide" => Role.Guide,
        _ => null,
    };

    public static Role TrackRole(this TrackChannel channel) => RoleNamed(channel.Role) ?? Role.Music;

    /// <summary>The song's stems (cues on track lanes with audio), in time order.</summary>
    public static List<TimelineCue> Stems(IReadOnlyList<TimelineCue> cues) =>
        cues.Where(c => c.TrackChannelId() is not null && c.MediaId is not null).OrderBy(c => c.At).ToList();

    /// <summary>The id prefix of the one bar drawn for all stems when the track rows are folded away.</summary>
    public const string FoldedPrefix = "folded:";

    /// <summary>One bar spanning every stem, from the first start to the last end.</summary>
    public static TimelineCue? Folded(IReadOnlyList<TimelineCue> stems, IReadOnlyList<TimelineCue> cues)
    {
        if (stems.MinBy(s => s.At) is not { } first) return null;
        var bar = first.Copy();
        var ends = stems.Select(s => PresentationTimelines.End(s, cues)).Where(double.IsFinite).ToList();
        bar.Id = FoldedPrefix + bar.Id;
        bar.Duration = ends.Count == 0 ? null : Math.Max(ends.Max() - bar.At, 0);
        return bar;
    }

    /// <summary>The cues a drag on this card moves: every stem for the folded bar, the stems starting with it for a stem.</summary>
    public static HashSet<string> Reach(TimelineCue cue, IReadOnlyList<TimelineCue> cues)
    {
        if (cue.Id.StartsWith(FoldedPrefix, StringComparison.Ordinal)) return Stems(cues).Select(c => c.Id).ToHashSet();
        return Group(cue, cues).Select(c => c.Id).ToHashSet();
    }

    /// <summary>A song's stems start together, so they move together.</summary>
    public static List<TimelineCue> Group(TimelineCue cue, IReadOnlyList<TimelineCue> cues)
    {
        if (cue.TrackChannelId() is null) return [cue];
        return Stems(cues).Where(s => Math.Abs(s.At - cue.At) < PresentationTimelines.Frame / 2).ToList();
    }

    private static readonly (Role Role, string[][] Words)[] RoleWords =
    [
        (Role.Click, [["click"], ["clk"], ["metronome"], ["count", "in"], ["countin"]]),
        (Role.Guide, [["guide"], ["cues"], ["cue"], ["vocal", "cues"], ["vox", "cues"]]),
    ];

    /// <summary>Names that are spelled a set way as a whole (keyed by their words joined with spaces).</summary>
    private static readonly Dictionary<string, string> WholeNames = new()
    {
        ["click track"] = "Click", ["click"] = "Click", ["clk"] = "Click", ["metronome"] = "Click",
        ["guide"] = "Guide", ["cues"] = "Guide", ["cue"] = "Guide", ["vocal cues"] = "Guide", ["vox cues"] = "Guide",
    };

    private static readonly Dictionary<string, string> ShortWords = new()
    {
        ["eg"] = "Electric Guitar", ["egtr"] = "Electric Guitar", ["ag"] = "Acoustic Guitar", ["agtr"] = "Acoustic Guitar",
        ["gtr"] = "Guitar", ["bgv"] = "Background Vocals", ["bgvs"] = "Background Vocals", ["perc"] = "Percussion",
        ["percs"] = "Percussion", ["clk"] = "Click",
    };

    /// <summary>Words that may carry a number glued on ("keys2"), which is read as the channel number.</summary>
    private static readonly HashSet<string> StemWords =
    [
        .. ShortWords.Keys,
        "click", "guide", "keys", "key", "piano", "organ", "synth", "synths", "pad", "pads", "bass",
        "drums", "drum", "strings", "track", "tracks", "vox", "loop", "loops",
    ];

    /// <summary>A file or channel name as lower-case words, without its extension or a leading track number.</summary>
    internal static List<string> Words(string name)
    {
        var dot = name.LastIndexOf('.');
        var slash = name.LastIndexOf('/');
        var stem = (dot > 0 && dot > slash + 1 ? name[..dot] : name).ToLowerInvariant();
        var words = SwiftParse.Split(stem, c => !char.IsLetter(c) && !char.IsNumber(c));
        if (words.Count > 0 && words[0].All(char.IsNumber)) words.RemoveAt(0);
        return words;
    }

    private static bool Contains(IReadOnlyList<string> words, IReadOnlyList<string> phrase)
    {
        if (phrase.Count == 0 || phrase.Count > words.Count) return false;
        return Enumerable.Range(0, words.Count - phrase.Count + 1).Any(at => words.Skip(at).Take(phrase.Count).SequenceEqual(phrase));
    }

    /// <summary>The name's words and its channel number ("EG 2" → [eg], 2; "Keys2" → [keys], 2).</summary>
    internal static (List<string> Words, int? Number) Numbered(string name)
    {
        var words = Words(name);
        if (words.Count == 0) return (words, null);
        var last = words[^1];
        var digitCount = 0;
        while (digitCount < last.Length && char.IsNumber(last[last.Length - 1 - digitCount])) digitCount++;
        if (digitCount == 0) return (words, null);
        var digits = last[^digitCount..];
        if (digitCount == last.Length)
        {
            if (words.Count <= 1) return (words, null);
            words.RemoveAt(words.Count - 1);
        }
        else
        {
            var stem = last[..^digitCount];
            if (!StemWords.Contains(stem)) return (words, null);
            words[^1] = stem;
        }
        return (words, SwiftParse.Int(digits));
    }

    /// <summary>The channel name for some words ("eg lead" → "Electric Guitar Lead") and the words it reads back as.</summary>
    internal static (string Name, List<string> Key) Canonical(IReadOnlyList<string> name)
    {
        var spelled = WholeNames.GetValueOrDefault(string.Join(' ', name))
            ?? string.Join(' ', name.Select(word => ShortWords.GetValueOrDefault(word) ?? word.Capitalized()));
        return (spelled, Words(spelled));
    }

    public static Role RoleForName(string name)
    {
        var words = Numbered(name).Words;
        foreach (var (role, phrases) in RoleWords)
            if (phrases.Any(phrase => Contains(words, phrase))) return role;
        return Role.Music;
    }

    /// <summary>The existing channel a dropped file belongs on, by name, number and remembered names.</summary>
    public static TrackChannel? Channel(string fileName, IReadOnlyList<TrackChannel> channels)
    {
        var file = Numbered(fileName);
        return Channel(file.Words, file.Number, channels);
    }

    internal static TrackChannel? Channel(IReadOnlyList<string> words, int? number, IReadOnlyList<TrackChannel> channels)
    {
        var key = Canonical(words).Key;
        var same = channels.Where(c => (Numbered(c.Name).Number ?? 1) == (number ?? 1)).ToList();
        if (same.FirstOrDefault(c => Canonical(Numbered(c.Name).Words).Key.SequenceEqual(key)) is { } named) return named;

        var remembered = channels.FirstOrDefault(channel => (channel.MatchNames ?? []).Any(name =>
        {
            var match = Numbered(name);
            return Canonical(match.Words).Key.SequenceEqual(key) && (match.Number ?? 1) == (number ?? 1);
        }));
        if (remembered is not null) return remembered;

        (TrackChannel Channel, int Length)? best = null;
        foreach (var channel in same)
            foreach (var phrase in (channel.MatchNames ?? []).Select(Words))
                if (Contains(words, phrase) && phrase.Count > (best?.Length ?? 0)) best = (channel, phrase.Count);
        return best?.Channel;
    }

    /// <summary>
    /// Puts each file on a channel: an unused matching one, else a new one (named from the
    /// file, numbered past any channel of that name already taken). A shared prefix like
    /// "Way Maker - " is ignored. Returns the channel list with the new ones in playback order.
    /// </summary>
    public static (List<TrackChannel> Channels, Dictionary<string, string> Assignments) Assign(
        IReadOnlyList<string> fileNames, IReadOnlyList<TrackChannel> to, IReadOnlySet<string>? taken = null, Func<string>? newId = null)
    {
        var channels = to.ToList();
        var assignments = new Dictionary<string, string>();
        var used = new HashSet<string>(taken ?? new HashSet<string>());
        var split = fileNames.Select(Numbered).ToList();
        var prefix = fileNames.Count > 1 ? CommonPrefix(split.Select(s => s.Words).ToList()) : 0;
        for (var index = 0; index < fileNames.Count; index++)
        {
            var fileName = fileNames[index];
            var words = split[index].Words.Skip(prefix).ToList();
            var matched = Channel(words, split[index].Number, channels);
            if (matched is not null && !used.Contains(matched.Id))
            {
                assignments[fileName] = matched.Id;
                used.Add(matched.Id);
                continue;
            }
            var @base = Canonical(words).Name;
            var role = RoleForName(fileName);
            var number = split[index].Number;
            TrackChannel? reuse = null;
            if (matched is not null)
            {
                number = (number ?? 1) + 1;
                while (channels.FirstOrDefault(c => c.Name == $"{@base} {number}") is { } existing)
                {
                    if (!used.Contains(existing.Id))
                    {
                        reuse = existing;
                        break;
                    }
                    number++;
                }
            }
            var chosen = reuse;
            if (chosen is null)
            {
                var name = number is { } n ? $"{@base} {n}" : @base;
                chosen = new TrackChannel
                {
                    Id = (newId ?? TimelineClips.NewId)(),
                    Name = name.TrimmingWhitespaces().Length == 0 ? "Track" : name,
                    Role = role == Role.Music ? null : role.Raw(),
                };
                channels.Insert(InsertionIndex(chosen, channels), chosen);
            }
            assignments[fileName] = chosen.Id;
            used.Add(chosen.Id);
        }
        return (channels, assignments);
    }

    /// <summary>Soloing a music channel quiets the other music channels; click and guide always play.</summary>
    public static HashSet<string> SilencedBySolo(IReadOnlyList<TrackChannel> channels, IReadOnlySet<string> soloed)
    {
        var soloing = channels.Any(c => soloed.Contains(c.Id) && c.TrackRole() == Role.Music);
        if (!soloing) return [];
        return channels.Where(c => c.TrackRole() == Role.Music && !soloed.Contains(c.Id)).Select(c => c.Id).ToHashSet();
    }

    /// <summary>Where a new channel goes: with its family (in family order), alphabetically within it.</summary>
    public static int InsertionIndex(TrackChannel channel, IReadOnlyList<TrackChannel> channels)
    {
        static int Rank(TrackChannel c) => Math.Max(0, Families.ToList().IndexOf(FamilyOf(c)));
        var mine = Rank(channel);
        for (var index = 0; index < channels.Count; index++)
        {
            var rank = Rank(channels[index]);
            if (rank > mine || (rank == mine && SwiftParse.StandardCompare(channels[index].Name, channel.Name) > 0)) return index;
        }
        return channels.Count;
    }

    private static int CommonPrefix(IReadOnlyList<List<string>> names)
    {
        if (names.Count == 0) return 0;
        var first = names[0];
        var shortest = names.Min(n => n.Count);
        if (shortest <= 1) return 0;
        var count = 0;
        while (count < shortest - 1 && names.All(n => n[count] == first[count])) count++;
        return count;
    }

    // MARK: Placing stems

    /// <summary>Plans, resolves with the defaults, and places in one go; returns the channels it made.</summary>
    public static List<TrackChannel> Place(
        IReadOnlyList<TimelineStemImport> stems, double at, IReadOnlyList<TrackChannel> channels, List<TimelineCue> into,
        string? channel = null, Func<string>? newId = null)
    {
        var resolution = Resolve(Plan(stems, channels, channel, newId));
        Place(stems, at, resolution.Assignments, into, newId);
        return resolution.Made;
    }

    /// <summary>A file the import would make a new channel for, and that channel.</summary>
    public sealed record UnmatchedStem(string FileName, TrackChannel Proposed);

    /// <summary>What an import will do before it is confirmed: files already on channels, and files needing new ones.</summary>
    public sealed record StemPlan(Dictionary<string, string> Assignments, List<UnmatchedStem> Unmatched);

    /// <summary>
    /// Plans an import. A single file dropped on a row goes on that row; a stem that remembers
    /// its channel keeps it; the rest are matched by name.
    /// </summary>
    public static StemPlan Plan(
        IReadOnlyList<TimelineStemImport> stems, IReadOnlyList<TrackChannel> channels, string? channel = null, Func<string>? newId = null)
    {
        if (channel is not null && stems.Count == 1) return new StemPlan(new() { [stems[0].FileName] = channel }, []);
        var known = channels.Select(c => c.Id).ToHashSet();
        var kept = new Dictionary<string, string>();
        foreach (var stem in stems)
            if (stem.ChannelId is { } id && known.Contains(id)) kept[stem.FileName] = id;
        var result = Assign(
            stems.Select(s => s.FileName).Where(f => !kept.ContainsKey(f)).ToList(), channels,
            kept.Values.ToHashSet(), newId);
        var assignments = new Dictionary<string, string>(kept);
        var unmatched = new List<UnmatchedStem>();
        foreach (var stem in stems.Where(s => !kept.ContainsKey(s.FileName)))
        {
            if (!result.Assignments.TryGetValue(stem.FileName, out var id)) continue;
            if (known.Contains(id))
            {
                assignments[stem.FileName] = id;
            }
            else if (result.Channels.FirstOrDefault(c => c.Id == id) is { } proposed)
            {
                var made = proposed.Copy();
                if (stem.Channels == 1) made.Mono = true;
                unmatched.Add(new UnmatchedStem(stem.FileName, made));
            }
        }
        return new StemPlan(assignments, unmatched);
    }

    /// <summary>The import sheet's answer for an unmatched file: a new channel with this name, or an existing channel.</summary>
    public abstract record StemChoice
    {
        private StemChoice() { }

        public sealed record New(string Name) : StemChoice;

        public sealed record Existing(string ChannelId) : StemChoice;
    }

    /// <summary>
    /// The confirmed import: where every file goes, the channels to make, and the file names
    /// each existing channel should remember for next time.
    /// </summary>
    public sealed record StemResolution(Dictionary<string, string> Assignments, List<TrackChannel> Made, Dictionary<string, List<string>> Remembered);

    public static StemResolution Resolve(StemPlan plan, IReadOnlyDictionary<string, StemChoice>? choices = null, bool remember = true)
    {
        var resolution = new StemResolution(new(plan.Assignments), [], []);
        foreach (var file in plan.Unmatched)
        {
            var choice = choices?.GetValueOrDefault(file.FileName) ?? new StemChoice.New(file.Proposed.Name);
            switch (choice)
            {
                case StemChoice.Existing existing:
                    resolution.Assignments[file.FileName] = existing.ChannelId;
                    if (remember)
                    {
                        if (!resolution.Remembered.TryGetValue(existing.ChannelId, out var names)) resolution.Remembered[existing.ChannelId] = names = [];
                        names.Add(file.Proposed.Name);
                    }
                    break;
                case StemChoice.New fresh:
                {
                    var made = file.Proposed.Copy();
                    var trimmed = fresh.Name.TrimmingWhitespacesAndNewlines();
                    if (trimmed.Length > 0 && trimmed != made.Name)
                    {
                        made.Name = trimmed;
                        var role = RoleForName(trimmed);
                        made.Role = role == Role.Music ? null : role.Raw();
                    }
                    resolution.Made.Add(made);
                    resolution.Assignments[file.FileName] = made.Id;
                    break;
                }
            }
        }
        return resolution;
    }

    /// <summary>The channel list with an import's new channels added and its remembered names stored.</summary>
    public static List<TrackChannel> Adding(StemResolution resolution, IReadOnlyList<TrackChannel> to)
    {
        var channels = to.ToList();
        foreach (var made in resolution.Made)
            if (!channels.Any(c => c.Id == made.Id)) channels.Insert(InsertionIndex(made, channels), made);
        foreach (var (id, names) in resolution.Remembered)
        {
            var index = channels.FindIndex(c => c.Id == id);
            if (index < 0) continue;
            var channel = channels[index].Copy();
            var matchNames = channel.MatchNames ?? [];
            foreach (var name in names)
                if (!matchNames.Any(existing => SwiftString.CaseInsensitiveEquals(existing, name))) matchNames.Add(name);
            channel.MatchNames = matchNames;
            channels[index] = channel;
        }
        return channels;
    }

    /// <summary>Lays the files' stems on their channels at a time, replacing any stem a channel already had there.</summary>
    public static void Place(
        IReadOnlyList<TimelineStemImport> stems, double at, IReadOnlyDictionary<string, string> assignments, List<TimelineCue> into,
        Func<string>? newId = null)
    {
        at = PresentationTimelines.SnappedToFrame(Math.Max(0, at));
        foreach (var stem in stems)
        {
            if (!assignments.TryGetValue(stem.FileName, out var id)) continue;
            var lane = Lane(id);
            into.RemoveAll(c => c.Lane == lane && Math.Abs(c.At - at) < PresentationTimelines.Frame / 2);
            into.Add(new TimelineCue { Id = (newId ?? TimelineClips.NewId)(), Lane = lane, At = at, Duration = stem.Seconds, MediaId = stem.AudioId });
        }
    }

    // MARK: Families

    /// <summary>A mixer family: each gets a bus, and the one-click patch gives each an output.</summary>
    public enum Family { Click, Guide, Drums, Percussion, Bass, ElectricGuitars, AcousticGuitars, Keys, Synths, Vocals, Strings, Other }

    public static readonly IReadOnlyList<Family> Families = Enum.GetValues<Family>();

    public static string Name(this Family family) => family switch
    {
        Family.ElectricGuitars => "Electric Guitars",
        Family.AcousticGuitars => "Acoustic Guitars",
        Family.Synths => "Synths & Pads",
        _ => family.ToString(),
    };

    public static string ColorName(this Family family) => family switch
    {
        Family.Click or Family.Guide => "Orange",
        Family.Drums or Family.Percussion => "Red",
        Family.Bass => "Purple",
        Family.ElectricGuitars => "Yellow",
        Family.AcousticGuitars => "Green",
        Family.Keys => "Blue",
        Family.Synths or Family.Strings => "Teal",
        Family.Vocals => "Pink",
        _ => "Gray",
    };

    private static readonly (Family Family, string[][] Phrases)[] FamilyWords =
    [
        (Family.Keys, [["electric", "piano"], ["keys"], ["key"], ["piano"], ["organ"], ["rhodes"], ["wurli"],
            ["wurlitzer"], ["mellotron"], ["ep"], ["b3"], ["clav"]]),
        (Family.Drums, [["drums"], ["drum"], ["kit"], ["kick"], ["snare"], ["toms"], ["overheads"]]),
        (Family.Percussion, [["percussion"], ["perc"], ["percs"], ["shaker"], ["tambourine"], ["tamb"], ["loops"],
            ["loop"], ["conga"], ["congas"]]),
        (Family.ElectricGuitars, [["electric", "guitar"], ["lead", "guitar"], ["eg"], ["egtr"], ["electric"],
            ["guitar"], ["gtr"]]),
        (Family.AcousticGuitars, [["acoustic", "guitar"], ["acoustic"], ["ag"], ["agtr"]]),
        (Family.Synths, [["synth"], ["synths"], ["pad"], ["pads"], ["lead"], ["arp"], ["atmos"], ["ambient"], ["swell"]]),
        (Family.Vocals, [["lead", "vocal"], ["lead", "vocals"], ["background", "vocals"], ["vocals"], ["vocal"],
            ["vox"], ["bgv"], ["bgvs"], ["choir"]]),
        (Family.Strings, [["strings"], ["string"], ["orchestra"], ["orch"], ["brass"], ["horns"], ["cello"], ["violin"]]),
    ];

    /// <summary>
    /// A channel's family: click and guide by role, anything with "bass" is bass, else the
    /// family whose word comes first in the name (the longer phrase wins a tie).
    /// </summary>
    public static Family FamilyOf(TrackChannel channel)
    {
        switch (channel.TrackRole())
        {
            case Role.Click: return Family.Click;
            case Role.Guide: return Family.Guide;
        }
        var words = Numbered(channel.Name).Words;
        if (words.Contains("bass")) return Family.Bass;
        (Family Family, int At, int Length)? best = null;
        foreach (var (family, phrases) in FamilyWords)
        {
            foreach (var phrase in phrases)
            {
                if (Position(phrase, words) is not { } at) continue;
                if (best is not { } current || at < current.At || (at == current.At && phrase.Length > current.Length))
                    best = (family, at, phrase.Length);
            }
        }
        return best?.Family ?? Family.Other;
    }

    private static int? Position(IReadOnlyList<string> phrase, IReadOnlyList<string> words)
    {
        if (phrase.Count == 0 || phrase.Count > words.Count) return null;
        for (var at = 0; at <= words.Count - phrase.Count; at++)
            if (words.Skip(at).Take(phrase.Count).SequenceEqual(phrase)) return at;
        return null;
    }

    /// <summary>The track rows a song shows: channels it has stems on or that were added to it, then stems on removed channels.</summary>
    public static List<TrackChannel> Rows(IReadOnlyList<TrackChannel> channels, IReadOnlyList<TimelineCue> cues, IReadOnlySet<string> added)
    {
        var used = Stems(cues).Select(c => c.TrackChannelId()!).ToHashSet();
        var shown = new HashSet<string>(used);
        shown.UnionWith(added);
        var lost = used.Except(channels.Select(c => c.Id)).Order(StringComparer.Ordinal).Select(id => new TrackChannel { Id = id, Name = "Removed channel" });
        return channels.Where(c => shown.Contains(c.Id)).Concat(lost).ToList();
    }

    // MARK: Mixer and outputs

    public static bool HasTracks(IReadOnlyList<TrackChannel> channels) => channels.Count > 0;

    /// <summary>Before a church has any tracks, the mixer hides the family buses (and folders left empty by that).</summary>
    public static (HashSet<string> Mixes, HashSet<string> Folders) ShownMixes(
        IReadOnlyList<(string Id, string? FolderId)> mixes, IReadOnlyList<string> folders, IReadOnlySet<string> buses, bool hasTracks)
    {
        if (hasTracks) return (mixes.Select(m => m.Id).ToHashSet(), folders.ToHashSet());
        var shown = mixes.Where(m => !buses.Contains(m.Id)).ToList();
        var kept = folders.Where(folder =>
        {
            var members = mixes.Where(m => m.FolderId == folder).ToList();
            return members.Count == 0 || shown.Any(m => m.FolderId == folder);
        });
        return (shown.Select(m => m.Id).ToHashSet(), kept.ToHashSet());
    }

    /// <summary>The one-click patch: each family on the next output from <paramref name="start"/>, while there are outputs.</summary>
    public static List<(Family Family, int Channel)> PatchPlan(int start, int deviceChannels, IReadOnlyList<Family>? families = null) =>
        (families ?? Families).Select((family, index) => (family, channel: start + index))
            .Where(p => p.channel >= 0 && p.channel < deviceChannels).Select(p => (p.family, p.channel)).ToList();

    /// <summary>The interface sizes a standard output layout exists for.</summary>
    public static readonly IReadOnlyList<int> LayoutSizes = [1, 2, 3, 4, 8, 16, 22];

    /// <summary>The largest standard layout that fits the outputs a device has.</summary>
    public static Dictionary<Family, int[]> LayoutFitting(int outputs) => Layout(LayoutSizes.LastOrDefault(size => size <= outputs));

    /// <summary>Which outputs each family plays from, for a standard interface size (empty for other sizes).</summary>
    public static Dictionary<Family, int[]> Layout(int outputs)
    {
        var music = Families.Where(f => f != Family.Click && f != Family.Guide).ToList();
        Dictionary<Family, int[]> With(params (Family, int[])[] lead)
        {
            var layout = lead.ToDictionary(p => p.Item1, p => p.Item2);
            return layout;
        }
        switch (outputs)
        {
            case 1:
                return music.ToDictionary(f => f, _ => new[] { 0 });
            case 2:
            {
                var layout = With((Family.Click, [0]), (Family.Guide, [0]));
                foreach (var f in music) layout[f] = [1];
                return layout;
            }
            case 3:
            {
                var layout = With((Family.Click, [0]), (Family.Guide, [0]));
                foreach (var f in music) layout[f] = [1, 2];
                return layout;
            }
            case 4:
            {
                var layout = With((Family.Click, [0]), (Family.Guide, [1]));
                foreach (var f in music) layout[f] = [2, 3];
                return layout;
            }
            case 8:
                return new()
                {
                    [Family.Click] = [0], [Family.Guide] = [1], [Family.Drums] = [2], [Family.Percussion] = [2], [Family.Bass] = [3],
                    [Family.ElectricGuitars] = [4], [Family.AcousticGuitars] = [4], [Family.Keys] = [5], [Family.Synths] = [6, 7],
                    [Family.Vocals] = [6, 7], [Family.Strings] = [6, 7], [Family.Other] = [6, 7],
                };
            case 16:
                return new()
                {
                    [Family.Click] = [0], [Family.Guide] = [1], [Family.Drums] = [2], [Family.Percussion] = [3], [Family.Bass] = [4],
                    [Family.ElectricGuitars] = [5], [Family.AcousticGuitars] = [6], [Family.Keys] = [7, 8], [Family.Synths] = [9, 10],
                    [Family.Vocals] = [11], [Family.Strings] = [12, 13], [Family.Other] = [14],
                };
            case 22:
            {
                var layout = With((Family.Click, [0]), (Family.Guide, [1]));
                for (var index = 0; index < music.Count; index++) layout[music[index]] = [2 + 2 * index, 3 + 2 * index];
                return layout;
            }
            default:
                return [];
        }
    }
}
