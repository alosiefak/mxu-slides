using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using F = MxU.Slides.Core.Automation.TimelineTracks.Family;

namespace MxU.Slides.Core.Tests.Automation;

// Ported from PresenterCoreTests/TimelineTracksTests.swift, plus the three tests in
// TrackChannelsTests.swift that exercise TimelineTracks.swift (plan, resolve, adding, channel).
public class TimelineTracksTests
{
    private static TimelineCue Stem(string channel, double at = 0, double duration = 180, string? id = null) =>
        Make.Cue(id ?? Guid.NewGuid().ToString(), TimelineTracks.Lane(channel), at, duration, "audio-" + channel);

    private static TrackChannel Channel(string id, string name, string? role = null, List<string>? matchNames = null) =>
        new() { Id = id, Name = name, Role = role, MatchNames = matchNames };

    [Fact]
    public void DroppedStemsLandOnTheChannelsTheirNamesSay()
    {
        List<TrackChannel> channels =
        [
            Channel("click", "Click", "click"), Channel("keys", "Keys"), Channel("eg", "Electric Guitar"), Channel("loop", "Loop Pad", matchNames: ["Atmos"]),
        ];
        string? On(string file) => TimelineTracks.Channel(file, channels)?.Id;
        Assert.Equal("click", On("Click.wav"));
        Assert.Equal("keys", On("01 Keys 1.wav"));
        Assert.Null(On("Piano.m4a"));
        Assert.Equal("eg", On("EG.wav"));
        Assert.Null(On("EG 2.wav"));
        Assert.Equal("loop", On("Atmos.wav"));
        Assert.Equal("click", On("Metronome.aif"));
        Assert.Null(On("Bass.wav"));
    }

    [Fact]
    public void TheFirstDropBuildsTheChannelsWithClickAndGuideMarked()
    {
        var result = TimelineTracks.Assign(
            ["Click Track.wav", "Cues.wav", "Drums.wav", "Keys.wav", "Piano.wav", "Strange Noise.wav"], [], newId: Make.Ids("c"));
        Assert.Equal(["Click", "Guide", "Drums", "Keys", "Piano", "Strange Noise"], result.Channels.Select(c => c.Name));
        Assert.Equal(
            [TimelineTracks.Role.Click, TimelineTracks.Role.Guide, TimelineTracks.Role.Music, TimelineTracks.Role.Music, TimelineTracks.Role.Music, TimelineTracks.Role.Music],
            result.Channels.Select(c => c.TrackRole()));
        Assert.NotEqual(result.Assignments["Keys.wav"], result.Assignments["Piano.wav"]);
        Assert.Equal(TimelineTracks.Role.Click, TimelineTracks.RoleForName("Click"));
        Assert.Equal(TimelineTracks.Role.Guide, TimelineTracks.RoleForName("Vocal Cues"));
        Assert.Equal(TimelineTracks.Role.Click, TimelineTracks.RoleForName("Drum Click"));
        Assert.Equal("Electric Guitar Lead", TimelineTracks.Canonical(["eg", "lead"]).Name);
    }

    [Fact]
    public void StemsAreBlocksThatMoveTogetherAndTrimWithTheirAudioInPlace()
    {
        var a = Stem("click", 2, 200);
        var b = Stem("keys", 2, 190);
        var late = Stem("pad", 30, 20);
        var slide = Make.Cue("s", TimelineLane.Slide.Raw(), 5);
        List<TimelineCue> cues = [a, b, late, slide];
        Assert.Equal([a.Id, b.Id, late.Id], TimelineTracks.Stems(cues).Select(c => c.Id));
        Assert.Equal(new HashSet<string> { a.Id, b.Id }, TimelineTracks.Group(a, cues).Select(c => c.Id).ToHashSet());
        Assert.Equal(["s"], TimelineTracks.Group(slide, cues).Select(c => c.Id));
        Assert.Equal(202, PresentationTimelines.ContentEnd(cues));

        var trimmed = PresentationTimelines.Edited(a, cues, TimelineBlockEdit.Mode.Trim, 1);
        Assert.Equal((3.0, (double?)199, (double?)1), (trimmed.At, trimmed.Duration, trimmed.InPoint));
        Assert.Equal(2, PresentationTimelines.Edited(a, cues, TimelineBlockEdit.Mode.Trim, -1).At);
        Assert.Equal(190, PresentationTimelines.Edited(a, cues, TimelineBlockEdit.Mode.Stretch, -10).Duration);
        Assert.Null(a.Kind());
        Assert.Equal("click", a.TrackChannelId());
    }

    [Fact]
    public void PlacingStemsMakesChannelsAndSwapsASongsOldStems()
    {
        var newId = Make.Ids();
        var cues = new List<TimelineCue>();
        var keys = Channel("keys", "Keys");
        var made = TimelineTracks.Place([new("Click.wav", "a1", 200), new("Keys.wav", "a2", 190)], 1.001, [keys], cues, newId: newId);
        Assert.Equal(["Click"], made.Select(c => c.Name));
        Assert.Equal(TimelineTracks.Role.Click, made[0].TrackRole());
        Assert.Equal(2, cues.Count);
        Assert.All(cues, c => Assert.Equal(1, c.At));
        Assert.Equal("keys", cues.First(c => c.MediaId == "a2").TrackChannelId());

        TimelineTracks.Place([new("Keys.wav", "a3", 190)], 1, [keys, .. made], cues, newId: newId);
        Assert.Equal(2, cues.Count);
        Assert.True(cues.Any(c => c.MediaId == "a3") && !cues.Any(c => c.MediaId == "a2"));

        TimelineTracks.Place([new("Mystery.wav", "a4", 10)], 1, [keys, .. made], cues, channel: "keys", newId: newId);
        Assert.Equal("keys", cues.First(c => c.MediaId == "a4").TrackChannelId());
    }

    [Fact]
    public void EightGuitarsAreEightChannelsAndNoStemReplacesAnother()
    {
        var newId = Make.Ids("g");
        var guitars = Enumerable.Range(1, 8).Select(i => $"EG {i}.wav").ToList();
        var first = TimelineTracks.Assign(guitars, [], newId: newId);
        Assert.Equal(Enumerable.Range(1, 8).Select(i => $"Electric Guitar {i}"), first.Channels.Select(c => c.Name));
        Assert.Equal(8, first.Assignments.Values.Distinct().Count());

        var again = TimelineTracks.Assign(Enumerable.Reverse(guitars).ToList(), first.Channels, newId: newId);
        Assert.True(Make.Same(first.Channels, again.Channels));
        Assert.Equal(first.Assignments["EG 3.wav"], again.Assignments["EG 3.wav"]);

        Assert.Equal("Electric Guitar 2", TimelineTracks.Channel("EG2.aif", first.Channels)?.Name);
        List<TrackChannel> keys = [Channel("k", "Keys")];
        Assert.Equal("k", TimelineTracks.Channel("Keys 1.wav", keys)?.Id);
        Assert.Null(TimelineTracks.Channel("Keys 2.wav", keys));

        var pair = TimelineTracks.Assign(["EG.wav", "EG Lead.wav", "Keys.wav", "Piano.wav"], keys, newId: newId);
        Assert.Equal(["Electric Guitar", "Electric Guitar Lead", "Keys", "Piano"], pair.Channels.Select(c => c.Name));

        var twice = TimelineTracks.Assign(["EG.wav", "eg.WAV"], [], newId: newId);
        Assert.Equal(["Electric Guitar", "Electric Guitar 2"], twice.Channels.Select(c => c.Name));
        var cues = new List<TimelineCue>();
        TimelineTracks.Place(guitars.Select((name, i) => new TimelineStemImport(name, $"a{i}", 60)).ToList(), 0, first.Channels, cues, newId: newId);
        Assert.Equal(8, cues.Count);
    }

    [Fact]
    public void AChannelMadeFromMonoStemsStartsMono()
    {
        var cues = new List<TimelineCue>();
        var made = TimelineTracks.Place([new("Click.wav", "a", 10, 1), new("Keys.wav", "b", 10, 2), new("Bass.wav", "c", 10)], 0, [], cues);
        Assert.True(made.First(c => c.Name == "Click").Mono);
        Assert.Null(made.First(c => c.Name == "Keys").Mono);
        Assert.Null(made.First(c => c.Name == "Bass").Mono);
    }

    [Fact]
    public void ASongShowsOnlyTheTracksItHasStemsOnAndTheOnesAddedToIt()
    {
        var channels = Enumerable.Range(1, 10).Select(i => Channel($"eg{i}", $"Electric Guitar {i}")).Append(Channel("click", "Click", "click")).ToList();
        List<TimelineCue> cues = [Stem("eg2"), Stem("click"), Stem("gone"), Make.Cue("s", TimelineLane.Slide.Raw(), 0)];
        var rows = TimelineTracks.Rows(channels, cues, new HashSet<string> { "eg7" });
        Assert.Equal(["eg2", "eg7", "click", "gone"], rows.Select(r => r.Id));
        Assert.Equal("Removed channel", rows[^1].Name);
        Assert.Empty(TimelineTracks.Rows(channels, [], new HashSet<string>()));
    }

    [Fact]
    public void ARealMultiTracksSetBuildsChannelsNamedAfterItsStems()
    {
        var files = new[]
        {
            "AG", "BGVS", "Bass", "Click Track", "Drums", "EG 1", "EG 2", "EG 3", "EG 4", "Electric Piano",
            "Guide", "Keys 1", "Mellotron", "Organ", "Perc", "Piano", "Strings", "Synth Bass 2", "Synth Bass",
        }.Select(f => f + ".m4a").ToList();
        var result = TimelineTracks.Assign(files, []);
        Assert.Equal(
        [
            "Click", "Guide", "Drums", "Percussion", "Bass", "Synth Bass", "Synth Bass 2",
            "Electric Guitar 1", "Electric Guitar 2", "Electric Guitar 3", "Electric Guitar 4", "Acoustic Guitar",
            "Electric Piano", "Keys 1", "Mellotron", "Organ", "Piano", "Background Vocals", "Strings",
        ], result.Channels.Select(c => c.Name));
        Assert.Equal(files.Count, result.Assignments.Values.Distinct().Count());
        Assert.Equal(["Click", "Guide"], result.Channels.Where(c => c.TrackRole() != TimelineTracks.Role.Music).Select(c => c.Name));

        var next = TimelineTracks.Assign(["Way Maker - Click.wav", "Way Maker - EG 2.wav", "Way Maker - Keys.wav", "Way Maker - Bass.wav"], result.Channels);
        Assert.Equal(result.Channels.Count, next.Channels.Count);
        var byName = result.Channels.ToDictionary(c => c.Name, c => c.Id);
        Assert.Equal(byName["Click"], next.Assignments["Way Maker - Click.wav"]);
        Assert.Equal(byName["Electric Guitar 2"], next.Assignments["Way Maker - EG 2.wav"]);
        Assert.Equal(byName["Keys 1"], next.Assignments["Way Maker - Keys.wav"]);

        var moved = result.Channels.ToList();
        moved.Add(moved[0]);
        moved.RemoveAt(0);
        var pads = TimelineTracks.Assign(["Pads.wav"], moved);
        Assert.Equal("Click", pads.Channels[^1].Name);
        Assert.Equal(pads.Channels.FindIndex(c => c.Name == "Background Vocals") - 1, pads.Channels.FindIndex(c => c.Name == "Pads"));
    }

    [Fact]
    public void EveryChannelFeedsItsFamilysBus()
    {
        (string Name, string? Role, F Family)[] expected =
        [
            ("Acoustic Guitar", null, F.AcousticGuitars), ("Background Vocals", null, F.Vocals), ("Bass", null, F.Bass),
            ("Click", "click", F.Click), ("Drums", null, F.Drums), ("Electric Guitar 3", null, F.ElectricGuitars),
            ("Electric Piano", null, F.Keys), ("Guide", "guide", F.Guide), ("Keys 1", null, F.Keys),
            ("Mellotron", null, F.Keys), ("Organ", null, F.Keys), ("Percussion", null, F.Percussion), ("Piano", null, F.Keys),
            ("Strings", null, F.Strings), ("Synth Bass 2", null, F.Bass), ("Synth Bass", null, F.Bass),
            ("Lead Vocal", null, F.Vocals), ("Lead Guitar", null, F.ElectricGuitars), ("Pads", null, F.Synths),
            ("Track", null, F.Other),
        ];
        foreach (var (name, role, family) in expected)
            Assert.True(family == TimelineTracks.FamilyOf(Channel(name, name, role)), name);
        Assert.Equal("Orange", F.Click.ColorName());
        Assert.Equal(F.Percussion.ColorName(), F.Drums.ColorName());
        Assert.Equal("Synths & Pads", F.Synths.Name());
    }

    [Fact]
    public void TheMixerShowsTracksOnceTheChurchHasThem()
    {
        var channels = new List<TrackChannel>();
        Assert.False(TimelineTracks.HasTracks(channels));
        (string, string?)[] mixes = [("main", null), ("bus-keys", "tracks"), ("bus-click", "tracks"), ("stream", "mine"), ("lobby", null)];
        string[] folders = ["tracks", "mine", "empty"];
        var buses = new HashSet<string> { "bus-keys", "bus-click" };
        var before = TimelineTracks.ShownMixes(mixes, folders, buses, TimelineTracks.HasTracks(channels));
        Assert.Equal(new HashSet<string> { "main", "stream", "lobby" }, before.Mixes);
        Assert.Equal(new HashSet<string> { "mine", "empty" }, before.Folders);
        var cues = new List<TimelineCue>();
        channels = TimelineTracks.Place([new("Click.wav", "a", 9), new("Keys.wav", "b", 9)], 0, channels, cues);
        Assert.True(TimelineTracks.HasTracks(channels));
        var after = TimelineTracks.ShownMixes(mixes, folders, buses, TimelineTracks.HasTracks(channels));
        Assert.True(after.Mixes.Count == 5 && after.Folders.Count == 3);
    }

    [Fact]
    public void TheOneClickPatchLaysTheFamilyMixesAcrossTheInterface()
    {
        var sixteen = TimelineTracks.PatchPlan(0, 16);
        Assert.Equal(12, sixteen.Count);
        Assert.True(sixteen[0] == (F.Click, 0) && sixteen[1] == (F.Guide, 1) && sixteen[^1] == (F.Other, 11));

        var late = TimelineTracks.PatchPlan(8, 16);
        Assert.True(late.Count == 8 && late[^1].Channel == 15);

        var room = TimelineTracks.PatchPlan(2, 16, TimelineTracks.Families.Where(f => f is not (F.Click or F.Guide)).ToList());
        Assert.True(room[0] == (F.Drums, 2) && room.Count == 10);
    }

    [Fact]
    public void LayoutsByOutputCountPutClickAndGuideFirst()
    {
        var two = TimelineTracks.Layout(2);
        Assert.True(two[F.Click].SequenceEqual([0]) && two[F.Guide].SequenceEqual([0]));
        Assert.True(two[F.Drums].SequenceEqual([1]) && two[F.Other].SequenceEqual([1]));
        var three = TimelineTracks.Layout(3);
        Assert.True(three[F.Click].SequenceEqual([0]) && three[F.Keys].SequenceEqual([1, 2]));
        var four = TimelineTracks.Layout(4);
        Assert.True(four[F.Click].SequenceEqual([0]) && four[F.Guide].SequenceEqual([1]) && four[F.Bass].SequenceEqual([2, 3]));
        foreach (var size in TimelineTracks.LayoutSizes)
        {
            var layout = TimelineTracks.Layout(size);
            Assert.Equal(TimelineTracks.Families.Count - (size == 1 ? 2 : 0), layout.Count);
            Assert.All(layout.Values, outputs => Assert.All(outputs, output => Assert.True(output < size)));
        }
        static string Text(Dictionary<F, int[]> layout) =>
            string.Join(";", layout.OrderBy(p => p.Key).Select(p => $"{p.Key}={string.Join(",", p.Value)}"));
        Assert.Equal(Text(TimelineTracks.Layout(4)), Text(TimelineTracks.LayoutFitting(6)));
        Assert.Equal(Text(TimelineTracks.Layout(22)), Text(TimelineTracks.LayoutFitting(32)));
        Assert.Equal([20, 21], TimelineTracks.LayoutFitting(22)[F.Other]);
        Assert.Empty(TimelineTracks.Layout(0));
    }

    [Fact]
    public void SoloQuietsTheOtherMusicChannelsButNeverClickOrGuide()
    {
        List<TrackChannel> channels = [Channel("c", "Click", "click"), Channel("k", "Keys"), Channel("b", "Bass"), Channel("d", "Drums")];
        Assert.Empty(TimelineTracks.SilencedBySolo(channels, new HashSet<string>()));
        Assert.Equal(new HashSet<string> { "b", "d" }, TimelineTracks.SilencedBySolo(channels, new HashSet<string> { "k" }));
        Assert.Equal(new HashSet<string> { "d" }, TimelineTracks.SilencedBySolo(channels, new HashSet<string> { "k", "b" }));
        Assert.Empty(TimelineTracks.SilencedBySolo(channels, new HashSet<string> { "c" }));
    }

    [Fact]
    public void TheFoldedBarSpansEveryStemAndReachesThemAll()
    {
        var song = Enumerable.Range(0, 3).Select(i => Make.Cue($"s{i}", $"track:c{i}", 2, 300, $"a{i}")).ToList();
        var later = Make.Cue("late", "track:c9", 90, 250, "z");
        var slide = Make.Cue("slide", "slides", 5);
        List<TimelineCue> cues = [.. song, later, slide];
        var bar = TimelineTracks.Folded(TimelineTracks.Stems(cues), cues);
        Assert.True(bar?.At == 2 && bar.Duration == 338);
        Assert.Equal(new HashSet<string> { "s0", "s1", "s2", "late" }, TimelineTracks.Reach(bar!, cues));
        Assert.Equal(new HashSet<string> { "s0", "s1", "s2" }, TimelineTracks.Reach(song[0], cues));
        Assert.Equal(new HashSet<string> { "slide" }, TimelineTracks.Reach(slide, cues));
    }

    [Fact]
    public void ThePlayheadReadsLikeTheRuler()
    {
        Assert.Equal("2:38.7", PresentationTimelines.PlayheadClock(158.74));
        Assert.Equal("0:00.0", PresentationTimelines.PlayheadClock(0));
        Assert.Equal("59:59.9", PresentationTimelines.PlayheadClock(3599.99));
    }

    [Fact]
    public void ADropFindsItsRowWhateverTheHeights()
    {
        double[] heights = [26, 26, 60, 20];
        Assert.Equal(0, PresentationTimelines.Row(0, heights));
        Assert.Equal(1, PresentationTimelines.Row(30, heights));
        Assert.Equal(2, PresentationTimelines.Row(54, heights));
        Assert.Equal(2, PresentationTimelines.Row(114, heights));
        Assert.Equal(3, PresentationTimelines.Row(115, heights));
        Assert.Null(PresentationTimelines.Row(136, heights));
        Assert.Null(PresentationTimelines.Row(-1, heights));
    }

    // From TrackChannelsTests.swift: the import sheet's plan and resolution.
    private static readonly TrackChannel Keys = Channel("keys", "Keys");
    private static readonly TrackChannel Click = Channel("click", "Click", "click");

    [Fact]
    public void AnImportPlansWhatMatchedAndWhatWouldMakeAChannel()
    {
        var plan = TimelineTracks.Plan(
            [new("Click.wav", "a", 9), new("Piano.wav", "b", 9, 1), new("Bass.wav", "c", 9)], [Click, Keys], newId: Make.Ids());
        Assert.Equal(new Dictionary<string, string> { ["Click.wav"] = "click" }, plan.Assignments);
        Assert.Equal(["Piano.wav", "Bass.wav"], plan.Unmatched.Select(u => u.FileName));
        Assert.Equal(["Piano", "Bass"], plan.Unmatched.Select(u => u.Proposed.Name));
        Assert.True(plan.Unmatched[0].Proposed.Mono);

        var onRow = TimelineTracks.Plan([new("Mystery.wav", "d", 1)], [Keys], channel: "keys");
        Assert.Equal(new Dictionary<string, string> { ["Mystery.wav"] = "keys" }, onRow.Assignments);
        Assert.Empty(onRow.Unmatched);
    }

    [Fact]
    public void TheSheetsChoicesPutFilesOnChannelsAndRememberThem()
    {
        var plan = TimelineTracks.Plan(
            [new("Piano.wav", "a", 9), new("Organ.wav", "b", 9), new("Bells.wav", "c", 9)], [Click, Keys], newId: Make.Ids());
        var resolution = TimelineTracks.Resolve(plan, new Dictionary<string, TimelineTracks.StemChoice>
        {
            ["Piano.wav"] = new TimelineTracks.StemChoice.Existing("keys"),
            ["Organ.wav"] = new TimelineTracks.StemChoice.New("Count In"),
        });
        Assert.Equal("keys", resolution.Assignments["Piano.wav"]);
        Assert.Equal(["Count In", "Bells"], resolution.Made.Select(c => c.Name));
        Assert.Equal(TimelineTracks.Role.Click, resolution.Made[0].TrackRole());
        Assert.Equal(["Piano"], resolution.Remembered["keys"]);
        Assert.Single(resolution.Remembered);
        var list = TimelineTracks.Adding(resolution, [Click, Keys]);
        Assert.Equal(["Click", "Count In", "Keys", "Bells"], list.Select(c => c.Name));
        Assert.Equal(["Piano"], list.First(c => c.Id == "keys").MatchNames);
        Assert.Null(Keys.MatchNames);

        Assert.Equal("keys", TimelineTracks.Channel("Piano.wav", list)?.Id);
        Assert.Equal(["Piano"], TimelineTracks.Adding(resolution, list).First(c => c.Id == "keys").MatchNames);
        Assert.Empty(TimelineTracks.Resolve(plan, new Dictionary<string, TimelineTracks.StemChoice> { ["Piano.wav"] = new TimelineTracks.StemChoice.Existing("keys") }, remember: false).Remembered);
        Assert.Equal(["Piano", "Organ", "Bells"], TimelineTracks.Resolve(plan).Made.Select(c => c.Name));
    }

    [Fact]
    public void ARememberedNameLandsOnANumberedChannel()
    {
        var guitar = Channel("eg2", "Electric Guitar 2", matchNames: ["Lead Gtr"]);
        Assert.Equal("eg2", TimelineTracks.Channel("Lead Gtr.wav", [guitar])?.Id);
        Assert.Null(TimelineTracks.Channel("Lead Gtr 3.wav", [guitar]));
        var pads = Channel("pad", "Pads 2", matchNames: ["Atmos"]);
        Assert.Null(TimelineTracks.Channel("Big Atmos Swell.wav", [pads]));
    }
}
