using MxU.Slides.Broadcast.Hls.Vendored;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Rtmp;
using MxU.Slides.Broadcast.Streaming;

namespace MxU.Slides.Broadcast.Tests;

public sealed class Amf0Tests
{
    [Fact]
    public void EncodesTheExactBytesOfACommand()
    {
        var bytes = Amf0.Encode("createStream", 4, null);
        byte[] expected =
        [
            0x02, 0x00, 0x0C, .. "createStream"u8,
            0x00, 0x40, 0x10, 0, 0, 0, 0, 0, 0,
            0x05,
        ];
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void ObjectsKeepTheirOrderAndEndMarker()
    {
        var bytes = Amf0.Encode(new Amf0Object { ["b"] = true, ["a"] = "x" });
        byte[] expected = [0x03, 0x00, 0x01, (byte)'b', 0x01, 0x01, 0x00, 0x01, (byte)'a', 0x02, 0x00, 0x01, (byte)'x', 0x00, 0x00, 0x09];
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void RoundTripsEveryKind()
    {
        var date = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        var values = new object?[]
        {
            1.5, -3.0, true, false, "héllo", null, Amf0.Undefined,
            new Amf0Object { ["level"] = "status", ["code"] = "NetStream.Publish.Start", ["nested"] = new Amf0Object { ["n"] = 1.0 } },
            new Amf0EcmaArray { ["width"] = 1920.0, ["stereo"] = true },
            new object?[] { 1.0, "two", null },
            date,
            new string('z', 70_000),
        };
        var decoded = Amf0.DecodeAll(Amf0.Encode(values));
        Assert.Equal(values.Length, decoded.Count);
        Assert.Equal(1.5, decoded[0]);
        Assert.Equal(-3.0, decoded[1]);
        Assert.Equal(true, decoded[2]);
        Assert.Equal(false, decoded[3]);
        Assert.Equal("héllo", decoded[4]);
        Assert.Null(decoded[5]);
        Assert.Same(Amf0.Undefined, decoded[6]);
        var obj = Assert.IsType<Amf0Object>(decoded[7]);
        Assert.Equal(["level", "code", "nested"], obj.Keys);
        Assert.Equal(1.0, ((Amf0Object)obj["nested"]!)["n"]);
        var array = Assert.IsType<Amf0EcmaArray>(decoded[8]);
        Assert.Equal(1920.0, array["width"]);
        Assert.Equal(new object?[] { 1.0, "two", null }, Assert.IsType<object?[]>(decoded[9]));
        Assert.Equal(date, decoded[10]);
        Assert.Equal(70_000, Assert.IsType<string>(decoded[11]).Length);
    }

    [Fact]
    public void LongStringsUseTheLongMarker()
    {
        var bytes = Amf0.Encode(new string('a', 65_536));
        Assert.Equal(Amf0.LongString, bytes[0]);
        Assert.Equal([0x00, 0x01, 0x00, 0x00], bytes[1..5]);
    }

    [Fact]
    public void Amf3SwitchIsRejectedNotMisread()
    {
        Assert.Throws<FormatException>(() => Amf0.DecodeAll([Amf0.AvmPlus, 0x01]));
    }
}

public sealed class RtmpChunkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void FirstMessageHasAFullHeaderAndTheNextADelta()
    {
        var writer = new RtmpChunkWriter();
        var output = new MemoryStream();
        writer.Append(output, 4, new RtmpMessage(RtmpMessageType.Audio, 1, 1000, [0xAF, 0x01, 0x22]));
        writer.Append(output, 4, new RtmpMessage(RtmpMessageType.Audio, 1, 1021, [0xAF, 0x01, 0x23]));
        byte[] expected =
        [
            0x04, 0x00, 0x03, 0xE8, 0x00, 0x00, 0x03, 0x08, 0x01, 0x00, 0x00, 0x00, 0xAF, 0x01, 0x22,
            0x44, 0x00, 0x00, 0x15, 0x00, 0x00, 0x03, 0x08, 0xAF, 0x01, 0x23,
        ];
        Assert.Equal(expected, output.ToArray());
    }

    [Fact]
    public void LongMessagesContinueInTypeThreeChunks()
    {
        var writer = new RtmpChunkWriter { ChunkSize = 128 };
        var output = new MemoryStream();
        var payload = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        writer.Append(output, 5, new RtmpMessage(RtmpMessageType.Video, 1, 0, payload));
        var bytes = output.ToArray();
        Assert.Equal(12 + 300 + 2, bytes.Length);
        Assert.Equal(0xC5, bytes[12 + 128]);
        Assert.Equal(0xC5, bytes[12 + 128 + 1 + 128]);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(4096)]
    [InlineData(8192)]
    public async Task RoundTripsInterleavedStreamsAtAnyChunkSize(int chunkSize)
    {
        var writer = new RtmpChunkWriter { ChunkSize = chunkSize };
        var output = new MemoryStream();
        var random = new Random(chunkSize);
        var sent = new List<(int Stream, RtmpMessage Message)>();
        uint videoTime = 0, audioTime = 0;
        for (var i = 0; i < 40; i++)
        {
            var video = new byte[random.Next(0, 20_000)];
            random.NextBytes(video);
            var audio = new byte[random.Next(1, 400)];
            random.NextBytes(audio);
            var v = new RtmpMessage(RtmpMessageType.Video, 1, videoTime += 33, video);
            var a = new RtmpMessage(RtmpMessageType.Audio, 1, audioTime += 21, audio);
            writer.Append(output, RtmpChunkStreamId.Video, v);
            writer.Append(output, RtmpChunkStreamId.Audio, a);
            sent.Add((RtmpChunkStreamId.Video, v));
            sent.Add((RtmpChunkStreamId.Audio, a));
        }
        output.Position = 0;
        var reader = new RtmpChunkReader(output) { ChunkSize = chunkSize };
        foreach (var (_, expected) in sent)
        {
            var message = await reader.ReadMessageAsync(Ct);
            Assert.Equal(expected.TypeId, message.TypeId);
            Assert.Equal(expected.StreamId, message.StreamId);
            Assert.Equal(expected.Timestamp, message.Timestamp);
            Assert.Equal(expected.Payload, message.Payload);
        }
        Assert.Equal(output.Length, reader.BytesRead);
    }

    [Fact]
    public async Task ExtendedTimestampsSurviveTheirContinuationChunks()
    {
        var writer = new RtmpChunkWriter { ChunkSize = 128 };
        var output = new MemoryStream();
        var big = new RtmpMessage(RtmpMessageType.Video, 1, 0x0100_0000, new byte[500]);
        var next = new RtmpMessage(RtmpMessageType.Video, 1, 0x0100_0021, new byte[10]);
        writer.Append(output, 6, big);
        writer.Append(output, 6, next);
        var bytes = output.ToArray();
        Assert.Equal([0xFF, 0xFF, 0xFF], bytes[1..4]);
        Assert.Equal([0x01, 0x00, 0x00, 0x00], bytes[12..16]);
        Assert.Equal(0xC6, bytes[16 + 128]);
        Assert.Equal([0x01, 0x00, 0x00, 0x00], bytes[(17 + 128)..(21 + 128)]);
        output.Position = 0;
        var reader = new RtmpChunkReader(output);
        Assert.Equal(0x0100_0000u, (await reader.ReadMessageAsync(Ct)).Timestamp);
        Assert.Equal(0x0100_0021u, (await reader.ReadMessageAsync(Ct)).Timestamp);
    }

    [Theory]
    [InlineData(64, new byte[] { 0x00, 0x00 })]
    [InlineData(319, new byte[] { 0x00, 0xFF })]
    [InlineData(320, new byte[] { 0x01, 0x00, 0x01 })]
    public async Task WideChunkStreamIdsUseTwoAndThreeByteHeaders(int chunkStreamId, byte[] basicHeader)
    {
        var writer = new RtmpChunkWriter();
        var output = new MemoryStream();
        writer.Append(output, chunkStreamId, new RtmpMessage(RtmpMessageType.DataAmf0, 1, 5, [1, 2, 3]));
        Assert.Equal(basicHeader, output.ToArray()[..basicHeader.Length]);
        output.Position = 0;
        var message = await new RtmpChunkReader(output).ReadMessageAsync(Ct);
        Assert.Equal([1, 2, 3], message.Payload);
    }

    [Fact]
    public async Task HandshakeEchoesBothWays()
    {
        var (client, server) = DuplexPipe.Create();
        var serverSide = RtmpHandshake.ServerAsync(server, Ct);
        await RtmpHandshake.ClientAsync(client, Ct);
        var c1 = await serverSide;
        Assert.Equal(RtmpHandshake.SignatureSize, c1.Length);
        Assert.Equal([0, 0, 0, 0], c1[4..8]);
    }

    [Theory]
    [InlineData("rtmp://a.rtmp.youtube.com/live2", false, "a.rtmp.youtube.com", 1935, "live2", "rtmp://a.rtmp.youtube.com/live2")]
    [InlineData("rtmps://live-api-s.facebook.com:443/rtmp/", true, "live-api-s.facebook.com", 443, "rtmp", "rtmps://live-api-s.facebook.com:443/rtmp")]
    [InlineData("rtmps://global-live.mux.com/app", true, "global-live.mux.com", 443, "app", "rtmps://global-live.mux.com/app")]
    [InlineData("rtmp://user:pass@host:1936/app/inst?token=1", false, "host", 1936, "app/inst?token=1", "rtmp://host:1936/app/inst?token=1")]
    public void ParsesServerUrls(string text, bool secure, string host, int port, string app, string tcUrl)
    {
        var url = RtmpUrl.Parse(text);
        Assert.Equal(secure, url.Secure);
        Assert.Equal(host, url.Host);
        Assert.Equal(port, url.Port);
        Assert.Equal(app, url.App);
        Assert.Equal(tcUrl, url.TcUrl);
    }

    [Fact]
    public void RejectsUrlsThatArentRtmp()
    {
        var error = Assert.Throws<StreamSessionException>(() => RtmpUrl.Parse("https://example.com/live"));
        Assert.Equal(StreamFailureKind.BadUrl, error.Kind);
    }

    [Fact]
    public void EndsTransportOnErrorsAndClosesLikeTheMac()
    {
        Assert.True(RtmpPublisher.EndsTransport(new RtmpStatus("NetStream.Publish.BadName", "error", "")));
        Assert.True(RtmpPublisher.EndsTransport(new RtmpStatus("NetConnection.Connect.Closed", "status", "")));
        Assert.True(RtmpPublisher.EndsTransport(new RtmpStatus("NetConnection.Connect.IdleTimeOut", "status", "")));
        Assert.True(RtmpPublisher.EndsTransport(new RtmpStatus("NetConnection.Connect.AppShutdown", "error", "")));
        Assert.False(RtmpPublisher.EndsTransport(new RtmpStatus("NetStream.Publish.Start", "status", "")));
    }
}

public sealed class FlvTagTests
{
    private static readonly byte[] Sps = [0x67, 0x64, 0x00, 0x1E, 0xAC, 0xD9, 0x40, 0xA0, 0x2F, 0xF9, 0x70, 0x11];
    private static readonly byte[] Pps = [0x68, 0xEB, 0xEC, 0xB2, 0x2C];

    [Fact]
    public void AvcSequenceHeaderWrapsTheDecoderConfigurationRecord()
    {
        var tag = FlvTags.AvcSequenceHeader(new AvcParameterSets(Sps, Pps));
        byte[] expected =
        [
            0x17, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x64, 0x00, 0x1E, 0xFF, 0xE1, 0x00, (byte)Sps.Length, .. Sps,
            0x01, 0x00, (byte)Pps.Length, .. Pps,
        ];
        Assert.Equal(expected, tag);
    }

    [Fact]
    public void PicturesCarryFrameTypeAndAvccUnits()
    {
        var nals = new ReadOnlyMemory<byte>[] { new byte[] { 0x65, 0x88, 0x84 }, new byte[] { 0x06, 0x05 } };
        var frame = new EncodedVideoFrame
        {
            Avcc = AvcBitstream.ToAvcc(nals), IsKeyframe = true, PresentationTime = 0, ParameterSets = new AvcParameterSets(Sps, Pps),
        };
        Assert.Equal([0x17, 0x01, 0, 0, 0, 0, 0, 0, 3, 0x65, 0x88, 0x84, 0, 0, 0, 2, 0x06, 0x05], FlvTags.AvcPicture(frame));
        var inter = new EncodedVideoFrame { Avcc = [0, 0, 0, 1, 0x41], IsKeyframe = false, PresentationTime = 0, ParameterSets = frame.ParameterSets };
        Assert.Equal([0x27, 0x01, 0, 0, 0, 0, 0, 0, 1, 0x41], FlvTags.AvcPicture(inter, compositionTimeMs: 0));
    }

    [Fact]
    public void AacTagsUseTheAacHeaderByte()
    {
        var config = AudioSpecificConfig.AacLowComplexity(48_000, 2)!;
        Assert.Equal([0xAF, 0x00, 0x11, 0x90], FlvTags.AacSequenceHeader(config));
        Assert.Equal([0xAF, 0x01, 0x21, 0x10], FlvTags.AacRaw([0x21, 0x10]));
        Assert.Equal([0x12, 0x10], AudioSpecificConfig.AacLowComplexity(44_100, 2)!.Bytes);
        Assert.Equal([0x11, 0x88], AudioSpecificConfig.AacLowComplexity(48_000, 1)!.Bytes);
        Assert.Null(AudioSpecificConfig.AacLowComplexity(12_345, 2));
    }

    [Fact]
    public void AnnexBSplitsOnThreeAndFourByteStartCodes()
    {
        byte[] stream = [0, 0, 0, 1, 0x09, 0x10, 0, 0, 0, 1, 0x67, 0x64, 0, 0, 1, 0x68, 0xEB, 0, 0, 0, 1, 0x65, 0xAA, 0xBB];
        var units = AvcBitstream.SplitAnnexB(stream);
        Assert.Equal(4, units.Count);
        Assert.Equal([0x09, 0x10], units[0].ToArray());
        Assert.Equal([0x67, 0x64], units[1].ToArray());
        Assert.Equal([0x68, 0xEB], units[2].ToArray());
        Assert.Equal([0x65, 0xAA, 0xBB], units[3].ToArray());
        Assert.Equal(units.Select(u => u.ToArray()), AvcBitstream.SplitAvcc(AvcBitstream.ToAvcc(units)).Select(u => u.ToArray()));
    }
}

/// <summary>Two ends of a loopback TCP connection (what one side writes, the other reads).</summary>
internal static class DuplexPipe
{
    public static (Stream Client, Stream Server) Create()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var client = new System.Net.Sockets.TcpClient();
        client.Connect((System.Net.IPEndPoint)listener.LocalEndpoint);
        var server = listener.AcceptTcpClient();
        listener.Stop();
        return (client.GetStream(), server.GetStream());
    }
}
