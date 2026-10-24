using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;
using T = MxU.Slides.Core.Automation.PresentationTimelines;

namespace MxU.Slides.Core.Tests.Automation;

/// <summary>A show for the timeline player: presentations, a service, and what is up.</summary>
internal sealed class TimelineHost : ITimelineHost
{
    public Dictionary<string, Presentation> Decks { get; } = [];
    public List<ServiceItem> Items { get; } = [];
    public HashSet<string> Up { get; } = [];
    public List<string> Fired { get; } = [];
    public List<string> Saved { get; } = [];
    public string? Alert { get; set; }

    public Presentation? Presentation(string id) => Decks.GetValueOrDefault(id);

    public void SaveTimelines(string presentationId, List<PresentationTimeline>? timelines, string? undoLabel, TimelinesUndo? undoTo = null)
    {
        Decks[presentationId].Timelines = timelines;
        Saved.Add(undoLabel ?? "-");
    }

    public ServiceItem? CurrentServiceItem(string itemId) => Items.FirstOrDefault(i => i.Id == itemId);
    public IReadOnlyList<ServiceItem> FireableItems() => Items;
    public string? Name(string refId) => Decks.GetValueOrDefault(refId)?.Name;

    public void FireSlide(Slide slide, Presentation presentation, string? arrangementId, string? contextId, int occurrence, bool runSlideActions) =>
        Fired.Add($"slide:{slide.Id}#{occurrence}{(runSlideActions ? "+actions" : "")}");

    public void FireServiceItem(ServiceItem item) => Fired.Add($"item:{item.Id}");
    public void RunActions(IReadOnlyList<SlideAction> actions) => Fired.Add($"actions:{string.Join(",", actions.Select(a => a.Id))}");
    public IReadOnlySet<string> Live => Up;
    public string? LiveAlertId => Alert;
    public LayerKind? LayerShowing(string contentId, bool liveInput) => Up.Contains(contentId) ? LayerKind.Videos : null;
    public string? AudioFilePath(AudioItem item) => null;
    public ITimelineMusic? Music => null;
}

// C#: the logic of Sources/PresentationTimelineController.swift (the Mac tests its rules,
// in PresentationTimelineTests, not the controller), driven by a fake clock.
public class TimelinePlayerTests
{
    private readonly TimelineHost host = new();
    private readonly RecordingShow show = new();
    private readonly MemoryLibrary library = new();
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.Zero));

    private static readonly List<TimelinePlayedBlock> Blocks = [new("v", ["v1", "v2"]), new("c", ["c1"])];

    private Presentation Song(string id = "song", double? length = null)
    {
        var deck = Make.Deck(id, ("v1", "v"), ("v2", "v"), ("c1", "c"));
        deck.Timelines =
        [
            Make.Timeline("t-" + id, length: length, cues:
            [
                T.SlideCue("s0", 0, T.Anchor(0, Blocks)!),
                T.SlideCue("s1", 2, T.Anchor(1, Blocks)!),
                Make.Cue("ov", "overlays", 1, 2, overlayId: "lower"),
                Make.Cue("act", "actions", 2.5, actions: [Make.Action("a1", SlideActionKind.ClearAll)]),
                T.SlideCue("s2", 4, T.Anchor(2, Blocks)!),
            ]),
        ];
        host.Decks[id] = deck;
        return deck;
    }

    private TimelinePlayer Player()
    {
        library.Add(new Overlay { Id = "lower", Name = "Lower Third", Objects = [] });
        return new TimelinePlayer(host, show, library, clock: clock);
    }

    [Fact]
    public void PlayingFiresTheCuesInTimeOnItsOwnClock()
    {
        var deck = Song();
        var player = Player();
        player.Play("item", deck, null);
        Assert.True(player.Playing);
        clock.Advance(5);
        Assert.Equal(["slide:v1#0+actions", "slide:v2#1+actions", "actions:a1", "slide:c1#2+actions"], host.Fired);
        Assert.Equal(["overlay:lower", "dismissOverlay:lower"], show.Calls);
        Assert.True(Math.Abs(player.PlayheadTime - 5) < 0.05);
        Assert.True(player.Anchor is { Running: true, ItemId: "item" });
    }

    [Fact]
    public void ItStopsItselfASecondPastTheEnd()
    {
        var deck = Song(length: 6);
        var player = Player();
        player.Play("item", deck, null);
        clock.Advance(6.9);
        Assert.True(player.Playing);
        clock.Advance(0.2);
        Assert.False(player.Playing);
        Assert.Equal(0, clock.ActiveTimers);
        Assert.False(player.Anchor?.Running);
    }

    [Fact]
    public void ASeekWhilePlayingLandsWithoutActions()
    {
        var deck = Song();
        var player = Player();
        player.Play("item", deck, null);
        clock.Advance(0.1);
        host.Fired.Clear();
        player.Seek(3, "item", deck, null);
        Assert.Equal(["slide:v2#1"], host.Fired);
        clock.Advance(1.5);
        Assert.Equal(["slide:v2#1", "slide:c1#2+actions"], host.Fired);
        Assert.Equal("item", player.NextCue?.ItemId ?? "item");
    }

    [Fact]
    public void RecordingATakeTurnsFiredSlidesIntoCues()
    {
        var deck = Song();
        deck.Timelines = null;
        var player = Player();
        player.Record("item", deck, null, newStart: 108_000);
        Assert.True(player.Recording && player.Playing);
        clock.Advance(1);
        player.NoteSlideLanded("item", 1);
        clock.Advance(1);
        player.NoteSlideLanded("item", 2);
        player.NoteSlideLanded("other item", 0);
        player.Stop();
        var timeline = Assert.Single(host.Decks["song"].Timelines!);
        Assert.Equal(108_000, timeline.TimecodeStart);
        Assert.Equal([1, 2], timeline.Cues.Select(c => T.Occurrence(c, Blocks)));
        Assert.True(Math.Abs(timeline.Cues[0].At - 1) < 0.05 && Math.Abs(timeline.Cues[1].At - 2) < 0.05);
        Assert.Equal("Record Timeline", host.Saved[^1]);
        Assert.False(player.Recording);
    }

    [Fact]
    public void DiscardingATakePutsTheTimelinesBack()
    {
        var deck = Song();
        var before = deck.Timelines!;
        var player = Player();
        player.Record("item", deck, null, newStart: 0);
        clock.Advance(3);
        Assert.NotEqual(5, host.Decks["song"].Timelines![0].Cues.Count);
        player.DiscardTake();
        Assert.Same(before[0], host.Decks["song"].Timelines![0]);
        Assert.Equal("Discard Take", host.Saved[^1]);
    }

    [Fact]
    public void ATransitionHandsOverToTheNextItemAtTheEnd()
    {
        var first = Song("first", length: 6);
        Song("second");
        host.Items.AddRange(
        [
            new ServiceItem { Id = "one", ItemKind = ServiceItemKind.Presentation, Name = "One", RefId = "first", Transition = new SongTransition { Kind = "overlap", Seconds = 2 } },
            new ServiceItem { Id = "head", ItemKind = ServiceItemKind.Header, Name = "Header", RefId = "" },
            new ServiceItem { Id = "two", ItemKind = ServiceItemKind.Presentation, Name = "Two", RefId = "second" },
        ]);
        var player = Player();
        player.Play("one", first, null);
        Assert.Equal((SongTransitions.Kind.Overlap, "second"), (player.Pending?.Kind, player.Pending?.NextName));
        clock.Advance(4.05);
        Assert.Equal("two", player.ItemId);
        Assert.True(player.Playing);
        clock.Advance(1);
        Assert.Contains("slide:v1#0+actions", host.Fired.Skip(4));

        player.CancelGo();
        Assert.Null(player.Pending);
    }

    [Fact]
    public void FollowingTimecodeCrossesAndLandsWithoutTheOwnClock()
    {
        var deck = Song();
        var player = Player();
        player.Follow(new TimecodeFollow.Command.Play("item", -T.Frame, 0.1), "item", deck, null);
        Assert.Equal(["slide:v1#0+actions"], host.Fired);
        Assert.False(player.Playing);
        Assert.Equal(0, clock.ActiveTimers);
        player.Follow(new TimecodeFollow.Command.Land("item", 4.5), "item", deck, null);
        Assert.Equal(["slide:v1#0+actions", "slide:c1#2"], host.Fired);
        Assert.Equal(4.5, player.PlayheadTime);
        Assert.True(player.Anchor?.Running);
        player.TimecodeHeld();
        Assert.False(player.Anchor?.Running);
    }

    [Fact]
    public void ASongMapLaysTheTimelineOutBeforePlaying()
    {
        var deck = Song();
        deck.Timelines = null;
        deck.SongMap = new SongMap
        {
            Recording = [Make.Cue("stem", TimelineTracks.Lane("keys"), 0, 20, "keys")],
            Sections = [new AudioSection { Id = "v", Name = "Verse", Start = 0, SectionId = "v" }, new AudioSection { Id = "c", Name = "Chorus", Start = 10, SectionId = "c" }],
        };
        var player = Player();
        Assert.True(player.LayOutFromMap("song", null));
        var laid = Assert.Single(host.Decks["song"].Timelines!);
        Assert.Equal(2, laid.MapLayout?.Count);
        Assert.False(player.LayOutFromMap("song", null));
    }
}

// C#: the logic of Sources/TimecodeInput.swift's TimecodeFollower.
public class TimecodeFollowerTests
{
    private sealed class Inputs : ITimecodeInputs
    {
        public LtcReader Reader { get; } = new();
        public bool Fail { get; set; }
        public ITimecodeReader OpenLtc(string deviceId, int channel) => Fail ? throw new IOException("unplugged") : Reader;
        public ITimecodeReader OpenMtc(string sourceId) => new MtcReader();
    }

    private sealed class Host(Service service, Presentation deck) : ITimecodeHost
    {
        public Service? CurrentService => service;
        public IReadOnlyList<ServiceItem> RunOfShow(Service s) => s.Items;
        public Presentation? Presentation(string id) => id == deck.Id ? deck : null;
        public TimecodeDisplay? PublishedTimecode { get; set; }
        public string? LiveContextId => null;
    }

    [Fact]
    public void ItLocksShowsTheTimecodeAndDrivesTheSong()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.Zero));
        List<TimelinePlayedBlock> blocks = [new("v", ["v1"])];
        var deck = Make.Deck("deck", ("v1", "v"));
        deck.Timelines = [Make.Timeline("t", true, 108_000, 60, [T.SlideCue("s", 0.5, T.Anchor(0, blocks)!)])];
        var service = MxU.Slides.Core.Json.MxuJson.Deserialize<Service>("""{"id":"s","name":"Sunday","serviceDate":"2026-07-12","items":[]}""");
        service.Items = [new ServiceItem { Id = "item", ItemKind = ServiceItemKind.Presentation, Name = "Song", RefId = "deck" }];
        var host = new Host(service, deck);
        var timelineHost = new TimelineHost();
        timelineHost.Decks["deck"] = deck;
        var player = new TimelinePlayer(timelineHost, new RecordingShow(), new MemoryLibrary(), clock: clock);
        var inputs = new Inputs();
        var follower = new TimecodeFollower(host, inputs, player, new TimecodeSettings(DeviceId: "interface"), clock);
        Assert.Same(follower, player.Timecode);
        follower.Start();
        Assert.True(follower.HasInput);

        // A second of LTC from 01:00:00:00, fed as the audio callback would.
        var writer = new LtcSignal.Writer(30);
        writer.Frames(Enumerable.Range(108_000, 30).Select(n => LtcSignal.Label(n, 30)));
        var samples = writer.Samples.ToArray();
        const int chunk = 1600;
        for (var start = 0; start + chunk <= samples.Length; start += chunk)
        {
            inputs.Reader.Read(samples.AsSpan(start, chunk), LtcSignal.SampleRate, clock.HostSeconds);
            clock.Advance(chunk / LtcSignal.SampleRate);
        }
        Assert.True(follower.IsLocked);
        Assert.Equal(TimecodeRate.Fps30, follower.DetectedRate);
        Assert.Equal("item", follower.SongItemId);
        Assert.Equal(["slide:v1#0+actions"], timelineHost.Fired);
        Assert.NotNull(host.PublishedTimecode);
        var status = follower.Status();
        Assert.Equal(("ltc", "locked", "30", "item"), (status.Source, status.State, status.Rate, status.ServiceItemId));
        Assert.Equal(("Locked", TimecodeStatusTone.Green), TimecodeFollower.StatusWord(follower.Reading, true, false));

        clock.Advance(1);
        Assert.Equal(TimecodeClock.State.Stopped, follower.Reading.State);
        Assert.Equal("Stopped", TimecodeFollower.StatusWord(follower.Reading, true, false).Word);
    }

    [Fact]
    public void SettingsChangeAndAMissingDeviceSaysSo()
    {
        var deck = Make.Deck("deck");
        var service = MxU.Slides.Core.Json.MxuJson.Deserialize<Service>("""{"id":"s","name":"Sunday","serviceDate":"2026-07-12","items":[]}""");
        var inputs = new Inputs { Fail = true };
        var follower = new TimecodeFollower(new Host(service, deck), inputs, null, clock: new ManualClock(DateTimeOffset.UnixEpoch));
        var saves = 0;
        follower.SettingsChanged += () => saves++;
        follower.SetInput(new TimecodeFollower.Input.Audio("gone"));
        Assert.Equal("Not connected", follower.Problem);
        follower.SetOffset(99);
        Assert.Equal(30, follower.OffsetFrames);
        follower.SetInput(new TimecodeFollower.Input.Midi("port"));
        Assert.Null(follower.Problem);
        Assert.Equal(TimecodeSource.Mtc, follower.Source);
        follower.SetInput(new TimecodeFollower.Input.None());
        Assert.False(follower.HasInput);
        Assert.Equal(new TimecodeSettings(TimecodeSource.Mtc, null, null, 0, 30, true, false), follower.Settings);
        Assert.Equal(4, saves);
        Assert.Equal("+2 frames", TimecodeFollower.OffsetText(2));
        Assert.Equal("--:--:--:--", TimecodeFollower.LabelText(follower.Reading));
        Assert.Equal(("Off", TimecodeStatusTone.Secondary), TimecodeFollower.StatusWord(follower.Reading, false, true));
    }
}
