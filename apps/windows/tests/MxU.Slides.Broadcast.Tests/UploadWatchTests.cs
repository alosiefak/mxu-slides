using MxU.Slides.Broadcast.Streaming;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>
/// The adaptive bitrate behind the Mac's UploadWatch: HaishinKit's network monitor and
/// StreamVideoAdaptiveBitRateStrategy (ported for Windows, where there's no HaishinKit) with the
/// preset's floor. Each Tick is one second of the network.
/// </summary>
public sealed class UploadWatchTests
{
    private const int Full = 6_000_000;

    /// <summary>Seconds of a link that carries <paramref name="kbps"/> while the stream wants more: the queue grows.</summary>
    private static void Throttled(UploadWatch watch, ref long sent, ref long queued, int kbps, int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            sent += kbps * 125;
            queued += 200_000;
            watch.Tick(sent, queued);
        }
    }

    [Fact]
    public void AGrowingQueueStepsDownToWhatTheLinkCarriesButNotBelowTheFloor()
    {
        var watch = new UploadWatch(Full, floorBitRate: 2_000_000, audioBitRate: 128_000);
        long sent = 0, queued = 0;
        watch.Tick(sent, queued);
        Assert.Equal(Full, watch.VideoBitRate);

        Throttled(watch, ref sent, ref queued, kbps: 3000, seconds: 2);
        Assert.Equal(3_000_000 - 128_000, watch.VideoBitRate); // three readings, each queue longer: insufficient

        Throttled(watch, ref sent, ref queued, kbps: 1000, seconds: 1);
        Assert.Equal(2_000_000, watch.VideoBitRate); // the preset's lowest
        Assert.Equal(2000, watch.VideoKbps);
    }

    [Fact]
    public void ConstantModeNeverChangesTheBitrate()
    {
        var watch = new UploadWatch(Full, floorBitRate: null);
        long sent = 0, queued = 0;
        Throttled(watch, ref sent, ref queued, kbps: 500, seconds: 6);
        Assert.False(watch.IsAdaptive);
        Assert.Equal(Full, watch.VideoBitRate);
        Assert.Equal(500 * 125, watch.Stall.BytesOutPerSecond);
    }

    [Fact]
    public void AfterFifteenGoodSecondsItClimbsATenthAtATime()
    {
        var watch = new UploadWatch(Full, floorBitRate: 0);
        long sent = 0, queued = 0;
        Throttled(watch, ref sent, ref queued, kbps: 2128, seconds: 3);
        Assert.Equal(2_000_000, watch.VideoBitRate);
        // The link is fine again: the queue empties and stays empty.
        for (var second = 0; second < AdaptiveBitRateStrategy.SufficientBandwidthCountsThreshold; second++)
        {
            sent += 250_000;
            watch.Tick(sent, 0);
            Assert.Equal(2_000_000, watch.VideoBitRate);
        }
        sent += 250_000;
        watch.Tick(sent, 0);
        Assert.Equal(2_000_000 + Full / 10, watch.VideoBitRate);
    }

    [Fact]
    public void ItNeverDropsBelowATenthOfTheFullRate()
    {
        var watch = new UploadWatch(Full, floorBitRate: 0);
        long sent = 0, queued = 0;
        Throttled(watch, ref sent, ref queued, kbps: 100, seconds: 3);
        Assert.Equal(Full / 10, watch.VideoBitRate);
    }

    [Fact]
    public void ASecondWithNothingSentLeavesTheBitrateForTheStallCheck()
    {
        var watch = new UploadWatch(Full, floorBitRate: 2_000_000);
        long queued = 0;
        for (var second = 0; second < 4; second++)
        {
            queued += 100_000;
            watch.Tick(0, queued);
        }
        Assert.Equal(Full, watch.VideoBitRate);
        Assert.Equal(0, watch.Stall.BytesOutPerSecond);
        Assert.Null(watch.Stall.Stalled(6000)); // four silent seconds: not yet
    }

    [Fact]
    public void TheMonitorNeedsThreeReadingsOfAGrowingQueue()
    {
        var monitor = new NetworkMonitor();
        Assert.Equal(NetworkEvent.Status, monitor.Collect(0, 100).Event);
        Assert.Equal(NetworkEvent.Status, monitor.Collect(1000, 200).Event);
        var third = monitor.Collect(3000, 300);
        Assert.Equal(NetworkEvent.InsufficientBandwidth, third.Event);
        Assert.Equal(new NetworkReport(2000, 300), third.Report);
        Assert.Equal(NetworkEvent.Status, monitor.Collect(4000, 250).Event); // it shrank once
        Assert.Equal(2000, monitor.Collect(5000, 260, seconds: 0.5).Report.BytesOutPerSecond); // per second, whatever the interval
    }
}
