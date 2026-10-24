using System.Buffers.Binary;
using System.Net;
using System.Text;
using MxU.Slides.Broadcast.Hls;
using MxU.Slides.Broadcast.Hls.Vendored;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Streaming;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>Port of HLSPlaylistTests.</summary>
public sealed class HlsPlaylistTests
{
    [Fact]
    public void SequenceStartsAtZeroAndAdvancesWithTheWindow()
    {
        var playlist = new HlsPlaylist();
        Assert.Equal(0, playlist.MediaSequence);
        for (var index = 0; index < 7; index++) playlist.Add($"seg-1-{index}.ts", 2.0);
        Assert.Equal(5, playlist.Segments.Count);
        Assert.Equal(2, playlist.MediaSequence);
        Assert.Equal("seg-1-2.ts", playlist.Segments[0].Name);
    }

    [Fact]
    public void RenderMatchesTheIngestContract()
    {
        var playlist = new HlsPlaylist(mediaSequence: 10);
        playlist.Add("seg-1-10.ts", 1.966);
        playlist.Add("seg-1-11.ts", 2.033);
        var rendered = playlist.Render();
        Assert.StartsWith("#EXTM3U\n#EXT-X-VERSION:3\n", rendered);
        Assert.Contains("#EXT-X-TARGETDURATION:3\n", rendered);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:10\n", rendered);
        Assert.Contains("#EXTINF:1.966,\nseg-1-10.ts\n", rendered);
        Assert.Contains("#EXTINF:2.033,\nseg-1-11.ts\n", rendered);
        Assert.DoesNotContain("EXT-X-KEY", rendered);
    }

    [Fact]
    public void ContinuityKeepsNamesUniqueAndSequenceMonotonic()
    {
        var continuity = new HlsContinuity(epochMillis: 42);
        Assert.Equal("seg-42-0.ts", continuity.ClaimSegmentName());
        Assert.Equal("seg-42-1.ts", continuity.ClaimSegmentName());
        continuity.RecordSequence(7);
        continuity.RecordSequence(3);
        Assert.Equal(7, continuity.ResumeSequence);
        Assert.Equal(7, new HlsPlaylist(continuity.ResumeSequence).MediaSequence);
    }

    [Fact]
    public void UploadUrlCompletesTheTemplate()
    {
        Assert.Equal(
            "https://a.upload.youtube.com/http_upload_hls?cid=key&copy=0&file=seg-1-0.ts",
            HlsUploader.UploadUrl("https://a.upload.youtube.com/http_upload_hls?cid=key&copy=0&file=", "seg-1-0.ts")?.AbsoluteUri);
        Assert.Equal(
            "https://example.com/ingest?cid=key&file=p.m3u8",
            HlsUploader.UploadUrl("https://example.com/ingest?cid=key", "p.m3u8")?.AbsoluteUri);
        Assert.Equal("https://example.com/ingest?file=p.m3u8", HlsUploader.UploadUrl("https://example.com/ingest", "p.m3u8")?.AbsoluteUri);
        Assert.Null(HlsUploader.UploadUrl("not a url", "p.m3u8"));
    }
}

/// <summary>Port of HLSMuxTests, plus checks of the tables every segment opens with.</summary>
public sealed class HlsMuxTests
{
    internal static readonly byte[] Sps =
    [
        0x67, 0x64, 0x00, 0x1E, 0xAC, 0xD9, 0x40, 0xA0, 0x2F, 0xF9, 0x70, 0x11, 0x00, 0x00, 0x03, 0x00,
        0x01, 0x00, 0x00, 0x03, 0x00, 0x32, 0x0F, 0x16, 0x2D, 0x96,
    ];

    internal static readonly byte[] Pps = [0x68, 0xEB, 0xEC, 0xB2, 0x2C];

    internal static EncodedVideoFrame H264Sample(double seconds, bool keyframe)
    {
        byte[] payload = [0, 0, 0, 9, keyframe ? (byte)0x65 : (byte)0x41, .. Enumerable.Repeat((byte)0xAB, 8)];
        return new EncodedVideoFrame { Avcc = payload, IsKeyframe = keyframe, PresentationTime = seconds, ParameterSets = new AvcParameterSets(Sps, Pps) };
    }

    [Fact]
    public void WriterCutsSegmentsOnKeyframes()
    {
        var writer = new TsWriter(segmentDuration: 2.0) { ExpectedMedias = TsMedia.Video };
        var segments = new List<(byte[] Data, double Duration)>();
        var current = new MemoryStream();
        writer.Sink = bytes => current.Write(bytes.Span);
        writer.OnSegmentCut = duration =>
        {
            segments.Add((current.ToArray(), duration));
            current = new MemoryStream();
        };
        writer.VideoFormat = new AvcParameterSets(Sps, Pps);
        for (var frame = 0; frame < 181; frame++)
            writer.Append(H264Sample(frame / 30.0, frame % 60 == 0));

        Assert.Equal(3, segments.Count);
        foreach (var (data, duration) in segments)
        {
            Assert.Equal(2.0, duration, 0.05);
            Assert.Equal(0, data.Length % 188);
            Assert.Equal(0x47, data[0]);
            Assert.True(duration >= 2.0);
        }
        var second = segments[1].Data;
        Assert.Equal(0x47, second[0]);
        Assert.Equal(0, Pid(second, 0));
        Assert.Equal(TsWriter.DefaultPmtPid, Pid(second, 188));
        Assert.Equal(TsWriter.DefaultVideoPid, Pid(second, 376));
    }

    internal static int Pid(byte[] data, int offset) => (data[offset + 1] & 0x1F) << 8 | data[offset + 2];

    [Fact]
    public void ProgramTablesCarryValidCrcs()
    {
        var writer = new TsWriter { ExpectedMedias = TsMedia.Video | TsMedia.Audio };
        var output = new MemoryStream();
        writer.Sink = bytes => output.Write(bytes.Span);
        writer.VideoFormat = new AvcParameterSets(Sps, Pps);
        writer.AudioFormat = AudioSpecificConfig.AacLowComplexity(48_000, 2);
        var bytes = output.ToArray();
        Assert.Equal(2 * 188, bytes.Length);
        foreach (var offset in new[] { 0, 188 })
        {
            Assert.Equal(0x40, bytes[offset + 1] & 0x40);
            var section = bytes.AsSpan(offset + 5);
            var length = (section[1] & 0x0F) << 8 | section[2];
            var crc = BinaryPrimitives.ReadUInt32BigEndian(section[(3 + length - 4)..]);
            Assert.Equal(Crc32.Mpeg2.Calculate(section[..(3 + length - 4)]), crc);
        }
        // The PMT lists H.264 on 256 and ADTS AAC on 257, with the clock on the video.
        var pmt = bytes.AsSpan(188 + 5);
        Assert.Equal(0x02, pmt[0]);
        Assert.Equal(TsWriter.DefaultVideoPid, (pmt[8] & 0x1F) << 8 | pmt[9]);
        Assert.Equal(0x1B, pmt[12]);
        Assert.Equal(0x0F, pmt[17]);
    }

    [Fact]
    public void PesTimestampsStartATenthOfASecondIn()
    {
        var writer = new TsWriter { ExpectedMedias = TsMedia.Video };
        var output = new MemoryStream();
        writer.Sink = bytes => output.Write(bytes.Span);
        writer.VideoFormat = new AvcParameterSets(Sps, Pps);
        output.SetLength(0);
        writer.Append(H264Sample(500.0, keyframe: true));
        var packet = output.ToArray();
        Assert.Equal(TsWriter.DefaultVideoPid, Pid(packet, 0));
        Assert.Equal(188, packet.Length);
        // A short picture fits one packet: header (4), the stuffing adaptation field, then the PES:
        // 00 00 01 E0, length (2), flags (2), header length (1), PTS (5), data.
        Assert.Equal(0x30, packet[3] & 0x30);
        var p = 5 + packet[4];
        Assert.Equal([0x00, 0x00, 0x01, 0xE0], packet[p..(p + 4)]);
        Assert.Equal(0x80, packet[p + 7] & 0xC0);
        var pts = packet.AsSpan(p + 9, 5);
        long value = (long)(pts[0] & 0x0E) << 29 | (long)pts[1] << 22 | (long)(pts[2] & 0xFE) << 14 | (long)pts[3] << 7 | (long)(pts[4] >> 1);
        Assert.Equal(9000, value);
        // Keyframes open with an access unit delimiter and the parameter sets.
        Assert.Equal([0x00, 0x00, 0x00, 0x01, 0x09, 0x10, 0x00, 0x00, 0x00, 0x01, 0x67], packet[(p + 14)..(p + 25)]);
    }

    [Fact]
    public void AudioOnlyWaitsForItsFormat()
    {
        var writer = new TsWriter { ExpectedMedias = TsMedia.Video | TsMedia.Audio };
        var output = new MemoryStream();
        writer.Sink = bytes => output.Write(bytes.Span);
        writer.VideoFormat = new AvcParameterSets(Sps, Pps);
        writer.Append(H264Sample(1, true));
        Assert.Equal(0, output.Length);
    }
}

/// <summary>A stand-in HLS ingest: records every PUT and answers with queued status codes (200 when none).</summary>
internal sealed class StubIngest : HttpMessageHandler
{
    private readonly Lock gate = new();
    private readonly Queue<int> responses = new();
    private readonly List<(string Url, byte[] Body, string? ContentType)> requests = [];

    public StubIngest(params int[] statuses)
    {
        foreach (var status in statuses) responses.Enqueue(status);
    }

    public IReadOnlyList<(string Url, byte[] Body, string? ContentType)> Requests
    {
        get { lock (gate) return [.. requests]; }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        int status;
        lock (gate)
        {
            requests.Add((request.RequestUri!.AbsoluteUri, body, request.Content?.Headers.ContentType?.MediaType));
            status = responses.Count > 0 ? responses.Dequeue() : 200;
        }
        return new HttpResponseMessage((HttpStatusCode)status);
    }
}

/// <summary>Port of HLSUploaderTests.</summary>
public sealed class HlsUploaderTests
{
    private const string Primary = "https://a.upload.example/http_upload_hls?cid=key&copy=0&file=";
    private const string Backup = "https://b.upload.example/http_upload_hls?cid=key&copy=1&file=";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HlsUploader Make(StubIngest stub, string? backup = Backup) =>
        new(Primary, backup, new HlsContinuity(epochMillis: 42), stub) { Sleep = (_, _) => Task.CompletedTask };

    [Fact]
    public async Task DeliversSegmentThenPlaylistAndDrainsOnFinish()
    {
        var stub = new StubIngest();
        var uploader = Make(stub);
        uploader.Enqueue(new HlsUploader.Item("seg-42-0.ts", [0x47, 1, 2], 2.0));
        uploader.FinishEnqueuing();
        await uploader.RunAsync(Ct);
        Assert.Equal(2, stub.Requests.Count);
        Assert.EndsWith("file=seg-42-0.ts", stub.Requests[0].Url);
        Assert.Equal("video/mp2t", stub.Requests[0].ContentType);
        Assert.EndsWith("file=playlist.m3u8", stub.Requests[1].Url);
        Assert.Equal("application/vnd.apple.mpegurl", stub.Requests[1].ContentType);
        var playlist = Encoding.UTF8.GetString(stub.Requests[1].Body);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:0", playlist);
        Assert.Contains("seg-42-0.ts", playlist);
    }

    [Fact]
    public async Task RetriesServerErrorsThenFailsOverToBackup()
    {
        var stub = new StubIngest(500, 500, 200, 200);
        var uploader = Make(stub);
        uploader.Enqueue(new HlsUploader.Item("seg-42-0.ts", [0x47], 2.0));
        uploader.FinishEnqueuing();
        await uploader.RunAsync(Ct);
        var urls = stub.Requests.Select(r => r.Url).ToList();
        Assert.Equal(4, urls.Count);
        Assert.StartsWith("https://a.upload.example", urls[0]);
        Assert.StartsWith("https://a.upload.example", urls[1]);
        Assert.StartsWith("https://b.upload.example", urls[2]);
        Assert.StartsWith("https://b.upload.example", urls[3]);
    }

    [Fact]
    public async Task UnauthorizedIsFatalNotRetried()
    {
        var stub = new StubIngest(401);
        var uploader = Make(stub);
        uploader.Enqueue(new HlsUploader.Item("seg-42-0.ts", [0x47], 2.0));
        uploader.FinishEnqueuing();
        var error = await Assert.ThrowsAsync<StreamSessionException>(() => uploader.RunAsync(Ct));
        Assert.True(error.IsFatal);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task ExhaustedRetriesWithoutBackupThrowTransportClosed()
    {
        var stub = new StubIngest(500, 500, 500, 500);
        var delays = new List<TimeSpan>();
        var uploader = new HlsUploader(Primary, null, new HlsContinuity(42), stub)
        {
            Sleep = (delay, _) => { delays.Add(delay); return Task.CompletedTask; },
        };
        uploader.Enqueue(new HlsUploader.Item("seg-42-0.ts", [0x47], 2.0));
        uploader.FinishEnqueuing();
        var error = await Assert.ThrowsAsync<StreamSessionException>(() => uploader.RunAsync(Ct));
        Assert.False(error.IsFatal);
        Assert.Equal([TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }
}

/// <summary>Port of HLSAudioADTSTests: real AAC frames from Windows' encoder get valid ADTS headers.</summary>
public sealed class HlsAudioAdtsTests
{
    [Fact]
    public void EncodedPacketsCarryValidAdtsHeaders()
    {
        using var encoder = AacEncoder.Create(48_000, 2);
        var packets = new List<EncodedAudioFrame>();
        long sample = 0;
        for (var i = 0; i < 24; i++)
        {
            packets.AddRange(encoder.Encode(Synthetic.Tone(48_000, 2, 1024, sample), sample / 48_000.0));
            sample += 1024;
        }
        Assert.NotEmpty(packets);
        foreach (var packet in packets)
        {
            var data = packet.Config.Adts(packet.Data);
            Assert.Equal(0xFF, data[0]);
            Assert.Equal(0xF9, data[1]);
            Assert.Equal(0x4C, data[2]);
            Assert.Equal(0x80, data[3] & 0xC0);
            var declared = (data[3] & 0x03) << 11 | data[4] << 3 | data[5] >> 5;
            Assert.Equal(packet.Data.Length + 7, declared);
            Assert.Contains(data.Skip(7), b => b != 0);
        }
        for (var i = 1; i < packets.Count; i++)
            Assert.Equal(1024 / 48_000.0, packets[i].PresentationTime - packets[i - 1].PresentationTime, 6);
    }
}

/// <summary>Port of HLSVideoEncoderTests: on a still picture the forced cadence keeps every GOP within one segment.</summary>
public sealed class HlsVideoEncoderTests
{
    [Fact]
    public void H264KeyframeCadenceHoldsOnStaticContent()
    {
        const double segmentDuration = 2.0;
        const int frameRate = 30;
        const int totalFrames = 300;
        using var encoder = H264Encoder.Create(new H264Settings(1920, 1080, frameRate, 9000, segmentDuration), EncoderPreference.Software);
        var converter = new Nv12Converter(1920, 1080);
        var nv12 = new byte[converter.FrameSize];
        converter.Convert(Synthetic.Frame(1920, 1080, 0, shade: 128), 1920, 1080, 1920 * 4, nv12);
        var cadence = new KeyframeCadence(segmentDuration);
        var flags = new List<bool>();
        for (var index = 0; index < totalFrames; index++)
        {
            var pts = 1000 + index / (double)frameRate;
            flags.AddRange(encoder.Encode(nv12, pts, cadence.ShouldForce(pts)).Select(f => f.IsKeyframe));
        }
        flags.AddRange(encoder.Drain().Select(f => f.IsKeyframe));

        Assert.True(flags.Count > totalFrames * 9 / 10, "encoder dropped frames");
        var keyframes = flags.Select((key, i) => (key, i)).Where(x => x.key).Select(x => x.i).ToList();
        Assert.NotEmpty(keyframes);
        const int cadenceFrames = (int)(segmentDuration * frameRate);
        for (var i = 1; i < keyframes.Count; i++)
            Assert.True(keyframes[i] - keyframes[i - 1] <= cadenceFrames + 1, $"GOP stretched to {keyframes[i] - keyframes[i - 1]} frames");
        Assert.True(flags.Count - 1 - keyframes[^1] <= cadenceFrames + 1, "final GOP stretched");
    }
}
