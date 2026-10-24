using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;
using static MxU.Slides.Core.Stage.ServiceTrackingTimers;

namespace MxU.Slides.Core.Tests.Stage;

// Port of SlideSceneTests/ServiceTrackingTimersTests.swift. The merge cases file the timers
// into the Mac's timer board, which Windows doesn't have; TimerControllerTests covers how
// SetSystemTimers keeps them after the user's timers. The ServiceTiming cases check the C#
// port of the non-UI half of Sources/ServiceTimingController.swift.
public class ServiceTrackingTimersTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void RemainingCountsDownFromTheAnchorAndFreezesWhenNotRunning()
    {
        var running = Remaining(Subject.Item, 1800, 1500, running: true, Now);
        Assert.Equal("service_tracking.item.remaining", running.Id);
        Assert.Equal("Item Remaining", running.Name);
        Assert.Equal(1500, running.Value(Now), 3);
        Assert.Equal(1490, running.Value(Now.AddSeconds(10)), 3);
        Assert.True(IsPermanent(running.Id));

        var paused = Remaining(Subject.Item, 1800, 1500, running: false, Now);
        Assert.Equal(1500, paused.Value(Now.AddSeconds(60)), 3);

        var overrun = Remaining(Subject.Item, 300, -30, running: true, Now);
        Assert.Equal(TimerUrgency.Overrun, overrun.Urgency(Now));
    }

    [Fact]
    public void FiresStartLocalTiming()
    {
        Assert.False(LocalTiming(fired: false));
        Assert.True(LocalTiming(fired: true));
    }

    [Fact]
    public void LocalTimingEndsWhenThePlannedServiceWouldOrPastAnItemRunningOver()
    {
        const double hour = 3600;
        Assert.Equal(Now.AddSeconds(hour), LocalTimingEnds(Now, hour, Now.AddSeconds(300), 300)); // walked away early: the planned service's end
        Assert.Equal(Now.AddSeconds(3000 + 1200 + 900), LocalTimingEnds(Now, hour, Now.AddSeconds(3000), 1200)); // the message runs over the plan
        Assert.Equal(Now.AddSeconds(60 + hour), LocalTimingEnds(Now, 0, Now.AddSeconds(60), 0)); // no lengths: an hour after the last fire
        Assert.Equal(1500, PlannedSeconds([300, null, 1200]));
    }

    [Fact]
    public void ElapsedCountsUpAndUnplannedRemainingIsIdle()
    {
        var elapsed = Elapsed(Subject.Service, 600, 0, running: true, Now);
        Assert.Equal(TimerMode.CountUp, elapsed.Mode);
        Assert.Equal(605, elapsed.Elapsed(Now.AddSeconds(5)), 3);

        var idle = Remaining(Subject.Item, 0, null, running: true, Now);
        Assert.False(idle.IsRunning);
        Assert.Equal(0, idle.Value(Now.AddSeconds(100)), 3);
        Assert.Equal(10, AllIds.Count);
        Assert.Equal("Timeline Remaining", Name(Subject.Timeline, Face.Remaining));
        Assert.Contains("service_tracking.timeline.elapsed", AllIds);
    }

    [Fact]
    public void RetiredPreAndPostServiceIdsStayPermanentAndDropOnMerge()
    {
        var retired = new TimerSnapshot("Pre-Service Remaining", TimerMode.Countdown, id: "service_tracking.pre_service.remaining");
        Assert.True(IsPermanent(retired.Id));
        Assert.Null(SubjectAndFace(retired.Id));

        var timers = new TimerController(new FixedClock(Now));
        timers.SetSystemTimers([retired]);
        timers.SetSystemTimers([Idle(Subject.Video, Face.Remaining)]);
        Assert.DoesNotContain(timers.Timers, t => t.Id == retired.Id); // a refresh replaces every permanent timer
        Assert.Empty(timers.Export()); // and none of them is ever saved
    }

    [Fact]
    public void PermanentIdsParseBackToTheirSubjectAndFace()
    {
        foreach (var id in AllIds)
        {
            var parsed = SubjectAndFace(id);
            Assert.NotNull(parsed);
            Assert.Equal(id, Id(parsed.Value.Subject, parsed.Value.Face));
            Assert.NotEmpty(Description(parsed.Value.Subject, parsed.Value.Face));
        }
        Assert.Null(SubjectAndFace("user-timer"));
    }

    [Fact]
    public void ARefreshThatReadsTheSameIsNotNews()
    {
        List<TimerSnapshot> first = [Remaining(Subject.Item, 300, 200, running: true, Now), Idle(Subject.Video, Face.Remaining)];
        List<TimerSnapshot> later = [Remaining(Subject.Item, 300, 195, running: true, Now.AddSeconds(5)), Idle(Subject.Video, Face.Remaining)];
        Assert.True(Tracks(first, later, Now.AddSeconds(5))); // re-anchored, same value
        List<TimerSnapshot> jumped = [Remaining(Subject.Item, 300, 100, running: true, Now.AddSeconds(5)), Idle(Subject.Video, Face.Remaining)];
        Assert.False(Tracks(first, jumped, Now.AddSeconds(5)));
        List<TimerSnapshot> stopped = [Remaining(Subject.Item, 300, 195, running: false, Now.AddSeconds(5)), Idle(Subject.Video, Face.Remaining)];
        Assert.False(Tracks(first, stopped, Now.AddSeconds(5)));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // MARK: Timing a service

    private static ServiceItem Row(string id, ServiceItemKind kind = ServiceItemKind.Presentation, int? duration = null) =>
        new() { Id = id, ItemKind = kind, Name = id, RefId = "", Duration = duration };

    private static Service Service() => new()
    {
        Id = "svc", Name = "Sunday", ServiceDate = "2026-10-25",
        Items =
        [
            Row("worship", ServiceItemKind.Header), Row("song1", duration: 300), Row("song2", duration: 240),
            Row("word", ServiceItemKind.Header), Row("sermon", duration: 1800), Row("close", ServiceItemKind.Media, duration: 60),
        ],
    };

    private static TimerSnapshot Pick(List<TimerSnapshot> timers, Subject subject, Face face) => timers.Single(t => t.Id == Id(subject, face));

    [Fact]
    public void NothingFiredIsAllIdle()
    {
        var timing = new ServiceTiming();
        var service = Service();
        var timers = timing.Snapshots(service, service.Items, Now);
        Assert.Equal(10, timers.Count);
        Assert.All(timers, t => Assert.False(t.IsRunning));
        Assert.False(timing.IsTiming(service));
        Assert.Equal(AllIds, timers.Select(t => t.Id));
    }

    [Fact]
    public void FiringAnItemStartsTheItemSectionAndServiceFaces()
    {
        var timing = new ServiceTiming();
        var service = Service();
        timing.ServiceChanged(service.Id);
        Assert.True(timing.NoteItemFired("song1", Now));
        Assert.False(timing.NoteItemFired("song1", Now.AddSeconds(30))); // firing it again (its next slide) changes nothing

        var at = Now.AddSeconds(100);
        var timers = timing.Snapshots(service, service.Items, at);
        Assert.Equal(200, Pick(timers, Subject.Item, Face.Remaining).Value(at), 3);
        Assert.Equal(100, Pick(timers, Subject.Item, Face.Elapsed).Value(at), 3);
        Assert.Equal(440, Pick(timers, Subject.Section, Face.Remaining).Value(at), 3); // the two songs under "worship"
        Assert.Equal(2300, Pick(timers, Subject.Service, Face.Remaining).Value(at), 3); // every row's length
        Assert.True(timing.IsTiming(service));
        Assert.Equal("song2", timing.NextRow(service.Items)?.Id);
    }

    [Fact]
    public void TheSectionCountsFromItsFirstFiredItem()
    {
        var timing = new ServiceTiming();
        var service = Service();
        timing.NoteItemFired("song1", Now);
        timing.NoteItemFired("song2", Now.AddSeconds(320));
        timing.NoteItemFired("sermon", Now.AddSeconds(600));
        var at = Now.AddSeconds(700);
        var timers = timing.Snapshots(service, service.Items, at);
        Assert.Equal(100, Pick(timers, Subject.Section, Face.Elapsed).Value(at), 3); // "word" started with the sermon
        Assert.Equal(1760, Pick(timers, Subject.Section, Face.Remaining).Value(at), 3); // sermon + close, less 100
        Assert.Equal(700, Pick(timers, Subject.Service, Face.Elapsed).Value(at), 3); // from the very first fire
        Assert.Equal(1700, Pick(timers, Subject.Item, Face.Remaining).Value(at), 3);
    }

    [Fact]
    public void AnItemWithNoLengthHasAnIdleRemainingButCountsUp()
    {
        var timing = new ServiceTiming();
        var service = Service();
        service.Items[1].Duration = null;
        timing.NoteItemFired("song1", Now);
        var at = Now.AddSeconds(10);
        var timers = timing.Snapshots(service, service.Items, at);
        Assert.False(Pick(timers, Subject.Item, Face.Remaining).IsRunning);
        Assert.Equal(10, Pick(timers, Subject.Item, Face.Elapsed).Value(at), 3);
    }

    [Fact]
    public void ChangingServiceOrClearingStopsTheTimers()
    {
        var timing = new ServiceTiming();
        var service = Service();
        timing.ServiceChanged("svc");
        timing.NoteItemFired("song1", Now);
        timing.ServiceChanged("svc"); // the same service again: nothing changes
        Assert.Equal("song1", timing.CurrentServiceItemId);
        timing.ServiceChanged("other");
        Assert.Null(timing.CurrentServiceItemId);
        timing.NoteItemFired("song1", Now);
        timing.Clear();
        Assert.False(timing.IsTiming(service));
    }

    [Fact]
    public void AHiddenCurrentRowLeavesTheSpansIdle()
    {
        var timing = new ServiceTiming();
        var service = Service();
        timing.NoteItemFired("song1", Now);
        var visible = service.Items.Where(i => i.Id != "song1").ToList();
        var timers = timing.Snapshots(service, visible, Now.AddSeconds(5));
        Assert.False(Pick(timers, Subject.Section, Face.Elapsed).IsRunning);
        Assert.True(Pick(timers, Subject.Item, Face.Elapsed).IsRunning);
    }

    [Fact]
    public void TheVideoAndTimelineFacesFollowWhatPlays()
    {
        var timing = new ServiceTiming();
        var video = new VideoCountdown("Bumper", 90, 30, Now, IsPlaying: true);
        var timers = timing.Snapshots(null, [], Now.AddSeconds(10), video, new TimelineProgress(20, 200, Playing: false));
        var at = Now.AddSeconds(15);
        Assert.Equal(45, Pick(timers, Subject.Video, Face.Remaining).Value(at), 3);
        Assert.Equal(45, Pick(timers, Subject.Video, Face.Elapsed).Value(at), 3);
        Assert.Equal(180, Pick(timers, Subject.Timeline, Face.Remaining).Value(at), 3); // paused: frozen
        Assert.False(Pick(timers, Subject.Timeline, Face.Elapsed).IsRunning);
    }

    [Fact]
    public void LocalTimingEndsAfterThePlannedService()
    {
        var timing = new ServiceTiming();
        var service = Service();
        timing.NoteItemFired("song1", Now);
        Assert.Equal(Now.AddSeconds(2400), timing.EndsAt(service.Items, "song1", Now)); // the planned 40 minutes
        Assert.Equal(Now.AddSeconds(3000 + 1800 + 900), timing.EndsAt(service.Items, "sermon", Now.AddSeconds(3000))); // the sermon runs over
    }
}
