using System.Globalization;
using System.IO.Compression;
using System.Text;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Automation;

// Ported from PresenterCoreTests/SongMapsTests.swift (the real-.als test, gated on
// MXU_REAL_ALS, is ported with the same gate).
public class SongMapsTests
{
    private static readonly (double Beats, string Name)[] RunToTheFather =
    [
        (354, "Count Off"), (0, "Count Off"), (6, "Intro"), (18, "Intro"), (30, "Verse 1"), (54, "Verse 2"), (78, "Chorus"),
        (102, "Refrain"), (114, "Verse 3"), (138, "Verse 4"), (162, "Chorus"), (186, "Refrain"), (198, "Refrain"),
        (210, "Bridge 1"), (234, "Bridge 2"), (258, "Chorus"), (282, "Chorus"), (306, "Refrain"), (318, "Refrain"),
        (330, "Outro"), (342, "Ending"),
    ];

    private static string D(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static byte[] LiveSet((double Beats, string Name)[] locators)
    {
        (double, double)[] tempo =
        [
            (-63_072_000.0, 68.5), (336, 68.5), (336, 65.3538666), (337.5, 65.3538666), (337.5, 61.7001839),
            (339, 61.7001839), (339, 52.5598602), (340.5, 52.5598602), (340.5, 37.4808044), (342, 37.4808044), (342, 68.4999924),
        ];
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Ableton><LiveSet><MasterTrack><DeviceChain><Mixer><Tempo><LomId Value="0" /><Manual Value="68.5" />
            <ArrangerAutomation><Events>{string.Concat(tempo.Select(t => $"<FloatEvent Time=\"{D(t.Item1)}\" Value=\"{D(t.Item2)}\" />"))}</Events></ArrangerAutomation>
            </Tempo></Mixer></DeviceChain></MasterTrack>
            <Locators><Locators>{string.Concat(locators.Select(l => $"<Locator><LomId Value=\"0\" /><Time Value=\"{D(l.Beats)}\" /><Name Value=\"{l.Name}\" /></Locator>"))}</Locators></Locators>
            </LiveSet></Ableton>
            """;
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(xml));
        return output.ToArray();
    }

    [Fact]
    public void AMultiTracksSetGivesItsSectionsInSongSeconds()
    {
        var set = SongMaps.ReadAbletonSet(LiveSet(RunToTheFather))!;
        Assert.Equal(21, set.Locators.Count);
        Assert.Equal(0, set.Locators[0].Beats);
        var sections = SongMaps.Sections(set, 68.5, recordingEnd: 309.14, newId: Make.Ids("a"));
        Assert.Equal(20, sections.Count);
        Assert.Equal(["Count Off", "Intro", "Intro", "Verse 1", "Verse 2", "Chorus"], sections.Take(6).Select(s => s.Name));
        Assert.True(Math.Abs(sections[3].Start - 26.277) < 0.001);
        Assert.True(Math.Abs(sections[18].Start - 289.051) < 0.001);
        Assert.True(Math.Abs(sections[19].Start - 301.256) < 0.001);
    }

    [Fact]
    public void TempoRampsAndStepsAndTheFolderNamesTempo()
    {
        List<SongMaps.TempoPoint> ramp = [new(0, 60), new(4, 120)];
        Assert.True(Math.Abs(SongMaps.Seconds(4, ramp, 100) - 4 * Math.Log(2.0)) < 1e-9);
        Assert.True(Math.Abs(SongMaps.Seconds(6, ramp, 100) - (4 * Math.Log(2.0) + 1)) < 1e-9);
        Assert.Equal(4, SongMaps.Seconds(8, [], 120));
        Assert.Equal(68.5, SongMaps.Bpm("Run To The Father-Run to the Father-C-68.50bpm"));
        Assert.Null(SongMaps.Bpm("Way Maker"));
        Assert.Null(SongMaps.ReadAbletonSet(Encoding.UTF8.GetBytes("not a set")));
    }

    // C#: the Mac reads a set's raw deflate stream and checks only its size, as here.
    [Fact]
    public void APlainXmlSetReadsAndABrokenGzipDoesNot()
    {
        var gz = LiveSet([(0, "Intro")]);
        var plain = SongMaps.Gunzip(gz)!;
        Assert.Single(SongMaps.ReadAbletonSet(plain)!.Locators);
        gz[^1] ^= 0x7F;
        Assert.Null(SongMaps.ReadAbletonSet(gz));
        Assert.Equal([new SongMaps.TempoPoint(0, 72)],
            SongMaps.ReadAbletonSet(Encoding.UTF8.GetBytes("<Ableton><MasterTrack><Tempo><Manual Value=\"72\"/></Tempo></MasterTrack></Ableton>"))!.Tempo);
    }

    [Fact]
    public void SectionsLinkToSlideSectionsByName()
    {
        List<PresentationSection> slides = [new() { Id = "v1", Name = "Verse 1" }, new() { Id = "c", Name = "Chorus" }, new() { Id = "b1", Name = "Bridge" }];
        var linked = SongMaps.Linked(
        [
            new() { Id = "1", Name = "VERSE 1", Start = 0 }, new() { Id = "2", Name = "Chorus", Start = 10 },
            new() { Id = "3", Name = "Bridge 1", Start = 20 }, new() { Id = "4", Name = "Count Off", Start = 30 },
            new() { Id = "5", Name = "Chorus", Start = 40, SectionId = "v1" },
        ], slides);
        Assert.Equal(["v1", "c", "b1", null, "v1"], linked.Select(s => s.SectionId));
    }

    private static SongMap Song()
    {
        var stem = Make.Cue("stem", TimelineTracks.Lane("keys"), 0, 50, "keys");
        var slides = new (double At, string Section, int Occurrence)[] { (4.0, "v", 0), (14, "c", 0), (24, "v", 1), (34, "c", 1) }
            .Select(s => Make.Cue($"s{s.At.ToString(CultureInfo.InvariantCulture)}", TimelineLane.Slide.Raw(), s.At, sectionId: s.Section,
                sectionOccurrence: s.Occurrence == 0 ? null : s.Occurrence, slideId: $"{s.Section}-1")).ToList();
        var sections = new (string Id, string Name, double Start, string? Section)[]
        {
            ("i", "Intro", 0.0, null), ("v1", "Verse", 4, "v"), ("c1", "Chorus", 14, "c"), ("v2", "Verse", 24, "v"),
            ("c2", "Chorus", 34, "c"), ("c3", "Chorus", 39, "c"), ("e", "Ending", 44, null),
        }.Select(s => new AudioSection { Id = s.Id, Name = s.Name, Start = s.Start, SectionId = s.Section }).ToList();
        return new SongMap { Recording = [stem, .. slides], Sections = sections };
    }

    private static List<TimelinePlayedBlock> Blocks(params string[] order) => order.Select(id => new TimelinePlayedBlock(id, [$"{id}-1"])).ToList();

    private static PresentationTimeline Empty() => Make.Timeline("t");

    [Fact]
    public void TheRecordingsOwnOrderLaysOutAsRecorded()
    {
        var map = Song();
        var layout = SongMaps.Layout(map, Blocks("v", "c", "v", "c"));
        Assert.Equal([["i"], ["v1"], ["c1"], ["v2"], ["c2", "c3", "e"]], layout.Select(b => b.AudioSectionIds));
        Assert.Equal([0.0, 4, 14, 24, 34], layout.Select(b => b.Start));
        var laid = SongMaps.LaidOut(map, Blocks("v", "c", "v", "c"), Empty(), Make.Ids("a"))!;
        var stems = TimelineTracks.Stems(laid.Cues);
        Assert.Equal([0.0, 4, 14, 24, 34], stems.Select(s => s.At));
        Assert.Equal([0.0, 4, 14, 24, 34], stems.Select(s => s.InPoint ?? 0));
        Assert.Equal((false, false), TimelineClips.Edges(stems[2], laid.Cues));
        Assert.Equal([4.0, 14, 24, 34], laid.Cues.Where(c => c.Kind() == TimelineLane.Slide).Select(c => c.At));

        var unlinked = map.Copy();
        unlinked.Sections = unlinked.Sections.Select(s =>
        {
            var copy = s.Copy();
            copy.SectionId = null;
            return copy;
        }).ToList();
        Assert.Null(SongMaps.LaidOut(unlinked, Blocks("v"), Empty()));
        Assert.Null(SongMaps.LaidOut(map, Blocks("v", "c", "v", "c"), laid));
    }

    [Fact]
    public void AnArrangementPlaysItsSectionsAudioInItsOrder()
    {
        var map = Song();
        var order = Blocks("v", "c", "c");
        var layout = SongMaps.Layout(map, order);
        Assert.Equal([["i"], ["v1"], ["c1"], ["c2", "c3", "e"]], layout.Select(b => b.AudioSectionIds));
        Assert.Equal([0.0, 4, 14, 24], layout.Select(b => b.Start));
        var laid = SongMaps.LaidOut(map, order, Empty(), Make.Ids("a"))!;
        var stems = TimelineTracks.Stems(laid.Cues);
        Assert.Equal([0.0, 4, 14, 24], stems.Select(s => s.At));
        Assert.True(stems[^1].InPoint == 34 && stems[^1].Duration == 16);
        Assert.True(TimelineClips.Edges(stems[3], laid.Cues).Start);

        var slides = laid.Cues.Where(c => c.Kind() == TimelineLane.Slide).ToList();
        Assert.Equal([4.0, 14, 24], slides.Select(c => c.At));
        Assert.Equal(1, slides[^1].SectionOccurrence);
    }

    [Fact]
    public void EditsStayWithTheirBlockWhenTheOrderChanges()
    {
        var map = Song();
        var laid = SongMaps.LaidOut(map, Blocks("v", "c", "v", "c"), Empty(), Make.Ids("a"))!;
        var chorus = laid.Cues.FindIndex(c => c.TrackChannelId() is not null && c.At == 14);
        laid.Cues[chorus].FadeOut = Make.Fade(2);

        var relaid = SongMaps.LaidOut(map, Blocks("v", "c", "c"), laid, Make.Ids("b"))!;
        var stems = TimelineTracks.Stems(relaid.Cues);
        Assert.Equal(2, stems.First(s => s.At == 14).FadeOut?.Seconds);
        Assert.Equal([0.0, 4, 14, 24], stems.Select(s => s.At));
    }

    [Fact]
    public void TheArrangementEditorNamesSectionsWithNoAudio()
    {
        var map = Song();
        var names = new Dictionary<string, string> { ["v"] = "Verse", ["c"] = "Chorus", ["b"] = "Bridge" };
        Assert.Empty(SongMaps.Missing(map, Blocks("v", "c", "v", "c"), names));
        Assert.Equal(["Bridge", "Chorus (4th time)"], SongMaps.Missing(map, Blocks("v", "c", "b", "c", "c", "c"), names));
        var missing = SongMaps.Layout(map, Blocks("b"));
        Assert.True(missing[^1].Length == 0 && missing[^1].AudioSectionIds.Count == 0);
    }

    [Fact]
    public void RunToTheFatherReadsAsTwentyOneLocators()
    {
        if (Environment.GetEnvironmentVariable("MXU_REAL_ALS") is not { } path) return;
        var set = SongMaps.ReadAbletonSet(File.ReadAllBytes(path))!;
        Assert.Equal(21, set.Locators.Count);
        Assert.Equal(11, set.Tempo.Count);
        var sections = SongMaps.Sections(set, 68.5, recordingEnd: 309.139, newId: Make.Ids("a"));
        Assert.Equal(20, sections.Count);
        Assert.True(Math.Abs(sections[19].Start - 301.256) < 0.001);
    }

    [Fact]
    public void AMarkerAtThePlayheadLandsWhereItsAudioIs()
    {
        var map = Song();
        var layout = SongMaps.Layout(map, Blocks("v", "c", "c"));
        Assert.Equal(36, SongMaps.RecordingTime(26, map, layout));
        Assert.Equal(26, SongMaps.RecordingTime(26, map, null));
        var marked = SongMaps.Adding("Chorus", 36, map, [new() { Id = "c", Name = "Chorus" }], "new");
        Assert.Equal(["i", "v1", "c1", "v2", "c2", "new", "c3", "e"], marked.Sections.Select(s => s.Id));
        Assert.Equal("c", marked.Sections[5].SectionId);
        Assert.Equal(7, map.Sections.Count);
    }

    [Fact]
    public void OrdinalsReadAsWords()
    {
        Assert.Equal(["1st", "2nd", "3rd", "4th", "11th", "12th", "13th", "21st", "112th"],
            new[] { 1, 2, 3, 4, 11, 12, 13, 21, 112 }.Select(SongMaps.Ordinal));
    }
}
