using MxU.Slides.Broadcast.Control;
using MxU.Slides.Broadcast.Library;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Recording;
using MxU.Slides.Broadcast.Streaming;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>A program source that just remembers who is listening, and can play frames to them.</summary>
internal sealed class FakeProgram : IProgramSource
{
    private readonly Lock gate = new();
    private readonly List<IProgramSink> sinks = [];

    public IReadOnlyList<IProgramSink> Sinks
    {
        get { lock (gate) return [.. sinks]; }
    }

    public void Add(IProgramSink sink)
    {
        lock (gate) sinks.Add(sink);
    }

    public void Remove(IProgramSink sink)
    {
        lock (gate) sinks.Remove(sink);
    }

    public void Play(VideoFrame frame)
    {
        foreach (var sink in Sinks) sink.WriteVideo(frame);
    }
}

/// <summary>The non-UI half of the Mac's StreamingController, RecordingController and ServiceControls.</summary>
public sealed class StreamingControllerTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "BroadcastControl-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static StreamPresetDestination Destination(string name, string? key = "k", int? maxHeight = null, int? width = null, int? height = null) => new()
    {
        Id = Guid.NewGuid().ToString(), Name = name, Transport = StreamTransport.Rtmp, Url = "rtmp://127.0.0.1:1/live", StreamKey = key,
        MaxHeight = maxHeight, Width = width, Height = height,
    };

    private static StreamRecordPreset Preset(string id = "sunday", int? bitrate = 4500) =>
        new() { Id = id, Name = "Sunday", PresetKind = StreamPresetKind.Stream, DestinationIds = ["x"], VideoBitrateKbps = bitrate };

    private static StreamSessionOptions Quiet => new() { Encoder = EncoderPreference.Software, ReconnectDelay = (_, ct) => Task.Delay(1000, ct) };

    [Fact]
    public void DestinationsGetThePresetPictureUnlessTheyOverrideOrCapIt()
    {
        var preset = Preset();
        var plain = StreamingController.ResolveVideo(preset, StreamingController.SessionPlanFor(Destination("A")));
        Assert.Equal(new StreamVideoConfiguration(1920, 1080, 30, 4500), plain);

        var overridden = StreamingController.ResolveVideo(preset, StreamingController.SessionPlanFor(Destination("B", width: 1280, height: 720)));
        Assert.Equal((1280, 720, 6000), (overridden.Width, overridden.Height, overridden.BitrateKbps));

        var vertical = StreamingController.ResolveVideo(preset, StreamingController.SessionPlanFor(Destination("C", width: 1080, height: 1920)));
        Assert.Equal(10000, vertical.BitrateKbps);

        var capped = StreamingController.ResolveVideo(preset, StreamingController.SessionPlanFor(Destination("D", maxHeight: 720)));
        Assert.Equal((1280, 720, 2000), (capped.Width, capped.Height, capped.BitrateKbps));

        var rich = StreamingController.ResolveVideo(Preset(bitrate: 12000), StreamingController.SessionPlanFor(Destination("E", maxHeight: 720)));
        Assert.Equal(5333, rich.BitrateKbps);
    }

    [Fact]
    public void OnlyHlsHevcKeepsHevcAndHdr()
    {
        var hls = new StreamPresetDestination { Id = "h", Name = "YT", Transport = StreamTransport.Hls, Url = "https://x", VideoCodec = StreamVideoCodec.Hevc, Hdr = true };
        var plan = StreamingController.SessionPlanFor(hls);
        Assert.Equal(StreamCodec.Hevc, plan.Codec);
        Assert.True(plan.Hdr);
        var rtmp = StreamingController.SessionPlanFor(new StreamPresetDestination { Id = "r", Name = "R", Transport = StreamTransport.Rtmps, Url = "rtmps://x", VideoCodec = StreamVideoCodec.Hevc, Hdr = true });
        Assert.Equal(StreamCodec.H264, rtmp.Codec);
        Assert.False(rtmp.Hdr);
        Assert.Equal(StreamEndpointKind.Rtmps, rtmp.Destination.Kind);
    }

    [Fact]
    public void RefusesWithANoticeWhenNothingCanStart()
    {
        var program = new FakeProgram();
        using var recording = new RecordingController(program, scratch);
        using var streaming = new StreamingController(program, recording, Quiet);
        streaming.StartCapture(Preset(), [Destination("Resi", key: null)]);
        Assert.Null(streaming.Live);
        Assert.Equal("Sunday — Won't go live: Resi: no stream key", streaming.StartNotice);
        Assert.Empty(program.Sinks);
        Assert.Equal(BroadcastStatus.ChipTint.Orange, BroadcastStatus.Tint(streaming, recording));
        Assert.Equal("NOT STARTED", BroadcastStatus.Caption(streaming, recording, DateTimeOffset.Now));

        streaming.SourceResolver = _ => new ResolvedSource.Unavailable("“Intro” isn't ready to play");
        streaming.StartCapture(Preset(), [Destination("Resi")]);
        Assert.Equal("Sunday: “Intro” isn't ready to play", streaming.StartNotice);
    }

    [Fact]
    public void GoingLiveStartsOneSessionPerDestinationAndEndingDetachesThem()
    {
        var program = new FakeProgram();
        using var recording = new RecordingController(program, scratch);
        using var streaming = new StreamingController(program, recording, Quiet);
        var changes = 0;
        streaming.LiveChanged += () => changes++;
        streaming.StartCapture(Preset(), [Destination("A"), Destination("B", maxHeight: 720)]);
        var live = Assert.IsType<LiveStream>(streaming.Live);
        Assert.Equal(2, live.Sessions.Count);
        Assert.Equal(2, program.Sinks.Count);
        Assert.All(program.Sinks, s => Assert.IsType<FramePacer>(s));
        Assert.Equal(720, live.Sessions[1].Video.Height);
        Assert.True(streaming.IsLive);
        Assert.NotEqual(StreamHealthRollup.Tint.None, streaming.WorstTint);
        Assert.StartsWith("A — ", BroadcastStatus.HealthLine(live.Sessions[0]));

        streaming.EndStream();
        Assert.Null(streaming.Live);
        Assert.Empty(program.Sinks);
        Assert.All(live.Sessions, s => Assert.Equal(StreamSessionState.Stopped, s.Status.State));
        Assert.Equal(2, changes);
        Assert.Null(BroadcastStatus.Caption(streaming, recording, DateTimeOffset.Now));
    }

    [Fact]
    public async Task RecordOnlyPresetsRecordAndRecordingsFinishIntoTheFolder()
    {
        var program = new FakeProgram();
        using var recording = new RecordingController(program, scratch) { Encoder = EncoderPreference.Software };
        using var streaming = new StreamingController(program, recording, Quiet);
        var preset = new StreamRecordPreset { Id = "rec", Name = "Archive", Width = 320, Height = 180, FrameRate = 30, VideoBitrateKbps = 800 };
        streaming.StartCapture(preset, []);
        Assert.Null(streaming.Live);
        var session = Assert.Single(recording.Sessions);
        Assert.True(recording.AnyRecording);
        Assert.Equal("rec", session.PresetId);
        Assert.Single(program.Sinks);
        for (var frame = 0; frame < 15; frame++)
        {
            program.Play(Synthetic.VideoFrame(640, 360, frame, 100 + frame / 30.0));
            await Task.Delay(20, Ct);
        }
        Assert.StartsWith("REC 0:00", BroadcastStatus.Caption(streaming, recording, DateTimeOffset.Now));
        Assert.Equal(BroadcastStatus.ChipTint.Green, BroadcastStatus.Tint(streaming, recording));
        await recording.StopAsync(session);
        Assert.Empty(recording.Sessions);
        Assert.Empty(program.Sinks);
        var file = Assert.Single(Directory.GetFiles(scratch, "*.mp4"));
        Assert.StartsWith("Archive ", Path.GetFileName(file));
        Assert.Contains(" at ", Path.GetFileName(file));
        Assert.Equal((320, 180), (MediaProbe.Read(file).Width, MediaProbe.Read(file).Height));
    }

    [Fact]
    public async Task StartingAnotherPresetEndsTheStreamOnAirAndItsRecordings()
    {
        var program = new FakeProgram();
        using var recording = new RecordingController(program, scratch) { Encoder = EncoderPreference.Software };
        using var streaming = new StreamingController(program, recording, Quiet);
        var first = Preset("first");
        first.RecordWhileStreaming = true;
        first.Width = 320;
        first.Height = 180;
        streaming.StartCapture(first, [Destination("A")]);
        Assert.Single(recording.Sessions);
        program.Play(Synthetic.VideoFrame(320, 180, 0, 1));
        var firstLive = streaming.Live!;

        streaming.StartCapture(Preset("second"), [Destination("B")]);
        Assert.Equal("second", streaming.Live!.PresetId);
        Assert.Equal(StreamSessionState.Stopped, firstLive.Sessions[0].Status.State);
        for (var i = 0; i < 100 && recording.Sessions.Count > 0; i++) await Task.Delay(50, Ct);
        Assert.Empty(recording.Sessions);
        streaming.EndStream();
    }

    [Fact]
    public async Task BroadcastControllerResolvesLibraryDestinationsWithTheirKeys()
    {
        var store = new LibraryStore(Path.Combine(scratch, "Library"));
        store.Load();
        var vault = new StreamKeyVault(Path.Combine(scratch, "Keys"));
        StreamDestinationLibrary.SaveDestination(store, vault, new StreamDestination
        {
            Id = "yt", Name = "YouTube", Transport = StreamTransport.Rtmp, Url = "rtmp://127.0.0.1:1/live2", StreamKey = "secret",
        });
        store.Save(new StreamRecordPreset { Id = "sunday", Name = "Sunday", PresetKind = StreamPresetKind.Stream, DestinationIds = ["yt", "gone"] });
        store.Save(new StreamRecordPreset { Id = "archive", Name = "Archive", PresetKind = StreamPresetKind.RecordOnly, Width = 320, Height = 180 });
        var program = new FakeProgram();
        using var controller = new BroadcastController(store, vault, program, Path.Combine(scratch, "Recordings"), Quiet);
        controller.Recording.Encoder = EncoderPreference.Software;
        Assert.Empty(controller.Prepare());

        var resolved = controller.ResolvedDestinations(store.Get<StreamRecordPreset>("sunday")!);
        Assert.Equal("secret", Assert.Single(resolved).StreamKey);

        controller.StartCapture("sunday");
        Assert.Equal("secret", controller.Streaming.Live!.Sessions[0].Destination.StreamKey);
        Assert.Equal(1, controller.RunningCount("sunday"));
        Assert.Equal("Sunday", controller.StartWouldReplace("sunday"));
        Assert.Null(controller.StartWouldReplace("archive"));

        controller.StartCapture("archive");
        Assert.Equal(1, controller.RunningCount("archive"));
        Assert.Equal("sunday", controller.Streaming.Live!.PresetId);
        program.Play(Synthetic.VideoFrame(320, 180, 0, 5));
        controller.EndAllCapture();
        Assert.Null(controller.Streaming.Live);
        for (var i = 0; i < 100 && controller.Recording.Sessions.Count > 0; i++) await Task.Delay(50, Ct);
        Assert.Empty(controller.Recording.Sessions);
    }

    [Fact]
    public void BytesReadLikeTheMac()
    {
        Assert.Equal("980 KB", BroadcastStatus.FormatBytes(980_000));
        Assert.Equal("4.7 GB", BroadcastStatus.FormatBytes(4_700_000_000).Replace(',', '.'));
        Assert.Equal("41.2 GB", BroadcastStatus.FormatBytes(41_234_000_000).Replace(',', '.'));
        Assert.Equal("512 bytes", BroadcastStatus.FormatBytes(512));
    }
}

public sealed class Nv12ConverterTests
{
    private static byte[] Solid(int width, int height, byte b, byte g, byte r)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    [Theory]
    [InlineData(255, 255, 255, 235, 128, 128)]
    [InlineData(0, 0, 0, 16, 128, 128)]
    [InlineData(0, 0, 255, 63, 102, 240)]
    [InlineData(0, 255, 0, 173, 42, 26)]
    [InlineData(255, 0, 0, 32, 240, 118)]
    public void UsesBt709VideoRange(byte b, byte g, byte r, byte y, byte u, byte v)
    {
        var converter = new Nv12Converter(4, 4);
        var nv12 = new byte[converter.FrameSize];
        converter.Convert(Solid(4, 4, b, g, r), 4, 4, 16, nv12);
        Assert.All(nv12[..16], value => Assert.Equal(y, value));
        for (var i = 16; i < nv12.Length; i += 2)
        {
            Assert.Equal(u, nv12[i]);
            Assert.Equal(v, nv12[i + 1]);
        }
    }

    [Fact]
    public void LetterboxesAFrameOfAnotherShape()
    {
        var converter = new Nv12Converter(640, 360);
        var nv12 = new byte[converter.FrameSize];
        converter.Convert(Solid(640, 480, 255, 255, 255), 640, 480, 640 * 4, nv12);
        // 4:3 in 16:9: a 480-wide picture with 80-pixel black bars either side.
        var row = 180 * 640;
        Assert.Equal(16, nv12[row + 10]);
        Assert.Equal(16, nv12[row + 79]);
        Assert.Equal(235, nv12[row + 80]);
        Assert.Equal(235, nv12[row + 559]);
        Assert.Equal(16, nv12[row + 560]);
        Assert.Equal(128, nv12[640 * 360 + 10]);
    }

    [Fact]
    public void HonoursTheStride()
    {
        var converter = new Nv12Converter(2, 2);
        var nv12 = new byte[converter.FrameSize];
        var pixels = new byte[2 * 16];
        pixels.AsSpan(0, 8).Fill(255);
        pixels.AsSpan(16, 8).Fill(255);
        converter.Convert(pixels, 2, 2, 16, nv12);
        Assert.Equal([235, 235, 235, 235, 128, 128], nv12);
    }
}

public sealed class PcmAndPacingTests
{
    [Fact]
    public void MonoIsCopiedToBothChannelsOfStereo()
    {
        var converter = new PcmConverter(48_000, 1, 48_000, 2);
        var pcm = converter.Convert([0.5f, -0.5f]);
        Assert.Equal([16384, 16384, -16384, -16384], pcm);
    }

    [Fact]
    public void ExtraChannelsAreLeftOutAndStereoPassesThrough()
    {
        Assert.Equal(2, new PcmConverter(48_000, 6).OutputChannels);
        Assert.Equal(44_100, new PcmConverter(44_100, 2).OutputSampleRate);
        Assert.Equal([32767, -32768], new PcmConverter(48_000, 2).Convert([2f, -2f]));
    }

    [Fact]
    public void OtherRatesAreResampledTo48k()
    {
        var converter = new PcmConverter(96_000, 2);
        Assert.Equal(48_000, converter.OutputSampleRate);
        var total = 0;
        for (var i = 0; i < 10; i++) total += converter.Convert(Synthetic.Tone(96_000, 2, 960, i * 960)).Length / 2;
        Assert.InRange(total, 4799, 4801);
    }

    [Fact]
    public void AacClockFollowsTheSampleCountAndReanchorsOnGaps()
    {
        var clock = new AacPacketClock(48_000);
        clock.Observe(10);
        Assert.Equal(10, clock.Next(), 9);
        Assert.Equal(10 + 1024 / 48_000.0, clock.Next(), 9);
        clock.Observe(10 + 2048 / 48_000.0 + 0.01);
        Assert.Equal(10 + 2048 / 48_000.0, clock.Next(), 9);
        clock.Observe(20);
        Assert.Equal(20, clock.Next(), 6);
    }

    private sealed class Counter : IProgramSink
    {
        public int Frames;
        public int Audio;

        public void WriteVideo(VideoFrame frame) => Frames++;

        public void WriteAudio(AudioBuffer buffer) => Audio++;
    }

    [Fact]
    public void PacerThinsA60FpsFeedTo30()
    {
        var counter = new Counter();
        var pacer = new FramePacer(counter, 30);
        for (var i = 0; i < 120; i++)
        {
            pacer.WriteVideo(new VideoFrame(2, 2, 8, new byte[16], i / 60.0));
            pacer.WriteAudio(new AudioBuffer(48_000, 2, new float[2], i / 60.0));
        }
        Assert.InRange(counter.Frames, 59, 61);
        Assert.Equal(120, counter.Audio);
        var steady = new Counter();
        var same = new FramePacer(steady, 30);
        for (var i = 0; i < 30; i++) same.WriteVideo(new VideoFrame(2, 2, 8, new byte[16], i / 30.0));
        Assert.Equal(30, steady.Frames);
    }

    [Fact]
    public void QualityLadderMatchesTheMac()
    {
        Assert.Equal(12000, StreamQualityRung.RecommendedKbps(1920, 1080, 60, false));
        Assert.Equal(15000, StreamQualityRung.RecommendedKbps(1920, 1080, 60, true));
        Assert.Equal(6000, StreamQualityRung.RecommendedKbps(1280, 720, 30, false));
        Assert.Equal(35000, StreamQualityRung.RecommendedKbps(3840, 2160, 24, false));
        Assert.Equal("1920x1080@30", StreamQualityRung.Matching(new StreamRecordPreset { Id = "p", Name = "P" })!.Id);
        Assert.Null(StreamQualityRung.Matching(new StreamRecordPreset { Id = "p", Name = "P", Width = 1000 }));
    }

    [Fact]
    public void RecordingCodecsReadPresetStrings()
    {
        Assert.Equal(RecordingCodec.Hevc10, RecordingCodecs.Parse("hevc10"));
        Assert.Equal(RecordingCodec.H264, RecordingCodecs.Parse(null));
        Assert.Equal(RecordingCodec.H264, RecordingCodecs.Parse("weird"));
        Assert.Equal("proRes4444", RecordingCodec.ProRes4444.Raw());
        Assert.Equal(RecordingCodec.H264, new RecordingConfiguration(RecordingCodec.Hevc, 2, 2).EffectiveCodec(hevcAvailable: false));
        Assert.Equal(RecordingCodec.Hevc, new RecordingConfiguration(RecordingCodec.Hevc10, 2, 2).EffectiveCodec(hevcAvailable: true));
        Assert.Equal(6_220_800, new RecordingConfiguration(RecordingCodec.H264, 1920, 1080, 30).EffectiveBitrate);
        Assert.Equal(120, new RecordingConfiguration(RecordingCodec.H264, 2, 2, 60).GopFrames);
    }
}
