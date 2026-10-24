using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;
using static MxU.Slides.Core.Stage.ServiceTrackingTimers;

namespace MxU.Slides.Core.Tests.Stage;

// Port of SlideSceneTests/ServiceTrackingTimersTests.swift.
public class ServiceTrackingTimersTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void RemainingCountsDownFromTheAnchorAndFreezesWhenNotRunning()
    {
        var running = Remaining(Subject.Item, plannedSeconds: 1800, remainingSeconds: 1500, running: true, Now);
        Assert.Equal("service_tracking.item.remaining", running.Id);
        Assert.Equal("Item Remaining", running.Name);
        Assert.Equal(1500, running.Value(Now), 3);
        Assert.Equal(1490, running.Value(Now.AddSeconds(10)), 3);
        Assert.True(IsPermanent(running.Id));

        var paused = Remaining(Subject.Item, plannedSeconds: 1800, remainingSeconds: 1500, running: false, Now);
        Assert.Equal(1500, paused.Value(Now.AddSeconds(60)), 3);

        var overrun = Remaining(Subject.Item, plannedSeconds: 300, remainingSeconds: -30, running: true, Now);
        Assert.Equal(TimerUrgency.Overrun, overrun.Urgency(Now));
    }

    [Fact]
    public void FiresStartLocalTiming()
    {
        Assert.Null(LocalTimingFor(fired: false));
        Assert.Equal(LocalTiming.Local, LocalTimingFor(fired: true));
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
        var elapsed = Elapsed(Subject.Service, elapsedSeconds: 600, running: true, Now);
        Assert.Equal(TimerMode.CountUp, elapsed.Mode);
        Assert.Equal(605, elapsed.Elapsed(Now.AddSeconds(5)), 3);

        var idle = Remaining(Subject.Item, plannedSeconds: 0, remainingSeconds: null, running: true, Now);
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

        var board = new TimerBoard([TimerBoardNode.Timer(retired.Id)]);
        var merged = MergeInto(SystemSet(Now, running: false), [retired], board);
        Assert.DoesNotContain(merged.Timers, t => t.Id == retired.Id);
        Assert.DoesNotContain(retired.Id, merged.Board.AllTimerIds);
        Assert.False(merged.UserTimersChanged);
    }

    [Fact]
    public void PermanentIdsParseBackToTheirSubjectAndFace()
    {
        foreach (var id in AllIds)
        {
            var parsed = SubjectAndFace(id);
            Assert.NotNull(parsed);
            Assert.Equal(id, ServiceTrackingTimers.Id(parsed!.Value.Subject, parsed.Value.Face));
            Assert.NotEmpty(Description(parsed.Value.Subject, parsed.Value.Face));
        }
        Assert.Null(SubjectAndFace("user-timer"));
    }

    private static List<TimerSnapshot> SystemSet(DateTimeOffset at, bool running) =>
    [
        Remaining(Subject.Item, plannedSeconds: 300, remainingSeconds: 200, running, at),
        Idle(Subject.Video, Face.Remaining),
    ];

    [Fact]
    public void MergeFilesThePermanentSetAndFlagsEveryStoreTheFirstTime()
    {
        var user = new TimerSnapshot("Walk-in", TimerMode.Countdown, id: "user-1", durationSeconds: 600);
        var merged = MergeInto(SystemSet(Now, running: false), [user], new TimerBoard());

        Assert.Equal(["service_tracking.item.remaining", "service_tracking.video.remaining", "user-1"], merged.Timers.Select(t => t.Id));
        Assert.Equal(["service_tracking.item.remaining", "service_tracking.video.remaining"], merged.Board.Folder(FolderId)?.TimerIds);
        Assert.True(merged.TimersChanged);
        Assert.True(merged.BoardChanged);
        Assert.False(merged.UserTimersChanged); // one user timer has nowhere to move
    }

    [Fact]
    public void TheSameSetAgainMovesNothing()
    {
        var user = new TimerSnapshot("Walk-in", TimerMode.Countdown, id: "user-1", durationSeconds: 600);
        var first = MergeInto(SystemSet(Now, running: false), [user], new TimerBoard());
        var again = MergeInto(SystemSet(Now.AddSeconds(5), running: false), first.Timers, first.Board);
        Assert.Equal(first.Timers, again.Timers);
        Assert.False(again.TimersChanged); // idle faces carry no anchor, so a later refresh is the same list
        Assert.False(again.UserTimersChanged);
        Assert.False(again.BoardChanged);
    }

    [Fact]
    public void ANewAnchorRepublishesWithoutSavingAnything()
    {
        var first = MergeInto(SystemSet(Now, running: true), [], new TimerBoard());
        var later = MergeInto(SystemSet(Now.AddSeconds(5), running: true), first.Timers, first.Board);
        Assert.True(later.TimersChanged); // a running face re-anchors at the refresh
        Assert.False(later.UserTimersChanged);
        Assert.False(later.BoardChanged);
    }

    [Fact]
    public void UserTimersReorderedByTheBoardAreSaved()
    {
        var a = new TimerSnapshot("A", TimerMode.Countdown, id: "a");
        var b = new TimerSnapshot("B", TimerMode.Countdown, id: "b");
        var board = new TimerBoard([TimerBoardNode.Timer("b"), TimerBoardNode.Timer("a")]);
        var merged = MergeInto([], [a, b], board);
        Assert.Equal(["b", "a"], merged.Timers.Select(t => t.Id));
        Assert.True(merged.UserTimersChanged);
    }
}
