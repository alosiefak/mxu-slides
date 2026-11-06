using MxU.Slides.Broadcast.Streaming;
using Input = MxU.Slides.Broadcast.Streaming.StreamHealthRollup.Input;
using Tint = MxU.Slides.Broadcast.Streaming.StreamHealthRollup.Tint;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>Port of StreamHealthTests.swift.</summary>
public sealed class StreamHealthTests
{
    [Fact]
    public void BadIsDegradedNeverNoDataAndNeverTints()
    {
        var tracker = new PlatformHealthTracker();
        tracker.Record("yt", "good", [], 200);
        tracker.Record("yt", "bad", ["not enough video"], 210);
        Assert.Equal(PlatformVerdict.Degraded(["not enough video"]), tracker.Verdict("yt"));
        Input[] inputs = [new(StreamSessionState.Publishing, tracker.Verdict("yt"))];
        Assert.Equal(Tint.Green, StreamHealthRollup.TintOf(inputs));
        Assert.Equal("LIVE", StreamHealthRollup.Word(inputs));
        Assert.True(tracker.PollsHot("yt"));
    }

    [Fact]
    public void EndedBroadcastIsRedUntilForgotten()
    {
        var tracker = new PlatformHealthTracker();
        tracker.Record("yt", "good", [], 200);
        tracker.Record("yt", "ended", [], 300);
        Assert.Equal(PlatformVerdict.Ended, tracker.Verdict("yt"));
        Input[] inputs = [new(StreamSessionState.Publishing, PlatformVerdict.Ended)];
        Assert.Equal(Tint.Red, StreamHealthRollup.TintOf(inputs));
        Assert.Equal("BROADCAST ENDED", StreamHealthRollup.Word(inputs));
        tracker.Forget("yt");
        Assert.Null(tracker.Verdict("yt"));
        Assert.True(tracker.PollsHot("yt"));
    }

    [Fact]
    public void UploadStallsWhenNothingLeavesOrTheBacklogGrows()
    {
        var stall = new UploadStall();
        for (var i = 1; i < UploadStall.SilentSeconds; i++) stall.Record(bytesOutPerSecond: 0, queuedBytes: 0);
        Assert.Null(stall.Stalled(targetKbps: 6000)); // a short silence is a blip
        stall.Record(bytesOutPerSecond: 0, queuedBytes: 0);
        Assert.Equal("nothing sent for 10s", stall.Stalled(targetKbps: 6000));
        stall.Record(bytesOutPerSecond: 100_000, queuedBytes: 6000 * 125 * 20);
        Assert.Null(stall.Stalled(targetKbps: 6000)); // bytes leaving clear the silence
        stall.Record(bytesOutPerSecond: 100_000, queuedBytes: 6000 * 125 * 20 + 1);
        Assert.Equal("upload 15 MB behind", stall.Stalled(targetKbps: 6000));
    }

    [Fact]
    public void NetworkHealthReadsTheBacklogNotTheSendRate()
    {
        static StreamSessionStatus Status(int? upload, int video = 10000, double backlog = 0, StreamSessionState? state = null) =>
            new(state ?? StreamSessionState.Publishing, 0, 0) { UploadKbps = upload, VideoKbps = video, BacklogSeconds = backlog };
        Assert.Null(NetworkHealths.Of(Status(null), fullKbps: 10000)); // not measured yet
        Assert.Null(NetworkHealths.Of(Status(500, state: StreamSessionState.Reconnecting(1)), fullKbps: 10000));
        Assert.Equal(NetworkHealth.Good, NetworkHealths.Of(Status(80), fullKbps: 10000)); // a still slide sends little
        Assert.Equal(NetworkHealth.Slow, NetworkHealths.Of(Status(4000, backlog: 2), fullKbps: 10000));
        Assert.Equal(NetworkHealth.Slow, NetworkHealths.Of(Status(2000, video: 2000), fullKbps: 10000)); // quality lowered
        Assert.Equal(NetworkHealth.Stalled, NetworkHealths.Of(Status(0, backlog: 3), fullKbps: 10000));
        Assert.Equal("stalled", NetworkHealth.Stalled.Raw());
    }

    [Fact]
    public void AdaptiveFloorFollowsThePresetChoice()
    {
        Assert.Null(StreamVideoConfiguration.AdaptiveFloor(adapt: false, minKbps: 3000, bitrateKbps: 6000)); // constant
        Assert.Equal(2000, StreamVideoConfiguration.AdaptiveFloor(adapt: null, minKbps: null, bitrateKbps: 6000));
        Assert.Equal(4000, StreamVideoConfiguration.AdaptiveFloor(adapt: true, minKbps: 4000, bitrateKbps: 6000));
        Assert.Equal(2500, StreamVideoConfiguration.AdaptiveFloor(adapt: true, minKbps: 4000, bitrateKbps: 2500)); // never above its own bitrate
    }

    [Fact]
    public void NoDataEscalatesOnlyAfterConsecutivePolls()
    {
        var tracker = new PlatformHealthTracker(escalationPolls: 3);
        tracker.Record("yt", "good", [], 200);
        tracker.Record("yt", "noData", [], 210);
        tracker.Record("yt", "noData", [], 220);
        Assert.Equal(PlatformVerdict.Healthy, tracker.Verdict("yt"));
        tracker.Record("yt", "noData", [], 230);
        Assert.Equal(PlatformVerdict.NoData, tracker.Verdict("yt"));
        Input[] inputs = [new(StreamSessionState.Publishing, PlatformVerdict.NoData)];
        Assert.Equal(Tint.Amber, StreamHealthRollup.TintOf(inputs));
        Assert.Equal("NO DATA", StreamHealthRollup.Word(inputs));

        tracker.Record("yt", "good", [], 240);
        Assert.Equal(PlatformVerdict.Healthy, tracker.Verdict("yt"));
        tracker.Record("yt", "noData", [], 250);
        Assert.Equal(PlatformVerdict.Healthy, tracker.Verdict("yt"));
    }

    [Fact]
    public void StartupGraceIgnoresEarlyNoDataUntilSettled()
    {
        var tracker = new PlatformHealthTracker(graceSeconds: 90, escalationPolls: 1);
        tracker.Record("yt", "noData", [], 1);
        tracker.Record("yt", "noData", [], 60);
        Assert.Null(tracker.Verdict("yt"));
        Assert.True(tracker.PollsHot("yt"));
        tracker.Record("yt", "noData", [], 91);
        Assert.Equal(PlatformVerdict.NoData, tracker.Verdict("yt"));
    }

    [Fact]
    public void UnknownIsIgnoredAndHealthySettles()
    {
        var tracker = new PlatformHealthTracker();
        tracker.Record("yt", "unknown", [], 500);
        Assert.Null(tracker.Verdict("yt"));
        tracker.Record("yt", "ok", [], 500);
        Assert.Equal(PlatformVerdict.Healthy, tracker.Verdict("yt"));
        Assert.False(tracker.PollsHot("yt"));
    }

    [Fact]
    public void CaptionWordMatchesSessionState()
    {
        Assert.Equal("CONNECTING", StreamHealthRollup.Word([new Input(StreamSessionState.Connecting)]));
        Assert.Equal(Tint.Amber, StreamHealthRollup.TintOf([new Input(StreamSessionState.Connecting)]));
        Assert.Equal("RECONNECTING", StreamHealthRollup.Word([new Input(StreamSessionState.Reconnecting(2))]));
        Assert.Equal("STREAM FAILED", StreamHealthRollup.Word([new Input(StreamSessionState.Failed("x")), new Input(StreamSessionState.Publishing)]));
        Assert.Equal(Tint.Red, StreamHealthRollup.TintOf([new Input(StreamSessionState.Failed("x")), new Input(StreamSessionState.Publishing)]));
        Assert.Equal("RECONNECTING", StreamHealthRollup.Word([
            new Input(StreamSessionState.Publishing, PlatformVerdict.NoData), new Input(StreamSessionState.Reconnecting(1)),
        ]));
        Assert.Equal(Tint.None, StreamHealthRollup.TintOf([]));
    }
}
