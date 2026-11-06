using System.Diagnostics;
using MxU.Slides.Broadcast.Control;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Rtmp;
using MxU.Slides.Broadcast.Streaming;
using MxU.Slides.Core.Media;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>
/// A stream over a slow or stalled upload, against the in-process RTMP server: a stalled upload
/// reconnects (and says why), and Adaptive mode lowers the encoder's bitrate to fit a slow one
/// while Constant keeps it. Windows' twins of what the Mac's UploadWatch does through HaishinKit.
/// </summary>
public sealed class SlowUploadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Random pictures: nothing for the encoder to save, so it really sends its whole bitrate.</summary>
    private static VideoFrame Noise(int width, int height, int index, double hostTime)
    {
        var pixels = new byte[width * height * 4];
        new Random(index).NextBytes(pixels);
        return new VideoFrame(width, height, width * 4, pixels, hostTime);
    }

    private static async Task FeedUntilAsync(StreamSession session, Func<bool> done, TimeSpan timeout, bool noise = false)
    {
        var clock = Stopwatch.StartNew();
        var frame = 0;
        long sample = 0;
        while (!done())
        {
            if (clock.Elapsed > timeout) throw new TimeoutException($"gave up after {timeout.TotalSeconds}s; session is {session.Status.State}, video {session.Status.VideoKbps} kbps");
            var now = Now;
            session.WriteVideo(noise ? Noise(320, 180, frame, now) : Synthetic.VideoFrame(320, 180, frame, now));
            frame++;
            session.WriteAudio(Synthetic.AudioBuffer(48_000, 2, 1600, sample, now));
            sample += 1600;
            await Task.Delay(33, Ct);
        }
    }

    [Fact]
    public async Task AStalledUploadReconnectsAndSaysWhy()
    {
        await using var server = new FakeRtmpServer(receiveBufferSize: 4096);
        var states = new List<StreamSessionState>();
        var session = new StreamSession(
            new StreamEndpoint("Stalls", StreamEndpointKind.Rtmp, server.Url(), "k"),
            new StreamVideoConfiguration(320, 180, 30, 800),
            new StreamSessionOptions
            {
                Encoder = EncoderPreference.Software,
                ReconnectDelay = (_, ct) => Task.Delay(50, ct),
                // Ten silent measurements a tenth of a second apart: the stall shows in a second.
                MonitorInterval = TimeSpan.FromMilliseconds(100),
                // The socket's own 15 s stall timeout mustn't be what notices.
                Rtmp = new RtmpPublishOptions { SendBufferSize = 8192, WriteStallTimeout = TimeSpan.FromSeconds(60) },
            });
        session.StateChanged += (_, state) => { lock (states) states.Add(state); };
        session.Start();
        try
        {
            await FeedUntilAsync(session, () => server.Connections.Count >= 1 && server.Connections[0].Video.Count >= 10, TimeSpan.FromSeconds(20));
            Assert.NotNull(session.Status.UploadKbps);
            server.Paused = true;
            await FeedUntilAsync(session, () => session.Status.State.Kind == StreamStateKind.Reconnecting, TimeSpan.FromSeconds(20));
            Assert.StartsWith("upload stalled: nothing sent for 10s", session.Status.LastDrop);
            server.Paused = false;
            await FeedUntilAsync(session, () => server.Connections.Count >= 2 && server.Connections[^1].Video.Count >= 2, TimeSpan.FromSeconds(30));
        }
        finally
        {
            session.Stop();
        }
        lock (states) Assert.Contains(StreamSessionState.Reconnecting(1), states);
        Assert.Equal(StreamStateKind.Stopped, session.Status.State.Kind);
    }

    [Fact]
    public async Task AdaptiveLowersTheBitrateToFitASlowUploadButNotBelowItsFloor()
    {
        await using var server = new FakeRtmpServer(receiveBufferSize: 8192) { ReadLimitBytesPerSecond = 40_000 };
        var session = new StreamSession(
            new StreamEndpoint("Slow", StreamEndpointKind.Rtmp, server.Url(), "k"),
            new StreamVideoConfiguration(320, 180, 30, 2000, AdaptiveFloorKbps: 600),
            new StreamSessionOptions
            {
                Encoder = EncoderPreference.Software,
                ReconnectDelay = (_, ct) => Task.Delay(50, ct),
                Rtmp = new RtmpPublishOptions { SendBufferSize = 32 * 1024 },
            });
        session.Start();
        try
        {
            await FeedUntilAsync(session, () => session.Status.VideoKbps is < 1800, TimeSpan.FromSeconds(40), noise: true);
            var status = session.Status;
            Assert.Equal(StreamStateKind.Publishing, status.State.Kind);
            Assert.Equal(600, status.VideoKbps); // 320 kbps got through, less the audio: the floor
            Assert.Equal(NetworkHealth.Slow, NetworkHealths.Of(status, 2000));
            Assert.Contains("quality lowered to 0.6 of 2.0 Mbps", BroadcastStatus.HealthLine(session));
        }
        finally
        {
            session.Stop();
        }
    }

    [Fact]
    public async Task ConstantKeepsItsBitrateOnASlowUpload()
    {
        await using var server = new FakeRtmpServer(receiveBufferSize: 8192) { ReadLimitBytesPerSecond = 40_000 };
        var session = new StreamSession(
            new StreamEndpoint("Slow", StreamEndpointKind.Rtmp, server.Url(), "k"),
            new StreamVideoConfiguration(320, 180, 30, 2000, AdaptiveFloorKbps: null),
            new StreamSessionOptions
            {
                Encoder = EncoderPreference.Software,
                ReconnectDelay = (_, ct) => Task.Delay(50, ct),
                Rtmp = new RtmpPublishOptions { SendBufferSize = 32 * 1024 },
            });
        session.Start();
        try
        {
            // The backlog grows past a second of stream while bytes still leave: the network is
            // slow (not stalled), and the quality isn't touched.
            await FeedUntilAsync(session, () => session.Status is { BacklogSeconds: >= 1, UploadKbps: > 0 }, TimeSpan.FromSeconds(40), noise: true);
            var status = session.Status;
            Assert.Equal(2000, status.VideoKbps);
            Assert.Equal(NetworkHealth.Slow, NetworkHealths.Of(status, 2000));
            Assert.Contains("max 2.0 Mbps", BroadcastStatus.HealthLine(session));
        }
        finally
        {
            session.Stop();
        }
    }

    [Fact]
    public void TheEncoderTakesANewBitrateWhileEncoding()
    {
        using var encoder = H264Encoder.Create(new H264Settings(320, 180, 30, 4000), EncoderPreference.Software);
        var converter = new Nv12Converter(320, 180);
        var nv12 = new byte[converter.FrameSize];
        int Encode(int from, int count)
        {
            var bytes = 0;
            for (var i = from; i < from + count; i++)
            {
                var frame = Noise(320, 180, i, 0);
                converter.Convert(frame.Pixels.Span, 320, 180, 320 * 4, nv12);
                bytes += encoder.Encode(nv12, 10 + i / 30.0).Sum(f => f.Avcc.Length);
            }
            return bytes;
        }
        Encode(0, 30);
        var full = Encode(30, 60);
        Assert.True(encoder.SetBitrate(500));
        Assert.Equal(500, encoder.BitrateKbps);
        Encode(90, 30); // the rate control settles
        var lowered = Encode(120, 60);
        Assert.True(lowered < full / 3, $"two seconds were {full} bytes at 4000 kbps and {lowered} at 500 kbps");
    }
}
