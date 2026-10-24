using MxU.Slides.Broadcast.Streaming;
using MxU.Slides.Core.Media;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>Port of StreamSessionTests.swift (the publish-to-a-real-server test lives in <see cref="RtmpPublishTests"/>).</summary>
public sealed class StreamSessionTests
{
    private static StreamSession Session() => new(
        new StreamEndpoint("Test", StreamEndpointKind.Rtmp, "rtmp://127.0.0.1:19999/live", "x"),
        new StreamVideoConfiguration(320, 180));

    [Fact]
    public void CopiedAudioBufferIsDeep()
    {
        var samples = new float[512];
        samples[0] = 0.5f;
        samples[511] = -0.25f;
        var original = new AudioBuffer(48_000, 2, samples, 1);
        var copy = PendingAudioBuffer.Copy(original);
        try
        {
            Assert.Equal(48_000, copy.SampleRate);
            Assert.Equal(2, copy.Channels);
            Assert.Equal(512, copy.Length);
            Assert.Equal(0.5f, copy.Span[0]);
            Assert.Equal(-0.25f, copy.Span[511]);
            samples[0] = 99;
            Assert.Equal(0.5f, copy.Span[0]);
        }
        finally
        {
            copy.Return();
        }
    }

    [Fact]
    public void CopiedFrameIsDeepAndKeepsItsStride()
    {
        var pixels = new byte[(320 * 4 + 64) * 180];
        pixels[0] = 7;
        var frame = new VideoFrame(320, 180, 320 * 4 + 64, pixels, 2);
        var copy = PendingVideoFrame.Copy(frame);
        try
        {
            pixels[0] = 8;
            Assert.Equal(7, copy.Span[0]);
            Assert.Equal(320 * 4 + 64, copy.Stride);
            Assert.Equal(2, copy.HostTime);
        }
        finally
        {
            copy.Return();
        }
    }

    [Fact]
    public void AppendAudioIsSafeWhenNotPublishing()
    {
        var session = Session();
        session.WriteAudio(new AudioBuffer(48_000, 2, new float[256], 0));
        session.WriteAudio(new AudioBuffer(48_000, 2, Array.Empty<float>(), 0));
        session.Stop();
        session.WriteAudio(new AudioBuffer(48_000, 2, new float[256], 0));
        Assert.Equal(StreamSessionState.Stopped, session.Status.State);
    }

    [Fact]
    public void SessionStartsIdleAndStopsCleanly()
    {
        var session = Session();
        Assert.Equal(StreamSessionState.Idle, session.Status.State);
        session.Stop();
        Assert.Equal(StreamSessionState.Stopped, session.Status.State);
    }

    [Fact]
    public void AppendBeforePublishingDropsNothingAndSubmitsNothing()
    {
        var session = Session();
        session.WriteVideo(Synthetic.VideoFrame(320, 180, 0, 1));
        Assert.Equal(0, session.Status.FramesSubmitted);
        Assert.Equal(0, session.Status.FramesDropped);
    }

    [Fact]
    public void StateChangesAreReportedAndStartIsIgnoredWhileRunning()
    {
        var session = Session();
        var seen = new List<StreamSessionState>();
        session.StateChanged += (_, state) => { lock (seen) seen.Add(state); };
        session.Start();
        session.Start();
        session.Stop();
        lock (seen)
        {
            Assert.Equal(StreamSessionState.Connecting, seen[0]);
            Assert.Equal(StreamSessionState.Stopped, seen[^1]);
            Assert.Single(seen, s => s == StreamSessionState.Connecting);
        }
    }

    [Fact]
    public void StatesPrintLikeTheMac()
    {
        Assert.Equal("reconnecting(attempt: 2)", StreamSessionState.Reconnecting(2).ToString());
        Assert.Equal("failed(bad)", StreamSessionState.Failed("bad").ToString());
        Assert.Equal("publishing", StreamSessionState.Publishing.ToString());
        Assert.Equal("Fake (Rtmp) rtmp://h/app", new StreamEndpoint("Fake", StreamEndpointKind.Rtmp, "rtmp://h/app", "secret").ToString());
    }
}
