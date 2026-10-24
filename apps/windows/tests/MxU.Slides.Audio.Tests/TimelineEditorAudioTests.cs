using MxU.Slides.Audio.Control;
using MxU.Slides.Audio.Multitrack;
using MxU.Slides.Audio.Timeline;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Audio.Tests;

/// <summary>
/// Windows: the timeline editor's audio side. Cue labels (PresentationTimelineContext.label),
/// fade handles (ClipFadesOverlay), the tracks' routing (TracksController.routing and the setup
/// guide), stem import into track sets (AppModel.importTrackSet), waveforms worked out off the
/// UI thread, and opening the sound device before a timeline's music is due.
/// </summary>
public sealed class TimelineEditorAudioTests
{
    private static readonly Presentation Song = new()
    {
        Id = "song", Name = "Song", PresentationKind = PresentationKind.Song, ThemeId = "",
        Sections = [new PresentationSection { Id = "verse", Name = "Verse 1" }, new PresentationSection { Id = "chorus", Name = "Chorus" }],
        Slides =
        [
            new Slide { Id = "v1", Name = "v1", SectionId = "verse", Objects = [new SlideObject { Id = "t", Name = "Lyrics", ObjectKind = SlideObjectKind.Text, Text = "Amazing grace how sweet the sound" }] },
            new Slide { Id = "c1", Name = "c1", SectionId = "chorus", Objects = [] },
        ],
    };

    private static readonly List<TimelinePlayedBlock> Blocks = [new("verse", ["v1"]), new("chorus", ["c1"])];

    private static readonly TimelineNames Names = new(
        id => id == "lt" ? "Lower Third" : null, id => id == "nursery" ? "Nursery" : null, id => id == "cam" ? "Pulpit camera" : null,
        id => id switch { "song" => "01 Way Maker", "stem" => "Way Maker · 03_Keys 2", _ => null },
        id => id == "loop" ? "Deep Blue" : null);

    private static TimelineCue Cue(string lane, Action<TimelineCue>? fill = null)
    {
        var cue = new TimelineCue { Id = "c", Lane = lane, At = 1 };
        fill?.Invoke(cue);
        return cue;
    }

    [Fact]
    public void ACueSaysWhatItFires()
    {
        string Label(TimelineCue cue) => TimelineLabels.Label(cue, Song, Blocks, Names);
        Assert.Equal("Slide 1 · Verse 1", Label(Cue("slide", c => { c.SlideId = "v1"; c.SectionId = "verse"; })));
        Assert.Equal("Missing slide · Chorus", Label(Cue("slide", c => { c.SlideId = "v1"; c.SectionId = "chorus"; })));
        Assert.Equal("Lower Third", Label(Cue("overlays", c => c.OverlayId = "lt")));
        Assert.Equal("Overlay", Label(Cue("overlays", c => c.OverlayId = "gone")));
        Assert.Equal("Nursery", Label(Cue("alerts", c => c.AlertId = "nursery")));
        Assert.Equal("Pulpit camera", Label(Cue("videoInput", c => c.LiveInputId = "cam")));
        Assert.Equal("Way Maker", Label(Cue("audio", c => c.MediaId = "song")));
        Assert.Equal("Deep Blue", Label(Cue("loopingVideos", c => c.MediaId = "loop")));
        Assert.Equal("No actions", Label(Cue("actions")));
        Assert.Equal("Clear All +1", Label(Cue("actions", c => c.Actions =
            [new SlideAction { Id = "a", Kind = SlideActionKind.ClearAll }, new SlideAction { Id = "b", Kind = SlideActionKind.ClearAudio }])));
        Assert.Equal("Keys 2", Label(Cue("track:keys", c => c.MediaId = "stem")));
        Assert.Equal("Unknown layer", Label(Cue("somethingNew")));
    }

    [Fact]
    public void ASlidePinShowsTheWordsThatFit()
    {
        var cue = Cue("slide", c => { c.SlideId = "v1"; c.SectionId = "verse"; });
        Assert.Equal("Amazing grace", TimelineLabels.SlideWords(cue, Song, Blocks, 100));
        Assert.Equal("1", TimelineLabels.SlideWords(cue, Song, Blocks, 20));
        Assert.Equal("", TimelineLabels.SlideWords(cue, Song, Blocks, 6));
        Assert.Equal("2", TimelineLabels.SlideWords(Cue("slide", c => { c.SlideId = "c1"; c.SectionId = "chorus"; }), Song, Blocks, 100));
    }

    [Fact]
    public void AFadeHasALengthHandleAtItsInnerEndAndAMidpointOnItsCurve()
    {
        var clip = new TimelineCue
        {
            Id = "m", Lane = "audio", At = 2, Duration = 10, MediaId = "song",
            FadeIn = new ClipFade { Seconds = 2, Shape = "linear" }, FadeOut = new ClipFade { Seconds = 4, Shape = "linear", Midpoint = 0.25 },
        };
        var handles = ClipFadeGeometry.Handles(clip, [clip]);
        Assert.Equal(4, handles.Count);
        var inLength = handles.Single(h => !h.Out && !h.Midpoint);
        var inMid = handles.Single(h => !h.Out && h.Midpoint);
        var outLength = handles.Single(h => h.Out && !h.Midpoint);
        var outMid = handles.Single(h => h.Out && h.Midpoint);
        Assert.Equal(4, inLength.Time);
        Assert.Equal(3, inMid.Time);
        Assert.Equal(0.5, inMid.Level, 6);
        Assert.Equal(8, outLength.Time);
        Assert.Equal(9, outMid.Time);
        Assert.Equal(0.5, outMid.Level, 6);
        Assert.Equal(12, outLength.End);
        // On a 26-point row: the length handle 4 points down, the midpoint on the curve.
        Assert.Equal(104, ClipFadeGeometry.HandleY(inLength, 100, 26));
        Assert.Equal(113, ClipFadeGeometry.HandleY(inMid, 100, 26));
        Assert.Same(inMid, ClipFadeGeometry.HandleAt(handles, 31, 112, t => t * 10, 100, 26));
        Assert.Null(ClipFadeGeometry.HandleAt(handles, 50, 112, t => t * 10, 100, 26));
        // A drag of the fade-in's handle a second later makes it a second longer.
        Assert.Equal(3, ClipFadeGeometry.Dragged(inLength, 1, clip).Seconds);
        Assert.Equal(3, ClipFadeGeometry.Dragged(outLength, 1, clip).Seconds);
        // A clip that runs on with no end has no handles.
        Assert.Empty(ClipFadeGeometry.Handles(new TimelineCue { Id = "o", Lane = "audio", At = 0, MediaId = "x", FadeIn = new ClipFade { Seconds = 1 } }, []));
        // While a handle is dragged, the live fade is drawn.
        var live = ClipFadeGeometry.Handles(clip, [clip], (id, @out) => @out ? null : new ClipFade { Seconds = 1 });
        Assert.Equal(3, live.Single(h => !h.Out && !h.Midpoint).Time);
    }

    [Fact]
    public void AFadeCurveRisesWhereTheEngineDoes()
    {
        var curve = ClipFadeGeometry.Curve(new ClipFade { Seconds = 2, Shape = "equalPower" }, @out: false, start: 1);
        Assert.Equal(33, curve.Count);
        Assert.Equal((1.0, 0.0), curve[0]);
        Assert.Equal(3, curve[^1].Time, 9);
        Assert.Equal(1, curve[^1].Level, 9);
        Assert.Equal(Math.Sin(Math.PI / 4), curve[16].Level, 6);
        var falling = ClipFadeGeometry.Curve(new ClipFade { Seconds = 2 }, @out: true, start: 5);
        Assert.Equal(1, falling[0].Level, 9);
        Assert.Equal(0, falling[^1].Level, 9);
        Assert.Equal(25, ClipFadeGeometry.ShapePicture(TimelineClips.Shape.Linear, @out: false).Count);
    }

    // MARK: Routing

    private static readonly TrackChannel Click = new() { Id = "click", Name = "Click", Role = "click" };
    private static readonly TrackChannel Keys = new() { Id = "keys", Name = "Keys" };
    private static readonly TrackChannel Bass = new() { Id = "bass", Name = "Bass", Mono = true };

    [Fact]
    public void MusicTracksPlayOnTheHouseAndTheClickOnlyOnItsMix()
    {
        var settings = new TrackOutputSettings();
        var routing = TrackRouting.Build([Click, Keys, Bass], settings, 2);
        var click = routing.Channels.Single(c => c.Id == "click");
        var keys = routing.Channels.Single(c => c.Id == "keys");
        Assert.Equal(["bus:click"], click.Sends.Keys);
        Assert.Equal(["bus:keys", "main"], keys.Sends.Keys.Order());
        Assert.True(routing.Channels.Single(c => c.Id == "bass").Mono);
        var main = routing.Mixes.Single(m => m.Id == TrackRouting.MainMix);
        Assert.Equal([0, 1], main.Outputs.Single().Channels);
        // The family mixes go nowhere until the setup gives them outputs.
        Assert.All(routing.Mixes.Where(m => m.Id != "main"), m => Assert.Empty(m.Outputs));
        Assert.Equal(1 + TimelineTracks.Families.Count, routing.Mixes.Count);
    }

    [Fact]
    public void SoloQuietsTheOtherMusicButNeverTheClick()
    {
        var settings = new TrackOutputSettings();
        settings.Mix("keys").Solo = true;
        settings.Mix("bass").Gain = 0.5f;
        var routing = TrackRouting.Build([Click, Keys, Bass], settings, 2);
        Assert.False(routing.Channels.Single(c => c.Id == "keys").Muted);
        Assert.True(routing.Channels.Single(c => c.Id == "bass").Muted);
        Assert.False(routing.Channels.Single(c => c.Id == "click").Muted);
        Assert.Equal(0.5f, routing.Channels.Single(c => c.Id == "bass").Gain);
        settings.Mix("click").Muted = true;
        Assert.True(TrackRouting.Build([Click, Keys, Bass], settings, 2).Channels.Single(c => c.Id == "click").Muted);
    }

    [Fact]
    public void AStemOnATrackNoLongerInTheListStillPlaysOnTheHouse()
    {
        var routing = TrackRouting.Build([Keys], new TrackOutputSettings(), 2, ["keys", "old"]);
        var old = routing.Channels.Single(c => c.Id == "old");
        Assert.Equal(["main"], old.Sends.Keys);
        Assert.Equal(2, routing.Channels.Count);
    }

    [Fact]
    public void TheSetupGivesEachFamilyItsOutputAndTakesTheMusicOffTheHouse()
    {
        var settings = new TrackOutputSettings();
        TrackRouting.Lay(settings, 8, 2, 16);
        Assert.Equal((8, 2, false), (settings.Outputs, settings.Start, settings.MusicOnMain));
        Assert.Equal((2, false), TrackRouting.Current(settings, TimelineTracks.Family.Click));
        Assert.Equal((3, false), TrackRouting.Current(settings, TimelineTracks.Family.Guide));
        Assert.Equal((8, true), TrackRouting.Current(settings, TimelineTracks.Family.Synths));
        var routing = TrackRouting.Build([Click, Keys], settings, 16);
        Assert.Equal([2], routing.Mixes.Single(m => m.Id == "bus:click").Outputs.Single().Channels);
        Assert.Equal([7], routing.Mixes.Single(m => m.Id == "bus:keys").Outputs.Single().Channels);
        Assert.Equal(["bus:keys"], routing.Channels.Single(c => c.Id == "keys").Sends.Keys);
        // Outputs past the interface's last are dropped, not wrapped.
        TrackRouting.Assign(settings, TimelineTracks.Family.Keys, 15, stereo: true);
        Assert.Empty(TrackRouting.Build([Keys], settings, 16).Mixes.Single(m => m.Id == "bus:keys").Outputs);
        // None: the music goes back to the house.
        TrackRouting.Lay(settings, 0, 0, 16);
        Assert.True(settings.MusicOnMain);
        Assert.Equal((null, true), TrackRouting.Current(new TrackOutputSettings(), TimelineTracks.Family.Keys));
        Assert.Equal((null, false), TrackRouting.Current(new TrackOutputSettings(), TimelineTracks.Family.Guide));
    }

    [Fact]
    public void TheSetupIsSavedAndADamagedFileMeansDefaults()
    {
        var folder = Path.Combine(TestFiles.RunFolder, $"tracks-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, TrackRouting.FileName);
        Assert.True(TrackRouting.Load(path).MusicOnMain);
        var settings = new TrackOutputSettings { DeviceUid = "interface" };
        TrackRouting.Lay(settings, 4, 0, 4);
        settings.Mix("keys").Muted = true;
        TrackRouting.Save(settings, path);
        var loaded = TrackRouting.Load(path);
        Assert.Equal("interface", loaded.DeviceUid);
        Assert.Equal(4, loaded.Outputs);
        Assert.True(loaded.Channels["keys"].Muted);
        Assert.Equal((1, false), TrackRouting.Current(loaded, TimelineTracks.Family.Guide));
        File.WriteAllText(path, "{ not json");
        Assert.Null(TrackRouting.Load(path).DeviceUid);
    }

    // MARK: Importing stems

    private static (LibraryStore Store, string Folder) Library()
    {
        var root = Path.Combine(TestFiles.RunFolder, $"library-{Guid.NewGuid():N}");
        var store = new LibraryStore(Path.Combine(root, "Library"));
        store.Load();
        var folder = Path.Combine(root, "Run To The Father-Run to the Father-C-68.50bpm");
        Directory.CreateDirectory(folder);
        return (store, folder);
    }

    private static string Stem(string folder, string name, double seconds = 1, int channels = 2, double hertz = 220)
    {
        var tone = TestAudio.Tone(name, seconds, hertz, 48_000, channels);
        var path = Path.Combine(folder, name + ".wav");
        File.Move(tone, path);
        return path;
    }

    [Fact]
    public void ASongsStemsBecomeATrackSetNamedFromTheirDownload()
    {
        var (store, folder) = Library();
        Stem(folder, "Click", channels: 1);
        Stem(folder, "Keys");
        var made = new StemImporter(store).ImportTrackSet([folder], "Run to the Father")!.Value;
        Assert.Equal("Run to the Father — C, 68.5 bpm", made.Set.Name);
        Assert.Equal(("C", 68.5, "MultiTracks"), (made.Set.Key, made.Set.Tempo, made.Set.Source));
        Assert.Equal(["Click.wav", "Keys.wav"], made.Stems.Select(s => s.FileName));
        Assert.Equal([1, 2], made.Stems.Select(s => s.Channels ?? 0));
        Assert.All(made.Stems, s => Assert.True(s.Seconds > 0.9));
        Assert.Equal(["Run to the Father · Click", "Run to the Father · Keys"], store.All<AudioItem>().Select(a => a.Name));
        Assert.Same(made.Set, store.Get<TrackSet>(made.Set.Id));

        // The stems remember their tracks, so the next song using the set puts them back there.
        Assert.True(StemImporter.Remember(made.Set, new Dictionary<string, string> { [made.Stems[0].AudioId] = "click-track" }));
        Assert.False(StemImporter.Remember(made.Set, new Dictionary<string, string> { [made.Stems[0].AudioId] = "click-track" }));
        store.Save(made.Set);
        var again = new StemImporter(store).ImportTrackSet([folder], "Run to the Father")!.Value;
        Assert.Equal(made.Set.Id, again.Set.Id);
        Assert.Equal("click-track", again.Stems[0].ChannelId);
        Assert.Single(store.All<TrackSet>());
        Assert.Equal(2, store.All<AudioItem>().Count);
    }

    [Fact]
    public void OneMoreFileJoinsTheSongsSet()
    {
        var (store, folder) = Library();
        Stem(folder, "Bass");
        var set = new StemImporter(store).ImportTrackSet([folder], "Song")!.Value.Set;
        // A different sound (the same file again would be the same stem).
        var extra = Stem(Path.GetDirectoryName(folder)!, "Pad", hertz: 330);
        List<TimelineCue> cues = [new() { Id = "b", Lane = "track:bass", At = 0, MediaId = set.Stems[0].AudioId }];
        var joining = StemImporter.JoiningSet([extra], cues, store.All<TrackSet>());
        Assert.Equal(set.Id, joining);
        Assert.Null(StemImporter.JoiningSet([folder], cues, store.All<TrackSet>()));
        var joined = new StemImporter(store).ImportTrackSet([extra], "Song", joining)!.Value;
        Assert.Equal(set.Id, joined.Set.Id);
        Assert.Equal(2, store.Get<TrackSet>(set.Id)!.Stems.Count);
        Assert.Null(new StemImporter(store).ImportTrackSet([Path.Combine(folder, "missing.wav")], "Song"));
    }

    [Fact]
    public void NoAbletonSetMeansNoSongMap()
    {
        var (_, folder) = Library();
        var stem = Stem(folder, "Keys");
        Assert.Null(SongMapImport.Find([folder]));
        Assert.Null(SongMapImport.Find([stem]));
        File.WriteAllBytes(Path.Combine(folder, SongMapImport.FileName), [1, 2, 3]);
        Assert.Null(SongMapImport.Find([folder]));
    }

    // MARK: Waveforms

    [Fact]
    public void AWaveformIsWorkedOutInTheBackgroundAndKept()
    {
        var cache = Path.Combine(TestFiles.RunFolder, $"waves-{Guid.NewGuid():N}");
        using var store = new TimelineWaveformStore(cache, ui: null);
        var changed = 0;
        store.Changed += () => Interlocked.Increment(ref changed);
        var path = TestAudio.Tone("wave", 2, 220, 48_000, 2, amplitude: 0.5f);
        var sources = new List<TimelineWaveformStore.Source> { new("hash", path, 0, 0, null) };
        Assert.Null(store.Levels(sources, 0, 2, "cue"));
        store.Settle(TimeSpan.FromSeconds(20));
        var levels = store.Levels(sources, 0, 2, "cue");
        Assert.NotNull(levels);
        Assert.True(levels.Levels[0].Count >= 199);
        Assert.Equal(1, changed);
        Assert.True(Directory.EnumerateFiles(cache, "*.peaks").Any());
        // A trimmed clip asks for a new shape; meanwhile it keeps drawing its last one.
        var trimmed = new List<TimelineWaveformStore.Source> { new("hash", path, 0, 0.5, 1.5) };
        Assert.Same(levels, store.Levels(trimmed, 0, 1.5, "cue"));
        Assert.Null(store.Levels(trimmed, 0, 1.5));
        // A file that isn't on this computer draws nothing (and isn't asked for again and again).
        var missing = new List<TimelineWaveformStore.Source> { new("other", null, 0, 0, null) };
        Assert.Null(store.Levels(missing, 0, 2));
        store.Settle(TimeSpan.FromSeconds(20));
        Assert.Null(store.Levels(missing, 0, 2));
        Assert.Null(store.Levels([], 0, 2));
    }

    // MARK: Opening the device ahead of the music

    private sealed class OneItemLibrary(AudioItem item, string path) : IAudioLibrary
    {
        public Playlist? Playlist(string id) => null;

        public AudioItem? AudioItem(string id) => id == item.Id ? item : null;

        public string? FilePath(AudioItem asked) => asked.Id == item.Id ? path : null;
    }

    [Fact]
    public void AWarmedEngineIsTheOneTheNextClipPlaysOn()
    {
        var item = new AudioItem { Id = "song", Name = "Song", FileHash = "h", FileName = "song.wav" };
        var path = TestAudio.Wav("warm-music", 48_000, 2, 3, _ => 0.25f);
        var made = new List<AudioEngine>();
        using var audio = new AudioController(new OneItemLibrary(item, path), engineFactory: key =>
        {
            var engine = new AudioEngine(key);
            engine.EnableManualRenderingForTests();
            lock (made) made.Add(engine);
            return engine;
        }, startsPolling: false) { FadeEnabled = false };
        audio.Warm();
        AudioThreads.Chores.Sync(() => { }, TimeSpan.FromSeconds(10));
        Assert.Single(made);
        // Warming again while one is ready does nothing.
        audio.Warm();
        AudioThreads.Chores.Sync(() => { }, TimeSpan.FromSeconds(10));
        Assert.Single(made);
        var music = new TimelineMusic(audio);
        music.Play(new TimelineMusicClip("clip", item, 0, null, 0, ClipEnvelopes.Make(), false, null));
        Assert.Single(made);
        Assert.Same(made[0], audio.Players.Single().Engine);
        // While something plays on the main mix there is nothing to warm.
        audio.Warm();
        AudioThreads.Chores.Sync(() => { }, TimeSpan.FromSeconds(10));
        Assert.Single(made);
    }
}
