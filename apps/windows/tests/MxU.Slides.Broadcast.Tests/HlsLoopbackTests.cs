using System.Text;
using MxU.Slides.Broadcast.Hls.Vendored;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Streaming;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>
/// Port of HLSLoopbackTests: a whole HLS session (encode, segment, upload) against a stand-in
/// ingest instead of the Mac's node sink. Checks the ingest contract the sink checks: segments
/// are whole TS packets that open with the tables and a keyframe, and the playlist is re-sent
/// after every segment.
/// </summary>
public sealed class HlsLoopbackTests
{
    private const string Template = "https://ingest.example/http_upload_hls?cid=test&copy=0&file=";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishesSegmentsToTheIngest(bool withAudio)
    {
        var ingest = new StubIngest();
        var session = new StreamSession(
            new StreamEndpoint("loopback", StreamEndpointKind.Hls, Template),
            new StreamVideoConfiguration(640, 360, 30, 800),
            new StreamSessionOptions { Encoder = EncoderPreference.Software, HlsHandler = ingest, HlsStopGrace = TimeSpan.FromSeconds(20) });
        session.Start();
        while (session.Status.State != StreamSessionState.Publishing) await Task.Delay(10, Ct);

        long audioSample = 0;
        for (var frame = 0; frame < 196; frame++)
        {
            var seconds = frame / 30.0;
            session.WriteVideo(Synthetic.VideoFrame(640, 360, frame, seconds));
            if (withAudio && frame >= 8)
            {
                session.WriteAudio(Synthetic.AudioBuffer(48_000, 2, 1600, audioSample, seconds));
                audioSample += 1600;
            }
            await Task.Delay(20, Ct);
        }
        session.Stop();
        await session.Supervisor!.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var segments = ingest.Requests.Where(r => r.Url.EndsWith(".ts", StringComparison.Ordinal)).ToList();
        var playlists = ingest.Requests.Where(r => r.Url.EndsWith("playlist.m3u8", StringComparison.Ordinal)).ToList();
        Assert.True(segments.Count >= 3, $"only {segments.Count} segments");
        Assert.True(playlists.Count >= segments.Count, "playlist re-PUT after every segment");
        Assert.Equal(segments.Select(s => s.Url).Distinct().Count(), segments.Count);

        foreach (var (url, body, _) in segments)
        {
            Assert.True(body.Length > 0 && body.Length % 188 == 0, $"{url}: not whole TS packets");
            for (var offset = 0; offset < body.Length; offset += 188) Assert.Equal(TsPacket.SyncByte, body[offset]);
            Assert.Equal(0, HlsMuxTests.Pid(body, 0));
            Assert.Equal(TsWriter.DefaultPmtPid, HlsMuxTests.Pid(body, 188));
            Assert.Equal(TsWriter.DefaultVideoPid, HlsMuxTests.Pid(body, 376));
            var pids = Enumerable.Range(0, body.Length / 188).Select(i => HlsMuxTests.Pid(body, i * 188)).ToHashSet();
            Assert.Equal(withAudio, pids.Contains(TsWriter.DefaultAudioPid));
        }
        var last = Encoding.UTF8.GetString(playlists[^1].Body);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:", last);
        Assert.Contains(segments[^1].Url[(segments[^1].Url.LastIndexOf('=') + 1)..], last);
        // Every finished segment is about two seconds (the last may be shorter).
        var durations = last.Split('\n').Where(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal))
            .Select(l => double.Parse(l[8..^1], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.All(durations.SkipLast(1), d => Assert.InRange(d, 1.9, 2.2));
    }

    [Fact]
    public async Task ExpiredIngestFailsTheSession()
    {
        var ingest = new StubIngest(401);
        var session = new StreamSession(
            new StreamEndpoint("expired", StreamEndpointKind.Hls, Template),
            new StreamVideoConfiguration(320, 180, 30, 500),
            new StreamSessionOptions { Encoder = EncoderPreference.Software, HlsHandler = ingest });
        session.Start();
        // Keep feeding until the first segment's upload is refused (a busy machine encodes slowly).
        var until = DateTime.UtcNow.AddSeconds(15);
        for (var frame = 0; DateTime.UtcNow < until && session.Status.State.Kind != StreamStateKind.Failed; frame++)
        {
            session.WriteVideo(Synthetic.VideoFrame(320, 180, frame, frame / 30.0));
            await Task.Delay(10, Ct);
        }
        await session.Supervisor!.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(StreamStateKind.Failed, session.Status.State.Kind);
        Assert.Contains("401", session.Status.State.Reason);
    }
}
