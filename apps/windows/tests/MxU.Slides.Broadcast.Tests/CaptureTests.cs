using MxU.Slides.Broadcast.Control;
using MxU.Slides.Broadcast.Library;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Recording;
using MxU.Slides.Broadcast.Streaming;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>
/// The Phase 4 broadcast extras: recording or streaming one particular screen, 60 fps, the sound
/// a preset asks for, replaying a media file, and filing recordings into the library. Pictures
/// and sound are fakes; the encoders, MP4 files and RTMP server are real.
/// </summary>
public sealed class CaptureTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "Capture-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public CaptureTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static StreamSessionOptions Quiet => new() { Encoder = EncoderPreference.Software, ReconnectDelay = (_, ct) => Task.Delay(1000, ct) };

    /// <summary>Screens by id, remembering every request (target, width, height, rate).</summary>
    private sealed class FakeScreens
    {
        public Dictionary<string, FakeProgram> Screens { get; } = [];
        public List<(string Target, int Width, int Height, int Rate)> Requests { get; } = [];

        public IProgramSource? Get(string target, int width, int height, int rate)
        {
            Requests.Add((target, width, height, rate));
            return Screens.GetValueOrDefault(target);
        }
    }

    /// <summary>A sound opener that remembers what was asked for and lets the test play sound into each sink.</summary>
    private sealed class FakeAudio
    {
        public List<(SessionAudioRequest Request, IProgramSink Sink)> Opened { get; } = [];
        public int Closed;

        public IDisposable Open(SessionAudioRequest request, IProgramSink sink)
        {
            Opened.Add((request, sink));
            return new Handle(this);
        }

        private sealed class Handle(FakeAudio owner) : IDisposable
        {
            private int done;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref done, 1) == 0) Interlocked.Increment(ref owner.Closed);
            }
        }
    }

    private static StreamPresetDestination Destination(string name, string url = "rtmp://127.0.0.1:1/live", string? canvas = null, int? maxHeight = null) => new()
    {
        Id = Guid.NewGuid().ToString(), Name = name, Transport = StreamTransport.Rtmp, Url = url, StreamKey = "k",
        CanvasScreenId = canvas, MaxHeight = maxHeight,
    };

    [Fact]
    public void TheProgramIsUsedUpTo30FpsAndScreensForEverythingElse()
    {
        var program = new FakeProgram();
        var stage = new FakeProgram();
        var screens = new FakeScreens { Screens = { ["STAGE"] = stage, ["program"] = new FakeProgram() } };
        var sources = new CaptureSources(program) { Screens = screens.Get };
        Assert.Same(program, sources.PicturesFor("program", 1920, 1080, 30));
        Assert.Empty(screens.Requests);
        Assert.Same(stage, sources.PicturesFor("STAGE", 1280, 720, 30));
        Assert.Same(screens.Screens["program"], sources.PicturesFor("program", 1920, 1080, 60));
        Assert.Equal([("STAGE", 1280, 720, 30), ("program", 1920, 1080, 60)], screens.Requests);

        // A screen that's gone (or a PC that can't picture one) records the program instead.
        Assert.Same(program, sources.PicturesFor("GONE", 1920, 1080, 30));
        var broken = new CaptureSources(program) { Screens = (_, _, _, _) => throw new InvalidOperationException("no window") };
        Assert.Same(program, broken.PicturesFor("STAGE", 1920, 1080, 30));
        Assert.Same(program, new CaptureSources(program).PicturesFor("STAGE", 1920, 1080, 60));
    }

    [Fact]
    public void SoundChoicesAreReadFromThePreset()
    {
        var preset = new StreamRecordPreset { Id = "p", Name = "P" };
        Assert.Equal(SessionAudioRequest.Program, SessionAudioRequest.Of(preset));
        Assert.True(SessionAudioRequest.Of(preset).IsPlainProgram);
        preset.AudioMixId = "main";
        preset.AudioIncludesProgram = true;
        preset.AudioDelayMs = 5000;
        var request = SessionAudioRequest.Of(preset);
        Assert.Equal(new SessionAudioRequest(StreamAudioSource.Mix("main"), null, true, 2000), request);
        Assert.Equal("mix:main+program +2000ms", request.ToString());
        Assert.False(request.IsPlainProgram);
        var legacy = new StreamRecordPreset { Id = "p", Name = "P", AudioInputUid = "{0.0.1.00000000}.{desk}" };
        Assert.Equal("{0.0.1.00000000}.{desk}", SessionAudioRequest.Of(legacy).InputUid);
        Assert.False(SessionAudioRequest.Of(legacy).IsPlainProgram);
    }

    [Fact]
    public async Task RecordingTheStageScreenRecordsItsPicturesNotTheProgram()
    {
        var program = new FakeProgram();
        var screens = new FakeScreens { Screens = { ["STAGE"] = new FakeProgram() } };
        var sources = new CaptureSources(program) { Screens = screens.Get };
        using var recording = new RecordingController(sources, scratch) { Encoder = EncoderPreference.Software };
        using var streaming = new StreamingController(sources, recording, Quiet);
        var preset = new StreamRecordPreset { Id = "stage", Name = "Stage", Width = 320, Height = 180, FrameRate = 30, CanvasScreenId = "STAGE" };
        streaming.StartCapture(preset, []);
        var session = Assert.Single(recording.Sessions);
        Assert.Equal("STAGE", session.TargetId);
        Assert.Equal([("STAGE", 320, 180, 30)], screens.Requests);
        Assert.IsType<CaptureSources.VideoOnly>(Assert.Single(screens.Screens["STAGE"].Sinks));
        // With no sound opener, the sound is the program's own.
        Assert.IsType<CaptureSources.AudioOnly>(Assert.Single(program.Sinks));

        for (var frame = 0; frame < 15; frame++)
        {
            screens.Screens["STAGE"].Play(Synthetic.VideoFrame(640, 360, frame, 100 + frame / 30.0));
            program.Play(Synthetic.VideoFrame(1920, 1080, frame, 100 + frame / 30.0));
            await Task.Delay(20, Ct);
        }
        await recording.StopAsync(session);
        Assert.Empty(screens.Screens["STAGE"].Sinks);
        Assert.Empty(program.Sinks);
        var probe = MediaProbe.Read(Assert.Single(Directory.GetFiles(scratch, "*.mp4")));
        Assert.Equal((320, 180), (probe.Width, probe.Height));
        Assert.InRange(probe.VideoSamples, 13, 15);
    }

    [Fact]
    public void A60FpsPresetTakesTheProgramFromTheScreenFeeds()
    {
        var program = new FakeProgram();
        var fast = new FakeProgram();
        var screens = new FakeScreens { Screens = { ["program"] = fast } };
        var sources = new CaptureSources(program) { Screens = screens.Get };
        using var recording = new RecordingController(sources, scratch) { Encoder = EncoderPreference.Software };
        var session = recording.Start("program", "Fast", RecordingCodec.H264, 640, 360, 60);
        Assert.Equal([("program", 640, 360, 60)], screens.Requests);
        var pacer = Assert.IsType<FramePacer>(Assert.IsType<CaptureSources.VideoOnly>(Assert.Single(fast.Sinks)).Inner);
        Assert.Same(session.Recorder, pacer.Inner);
        recording.Dismiss(session);
        _ = session.Recorder.FinishAsync();
    }

    [Fact]
    public void DestinationsOnTheSameScreenShareItsBiggestPicture()
    {
        var program = new FakeProgram();
        var screens = new FakeScreens { Screens = { ["LOWER"] = new FakeProgram(), ["STAGE"] = new FakeProgram() } };
        var sources = new CaptureSources(program) { Screens = screens.Get };
        using var recording = new RecordingController(sources, scratch);
        using var streaming = new StreamingController(sources, recording, Quiet);
        var preset = new StreamRecordPreset { Id = "s", Name = "Lower thirds", PresetKind = StreamPresetKind.Stream, CanvasScreenId = "LOWER" };
        streaming.StartCapture(preset, [Destination("Full"), Destination("Small", maxHeight: 720), Destination("Confidence", canvas: "STAGE")]);
        var live = Assert.IsType<LiveStream>(streaming.Live);
        Assert.Equal(3, live.Sessions.Count);
        Assert.Equal([("LOWER", 1920, 1080, 30), ("LOWER", 1920, 1080, 30), ("STAGE", 1920, 1080, 30)], screens.Requests);
        Assert.Equal(2, screens.Screens["LOWER"].Sinks.Count);
        Assert.Single(screens.Screens["STAGE"].Sinks);
        Assert.Equal(720, live.Sessions[1].Video.Height);
        streaming.EndStream();
        Assert.Empty(screens.Screens["LOWER"].Sinks);
        Assert.Empty(screens.Screens["STAGE"].Sinks);
        Assert.Empty(program.Sinks);
    }

    [Fact]
    public void AStreamOpensItsSoundOnceForEveryDestinationAndClosesItWhenItEnds()
    {
        var program = new FakeProgram();
        var audio = new FakeAudio();
        var sources = new CaptureSources(program) { Audio = audio.Open };
        using var recording = new RecordingController(sources, scratch);
        using var streaming = new StreamingController(sources, recording, Quiet);
        var preset = new StreamRecordPreset
        {
            Id = "s", Name = "Sunday", PresetKind = StreamPresetKind.Stream, AudioInputId = "desk", AudioIncludesProgram = true, AudioDelayMs = 120,
        };
        streaming.StartCapture(preset, [Destination("A"), Destination("B")]);
        var (request, _) = Assert.Single(audio.Opened);
        Assert.Equal(new SessionAudioRequest(StreamAudioSource.Input("desk"), null, true, 120), request);
        // The program gives only pictures now: its sound would be a second copy.
        Assert.All(program.Sinks, sink => Assert.IsType<CaptureSources.VideoOnly>(sink));
        streaming.EndStream();
        Assert.Equal(1, audio.Closed);
    }

    [Fact]
    public async Task ARecordingWithADeskInputHasItsSound()
    {
        var program = new FakeProgram();
        var audio = new FakeAudio();
        var sources = new CaptureSources(program) { Audio = audio.Open };
        using var recording = new RecordingController(sources, scratch) { Encoder = EncoderPreference.Software };
        var session = recording.Start("program", "Desk", RecordingCodec.H264, 320, 180, 30,
            audio: new SessionAudioRequest(StreamAudioSource.Input("desk")));
        var sink = Assert.Single(audio.Opened).Sink;
        Assert.Same(session.Recorder, sink);
        long sample = 0;
        for (var frame = 0; frame < 30; frame++)
        {
            var at = 200 + frame / 30.0;
            program.Play(Synthetic.VideoFrame(320, 180, frame, at));
            sink.WriteAudio(Synthetic.AudioBuffer(48_000, 2, 1600, sample, at));
            sample += 1600;
            await Task.Delay(15, Ct);
        }
        await recording.StopAsync(session);
        Assert.Equal(1, audio.Closed);
        var probe = MediaProbe.Read(session.FilePath);
        Assert.True(probe.HasAudio, "the recording has a sound track");
        Assert.True(probe.AudioSamples > 10);
    }

    [Fact]
    public async Task ReplayingAFileStreamsItThenEndsTheStream()
    {
        var movie = await TestMovies.WriteAsync(Path.Combine(scratch, "service.mp4"), frames: 60, withAudio: true, width: 320, height: 180);
        await using var server = new FakeRtmpServer();
        var program = new FakeProgram();
        var audio = new FakeAudio();
        var sources = new CaptureSources(program) { Audio = audio.Open };
        using var recording = new RecordingController(sources, scratch);
        using var streaming = new StreamingController(sources, recording, Quiet)
        {
            SourceResolver = _ => new ResolvedSource.MediaFile(movie, "Service", null, null, 2),
        };
        var preset = new StreamRecordPreset
        {
            Id = "replay", Name = "Replay", PresetKind = StreamPresetKind.Stream, Width = 320, Height = 180, VideoBitrateKbps = 800,
            RecordWhileStreaming = true,
        };
        streaming.StartCapture(preset, [Destination("Fake", server.Url("live"))]);
        var live = Assert.IsType<LiveStream>(streaming.Live);
        Assert.NotNull(live.FileSource);
        Assert.Equal(2, live.MediaDurationSeconds);
        Assert.Empty(program.Sinks);
        Assert.Empty(audio.Opened);
        Assert.Empty(recording.Sessions);
        for (var i = 0; i < 100 && live.Sessions[0].Status.State.Kind != StreamStateKind.Publishing; i++) await Task.Delay(20, Ct);
        // On air, the toolbar counts the replay down instead of up.
        Assert.StartsWith("REPLAY 0:0", BroadcastStatus.Caption(streaming, recording, DateTimeOffset.Now));

        for (var i = 0; i < 200 && streaming.Live is not null; i++) await Task.Delay(50, Ct);
        Assert.Null(streaming.Live);
        Assert.Null(streaming.StartNotice);
        var connection = Assert.Single(server.Connections);
        Assert.NotEmpty(connection.Video);
        Assert.NotEmpty(connection.Audio);
        Assert.True(live.FileSource!.FramesDelivered > 30);
    }

    [Fact]
    public async Task AReplayThatCantBeReadEndsWithANotice()
    {
        var junk = Path.Combine(scratch, "junk.mp4");
        await File.WriteAllTextAsync(junk, "not a movie", Ct);
        var program = new FakeProgram();
        using var recording = new RecordingController(program, scratch);
        using var streaming = new StreamingController(program, recording, Quiet)
        {
            SourceResolver = _ => new ResolvedSource.MediaFile(junk, "Junk", null, null, null),
        };
        streaming.StartCapture(new StreamRecordPreset { Id = "r", Name = "Replay", PresetKind = StreamPresetKind.Stream }, [Destination("A")]);
        Assert.NotNull(streaming.Live);
        for (var i = 0; i < 100 && streaming.Live is not null; i++) await Task.Delay(50, Ct);
        Assert.Null(streaming.Live);
        Assert.StartsWith("Replay: unreadable file", streaming.StartNotice);
    }

    [Fact]
    public void ARecordOnlyPresetWontRecordAFile()
    {
        var program = new FakeProgram();
        using var recording = new RecordingController(program, scratch);
        using var streaming = new StreamingController(program, recording, Quiet);
        streaming.StartCapture(new StreamRecordPreset { Id = "r", Name = "Odd", PresetKind = StreamPresetKind.RecordOnly, SourceKind = StreamSourceKind.MediaItem }, []);
        Assert.Empty(recording.Sessions);
        Assert.NotNull(streaming.StartNotice);
    }

    [Fact]
    public async Task FinishedRecordingsAreFiledIntoTheLibrary()
    {
        var store = new LibraryStore(Path.Combine(scratch, "Library"));
        store.Load();
        var program = new FakeProgram();
        using var controller = new BroadcastController(store, new StreamKeyVault(Path.Combine(scratch, "Keys")), program, Path.Combine(scratch, "Videos"), Quiet);
        controller.Recording.Encoder = EncoderPreference.Software;
        var filed = new TaskCompletionSource<MediaItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.RecordingFiled += item => filed.TrySetResult(item);
        controller.FileRecordingsIntoLibrary();
        controller.Prepare();
        var export = Path.Combine(scratch, "Copies");
        store.Save(new StreamRecordPreset
        {
            Id = "sermon", Name = "Sermon", PresetKind = StreamPresetKind.RecordOnly, Width = 320, Height = 180,
            RecordLibraryFolder = "Sermons", RecordExportFolder = export,
        });
        controller.StartCapture("sermon");
        var session = Assert.Single(controller.Recording.Sessions);
        Assert.StartsWith(Path.Combine(store.Root, "spool"), session.FilePath);
        for (var frame = 0; frame < 20; frame++)
        {
            program.Play(Synthetic.VideoFrame(320, 180, frame, 300 + frame / 30.0));
            await Task.Delay(15, Ct);
        }
        controller.EndAll("sermon");

        var item = await filed.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal("Sermons", item.Folder);
        Assert.Equal("sermon", item.RecordedByPresetId);
        Assert.Equal(MediaFileStatus.Ready, item.FileStatus);
        Assert.StartsWith("Sermon ", item.Name);
        Assert.NotNull(store.BlobPath(item));
        Assert.Same(item, store.Get<MediaItem>(item.Id));
        Assert.Empty(Directory.GetFiles(Path.Combine(store.Root, "spool")));
        Assert.True(File.Exists(Path.Combine(export, item.FileName)));
        Assert.False(Directory.Exists(Path.Combine(scratch, "Videos")), "nothing goes to the Videos folder");
    }

    [Fact]
    public async Task RecordingsLeftInTheSpoolAreFiledAtLaunch()
    {
        var store = new LibraryStore(Path.Combine(scratch, "Library"));
        store.Load();
        var spool = RecordingLibrary.SpoolDirectory(store.Root);
        await TestMovies.WriteAsync(Path.Combine(spool, "Before the crash.mp4"), frames: 6);
        using var controller = new BroadcastController(store, new StreamKeyVault(Path.Combine(scratch, "Keys")), new FakeProgram(), null, Quiet);
        var filed = new TaskCompletionSource<MediaItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.RecordingFiled += item => filed.TrySetResult(item);
        controller.FileRecordingsIntoLibrary();
        controller.Prepare();
        var item = await filed.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal("Before the crash", item.Name);
        Assert.Equal(RecordingLibrary.DefaultFolder, item.Folder);
        Assert.True(controller.WaitForFiling(TimeSpan.FromSeconds(5)));
        Assert.Empty(Directory.GetFiles(spool));
    }

    [Fact]
    public void TheControllerStreamsWhatThePresetsSourceSays()
    {
        var store = new LibraryStore(Path.Combine(scratch, "Library"));
        store.Load();
        using var controller = new BroadcastController(store, new StreamKeyVault(Path.Combine(scratch, "Keys")), new FakeProgram(), null, Quiet);
        var preset = new StreamRecordPreset
        {
            Id = "latest", Name = "Replay", PresetKind = StreamPresetKind.Stream, SourceKind = StreamSourceKind.LatestRecording,
        };
        controller.Streaming.StartCapture(preset, [Destination("A")]);
        Assert.Null(controller.Streaming.Live);
        Assert.Equal("Replay: no finished recording in “Recordings”", controller.Streaming.StartNotice);
        Assert.Equal(new StreamSources.Preview("Won't go live: no destinations selected", true), controller.CapturePreview(preset));
    }
}
