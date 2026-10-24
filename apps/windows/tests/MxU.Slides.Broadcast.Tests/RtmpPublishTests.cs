using System.Buffers.Binary;
using System.Diagnostics;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Rtmp;
using MxU.Slides.Broadcast.Streaming;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>
/// A whole stream against the in-process RTMP server: handshake, command sequence, metadata,
/// the first video and audio tags, reconnects and failures. Stands in for the Mac's
/// testPublishesToLocalRTMPServer (which needs a real server on 1935).
/// </summary>
public sealed class RtmpPublishTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StreamSessionOptions Fast(List<TimeSpan>? delays = null, RtmpPublishOptions? rtmp = null) => new()
    {
        Encoder = EncoderPreference.Software,
        ReconnectDelay = (delay, ct) =>
        {
            lock (delays ?? []) delays?.Add(delay);
            return Task.Delay(50, ct);
        },
        Rtmp = rtmp,
    };

    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Feeds 30 fps pictures and matching 48 kHz audio until <paramref name="done"/>.</summary>
    private static async Task FeedAsync(StreamSession session, Func<bool> done, TimeSpan timeout, int width = 320, int height = 180)
    {
        var clock = Stopwatch.StartNew();
        var frame = 0;
        long sample = 0;
        while (!done())
        {
            if (clock.Elapsed > timeout) throw new TimeoutException($"gave up after {timeout.TotalSeconds}s; session is {session.Status.State}");
            var now = Now;
            session.WriteVideo(Synthetic.VideoFrame(width, height, frame++, now));
            session.WriteAudio(Synthetic.AudioBuffer(48_000, 2, 1600, sample, now));
            sample += 1600;
            await Task.Delay(33, Ct);
        }
    }

    private static string? Name(List<object?> command) => command.Count > 0 ? command[0] as string : null;

    [Fact]
    public async Task PublishesHandshakeCommandsMetadataAndFirstTags()
    {
        await using var server = new FakeRtmpServer();
        var session = new StreamSession(
            new StreamEndpoint("Fake", StreamEndpointKind.Rtmp, server.Url("live"), "secret-key"),
            new StreamVideoConfiguration(320, 180, 30, 800), Fast());
        session.Start();
        try
        {
            FakeRtmpConnection? connection = null;
            await FeedAsync(session, () =>
            {
                connection ??= server.Connections.FirstOrDefault();
                return connection is { Video.Count: >= 4, Audio.Count: >= 4 };
            }, TimeSpan.FromSeconds(30));

            Assert.Equal(StreamSessionState.Publishing, session.Status.State);
            Assert.True(session.Status.FramesSubmitted > 0);
            Assert.True(connection!.HandshakeVerified, "C2 must echo S1");

            var names = connection.Commands.Select(Name).ToList();
            Assert.Equal("connect", names[0]);
            var order = new[] { "connect", "releaseStream", "FCPublish", "createStream", "publish", "@setDataFrame" }
                .Select(n => names.IndexOf(n)).ToList();
            Assert.DoesNotContain(-1, order);
            Assert.Equal(order.OrderBy(i => i), order);

            var connect = (Amf0Object)connection.Commands[0][2]!;
            Assert.Equal("live", connect["app"]);
            Assert.Equal(server.Url("live"), connect["tcUrl"]);
            Assert.Equal("FMLE/3.0 (compatible; FMSc/1.0)", connect["flashVer"]);
            Assert.Equal(128.0, connect["videoCodecs"]);
            Assert.Equal(1024.0, connect["audioCodecs"]);

            var publish = connection.Commands.First(c => Name(c) == "publish");
            Assert.Equal("secret-key", publish[3]);
            Assert.Equal("live", publish[4]);
            var release = connection.Commands.First(c => Name(c) == "releaseStream");
            Assert.Equal("secret-key", release[3]);

            var metadata = connection.Commands.First(c => Name(c) == "@setDataFrame");
            Assert.Equal("onMetaData", metadata[1]);
            var fields = (Amf0EcmaArray)metadata[2]!;
            Assert.Equal(320.0, fields["width"]);
            Assert.Equal(180.0, fields["height"]);
            Assert.Equal(7.0, fields["videocodecid"]);
            Assert.Equal(10.0, fields["audiocodecid"]);

            // Control traffic: our 8 KB chunk size, the ping answered, the server's window echoed.
            Assert.Equal(8192, connection.ClientChunkSize);
            Assert.Contains(connection.Messages, m => m.TypeId == RtmpMessageType.UserControl
                && m.Payload[0] == 0 && m.Payload[1] == 7 && m.ReadUInt32(2) == 0x1234_5678);
            Assert.Contains(connection.Messages, m => m.TypeId == RtmpMessageType.WindowAcknowledgementSize && m.ReadUInt32() == 5_000_000);

            // Video: the AVC sequence header (AVCDecoderConfigurationRecord), then a keyframe of AVCC NAL units.
            var video = connection.Video;
            Assert.All(video, v => Assert.Equal(1u, v.StreamId));
            var header = video[0].Payload;
            Assert.Equal([0x17, 0x00, 0, 0, 0], header[..5]);
            Assert.Equal(1, header[5]);
            Assert.Equal(100, header[6]);
            Assert.Equal(0xFF, header[9]);
            Assert.Equal(0xE1, header[10]);
            var spsLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(11));
            Assert.Equal(0x67, header[13] & 0x7F);
            Assert.Equal(1, header[13 + spsLength]);
            var first = video[1].Payload;
            Assert.Equal([0x17, 0x01, 0, 0, 0], first[..5]);
            var nals = AvcBitstream.SplitAvcc(first.AsMemory(5));
            Assert.NotEmpty(nals);
            Assert.Equal(first.Length - 5, nals.Sum(n => n.Length + 4));
            Assert.Contains(nals, n => AvcBitstream.TypeOf(n.Span) == AvcBitstream.NalIdr);
            Assert.Equal(0x27, video[2].Payload[0]);
            Assert.True(video[2].Timestamp > video[1].Timestamp);
            Assert.True(video[1].Timestamp < 100, $"first picture at {video[1].Timestamp} ms");

            // Audio: the AAC sequence header (AAC-LC, 48 kHz, stereo), then raw frames ~21 ms apart.
            var audio = connection.Audio;
            Assert.Equal([0xAF, 0x00, 0x11, 0x90], audio[0].Payload);
            Assert.Equal([0xAF, 0x01], audio[1].Payload[..2]);
            Assert.True(audio[1].Payload.Length > 2);
            for (var i = 2; i < audio.Count; i++) Assert.True(audio[i].Timestamp >= audio[i - 1].Timestamp);
            Assert.InRange(audio[3].Timestamp - audio[2].Timestamp, 20u, 23u);
        }
        finally
        {
            session.Stop();
        }
        await session.Supervisor!.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var closed = server.Connections[0];
        await closed.WaitUntilAsync(c => c.Commands.Any(x => Name(x) == "deleteStream"), TimeSpan.FromSeconds(5), Ct);
        Assert.Contains(closed.Commands, c => Name(c) == "FCUnpublish" && c[3] as string == "secret-key");
        Assert.Equal(StreamSessionState.Stopped, session.Status.State);
    }

    [Fact]
    public async Task AcknowledgesTheServerOnceItsWindowIsRead()
    {
        // A 4 KB window: the ~9 KB padded onBWDone the server sends must be acknowledged.
        await using var server = new FakeRtmpServer { Window = 4096 };
        var session = new StreamSession(
            new StreamEndpoint("Acks", StreamEndpointKind.Rtmp, server.Url(), "k"),
            new StreamVideoConfiguration(320, 180, 30, 500), Fast());
        session.Start();
        try
        {
            await FeedAsync(session, () => server.Connections.FirstOrDefault() is { } c
                && c.Messages.Any(m => m.TypeId == RtmpMessageType.Acknowledgement), TimeSpan.FromSeconds(20));
        }
        finally
        {
            session.Stop();
        }
        var connection = server.Connections[0];
        Assert.Contains(connection.Messages, m => m.TypeId == RtmpMessageType.WindowAcknowledgementSize && m.ReadUInt32() == 4096);
        var ack = connection.Messages.First(m => m.TypeId == RtmpMessageType.Acknowledgement);
        Assert.True(ack.ReadUInt32() >= 4096, $"acknowledged {ack.ReadUInt32()} bytes");
    }

    [Fact]
    public async Task ReconnectsAfterTheServerDropsAndStartsCountingAgain()
    {
        await using var server = new FakeRtmpServer { DropAfterVideoMessages = 5 };
        var delays = new List<TimeSpan>();
        var states = new List<StreamSessionState>();
        var session = new StreamSession(
            new StreamEndpoint("Flaky", StreamEndpointKind.Rtmp, server.Url(), "k"),
            new StreamVideoConfiguration(320, 180, 30, 800), Fast(delays));
        session.StateChanged += (_, state) => { lock (states) states.Add(state); };
        session.Start();
        try
        {
            await FeedAsync(session, () => server.Connections.Count >= 3 && server.Connections[2].Video.Count >= 2, TimeSpan.FromSeconds(40));
        }
        finally
        {
            session.Stop();
        }
        lock (states)
        {
            // Each drop came while live, so every reconnect is "attempt 1" with a 1 s back-off.
            Assert.Contains(StreamSessionState.Reconnecting(1), states);
            Assert.DoesNotContain(StreamSessionState.Reconnecting(2), states);
            var firstRetry = states.IndexOf(StreamSessionState.Reconnecting(1));
            Assert.Contains(StreamSessionState.Publishing, states.Skip(firstRetry));
        }
        lock (delays) Assert.All(delays, d => Assert.Equal(TimeSpan.FromSeconds(1), d));
        Assert.All(server.Connections.Take(3), c => Assert.Equal(0x17, c.Video[0].Payload[0]));
    }

    [Fact]
    public async Task RejectedKeyBacksOffOneTwoFourSeconds()
    {
        await using var server = new FakeRtmpServer { RejectPublish = true };
        var delays = new List<TimeSpan>();
        var session = new StreamSession(
            new StreamEndpoint("Wrong key", StreamEndpointKind.Rtmp, server.Url(), "nope"),
            new StreamVideoConfiguration(320, 180), Fast(delays));
        session.Start();
        try
        {
            var clock = Stopwatch.StartNew();
            while (session.Status.State != StreamSessionState.Reconnecting(3) && clock.Elapsed < TimeSpan.FromSeconds(20))
                await Task.Delay(20, Ct);
            Assert.Equal(StreamSessionState.Reconnecting(3), session.Status.State);
        }
        finally
        {
            session.Stop();
        }
        lock (delays) Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays.Take(3));
        Assert.Equal(0, session.Status.FramesSubmitted);
    }

    [Fact]
    public async Task PublishesOverRtmps()
    {
        using var certificate = FakeRtmpServer.SelfSignedCertificate();
        await using var server = new FakeRtmpServer(certificate);
        var session = new StreamSession(
            new StreamEndpoint("Secure", StreamEndpointKind.Rtmps, server.Url("app/instance"), "k"),
            new StreamVideoConfiguration(320, 180, 30, 800),
            Fast(rtmp: new RtmpPublishOptions { CertificateValidation = (_, _, _, _) => true }));
        session.Start();
        try
        {
            await FeedAsync(session, () => server.Connections.FirstOrDefault() is { Video.Count: >= 2 }, TimeSpan.FromSeconds(30));
        }
        finally
        {
            session.Stop();
        }
        var connection = server.Connections[0];
        Assert.True(connection.HandshakeVerified);
        Assert.Equal("app/instance", ((Amf0Object)connection.Commands[0][2]!)["app"]);
    }

    [Fact]
    public async Task UntrustedCertificateIsATransportFailureNotACrash()
    {
        using var certificate = FakeRtmpServer.SelfSignedCertificate();
        await using var server = new FakeRtmpServer(certificate);
        var session = new StreamSession(
            new StreamEndpoint("Secure", StreamEndpointKind.Rtmps, server.Url(), "k"),
            new StreamVideoConfiguration(320, 180), Fast());
        session.Start();
        var clock = Stopwatch.StartNew();
        while (session.Status.State.Kind != StreamStateKind.Reconnecting && clock.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(20, Ct);
        session.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "a bad certificate must end the attempt");
    }

    [Fact]
    public async Task UnreachableServerReconnects()
    {
        var session = new StreamSession(
            new StreamEndpoint("Nobody", StreamEndpointKind.Rtmp, "rtmp://127.0.0.1:1/live", "k"),
            new StreamVideoConfiguration(320, 180), Fast());
        session.Start();
        var clock = Stopwatch.StartNew();
        while (session.Status.State.Kind != StreamStateKind.Reconnecting && clock.Elapsed < TimeSpan.FromSeconds(20))
            await Task.Delay(20, Ct);
        session.Stop();
        Assert.Equal(StreamSessionState.Stopped, session.Status.State);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Theory]
    [InlineData("http://example.com/live")]
    [InlineData("not a url")]
    public async Task BadUrlFailsWithoutRetrying(string url)
    {
        var session = new StreamSession(new StreamEndpoint("Typo", StreamEndpointKind.Rtmp, url, "k"), new StreamVideoConfiguration(320, 180), Fast());
        session.Start();
        await session.Supervisor!.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(StreamStateKind.Failed, session.Status.State.Kind);
        Assert.StartsWith("bad URL:", session.Status.State.Reason);
    }

    [Fact]
    public async Task SrtFailsWithAReason()
    {
        var session = new StreamSession(new StreamEndpoint("North", StreamEndpointKind.Srt, "srt://h:9000"), new StreamVideoConfiguration(320, 180), Fast());
        session.Start();
        await session.Supervisor!.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(StreamSessionState.Failed("SRT isn't available on Windows yet"), session.Status.State);
    }
}
