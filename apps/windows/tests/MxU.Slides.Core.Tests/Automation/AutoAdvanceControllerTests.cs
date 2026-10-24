using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Automation;

/// <summary>
/// A live show for auto-advance to drive: firing a slide lands it in a real <see cref="ShowState"/>
/// and arms the controller, as the app does; firing the next service row is recorded.
/// </summary>
internal sealed class AutoAdvanceHost(ManualClock clock) : IAutoAdvanceHost
{
    public ShowState Show { get; } = new();
    public double HostNow => clock.HostSeconds;
    public string? LiveContextId { get; set; }
    public int? LiveOccurrence { get; set; }
    public Dictionary<string, MediaItem> MediaItems { get; } = [];
    public Dictionary<string, MediaTransportState> Transports { get; } = [];
    public HashSet<string> ServiceRows { get; } = [];
    public List<string> Fired { get; } = [];
    public AutoAdvanceController? Controller { get; set; }

    public MediaItem? Media(string id) => MediaItems.GetValueOrDefault(id);
    public MediaTransportState? Transport(string mediaId) => Transports.GetValueOrDefault(mediaId);
    public bool IsMediaLive(string mediaId) => Show.LiveMedia.Values.Any(m => m.MediaId == mediaId);
    public bool ServiceContains(string itemId) => ServiceRows.Contains(itemId);
    public void FireNextServiceItem(string itemId) => Fired.Add($"next-after:{itemId}");

    public void FireSlide(Slide slide, Presentation presentation, string? arrangementId, string? contextId, int occurrence)
    {
        Show.Fire(slide, presentation, null, arrangementId, atHostTime: HostNow);
        LiveContextId = contextId;
        LiveOccurrence = occurrence;
        Fired.Add($"{slide.Id}");
        Controller?.SlideLanded(slide, presentation);
    }
}

// The Mac's auto-advance lives in ServiceControls (app code, no unit tests); these check the
// C# port of it against the behaviour the Mac shows: timers, loops, video ends, builds and holds.
public class AutoAdvanceControllerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private static (AutoAdvanceController Controller, AutoAdvanceHost Host, ManualClock Clock) Make()
    {
        var clock = new ManualClock(Start);
        var host = new AutoAdvanceHost(clock);
        var controller = new AutoAdvanceController(host, clock);
        host.Controller = controller;
        return (controller, host, clock);
    }

    private static Presentation Deck(AutoAdvance? advance, int count = 3, Func<int, Slide>? slide = null) => new()
    {
        Id = "deck", Name = "Announcements", PresentationKind = PresentationKind.Deck, ThemeId = "",
        Slides = [.. Enumerable.Range(0, count).Select(i => slide?.Invoke(i) ?? new Slide { Id = $"s{i}", Name = $"Slide {i + 1}", Objects = [] })],
        AutoAdvance = advance,
    };

    private static void Land(AutoAdvanceHost host, Presentation deck, int occurrence, string? item = null) =>
        host.FireSlide(deck.Slides[occurrence], deck, null, item, occurrence);

    private static MediaItem Video(string id, AutoAdvance? advance = null) => new()
    {
        Id = id, Name = id, MediaKind = MediaKind.Video, Classification = MediaClassification.Foreground, FileHash = "", FileName = id + ".mp4",
        FileStatus = MediaFileStatus.Ready, StatusDetail = "", Tags = [], Favorite = false, Collections = [], Loops = false, AutoAdvance = advance,
    };

    [Fact]
    public void ASlideTimerFiresTheNextSlideAndTheChipCountsDown()
    {
        var (controller, host, clock) = Make();
        var deck = Deck(new AutoAdvance { DelaySeconds = 5 });
        Land(host, deck, 0);
        Assert.NotNull(controller.Pending);
        Assert.Equal(5, controller.Remaining(clock.Now)!.Value, 3);
        clock.Advance(3);
        Assert.Equal(2, controller.Remaining(clock.Now)!.Value, 3);
        Assert.Equal(["s0"], host.Fired);
        clock.Advance(2.01);
        Assert.Equal(["s0", "s1"], host.Fired);
        Assert.Equal(1, host.LiveOccurrence);
        Assert.NotNull(controller.Pending); // slide 2 arms its own timer
    }

    [Fact]
    public void ASlideShowLoopsBackToItsFirstSlide()
    {
        var (_, host, clock) = Make();
        var deck = Deck(new AutoAdvance { DelaySeconds = 2, LoopToStart = true });
        Land(host, deck, 0);
        clock.Advance(6.5);
        Assert.Equal(["s0", "s1", "s2", "s0"], host.Fired);
    }

    [Fact]
    public void TheLastSlideMovesTheServiceOnOnlyFromAServiceRow()
    {
        var (_, host, clock) = Make();
        var deck = Deck(new AutoAdvance { DelaySeconds = 1 }, count: 1);
        host.ServiceRows.Add("row-1");
        Land(host, deck, 0, item: "row-1");
        clock.Advance(1.5);
        Assert.Equal(["s0", "next-after:row-1"], host.Fired);

        var (_, loose, looseClock) = Make();
        Land(loose, deck, 0);
        looseClock.Advance(5);
        Assert.Equal(["s0"], loose.Fired); // opened from the library: nowhere to go
    }

    [Fact]
    public void ASlidesOwnTimerBeatsItsPresentations()
    {
        var (controller, host, clock) = Make();
        var deck = Deck(new AutoAdvance { DelaySeconds = 10 }, slide: i => new Slide
        {
            Id = $"s{i}", Name = $"Slide {i + 1}", Objects = [], AutoAdvance = i == 0 ? new AutoAdvance { DelaySeconds = 2 } : null,
        });
        Land(host, deck, 0);
        Assert.Equal(2, controller.Remaining(clock.Now)!.Value, 3);
        clock.Advance(2.1);
        Assert.Equal(["s0", "s1"], host.Fired);
        Assert.Equal(9.9, controller.Remaining(clock.Now)!.Value, 3); // slide 2 uses the presentation's 10 s (0.1 s ago)
    }

    [Fact]
    public void ManualNextRestartsTheTimerFromTheNewSlide()
    {
        var (controller, host, clock) = Make();
        var deck = Deck(new AutoAdvance { DelaySeconds = 5 });
        Land(host, deck, 0);
        clock.Advance(4);
        Land(host, deck, 1); // the operator pressed Next
        clock.Advance(4);
        Assert.Equal(["s0", "s1"], host.Fired); // slide 1's old timer never fired
        Assert.Equal(1, controller.Remaining(clock.Now)!.Value, 3);
        clock.Advance(1.01);
        Assert.Equal(["s0", "s1", "s2"], host.Fired);
    }

    [Fact]
    public void AnotherSlideWithoutATimerStopsTheCountdown()
    {
        var (controller, host, clock) = Make();
        var timed = Deck(new AutoAdvance { DelaySeconds = 5 });
        var plain = Deck(null);
        Land(host, timed, 0);
        Land(host, plain, 2);
        Assert.Null(controller.Pending);
        clock.Advance(10);
        Assert.Equal(["s0", "s2"], host.Fired);
    }

    [Fact]
    public void ClearingTheSlideCancelsItsTimer()
    {
        var (controller, host, clock) = Make();
        Land(host, Deck(new AutoAdvance { DelaySeconds = 3 }), 0);
        host.Show.Clear(ShowFunction.Slides);
        controller.Cancel();
        Assert.Null(controller.Pending);
        clock.Advance(10);
        Assert.Equal(["s0"], host.Fired);
    }

    [Fact]
    public void HoldingKeepsTheTimeLeftAndReleasingCarriesOn()
    {
        var (controller, host, clock) = Make();
        Land(host, Deck(new AutoAdvance { DelaySeconds = 10 }), 0);
        clock.Advance(4);
        controller.SetPaused(true);
        Assert.True(controller.IsPaused);
        clock.Advance(30);
        Assert.Equal(6, controller.Remaining(clock.Now)!.Value, 3); // the chip shows the time it held
        Assert.Equal(["s0"], host.Fired);
        controller.SetPaused(false);
        clock.Advance(5.9);
        Assert.Equal(["s0"], host.Fired);
        clock.Advance(0.2);
        Assert.Equal(["s0", "s1"], host.Fired);
        Assert.False(controller.IsPaused); // the next slide's timer isn't held
    }

    [Fact]
    public void HoldingWithNothingPendingDoesNothing()
    {
        var (controller, _, _) = Make();
        controller.SetPaused(true);
        Assert.False(controller.IsPaused);
    }

    [Fact]
    public void AnyDelayShowsTheSlideForAMoment()
    {
        var (_, host, clock) = Make();
        Land(host, Deck(new AutoAdvance { DelaySeconds = 0 }), 0);
        clock.Advance(0.05);
        Assert.Equal(["s0"], host.Fired);
        clock.Advance(0.06);
        Assert.Equal(["s0", "s1"], host.Fired);
    }

    // MARK: Builds

    private static Slide BuildSlide(int i) => new()
    {
        Id = $"s{i}", Name = $"Slide {i + 1}",
        Objects =
        [
            new SlideObject
            {
                Id = "t", ObjectKind = SlideObjectKind.Text, Name = "T", Text = "Point",
                AnimationSteps = [new AnimationStep { Id = "c1", Kind = AnimationKind.In, Animation = StepAnimation.Fade, Trigger = AnimationTrigger.OnClick, DurationSeconds = 0.5 }],
            },
        ],
    };

    [Fact]
    public void ASlideWithBuildsWaitsForEveryPressThenCountsFromTheLastBuild()
    {
        var (controller, host, clock) = Make();
        var deck = Deck(new AutoAdvance { DelaySeconds = 3 }, slide: BuildSlide);
        Land(host, deck, 0);
        Assert.Null(controller.Pending); // never presses for the operator
        clock.Advance(10);
        Assert.Equal(["s0"], host.Fired);

        host.Show.AdvanceStep(clock.HostSeconds);
        controller.StepsChanged();
        Assert.Null(controller.Pending); // the build is still playing
        clock.Advance(0.6);
        Assert.NotNull(controller.Pending);
        clock.Advance(3.1);
        Assert.Equal(["s0", "s1"], host.Fired);
    }

    [Fact]
    public void TakingABuildBackStopsTheCount()
    {
        var (controller, host, clock) = Make();
        var deck = Deck(new AutoAdvance { DelaySeconds = 3 }, slide: BuildSlide);
        Land(host, deck, 0);
        host.Show.AdvanceStep(clock.HostSeconds);
        controller.StepsChanged();
        clock.Advance(1);
        Assert.NotNull(controller.Pending);
        host.Show.UnadvanceStep();
        controller.StepsChanged();
        Assert.Null(controller.Pending);
        clock.Advance(10);
        Assert.Equal(["s0"], host.Fired);
    }

    // MARK: After a video

    private static Presentation VideoDeck(AutoAdvance advance, string mediaId = "clip") =>
        Deck(null, slide: i => new Slide
        {
            Id = $"s{i}", Name = $"Slide {i + 1}", Objects = [], AutoAdvance = i == 0 ? advance : null,
            Background = i == 0 ? new CueMedia { MediaId = mediaId, Layer = CueMediaLayer.Videos, Loops = false } : null,
        });

    [Fact]
    public void AfterPlaybackWaitsForTheVideoThenItsDelay()
    {
        var (controller, host, clock) = Make();
        host.MediaItems["clip"] = Video("clip");
        host.Transports["clip"] = new MediaTransportState(8, 0, true, false, clock.Now);
        Land(host, VideoDeck(new AutoAdvance { DelaySeconds = 2, AfterPlayback = true }), 0);
        Assert.Equal("clip", controller.Pending?.MediaId);
        Assert.Equal(10, controller.Remaining(clock.Now)!.Value, 3); // 8 s of video + 2 s after it
        clock.Advance(7);
        Assert.Equal(["s0"], host.Fired);
        host.Transports["clip"] = new MediaTransportState(8, 8, false, false, clock.Now); // it played out
        clock.Advance(0.3);
        Assert.NotNull(controller.Pending?.FiresAt); // now the delay after it
        clock.Advance(2.1);
        Assert.Equal(["s0", "s1"], host.Fired);
    }

    [Fact]
    public void ANegativeDelayFiresTheNextSlideBeforeTheVideoEnds()
    {
        var (_, host, clock) = Make();
        host.MediaItems["clip"] = Video("clip");
        host.Transports["clip"] = new MediaTransportState(10, 0, true, false, clock.Now);
        Land(host, VideoDeck(new AutoAdvance { DelaySeconds = -2, AfterPlayback = true }), 0);
        clock.Advance(7.8);
        Assert.Equal(["s0"], host.Fired);
        clock.Advance(0.3);
        Assert.Equal(["s0", "s1"], host.Fired); // two seconds before the end
    }

    [Fact]
    public void ALoopingVideoCountsOnePassFromWhenTheSlideLanded()
    {
        var (_, host, clock) = Make();
        host.MediaItems["clip"] = Video("clip");
        host.Transports["clip"] = new MediaTransportState(4, 1.5, true, true, clock.Now);
        Land(host, VideoDeck(new AutoAdvance { DelaySeconds = 0, AfterPlayback = true }), 0);
        clock.Advance(3.9);
        Assert.Equal(["s0"], host.Fired);
        clock.Advance(0.3);
        Assert.Equal(["s0", "s1"], host.Fired);
    }

    [Fact]
    public void AVideoThatNeverOpensCountsAsEndedAfterTheGrace()
    {
        var (_, host, clock) = Make();
        host.MediaItems["clip"] = Video("clip");
        Land(host, VideoDeck(new AutoAdvance { DelaySeconds = 0, AfterPlayback = true }), 0);
        clock.Advance(AutoAdvanceController.WatchGraceSeconds - 0.5);
        Assert.Equal(["s0"], host.Fired);
        clock.Advance(1);
        Assert.Equal(["s0", "s1"], host.Fired);
    }

    [Fact]
    public void HoldingThroughTheVideosEndSkipsItAndTheSlideStays()
    {
        var (controller, host, clock) = Make();
        host.MediaItems["clip"] = Video("clip");
        host.Transports["clip"] = new MediaTransportState(5, 0, true, false, clock.Now);
        Land(host, VideoDeck(new AutoAdvance { DelaySeconds = 0, AfterPlayback = true }), 0);
        controller.SetPaused(true);
        host.Transports["clip"] = new MediaTransportState(5, 5, false, false, clock.Now);
        clock.Advance(1);
        Assert.Null(controller.Pending);
        controller.SetPaused(false);
        clock.Advance(20);
        Assert.Equal(["s0"], host.Fired);
    }

    [Fact]
    public void PollNowReactsToAVideoEndingAtOnce()
    {
        var (_, host, clock) = Make();
        host.MediaItems["clip"] = Video("clip");
        host.Transports["clip"] = new MediaTransportState(5, 0, true, false, clock.Now);
        var controller = host.Controller!;
        Land(host, VideoDeck(new AutoAdvance { DelaySeconds = 0, AfterPlayback = true }), 0);
        host.Transports["clip"] = new MediaTransportState(5, 5, false, false, clock.Now);
        controller.PollNow();
        Assert.Equal(["s0", "s1"], host.Fired);
    }

    [Fact]
    public void AnObjectsVideoCountsWhenTheSlideHasNoForegroundVideo()
    {
        var (controller, host, clock) = Make();
        host.MediaItems["fill"] = Video("fill");
        var deck = Deck(new AutoAdvance { DelaySeconds = 0, AfterPlayback = true }, slide: i => new Slide
        {
            Id = $"s{i}", Name = $"Slide {i + 1}",
            Objects = [new SlideObject { Id = "v", ObjectKind = SlideObjectKind.Shape, Name = "Video", Text = "", Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = "fill" } }],
        });
        Land(host, deck, 0);
        Assert.Equal("fill", controller.Pending?.MediaId);
        _ = clock;
    }

    [Fact]
    public void AfterPlaybackWithNoVideoIsAPlainTimer()
    {
        var (controller, host, clock) = Make();
        Land(host, Deck(new AutoAdvance { DelaySeconds = 2, AfterPlayback = true }), 0);
        Assert.NotNull(controller.Pending?.FiresAt);
        clock.Advance(2.1);
        Assert.Equal(["s0", "s1"], host.Fired);
    }

    // MARK: Media service rows

    [Fact]
    public void AMediaRowFiresTheNextRowAfterItsDelay()
    {
        var (controller, host, clock) = Make();
        var bumper = Video("bumper", new AutoAdvance { DelaySeconds = 4 });
        host.ServiceRows.Add("row-bumper");
        host.Show.Fire(new CueMedia { MediaId = "bumper", Layer = CueMediaLayer.Videos }, LayerKind.Videos);
        controller.MediaFired(bumper, "row-bumper");
        Assert.Equal(4, controller.Remaining(clock.Now)!.Value, 3);
        clock.Advance(4.1);
        Assert.Equal(["next-after:row-bumper"], host.Fired);
    }

    [Fact]
    public void AMediaRowAfterPlaybackFiresWhenItsVideoEnds()
    {
        var (controller, host, clock) = Make();
        var bumper = Video("bumper", new AutoAdvance { DelaySeconds = 0, AfterPlayback = true });
        host.MediaItems["bumper"] = bumper;
        host.ServiceRows.Add("row-bumper");
        host.Transports["bumper"] = new MediaTransportState(6, 0, true, false, clock.Now);
        host.Show.Fire(new CueMedia { MediaId = "bumper", Layer = CueMediaLayer.Videos }, LayerKind.Videos);
        controller.MediaFired(bumper, "row-bumper");
        clock.Advance(3);
        Assert.Empty(host.Fired);
        host.Transports["bumper"] = new MediaTransportState(6, 6, false, false, clock.Now);
        clock.Advance(0.3);
        Assert.Equal(["next-after:row-bumper"], host.Fired);
    }

    [Fact]
    public void ClearingAMediaRowsVideoCancelsItsAdvance()
    {
        var (controller, host, clock) = Make();
        var bumper = Video("bumper", new AutoAdvance { DelaySeconds = 0, AfterPlayback = true });
        host.MediaItems["bumper"] = bumper;
        host.ServiceRows.Add("row-bumper");
        host.Transports["bumper"] = new MediaTransportState(6, 0, true, false, clock.Now);
        host.Show.Fire(new CueMedia { MediaId = "bumper", Layer = CueMediaLayer.Videos }, LayerKind.Videos);
        controller.MediaFired(bumper, "row-bumper");
        host.Show.Clear(ShowFunction.Media);
        clock.Advance(1);
        Assert.Null(controller.Pending);
        clock.Advance(20);
        Assert.Empty(host.Fired);
    }

    [Fact]
    public void FiringOtherMediaCancelsAMediaRowsTimerButNotTheSlides()
    {
        var (controller, host, clock) = Make();
        Land(host, Deck(new AutoAdvance { DelaySeconds = 5 }), 0);
        var bumper = Video("bumper", new AutoAdvance { DelaySeconds = 2 });
        host.ServiceRows.Add("row-bumper");
        host.Show.Fire(new CueMedia { MediaId = "bumper", Layer = CueMediaLayer.Videos }, LayerKind.Videos);
        controller.MediaFired(bumper, "row-bumper");
        Assert.Equal(2, controller.Remaining(clock.Now)!.Value, 3); // the chip shows the media row's timer
        controller.CancelMedia();
        Assert.Equal(5, controller.Remaining(clock.Now)!.Value, 3); // and the slide's again once that one goes
        clock.Advance(5.1);
        Assert.Equal(["s0", "s1"], host.Fired);
    }

    [Fact]
    public void EditingTheLiveSlidesPresentationArmsItsNewTimer()
    {
        var (controller, host, clock) = Make();
        var deck = Deck(null);
        Land(host, deck, 0);
        Assert.Null(controller.Pending);
        deck.AutoAdvance = new AutoAdvance { DelaySeconds = 2 };
        host.Show.PresentationEdited(deck);
        controller.PresentationEdited(deck.Id);
        Assert.NotNull(controller.Pending);
        clock.Advance(2.1);
        Assert.Equal(["s0", "s1"], host.Fired);
    }

    [Fact]
    public void AHandOffStopsBothTimers()
    {
        var (controller, host, clock) = Make();
        Land(host, Deck(new AutoAdvance { DelaySeconds = 2 }), 0);
        controller.HandingOff();
        Assert.Null(controller.Pending);
        clock.Advance(5);
        Assert.Equal(["s0"], host.Fired);
    }

    /// <summary>A UI thread that's busy: posted callbacks wait until the test drains them.</summary>
    private sealed class BusyUiThread : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> posted = new();
        public override void Post(SendOrPostCallback d, object? state) => posted.Enqueue((d, state));
        public void Drain()
        {
            while (posted.TryDequeue(out var item)) item.Callback(item.State);
        }
    }

    private static (AutoAdvanceController Controller, AutoAdvanceHost Host, ManualClock Clock, BusyUiThread Ui) MakeBusy()
    {
        var clock = new ManualClock(Start);
        var host = new AutoAdvanceHost(clock);
        var ui = new BusyUiThread();
        var controller = new AutoAdvanceController(host, clock, ui);
        host.Controller = controller;
        return (controller, host, clock, ui);
    }

    [Fact]
    public void HoldingAsTheTimeComesWinsOverTheTimerAlreadyOnItsWay()
    {
        // Found by the self-test: the timer's moment came while the UI thread was busy, then the
        // operator held it; the old timer's callback must not advance the slide anyway.
        var (controller, host, clock, ui) = MakeBusy();
        Land(host, Deck(new AutoAdvance { DelaySeconds = 1 }), 0);
        clock.Advance(1.05); // the timer elapsed: its callback is queued for the UI thread
        controller.SetPaused(true);
        ui.Drain();
        Assert.Equal(["s0"], host.Fired);
        Assert.True(controller.IsPaused);
        controller.SetPaused(false);
        clock.Advance(0.2);
        ui.Drain();
        Assert.Equal(["s0", "s1"], host.Fired);
    }

    [Fact]
    public void AnOldSlidesPollNeverCancelsTheNextSlidesTimer()
    {
        var (controller, host, clock, ui) = MakeBusy();
        host.MediaItems["clip"] = Video("clip");
        host.Transports["clip"] = new MediaTransportState(30, 0, true, false, clock.Now);
        Land(host, VideoDeck(new AutoAdvance { DelaySeconds = 0, AfterPlayback = true }), 0);
        clock.Advance(0.3); // a poll tick is queued
        Land(host, Deck(new AutoAdvance { DelaySeconds = 2 }), 1); // the operator moved on
        ui.Drain();
        Assert.NotNull(controller.Pending?.FiresAt);
        clock.Advance(2.1);
        ui.Drain();
        Assert.Equal(["s0", "s1", "s2"], host.Fired);
    }

    [Fact]
    public void ChangedIsRaisedWhenTheChipShouldRedraw()
    {
        var (controller, host, clock) = Make();
        var changes = 0;
        controller.Changed += () => changes++;
        Land(host, Deck(new AutoAdvance { DelaySeconds = 2 }), 0);
        Assert.Equal(1, changes);
        controller.SetPaused(true);
        controller.SetPaused(false);
        Assert.Equal(3, changes);
        _ = clock;
    }
}
