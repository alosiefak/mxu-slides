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
