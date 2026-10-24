using MxU.Slides.Core.Automation;
using Mode = MxU.Slides.Core.Automation.TimelineBlockEdit.Mode;

namespace MxU.Slides.Core.Tests.Automation;

// Ported from PresenterCoreTests/TimelineGeometryTests.swift (the SwiftUI source sweep is not ported).
public class TimelineGeometryTests
{
    [Fact]
    public void PinchScalesFromTheGestureStartWithinTheRange()
    {
        Assert.Equal(240, TimelineZoom.Pinched(120, 2));
        Assert.Equal(480, TimelineZoom.Pinched(120, 10));
        Assert.Equal(24, TimelineZoom.Pinched(120, 0.1));
    }

    [Fact]
    public void ButtonsStepByThirtyPercentWithinTheRange()
    {
        Assert.Equal(130, TimelineZoom.ZoomedIn(100));
        Assert.Equal(480, TimelineZoom.ZoomedIn(400));
        Assert.Equal(100, TimelineZoom.ZoomedOut(130));
        Assert.Equal(24, TimelineZoom.ZoomedOut(30));
    }

    [Fact]
    public void FitPacksTheSecondsIntoTheViewportLessTheReserve()
    {
        Assert.Equal(121, TimelineZoom.Fit(4, 600));
        Assert.Equal(480, TimelineZoom.Fit(0.1, 600));
        Assert.Equal(24, TimelineZoom.Fit(100, 600));
        Assert.Null(TimelineZoom.Fit(0, 600));
        Assert.Null(TimelineZoom.Fit(4, 140));
    }

    private static readonly TimelineMap Map = new(
    [
        new(0, 1, 120),
        new(1.5, 0.6, TimelineSegment.WidthFor(0.6, false, 120, 96)),
        new(2.1, 2, 240),
    ], 120);

    [Fact]
    public void SegmentWidthsFitContentAndFloorEmptySegments()
    {
        Assert.Equal(60, TimelineSegment.WidthFor(0.5, true, 120, 96));
        Assert.Equal(96, Map.Segments[1].Width);
        Assert.Equal(456, Map.Width);
        Assert.Equal(216, Map.XOfSegment(2));
    }

    [Fact]
    public void TimeMapsToXAndGapsPinToThePreviousEnd()
    {
        Assert.Equal(60, Map.XOfTime(0.5));
        Assert.Equal(120, Map.XOfTime(1.2));
        Assert.Equal(144, Map.XOfTime(1.7));
        Assert.Equal(216, Map.XOfTime(2.1));
        Assert.Equal(456, Map.XOfTime(10));
    }

    [Fact]
    public void XMapsBackToTimeAndItsSegment()
    {
        Assert.Equal((0.5, 0), Map.Locate(60));
        var second = Map.Locate(132)!.Value;
        Assert.True(Math.Abs(second.Time - 1.6) < 1e-9 && second.Segment == 1);
        var past = Map.Locate(1000)!.Value;
        Assert.True(Math.Abs(past.Time - 4.1) < 1e-9 && past.Segment == 2);
        Assert.Equal((0.0, 0), Map.Locate(-5));
        Assert.Null(new TimelineMap([], 120).Locate(10));
    }

    [Fact]
    public void SegmentUnderXAndBoundariesNearX()
    {
        Assert.Equal(0, Map.SegmentIndex(119));
        Assert.Equal(1, Map.SegmentIndex(120));
        Assert.Equal(2, Map.SegmentIndex(1000));
        Assert.Null(new TimelineMap([], 120).SegmentIndex(0));
        Assert.Equal(1, Map.Boundary(130, 12));
        Assert.Equal(2, Map.Boundary(205, 12));
        Assert.Null(Map.Boundary(170, 12));
        Assert.Null(Map.Boundary(456, 12));
    }

    [Fact]
    public void ThePressPointPicksTheMode()
    {
        Assert.Equal(Mode.Stretch, TimelineBlockEdit.ModeFor(145, 50, 100));
        Assert.Equal(Mode.Trim, TimelineBlockEdit.ModeFor(55, 50, 100));
        Assert.Equal(Mode.Move, TimelineBlockEdit.ModeFor(100, 50, 100));
        Assert.Equal(Mode.Stretch, TimelineBlockEdit.ModeFor(25, 0, 30));
        Assert.Equal(Mode.Move, TimelineBlockEdit.ModeFor(5, 0, 30));
    }

    [Fact]
    public void TheLiveSpanFollowsTheModeAndKeepsAMinimumWidth()
    {
        Assert.Equal((30.0, 50.0), TimelineBlockEdit.LiveSpan(10, 50, Mode.Move, 20));
        Assert.Equal((10.0, 70.0), TimelineBlockEdit.LiveSpan(10, 50, Mode.Stretch, 20));
        Assert.Equal((10.0, 26.0), TimelineBlockEdit.LiveSpan(10, 50, Mode.Stretch, -40));
        Assert.Equal((20.0, 40.0), TimelineBlockEdit.LiveSpan(10, 50, Mode.Trim, 10));
        Assert.Equal((10.0, 26.0), TimelineBlockEdit.LiveSpan(10, 20, null, 99));
    }

    [Fact]
    public void TheLiveDurationSnapsLikeTheWrite()
    {
        Assert.Equal(1.05, TimelineBlockEdit.LiveDuration(1, Mode.Stretch, 7, 120));
        Assert.Equal(0.5, TimelineBlockEdit.LiveDuration(1, Mode.Trim, 60, 120));
        Assert.Equal(0.05, TimelineBlockEdit.LiveDuration(0.3, Mode.Trim, 600, 120));
        Assert.Equal(1, TimelineBlockEdit.LiveDuration(1, Mode.Move, 600, 120));
    }

    [Fact]
    public void ADragSnapsTheNearestEdgeWithinReach()
    {
        Assert.Equal(24, TimelineBlockEdit.SnappedDelta(20, [100], [124, 300]));
        Assert.Equal(22, TimelineBlockEdit.SnappedDelta(20, [100, 150], [125, 172]));
        Assert.Equal(20, TimelineBlockEdit.SnappedDelta(20, [100], [130]));
    }

    [Fact]
    public void DurationLabelsShowTwoDecimalsOnlyWhenNeeded()
    {
        Assert.Equal("0.4s", TimelineBlockEdit.DurationLabel(0.4));
        Assert.Equal("0.45s", TimelineBlockEdit.DurationLabel(0.45));
        Assert.Equal("2.0s", TimelineBlockEdit.DurationLabel(2));
    }

    [Fact]
    public void HoldsWhileThePlayheadIsInView()
    {
        Assert.Null(TimelineFollow.Scroll(50, new(0, 400), 2000));
        Assert.Null(TimelineFollow.Scroll(383, new(0, 400), 2000));
    }

    [Fact]
    public void RunningOffTheRightPutsItNearTheLeft() => Assert.Equal(330, TimelineFollow.Scroll(390, new(0, 400), 2000));

    [Fact]
    public void ASeekOrAManualScrollOffEitherSideBringsItBack()
    {
        Assert.Equal(840, TimelineFollow.Scroll(900, new(137, 537), 2000));
        Assert.Equal(40, TimelineFollow.Scroll(100, new(600, 1000), 2000));
    }

    [Fact]
    public void ClampsToTheContent()
    {
        Assert.Equal(1600, TimelineFollow.Scroll(1995, new(1000, 1400), 2000));
        Assert.Equal(0, TimelineFollow.Scroll(20, new(300, 700), 2000));
    }
}
