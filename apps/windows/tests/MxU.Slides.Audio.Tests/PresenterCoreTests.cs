using MxU.Slides.Core.Automation;
using System.Globalization;
using MxU.Slides.Audio.Library;
using MxU.Slides.Audio.Timeline;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Audio.Tests;

/// <summary>Port of PresenterCore's MixerStripLayoutTests and PlaylistCrossfadeTests.</summary>
public sealed class MixerAndCrossfadeTests
{
    [Fact]
    public void MixerFaderHeightLeavesRoomForTheGrabBar()
    {
        Assert.Equal(262, MixerStripLayout.FaderHeight(500, 13));
        Assert.Equal(275, MixerStripLayout.FaderHeight(500, 0));
    }

    [Fact]
    public void MixerFaderHeightNeverDropsBelowClassicTravel()
    {
        Assert.Equal(92, MixerStripLayout.FaderHeight(300, 13));
        Assert.Equal(92, MixerStripLayout.FaderHeight(0, 13));
    }

    [Fact]
    public void AFoldCardStandsAsTallAsAStrip()
    {
        Assert.Equal(262 + 133, MixerStripLayout.StripHeight(500, 13));
        Assert.Equal(92 + 133, MixerStripLayout.StripHeight(0, 13));
        Assert.True(MixerStripLayout.StripChrome < MixerStripLayout.FixedChrome);
    }

    [Fact]
    public void MixerGrabBarSnapsNearMissesOntoTheBar()
    {
        Assert.Equal(8, MixerStripLayout.GrabBarY(8, 4, 17));
        Assert.Equal(16, MixerStripLayout.GrabBarY(19, 4, 17));
        Assert.Equal(5, MixerStripLayout.GrabBarY(1, 4, 17));
    }

    [Fact]
    public void CrossfadeOverrideBeatsRoomDefaultAndZeroMeansCut()
    {
        Assert.Equal(3, PlaylistCrossfade.Resolve(null, 3));
        Assert.Equal(0, PlaylistCrossfade.Resolve(0, 3));
        Assert.Equal(8, PlaylistCrossfade.Resolve(8, 3));
        Assert.Equal(0, PlaylistCrossfade.Resolve(null, -1));
        Assert.Null(PlaylistCrossfade.MigratedOverride(0));
        Assert.Equal(2, PlaylistCrossfade.MigratedOverride(2));
        Assert.Null(PlaylistCrossfade.MigratedOverride(null));
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("Off", PlaylistCrossfade.Label(0));
            Assert.Equal("3.5s", PlaylistCrossfade.Label(3.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void LegacyZeroCrossfadeMigratesToFollowTheRoom()
    {
        var root = Path.Combine(Path.GetTempPath(), "mxu-audio-tests", "library-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LibraryStore(root);
            store.Load();
            store.Save(new Playlist { Id = "legacy", Name = "Legacy", PlaybackMode = PlaybackMode.PlayAll, CrossfadeSeconds = 0 });
            store.Save(new Playlist { Id = "custom", Name = "Custom", PlaybackMode = PlaybackMode.PlayAll, CrossfadeSeconds = 2 });
            store.Save(new Playlist { Id = "fresh", Name = "Fresh", PlaybackMode = PlaybackMode.PlayAll });
            Assert.Equal(1, PlaylistCrossfade.MigrateCrossfadeDefaults(store));
            var reopened = new LibraryStore(root);
            reopened.Load();
            Assert.Null(reopened.Get<Playlist>("legacy")!.CrossfadeSeconds);
            Assert.Equal(2, reopened.Get<Playlist>("custom")!.CrossfadeSeconds);
            Assert.Null(reopened.Get<Playlist>("fresh")!.CrossfadeSeconds);
            Assert.Equal(0, PlaylistCrossfade.MigrateCrossfadeDefaults(reopened));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// Port of PresenterCore's TrackChannelsTests.swift. The naming rules (plan, resolve,
/// adding, matching) are Core's <see cref="TimelineTracks"/>; the list editing is
/// <see cref="TrackChannelEditing"/> here. Both are exercised, as the Mac test file does.
/// </summary>
public sealed class TrackChannelsTests
{
    private static TrackChannel Keys() => new() { Id = "keys", Name = "Keys" };

    private static TrackChannel Click() => new() { Id = "click", Name = "Click", Role = "click" };

    private static Func<string> Counter(string prefix = "n")
    {
        var ids = 0;
        return () => $"{prefix}{++ids}";
    }

    [Fact]
    public void AnImportPlansWhatMatchedAndWhatWouldMakeAChannel()
    {
        var plan = TimelineTracks.Plan(
            [new("Click.wav", "a", 9), new("Piano.wav", "b", 9, Channels: 1), new("Bass.wav", "c", 9)],
            [Click(), Keys()], newId: Counter());
        Assert.Equal(new Dictionary<string, string> { ["Click.wav"] = "click" }, plan.Assignments);
        Assert.Equal(["Piano.wav", "Bass.wav"], plan.Unmatched.Select(u => u.FileName));
        Assert.Equal(["Piano", "Bass"], plan.Unmatched.Select(u => u.Proposed.Name));
        Assert.True(plan.Unmatched[0].Proposed.Mono);
        var onRow = TimelineTracks.Plan([new("Mystery.wav", "d", 1)], [Keys()], channel: "keys");
        Assert.Equal(new Dictionary<string, string> { ["Mystery.wav"] = "keys" }, onRow.Assignments);
        Assert.Empty(onRow.Unmatched);
    }

    [Fact]
    public void TheSheetsChoicesPutFilesOnChannelsAndRememberThem()
    {
        var plan = TimelineTracks.Plan(
            [new("Piano.wav", "a", 9), new("Organ.wav", "b", 9), new("Bells.wav", "c", 9)],
            [Click(), Keys()], newId: Counter());
        var resolution = TimelineTracks.Resolve(plan, new Dictionary<string, TimelineTracks.StemChoice>
        {
            ["Piano.wav"] = new TimelineTracks.StemChoice.Existing("keys"),
            ["Organ.wav"] = new TimelineTracks.StemChoice.New("Count In"),
        });
        Assert.Equal("keys", resolution.Assignments["Piano.wav"]);
        Assert.Equal(["Count In", "Bells"], resolution.Made.Select(m => m.Name));
        Assert.Equal(TimelineTracks.Role.Click, resolution.Made[0].TrackRole());
        Assert.Equal(["Piano"], resolution.Remembered["keys"]);
        Assert.Single(resolution.Remembered);
        var list = TimelineTracks.Adding(resolution, [Click(), Keys()]);
        Assert.Equal(["Click", "Count In", "Keys", "Bells"], list.Select(c => c.Name));
        Assert.Equal(["Piano"], list.First(c => c.Id == "keys").MatchNames!);
        Assert.Equal("keys", TimelineTracks.Channel("Piano.wav", list)?.Id);
        Assert.Equal(["Piano"], TimelineTracks.Adding(resolution, list).First(c => c.Id == "keys").MatchNames!);
        Assert.Empty(TimelineTracks.Resolve(plan, new Dictionary<string, TimelineTracks.StemChoice>
        {
            ["Piano.wav"] = new TimelineTracks.StemChoice.Existing("keys"),
        }, remember: false).Remembered);
        Assert.Equal(["Piano", "Organ", "Bells"], TimelineTracks.Resolve(plan).Made.Select(m => m.Name));
    }

    [Fact]
    public void ARememberedNameLandsOnANumberedChannel()
    {
        var guitar = new TrackChannel { Id = "eg2", Name = "Electric Guitar 2", MatchNames = ["Lead Gtr"] };
        Assert.Equal("eg2", TimelineTracks.Channel("Lead Gtr.wav", [guitar])?.Id);
        Assert.Null(TimelineTracks.Channel("Lead Gtr 3.wav", [guitar]));
        var pads = new TrackChannel { Id = "pad", Name = "Pads 2", MatchNames = ["Atmos"] };
        Assert.Null(TimelineTracks.Channel("Big Atmos Swell.wav", [pads]));
    }

    [Fact]
    public void AddingATrackByNamePutsItInPlaybacksOrder()
    {
        var list = new List<TrackChannel> { Click(), Keys() };
        var made = TrackChannelEditing.Adding("  Guide ", list, id: "g");
        Assert.Equal(TimelineTracks.Role.Guide, made?.TrackRole());
        Assert.Equal(["click", "g", "keys"], list.Select(c => c.Id));
        Assert.Null(TrackChannelEditing.Adding(" ", list));
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void AMoveGivesThePreMoveIndicesAndTheNewOrder()
    {
        var list = new[] { "a", "b", "c", "d" }.Select(id => new TrackChannel { Id = id, Name = id }).ToList();
        var up = TrackChannelEditing.Move("c", "a", list);
        Assert.True(up?.From == 2 && up?.Before == 0);
        Assert.Equal(["c", "a", "b", "d"], up!.Value.Next.Select(c => c.Id));
        Assert.Equal(["b", "c", "a", "d"], TrackChannelEditing.Move("a", "d", list)!.Value.Next.Select(c => c.Id));
        Assert.Equal(["a", "c", "d", "b"], TrackChannelEditing.Move("b", null, list)!.Value.Next.Select(c => c.Id));
        Assert.Null(TrackChannelEditing.Move("b", "c", list));
        Assert.Null(TrackChannelEditing.Move("gone", "a", list));
    }

    [Fact]
    public void ASongUsesTheChannelsItsTimelinesHaveStemsOn()
    {
        var song = new Presentation { Id = "p", Name = "Song", PresentationKind = PresentationKind.Song, ThemeId = "", Slides = [] };
        Assert.Empty(TrackChannelEditing.ChannelIds(song));
        song.Timelines =
        [
            new PresentationTimeline
            {
                Id = "t1", TimecodeEnabled = false, TimecodeStart = 0,
                Cues = [new TimelineCue { Id = "s1", Lane = TimelineTracks.Lane("keys"), At = 0, MediaId = "a" }, new TimelineCue { Id = "x", Lane = "slide", At = 0 }],
            },
            new PresentationTimeline
            {
                Id = "t2", ArrangementId = "short", TimecodeEnabled = false, TimecodeStart = 0,
                Cues = [new TimelineCue { Id = "s2", Lane = TimelineTracks.Lane("click"), At = 0, MediaId = "b" }],
            },
        ];
        Assert.Equal(["click", "keys"], TrackChannelEditing.ChannelIds(song).Order());
    }

    [Fact]
    public void ARenameOrRoleChangeMovesTheFamilySendAndKeepsClickOffMain()
    {
        static string Bus(TimelineTracks.Family family) => $"bus-{family.Name()}";
        var guitar = new TrackChannel { Id = "x", Name = "Electric Guitar 2" };
        var piano = guitar.Copy();
        piano.Name = "Piano";
        var renamed = TrackChannelEditing.Edited(new Dictionary<string, float> { ["bus-Electric Guitars"] = 0.8f, ["monitor"] = 0.5f }, null, guitar, piano, Bus, "main");
        Assert.Equal(new Dictionary<string, float> { ["bus-Keys"] = 0.8f, ["monitor"] = 0.5f }, renamed.Sends);
        Assert.Null(renamed.Route);
        var clicked = guitar.Copy();
        clicked.Role = "click";
        var toClick = TrackChannelEditing.Edited(new Dictionary<string, float> { ["bus-Electric Guitars"] = 1 }, "main", guitar, clicked, Bus, "main");
        Assert.Equal(new Dictionary<string, float> { ["bus-Click"] = 1, ["main"] = 0 }, toClick.Sends);
        Assert.Equal("", toClick.Route);
        var kept = TrackChannelEditing.Edited(new Dictionary<string, float>(), "iem", guitar, clicked, Bus, "main");
        Assert.Equal(new Dictionary<string, float> { ["main"] = 0 }, kept.Sends);
        Assert.Equal("iem", kept.Route);
        var mono = guitar.Copy();
        mono.Mono = true;
        Assert.Equal(new Dictionary<string, float> { ["bus-Electric Guitars"] = 1 },
            TrackChannelEditing.Edited(new Dictionary<string, float> { ["bus-Electric Guitars"] = 1 }, null, guitar, mono, Bus, "main").Sends);
    }

    [Fact]
    public void TheTracksAreaTakesAudioFilesAndFolders()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mxu-audio-tests", "Run To The Father");
        Directory.CreateDirectory(folder);
        Assert.True(TrackChannelEditing.TakesDrop([Path.Combine(Path.GetTempPath(), "Click Track.m4a")]));
        Assert.True(TrackChannelEditing.TakesDrop(["C:\\tmp\\Album.jpg", "C:\\tmp\\Keys.wav"]));
        Assert.True(TrackChannelEditing.TakesDrop([folder]));
        Assert.False(TrackChannelEditing.TakesDrop(["C:\\tmp\\Walk-in.mp4", "C:\\tmp\\Album.jpg"]));
        Assert.False(TrackChannelEditing.TakesDrop([]));
    }

    [Fact]
    public void OneFileDroppedOnATrackRowGoesThereAndASongsFilesGoByName()
    {
        List<TimelineStemImport> stems = [new("Piano.wav", "a", 9), new("Keys.wav", "b", 9)];
        var several = TimelineTracks.Plan(stems, [Click(), Keys()], channel: "click");
        Assert.Equal(new Dictionary<string, string> { ["Keys.wav"] = "keys" }, several.Assignments);
        Assert.Equal(["Piano.wav"], several.Unmatched.Select(u => u.FileName));
        var cues = new List<TimelineCue>();
        TimelineTracks.Place(stems, 2, TimelineTracks.Plan([stems[0]], [Click()], channel: "click").Assignments, cues);
        Assert.Equal(["click"], cues.Select(c => c.TrackChannelId()));
        Assert.Equal(2, cues[0].At);
    }

    [Fact]
    public void TheTrackListSaysTheFamilyMixOnlyWhenTheNameDoesnt()
    {
        static string Line(string name, string? role, bool? mono) =>
            TrackChannelEditing.ListSubtitle(new TrackChannel { Id = "x", Name = name, Role = role, Mono = mono });
        Assert.Equal("Stereo", Line("Drums", null, null));
        Assert.Equal("Mono", Line("Electric Guitar 2", null, true));
        Assert.Equal("Stereo", Line("Click", "click", null));
        Assert.Equal("Bass mix · Stereo", Line("Synth Bass", null, null));
        Assert.Equal("Keys mix · Stereo", Line("Piano", null, null));
        Assert.Equal("Vocals mix · Stereo", Line("Background Vocals", null, null));
        Assert.Equal("Click mix · Mono", Line("Drum Click", "click", true));
    }

    [Fact]
    public void OneMoreFileJoinsTheTracksItemTheSongUses()
    {
        var set = new TrackSet { Id = "s", Name = "Song", Stems = [new() { AudioId = "a", Name = "Click" }, new() { AudioId = "b", Name = "Keys" }] };
        var other = new TrackSet { Id = "o", Name = "Other", Stems = [new() { AudioId = "z", Name = "Bass" }] };
        List<TimelineCue> cues = [new() { Id = "c", Lane = TimelineTracks.Lane("keys"), At = 0, MediaId = "b" }, new() { Id = "x", Lane = "slide", At = 0 }];
        Assert.Equal("s", TrackSets.UsedBy(cues, [other, set])?.Id);
        Assert.Null(TrackSets.UsedBy([], [other, set]));
        var shared = new TrackSet { Id = "x", Name = "Another", Stems = [new() { AudioId = "a", Name = "Click" }] };
        var both = cues.Append(new TimelineCue { Id = "d", Lane = TimelineTracks.Lane("click"), At = 0, MediaId = "a" }).ToList();
        Assert.Equal("s", TrackSets.UsedBy(both, [shared, set])?.Id);
        var joined = TrackSets.Joining([new() { AudioId = "n", Name = "Drums" }, new() { AudioId = "a", Name = "Click" }], set);
        Assert.Equal(["a", "b", "n"], joined.Stems.Select(s => s.AudioId));
        Assert.Equal(2, set.Stems.Count);
    }

    [Fact]
    public void ADraggedRowLandsInTheGapTheLineShows()
    {
        var list = new[] { "a", "b", "c", "d" }.Select(id => new TrackChannel { Id = id, Name = id }).ToList();
        IEnumerable<string>? Order(string id, string target, bool above)
        {
            if (TrackChannelEditing.Dropping(id, target, above, list) is not { } drop) return null;
            return TrackChannelEditing.Move(id, drop.Before, list)?.Next.Select(c => c.Id);
        }
        Assert.Equal(["a", "d", "b", "c"], Order("d", "b", above: true)!);
        Assert.Equal(["a", "b", "d", "c"], Order("d", "b", above: false)!);
        Assert.Equal(["b", "c", "a", "d"], Order("a", "c", above: false)!);
        Assert.Equal(["b", "c", "d", "a"], Order("a", "d", above: false)!);
        Assert.Null(TrackChannelEditing.Dropping("b", "b", true, list));
        Assert.Null(TrackChannelEditing.Dropping("b", "a", false, list));
        Assert.Null(TrackChannelEditing.Dropping("b", "c", true, list));
        Assert.Null(TrackChannelEditing.Dropping("gone", "a", true, list));
    }

    [Fact]
    public void ATrackAddedInSettingsShowsTheMixersTracksWithNoStems()
    {
        var list = new List<TrackChannel>();
        List<(string Id, string? FolderId)> mixes = [("main", null), ("bus-keys", "tracks")];
        var buses = new HashSet<string> { "bus-keys" };
        Assert.Equal(["main"], TimelineTracks.ShownMixes(mixes, ["tracks"], buses, TimelineTracks.HasTracks(list)).Mixes);
        TrackChannelEditing.Adding("Keys", list);
        Assert.True(TimelineTracks.HasTracks(list));
        Assert.Equal(["bus-keys", "main"], TimelineTracks.ShownMixes(mixes, ["tracks"], buses, TimelineTracks.HasTracks(list)).Mixes.Order());
    }
}

/// <summary>Port of PresenterCore's TrackSetsTests.swift.</summary>
public sealed class TrackSetsTests
{
    [Fact]
    public void AMultiTracksFolderSaysTheSongKeyAndTempo()
    {
        Assert.Equal(new TrackSets.Download("Run To The Father", "C", 68.5), TrackSets.FromFolder("Run To The Father-Run to the Father-C-68.50bpm"));
        Assert.Equal("Bb", TrackSets.FromFolder("Way Maker-Default-Bb-136bpm").Key);
        Assert.Null(TrackSets.FromFolder("Stems").Key);
        Assert.Null(TrackSets.FromFolder("Song-Live-H-90bpm").Key);
        Assert.Equal(68.5, TrackSets.ForFiles(["/Users/x/Downloads/Run To The Father-Run to the Father-C-68.50bpm/MultiTracks/EG 1.m4a"]).Tempo);
        Assert.Equal(68.5, TrackSets.ForFiles(["C:\\Users\\x\\Downloads\\Run To The Father-Run to the Father-C-68.50bpm\\MultiTracks\\EG 1.m4a"]).Tempo);
        Assert.Equal("C", TrackSets.ForFiles(["/Users/x/Downloads/Run To The Father-Run to the Father-C-68.50bpm"]).Key);
        Assert.Equal("Run to the Father — C, 68.5 bpm", TrackSets.Name("Run to the Father", "C", 68.5));
        Assert.Equal("Way Maker — 136 bpm", TrackSets.Name("Way Maker", null, 136));
        Assert.Equal("Song", TrackSets.Name("Song", null, null));
    }

    [Fact]
    public void SetsHideTheirStemsFromMusicAndASecondDropReusesTheSet()
    {
        var set = new TrackSet
        {
            Id = "s", Name = "Song",
            Stems = [new() { AudioId = "a1", Name = "Click" }, new() { AudioId = "a2", Name = "Keys", ChannelId = "keys" }],
        };
        Assert.Equal(["a1", "a2"], TrackSets.StemIds([set]).Order());
        Assert.Equal("s", TrackSets.Existing(["a2", "a1"], [set])?.Id);
        Assert.Null(TrackSets.Existing(["a1"], [set]));
    }

    [Fact]
    public void ASetsStemsLandOnTheChannelsTheyKept()
    {
        List<TrackChannel> channels = [new() { Id = "lead", Name = "Lead Guitar" }, new() { Id = "eg", Name = "Electric Guitar" }];
        var cues = new List<TimelineCue>();
        TimelineTracks.Place([new("EG", "a", 10, ChannelId: "lead"), new("EG 2", "b", 10, ChannelId: "gone")], 0, channels, cues);
        Assert.Equal("lead", cues.First(c => c.MediaId == "a").TrackChannelId());
        Assert.NotEqual("gone", cues.First(c => c.MediaId == "b").TrackChannelId());
    }

    [Fact]
    public void ClipNamesReadLikeTitles()
    {
        Assert.Equal("Wind To Breathe Glenn Campagna instrumental", TrackSets.ClipName("03 Wind_To_Breathe_Glenn_Campagna_instrumental_MP3"));
        Assert.Equal("Way Maker", TrackSets.ClipName("12 - Way Maker"));
        Assert.Equal("EG 1", TrackSets.ClipName("EG 1"));
        Assert.Equal("1999", TrackSets.ClipName("1999"));
    }
}

/// <summary>Port of PresenterCore's WaveformsTests.swift.</summary>
public sealed class WaveformsTests
{
    [Fact]
    public void BucketsHoldEachStretchsLowestAndHighestSampleAcrossChannelsAndBuffers()
    {
        var bucketer = new WaveformBucketer(400, 100);
        float[] left = [0.1f, -0.2f, 0.3f, 0, 0.5f, 0, 0, 0, -0.9f];
        float[] right = [0, 0, 0, 0.4f, 0, -0.6f, 0, 0, 0];
        for (var start = 0; start < left.Length; start += 3)
            bucketer.Add([left[start..(start + 3)], right[start..(start + 3)]], 3);
        var wave = bucketer.Finished();
        Assert.Equal([0.4f, 0.5f, 0], wave.Maxs);
        Assert.Equal([-0.2f, -0.6f, -0.9f], wave.Mins);
        Assert.Equal(0.03, wave.Seconds);
    }

    [Fact]
    public void TheCacheFileReadsBackAndRejectsTornBytes()
    {
        var wave = new Waveform([-0.5f, -0.25f], [0.5f, 1]);
        Assert.Equal(wave, Waveform.Decode(wave.Encoded()));
        Assert.Null(Waveform.Decode(wave.Encoded()[..^1]));
        Assert.Null(Waveform.Decode("nope"u8));
    }

    [Fact]
    public void FillScalesTheLoudestPeakToTheEdges()
    {
        Assert.Equal(2, new Waveform([-0.5f, -0.1f], [0.2f, 0.25f]).FillScale);
        Assert.Equal(0, new Waveform([0], [0]).FillScale);
    }

    [Fact]
    public void StemsCombineAtTheirPlacesAndTrims()
    {
        var ones = new Waveform(Enumerable.Repeat(-1f, 20).ToArray(), Enumerable.Repeat(1f, 20).ToArray(), 10);
        var song = Waveforms.Combined([new WaveformPart(ones, 0), new WaveformPart(ones, 1, 0.5, 0.5)], 0, 3);
        Assert.Equal(30, song.Count);
        Assert.Equal(1, song.Maxs[5]);
        Assert.Equal(MathF.Sqrt(2), song.Maxs[12]);
        Assert.Equal(1, song.Maxs[16]);
        Assert.Equal(-MathF.Sqrt(2), song.Mins[12]);
        Assert.Equal(0, song.Maxs[25]);
        var card = Waveforms.Combined([new WaveformPart(ones, 1, 0.5)], 1, 1.5);
        Assert.Equal(15, card.Count);
        Assert.True(card.Maxs.All(m => m == 1));
    }

    [Fact]
    public void AZoomDrawsTheCoarsestLevelWithABucketAPoint()
    {
        var baseWave = new Waveform(Enumerable.Repeat(-0.5f, 1000).ToArray(), Enumerable.Range(0, 1000).Select(i => i / 1000f).ToArray());
        var levels = new WaveformLevels(baseWave);
        Assert.Equal([100, 50, 25, 12.5, 6.25, 3.125, 1.5625], levels.Levels.Select(l => l.BucketsPerSecond));
        Assert.Equal(100, levels.Level(120).BucketsPerSecond);
        Assert.Equal(50, levels.Level(40).BucketsPerSecond);
        Assert.Equal(3.125, levels.Level(2).BucketsPerSecond);
        Assert.Equal(0.001f, levels.Levels[1].Maxs[0]);
        Assert.True(MathF.Abs(levels.FillScale - 1 / 0.999f) < 1e-5);
    }

    [Fact]
    public void AScrolledLaneRedrawsInHalfViewSteps()
    {
        var window = Waveforms.DrawnWindow(1250, 1000);
        Assert.Equal((0.0, 3000.0), window);
        Assert.Equal(window, Waveforms.DrawnWindow(1499, 1000));
        Assert.True(window.Lower <= 1250 && 2250 <= window.Upper);
    }
}
