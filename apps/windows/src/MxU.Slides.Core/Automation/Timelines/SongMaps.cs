using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// A song map: the sections of a multitrack recording (read from its Ableton Live set's
/// locators), linked to the presentation's slide sections, so an arrangement can lay the
/// recording out in its own order: verse audio under the verse slides, wherever they play.
/// Port of PresenterCore/Timeline/SongMaps.swift.
/// </summary>
public static partial class SongMaps
{
    /// <summary>A named marker in the Live set, in beats from the start.</summary>
    public sealed record Locator(double Beats, string Name);

    /// <summary>A tempo automation point: at this beat the tempo is this (ramping linearly to the next point).</summary>
    public sealed record TempoPoint(double Beat, double Bpm);

    public sealed record AbletonSet(IReadOnlyList<Locator> Locators, IReadOnlyList<TempoPoint> Tempo)
    {
        public bool Equals(AbletonSet? other) =>
            other is not null && Locators.SequenceEqual(other.Locators) && Tempo.SequenceEqual(other.Tempo);

        public override int GetHashCode() => HashCode.Combine(Locators.Count, Tempo.Count);
    }

    /// <summary>
    /// Reads a Live set (.als: gzipped XML, or plain XML): its locators and the master
    /// track's tempo automation (or its fixed tempo). Null when it isn't a Live set.
    /// </summary>
    public static AbletonSet? ReadAbletonSet(byte[] data)
    {
        var xml = data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B ? Gunzip(data) : data;
        if (xml is null) return null;
        var locators = new List<Locator>();
        var tempo = new List<TempoPoint>();
        double? manualTempo = null;
        var path = new List<string>();
        double? time = null;
        string? name = null;

        void Start(string element, Func<string, string?> attribute)
        {
            path.Add(element);
            var value = attribute("Value");
            if (EndsWith(path, "Locator", "Time")) time = SwiftParse.Double(value);
            if (EndsWith(path, "Locator", "Name")) name = value;
            if (path.Contains("MasterTrack") && path.Contains("Tempo"))
            {
                if (element == "FloatEvent" && SwiftParse.Double(attribute("Time")) is { } beat && SwiftParse.Double(attribute("Value")) is { } bpm)
                    tempo.Add(new TempoPoint(Math.Max(beat, 0), bpm));
                if (element == "Manual" && EndsWith(path, "Tempo", "Manual")) manualTempo = SwiftParse.Double(value);
            }
        }

        void End(string element)
        {
            if (element == "Locator" && time is { } t && name is { } n) locators.Add(new Locator(t, n));
            if (element == "Locator")
            {
                time = null;
                name = null;
            }
            path.RemoveAt(path.Count - 1);
        }

        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true };
            using var reader = XmlReader.Create(new MemoryStream(xml), settings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        var element = reader.Name;
                        var empty = reader.IsEmptyElement;
                        var r = reader;
                        Start(element, attribute => r.GetAttribute(attribute));
                        if (empty) End(element);
                        break;
                    }
                    case XmlNodeType.EndElement:
                        End(reader.Name);
                        break;
                }
            }
        }
        catch (XmlException)
        {
            return null;
        }
        var sortedTempo = tempo.OrderBy(p => p.Beat).ToList();
        if (sortedTempo.Count == 0 && manualTempo is { } manual) sortedTempo = [new TempoPoint(0, manual)];
        return new AbletonSet(locators.OrderBy(l => l.Beats).ToList(), sortedTempo);
    }

    private static bool EndsWith(List<string> path, string parent, string child) =>
        path.Count >= 2 && path[^2] == parent && path[^1] == child;

    /// <summary>
    /// Un-gzips a Live set the way the Mac does: the deflate stream is read raw and its size
    /// checked against the trailer (the CRC is not checked).
    /// </summary>
    internal static byte[]? Gunzip(byte[] bytes)
    {
        if (bytes.Length <= 18 || bytes[0] != 0x1F || bytes[1] != 0x8B || bytes[2] != 8) return null;
        var flags = bytes[3];
        var index = 10;
        if ((flags & 4) != 0) index += 2 + bytes[index] + (bytes[index + 1] << 8);
        if ((flags & 8) != 0)
        {
            while (index < bytes.Length && bytes[index] != 0) index++;
            index++;
        }
        if ((flags & 16) != 0)
        {
            while (index < bytes.Length && bytes[index] != 0) index++;
            index++;
        }
        if ((flags & 2) != 0) index += 2;
        if (index >= bytes.Length - 8) return null;
        var size = bytes[^4] | bytes[^3] << 8 | bytes[^2] << 16 | bytes[^1] << 24;
        try
        {
            using var stream = new DeflateStream(new MemoryStream(bytes, index, bytes.Length - 8 - index), CompressionMode.Decompress);
            using var output = new MemoryStream();
            stream.CopyTo(output);
            return output.Length == size ? output.ToArray() : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Beats to seconds through the tempo map (a ramp between points is integrated exactly);
    /// past the last point the last tempo holds. With no map, the folder's tempo.
    /// </summary>
    public static double Seconds(double beats, IReadOnlyList<TempoPoint> tempo, double bpm)
    {
        if (tempo.Count == 0) return beats * 60 / bpm;
        var seconds = 0.0;
        var beat = 0.0;
        var current = tempo[0].Bpm;
        foreach (var point in tempo)
        {
            if (point.Beat <= beat)
            {
                current = point.Bpm;
                continue;
            }
            var end = Math.Min(point.Beat, beats);
            seconds += Span(beat, current, end, current + (point.Bpm - current) * (end - beat) / (point.Beat - beat));
            if (point.Beat >= beats) return seconds;
            beat = point.Beat;
            current = point.Bpm;
        }
        current = tempo[^1].Bpm;
        return seconds + (beats - beat) * 60 / current;
    }

    private static double Span(double a, double v0, double b, double v1)
    {
        var beats = b - a;
        if (beats <= 0) return 0;
        if (Math.Abs(v1 - v0) < 1e-9) return beats * 60 / v0;
        return 60 * beats / (v1 - v0) * Math.Log(v1 / v0);
    }

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)\s*bpm")]
    private static partial Regex BpmPattern();

    /// <summary>The tempo in a MultiTracks folder name ("…-C-68.50bpm" → 68.5).</summary>
    public static double? Bpm(string folderName)
    {
        var match = BpmPattern().Match(folderName.ToLowerInvariant());
        return match.Success ? SwiftParse.Double(match.Groups[1].Value) : null;
    }

    /// <summary>
    /// The set's locators as audio sections in recording seconds. Locators at or past the
    /// recording's end, and duplicates at the same time, are dropped.
    /// </summary>
    public static List<AudioSection> Sections(
        AbletonSet set, double bpm, double offset = 0, double? recordingEnd = null, Func<string>? newId = null)
    {
        var sections = new List<AudioSection>();
        foreach (var locator in set.Locators)
        {
            var start = offset + Seconds(locator.Beats, set.Tempo, bpm);
            if (recordingEnd is { } end && start >= end - PresentationTimelines.Frame) continue;
            if (sections.Count > 0 && Math.Abs(sections[^1].Start - start) < 0.001) continue;
            sections.Add(new AudioSection { Id = (newId ?? TimelineClips.NewId)(), Name = locator.Name, Start = start });
        }
        return sections;
    }

    internal static string Key(string name) =>
        string.Join(' ', SwiftParse.Split(name.ToLowerInvariant(), c => !char.IsLetter(c) && !char.IsNumber(c)));

    [GeneratedRegex(@"\s*[0-9]+$")]
    private static partial Regex TrailingNumber();

    /// <summary>Links each unlinked audio section to the slide section of the same name ("Bridge 1" falls back to "Bridge").</summary>
    public static List<AudioSection> Linked(IReadOnlyList<AudioSection> sections, IReadOnlyList<PresentationSection> slideSections)
    {
        var byKey = new Dictionary<string, string>();
        foreach (var section in slideSections) byKey.TryAdd(Key(section.Name), section.Id);
        return sections.Select(section =>
        {
            if (section.SectionId is not null) return section;
            var linked = section.Copy();
            var name = Key(section.Name);
            linked.SectionId = byKey.GetValueOrDefault(name) ?? byKey.GetValueOrDefault(TrailingNumber().Replace(name, ""));
            return linked;
        }).ToList();
    }

    /// <summary>Where the recording ends: its last clip's end, else the last section's start.</summary>
    public static double RecordingEnd(SongMap map)
    {
        var ends = map.Recording.Where(TimelineClips.IsClip).Select(c => PresentationTimelines.End(c, map.Recording)).Where(double.IsFinite).ToList();
        return ends.Count > 0 ? ends.Max() : map.Sections.Count > 0 ? map.Sections[^1].Start : 0;
    }

    /// <summary>A played block and the audio sections (indices into the time-sorted sections) that play under it.</summary>
    public sealed record Assignment(string? SectionId, int Occurrence, IReadOnlyList<int> Audio);

    /// <summary>
    /// Which audio plays under each played block: the next unused recording of its section
    /// (searching on from the last one used, then from the top), plus any unlinked sections
    /// that follow it. Unlinked sections before the first linked one play first, as a lead-in.
    /// </summary>
    public static List<Assignment> Assignments(SongMap map, IReadOnlyList<TimelinePlayedBlock> blocks)
    {
        var sections = map.Sections.OrderBy(s => s.Start).ToList();
        var used = new HashSet<int>();
        var result = new List<Assignment>();
        var lead = Enumerable.Range(0, sections.TakeWhile(s => s.SectionId is null).Count()).ToList();
        if (lead.Count > 0 && lead.Count < sections.Count)
        {
            result.Add(new Assignment(null, 0, lead));
            used.UnionWith(lead);
        }
        var pointer = lead.Count;
        var seen = new Dictionary<string, int>();
        for (var index = 0; index < blocks.Count; index++)
        {
            if (blocks[index].SectionId is not { } id) continue;
            var occurrence = seen.GetValueOrDefault(id);
            seen[id] = occurrence + 1;
            bool Wanted(int i) => !used.Contains(i) && sections[i].SectionId == id;
            int? first = null;
            for (var i = pointer; i < sections.Count && first is null; i++) if (Wanted(i)) first = i;
            for (var i = 0; i < pointer && first is null; i++) if (Wanted(i)) first = i;
            if (first is not { } start)
            {
                result.Add(new Assignment(id, occurrence, []));
                continue;
            }
            var nextIsSame = index + 1 < blocks.Count && blocks[index + 1].SectionId == id;
            var taken = new List<int> { start };
            var next = start + 1;
            while (next < sections.Count && !used.Contains(next)
                   && (sections[next].SectionId is null || (sections[next].SectionId == id && !nextIsSame)))
            {
                taken.Add(next);
                next++;
            }
            used.UnionWith(taken);
            pointer = next;
            result.Add(new Assignment(id, occurrence, taken));
        }
        return result;
    }

    /// <summary>The map laid out for the played blocks: each block's start and length on the song's timeline.</summary>
    public static List<SongMapBlock> Layout(SongMap map, IReadOnlyList<TimelinePlayedBlock> blocks)
    {
        var sections = map.Sections.OrderBy(s => s.Start).ToList();
        var end = RecordingEnd(map);
        var cursor = 0.0;
        var layout = new List<SongMapBlock>();
        foreach (var block in Assignments(map, blocks))
        {
            var length = 0.0;
            if (block.Audio.Count > 0)
            {
                var spanStart = sections[block.Audio[0]].Start;
                var last = block.Audio[^1];
                var spanEnd = last + 1 < sections.Count ? sections[last + 1].Start : end;
                length = Math.Max(spanEnd - spanStart, 0);
            }
            layout.Add(new SongMapBlock
            {
                SectionId = block.SectionId,
                Occurrence = block.Occurrence,
                Start = cursor,
                Length = length,
                AudioSectionIds = block.Audio.Select(i => sections[i].Id).ToList(),
            });
            cursor += length;
        }
        return layout;
    }

    /// <summary>The arrangement editor's warning: the played sections with no audio ("Chorus (4th time)").</summary>
    public static List<string> Missing(SongMap map, IReadOnlyList<TimelinePlayedBlock> blocks, IReadOnlyDictionary<string, string> names)
    {
        var all = Assignments(map, blocks);
        var counts = all.GroupBy(a => a.SectionId ?? "").ToDictionary(g => g.Key, g => g.Count());
        return all.Where(a => a.Audio.Count == 0 && a.SectionId is not null).Select(block =>
        {
            var id = block.SectionId!;
            var name = names.GetValueOrDefault(id) ?? "A section";
            return counts.GetValueOrDefault(id, 1) > 1 ? $"{name} ({Ordinal(block.Occurrence + 1)} time)" : name;
        }).ToList();
    }

    internal static string Ordinal(int n)
    {
        var suffix = n % 100 is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    /// <summary>
    /// The timeline re-laid for the played blocks: each block gets a cut of the recording
    /// (stems, slide cues) moved to where it now plays. A block whose audio and length are
    /// unchanged keeps its cues, edits and all, just moved. Null when nothing changes or no
    /// section is linked.
    /// </summary>
    public static PresentationTimeline? LaidOut(
        SongMap map, IReadOnlyList<TimelinePlayedBlock> blocks, PresentationTimeline timeline, Func<string>? newId = null)
    {
        if (!map.Sections.Any(s => s.SectionId is not null)) return null;
        var layout = Layout(map, blocks);
        if (timeline.MapLayout is { } stored && MxU.Slides.Core.Json.MxuJson.StructurallyEqual(stored, layout)) return null;
        var sections = new Dictionary<string, AudioSection>();
        foreach (var section in map.Sections) sections.TryAdd(section.Id, section);
        var previous = timeline.MapLayout ?? [];
        var cues = new List<TimelineCue>();
        foreach (var block in layout)
        {
            var old = previous.FirstOrDefault(p =>
                p.SectionId == block.SectionId && p.Occurrence == block.Occurrence
                && p.AudioSectionIds.SequenceEqual(block.AudioSectionIds) && Math.Abs(p.Length - block.Length) < 0.0005);
            if (old is not null)
            {
                var shift = block.Start - old.Start;
                cues.AddRange(timeline.Cues
                    .Where(c => c.At >= old.Start - 0.0005 && c.At < old.Start + Math.Max(old.Length, 0.001) - 0.0005)
                    .Select(c =>
                    {
                        var moved = c.Copy();
                        moved.At += shift;
                        return moved;
                    }));
                continue;
            }
            if (block.AudioSectionIds.Count == 0 || !sections.TryGetValue(block.AudioSectionIds[0], out var first) || block.Length <= 0) continue;
            var range = new ClosedRange(first.Start, first.Start + block.Length);
            foreach (var cut in TimelineClips.Copied(map.Recording, map.Recording, range))
            {
                var cue = cut;
                if (!cue.IsBlock() && cue.At >= block.Length - 0.0005) continue;
                cue.Id = (newId ?? TimelineClips.NewId)();
                cue.At = PresentationTimelines.SnappedToFrameIfClose(cue.At + block.Start);
                if (cue.Kind() == TimelineLane.Slide && cue.SectionId == block.SectionId)
                    cue.SectionOccurrence = block.Occurrence == 0 ? null : block.Occurrence;
                cues.Add(cue);
            }
        }
        var oldEnd = previous.Count > 0 ? previous[^1].Start + previous[^1].Length : double.PositiveInfinity;
        var newEnd = layout.Count > 0 ? layout[^1].Start + layout[^1].Length : 0;
        cues.AddRange(timeline.Cues.Where(c => c.At >= oldEnd).Select(c =>
        {
            var moved = c.Copy();
            moved.At += newEnd - oldEnd;
            return moved;
        }));
        var laid = timeline.Copy();
        laid.Cues = cues.OrderBy(c => c.At).ToList();
        laid.MapLayout = layout;
        laid.Length = null;
        return laid;
    }

    /// <summary>Where a time on the laid-out timeline is in the recording (for dropping a marker there).</summary>
    public static double RecordingTime(double time, SongMap map, IReadOnlyList<SongMapBlock>? layout)
    {
        if (layout is null || layout.Count == 0) return time;
        var starts = new Dictionary<string, double>();
        foreach (var section in map.Sections) starts.TryAdd(section.Id, section.Start);
        var block = layout.LastOrDefault(b => b.Start <= time && b.Length > 0) ?? layout.FirstOrDefault(b => b.Length > 0);
        if (block is null || block.AudioSectionIds.Count == 0 || !starts.TryGetValue(block.AudioSectionIds[0], out var start)) return time;
        return start + Math.Min(Math.Max(time - block.Start, 0), block.Length);
    }

    /// <summary>The map with a new section marker (linked by name when it can be).</summary>
    public static SongMap Adding(string name, double start, SongMap map, IReadOnlyList<PresentationSection> slideSections, string? id = null)
    {
        var added = map.Copy();
        added.Sections.AddRange(Linked([new AudioSection { Id = id ?? TimelineClips.NewId(), Name = name, Start = Math.Max(0, start) }], slideSections));
        added.Sections = added.Sections.OrderBy(s => s.Start).ToList();
        return added;
    }
}
