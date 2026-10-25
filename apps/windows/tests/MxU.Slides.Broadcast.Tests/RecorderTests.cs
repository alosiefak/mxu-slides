using System.Diagnostics;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Media.Interop;
using MxU.Slides.Broadcast.Recording;
using MxU.Slides.Core.Media;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>
/// Port of RecorderTests.swift, read back with Windows' own media reader, plus the Windows-only
/// promises: the finished file appears only when finished, frames of another size are scaled,
/// and a recording killed mid-way plays up to its last fragment.
/// </summary>
public sealed class RecorderTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "RecorderTests-" + Guid.NewGuid().ToString("N"));

    public RecorderTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Timing checks use the software encoder, which behaves the same on every PC; a graphics-card
    /// encoder shared by tests running side by side can stall and (correctly) drop frames.
    /// <see cref="HardwareRecordingWhereThisPcHasOne"/> covers the hardware path.
    /// </summary>
    private static RecordingConfiguration Software(RecordingConfiguration configuration) =>
        configuration with { Encoder = EncoderPreference.Software };

    private static VideoFrame Gray(int width = 320, int height = 180, double hostTime = 0, byte gray = 0x80)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, gray);
        return new VideoFrame(width, height, width * 4, pixels, hostTime);
    }

    private async Task<(Recorder Recorder, string Path)> RecordAsync(RecordingConfiguration configuration, int frames, double interval = 1.0 / 30, string name = "out.mp4")
    {
        var path = Path.Combine(scratch, name);
        var recorder = new Recorder(path, configuration);
        for (var frame = 0; frame < frames; frame++)
        {
            recorder.Append(Gray(configuration.Width, configuration.Height, 1000 + frame * interval));
            if (frame == 0) await FirstFrameWritten(recorder);
            await Task.Delay(TimeSpan.FromSeconds(interval), Ct);
        }
        await recorder.FinishAsync();
        return (recorder, path);
    }

    /// <summary>
    /// Waits until the recorder has written its first picture. Its encoder starts on its own thread
    /// when the first picture comes, while up to 8 more wait in its queue (a quarter of a second at
    /// 30 a second). On a busy PC, such as several test projects at once, starting it can take longer
    /// and the test's next pictures would be dropped. These tests are about steady recording, and the
    /// pictures carry their own times, so waiting here doesn't change the file.
    /// </summary>
    private async Task FirstFrameWritten(Recorder recorder)
    {
        var limit = DateTime.UtcNow.AddSeconds(10);
        while (recorder.Status.AppendedFrames + recorder.Status.DroppedFrames == 0 && DateTime.UtcNow < limit)
            await Task.Delay(10, Ct);
    }

    [Fact]
    public async Task H264RecordingProducesPlayableFile()
    {
        var (recorder, path) = await RecordAsync(Software(new RecordingConfiguration(RecordingCodec.H264, 320, 180, 30)), frames: 30);
        Assert.Equal(RecorderState.Finished(RecordingStopReason.Requested), recorder.Status.State);
        Assert.Equal(30, recorder.Status.AppendedFrames);
        Assert.Equal(0, recorder.Status.DroppedFrames);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(Recorder.PartialPathFor(path)), "the partial file must be renamed, not left behind");

        var probe = MediaProbe.Read(path);
        Assert.Equal(320, probe.Width);
        Assert.Equal(180, probe.Height);
        Assert.Equal(MF.MFVideoFormat_H264, probe.VideoSubtype);
        Assert.True(probe.DurationSeconds > 0.8, $"30 frames at 30fps should span ~1s, got {probe.DurationSeconds}");
        Assert.Equal(30, probe.VideoSamples);
    }

    [Fact]
    public async Task HardwareRecordingWhereThisPcHasOne()
    {
        var path = Path.Combine(scratch, "hardware.mp4");
        var recorder = new Recorder(path, new RecordingConfiguration(RecordingCodec.H264, 640, 360, 30, IncludesAudio: true));
        long sample = 0;
        for (var frame = 0; frame < 45; frame++)
        {
            recorder.Append(Synthetic.VideoFrame(640, 360, frame, frame / 30.0));
            recorder.AppendAudio(Synthetic.AudioBuffer(48_000, 2, 1600, sample, frame / 30.0));
            sample += 1600;
            await Task.Delay(33, Ct);
        }
        await recorder.FinishAsync();
        var status = recorder.Status;
        Assert.Equal(RecorderStateKind.Finished, status.State.Kind);
        Assert.Equal(45, status.AppendedFrames + status.DroppedFrames);
        var probe = MediaProbe.Read(path);
        Assert.Equal((640, 360), (probe.Width, probe.Height));
        Assert.Equal(MF.MFVideoFormat_H264, probe.VideoSubtype);
        Assert.True(probe.VideoSamples > 0 && probe.VideoSamples <= status.AppendedFrames, $"{probe.VideoSamples} pictures in the file, {status}");
        Assert.True(probe.HasAudio);
    }

    [Fact]
    public async Task ProRes4444RecordsH264OnWindows()
    {
        var (recorder, path) = await RecordAsync(Software(new RecordingConfiguration(RecordingCodec.ProRes4444, 320, 180, 30)), frames: 10, name: "alpha.mp4");
        Assert.Equal(RecordingCodec.H264, recorder.RecordedCodec);
        Assert.Equal(MF.MFVideoFormat_H264, MediaProbe.Read(path).VideoSubtype);
        Assert.True(RecordingCodec.ProRes4444.PreservesAlpha());
        Assert.True(RecordingCodec.ProRes4444.UsesPcmAudio());
    }

    [Fact]
    public async Task HevcRecordsHevcWhereThisPcCan()
    {
        var (recorder, path) = await RecordAsync(Software(new RecordingConfiguration(RecordingCodec.Hevc, 320, 180, 30)), frames: 10, name: "hevc.mp4");
        var expected = recorder.RecordedCodec == RecordingCodec.Hevc ? MF.MFVideoFormat_HEVC : MF.MFVideoFormat_H264;
        Assert.Equal(expected, MediaProbe.Read(path).VideoSubtype);
        Assert.True(RecordingCodec.Hevc10.WantsHdrSource());
    }

    [Fact]
    public async Task AverageBitrateGovernsTheFile()
    {
        async Task<double> DataRate(int bitrate)
        {
            var path = Path.Combine(scratch, $"rate-{bitrate}.mp4");
            var recorder = new Recorder(path, Software(new RecordingConfiguration(RecordingCodec.H264, 640, 360, 30, AverageBitrate: bitrate)));
            var random = new Random(bitrate);
            for (var frame = 0; frame < 60; frame++)
            {
                var pixels = NoisyFrame(random, 640, 360);
                recorder.Append(new VideoFrame(640, 360, 640 * 4, pixels, 1000 + frame / 30.0));
                if (frame == 0) await FirstFrameWritten(recorder);
                await Task.Delay(33, Ct);
            }
            await recorder.FinishAsync();
            Assert.Equal(0, recorder.Status.DroppedFrames);
            var probe = MediaProbe.Read(path);
            return new FileInfo(path).Length * 8 / Math.Max(probe.DurationSeconds, 1.9);
        }
        var low = await DataRate(2_000_000);
        var high = await DataRate(6_000_000);
        Assert.True(low < 2_500_000, $"2 Mbps recording ran at {low / 1e6:0.00} Mbps");
        Assert.True(high < 7_500_000, $"6 Mbps recording ran at {high / 1e6:0.00} Mbps");
        Assert.True(high > low * 2, $"6 Mbps ({high / 1e6:0.00}) should be well above 2 Mbps ({low / 1e6:0.00})");
    }

    /// <summary>
    /// Detail the encoder must trade against size: 8×8 blocks of colour, each with fine grain on
    /// top, new every frame. (The Mac's test uses full-range colour noise; Windows' hardware encoders
    /// compress those flat blocks so well that a 6 Mbps setting has nothing left to spend, so they
    /// couldn't show it working.)
    /// </summary>
    private static byte[] NoisyFrame(Random random, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var blockY = 0; blockY < height / 8; blockY++)
            for (var blockX = 0; blockX < width / 8; blockX++)
            {
                var baseValue = 96 + random.Next(64);
                for (var y = blockY * 8; y < blockY * 8 + 8; y++)
                    for (var x = blockX * 8; x < blockX * 8 + 8; x++)
                    {
                        var i = (y * width + x) * 4;
                        var grain = random.Next(-12, 13);
                        pixels[i] = (byte)Math.Clamp(baseValue + grain, 0, 255);
                        pixels[i + 1] = (byte)Math.Clamp(baseValue + 8 + grain, 0, 255);
                        pixels[i + 2] = (byte)Math.Clamp(baseValue - 8 + grain, 0, 255);
                        pixels[i + 3] = 0xFF;
                    }
            }
        return pixels;
    }

    [Fact]
    public async Task SameInstantAppendsNudgeForwardInsteadOfDropping()
    {
        var recorder = new Recorder(Path.Combine(scratch, "nudge.mp4"), Software(new RecordingConfiguration(RecordingCodec.H264, 320, 180)));
        recorder.Append(Gray(hostTime: 5));
        recorder.Append(Gray(hostTime: 5));
        recorder.Append(Gray(hostTime: 5));
        await recorder.FinishAsync();
        Assert.Equal(3, recorder.Status.AppendedFrames);
        Assert.Equal(0, recorder.Status.DroppedFrames);
    }

    [Fact]
    public async Task AudioAppendsLandInAnAudioTrack()
    {
        var path = Path.Combine(scratch, "audio.mp4");
        var recorder = new Recorder(path, Software(new RecordingConfiguration(RecordingCodec.H264, 320, 180, IncludesAudio: true)));
        var pcm = new float[4800 * 2];
        for (var i = 0; i < 4800; i++) pcm[i * 2] = pcm[i * 2 + 1] = MathF.Sin(i * 0.05f);
        for (var frame = 0; frame < 10; frame++)
        {
            var seconds = 100 + frame / 30.0;
            recorder.Append(Gray(hostTime: seconds));
            recorder.AppendAudio(new AudioBuffer(48_000, 2, pcm, seconds));
        }
        await recorder.FinishAsync();
        var probe = MediaProbe.Read(path);
        Assert.True(probe.HasAudio, "audio appends should produce an audio track");
        Assert.Equal(MF.MFAudioFormat_AAC, probe.AudioSubtype);
    }

    [Fact]
    public async Task AudioBeforeFirstVideoFrameDropsHonestly()
    {
        var recorder = new Recorder(Path.Combine(scratch, "early-audio.mp4"), Software(new RecordingConfiguration(RecordingCodec.H264, 320, 180, IncludesAudio: true)));
        recorder.AppendAudio(new AudioBuffer(48_000, 2, new float[960], 1));
        Assert.Equal(1, recorder.Status.DroppedAudioBuffers);
        recorder.Append(Gray(hostTime: 2));
        await recorder.FinishAsync();
    }

    [Fact]
    public async Task ImpossibleDiskFloorAutoStops()
    {
        var recorder = new Recorder(Path.Combine(scratch, "disk.mp4"), Software(new RecordingConfiguration(RecordingCodec.H264, 320, 180, MinimumFreeDiskBytes: long.MaxValue)));
        recorder.Append(Gray(hostTime: 0));
        for (var i = 0; i < 50 && recorder.Status.State.Kind != RecorderStateKind.Finished; i++) await Task.Delay(100, Ct);
        Assert.Equal(RecorderState.Finished(RecordingStopReason.DiskFull), recorder.Status.State);
        Assert.NotEqual(long.MaxValue, recorder.Status.FreeDiskBytes);
        Assert.True(File.Exists(recorder.FilePath));
    }

    [Fact]
    public async Task ThreeSecondsOfFramesAndToneReadBackWithTheRightDurationAndSize()
    {
        var path = Path.Combine(scratch, "three-seconds.mp4");
        var recorder = new Recorder(path, Software(new RecordingConfiguration(RecordingCodec.H264, 640, 360, 30, IncludesAudio: true)));
        var clock = Stopwatch.StartNew();
        long sample = 0;
        for (var frame = 0; frame < 90; frame++)
        {
            var seconds = 50 + frame / 30.0;
            recorder.Append(Synthetic.VideoFrame(640, 360, frame, seconds));
            recorder.AppendAudio(Synthetic.AudioBuffer(48_000, 2, 1600, sample, seconds));
            sample += 1600;
            var due = TimeSpan.FromSeconds((frame + 1) / 30.0) - clock.Elapsed;
            if (due > TimeSpan.Zero) await Task.Delay(due, Ct);
        }
        await recorder.FinishAsync();
        Assert.Equal(90, recorder.Status.AppendedFrames);
        Assert.Equal(0, recorder.Status.DroppedAudioBuffers);
        Assert.True(recorder.IsCrashSafe, "Windows should write fragmented MP4");

        var probe = MediaProbe.Read(path);
        Assert.Equal(640, probe.Width);
        Assert.Equal(360, probe.Height);
        Assert.InRange(probe.DurationSeconds, 2.9, 3.2);
        Assert.Equal(90, probe.VideoSamples);
        Assert.InRange(probe.LastVideoTime, 2.9, 3.0);
        Assert.True(probe.HasAudio);
        Assert.InRange(probe.LastAudioTime, 2.8, 3.1);
    }

    [Fact]
    public async Task ASilentProgramStillMakesAPlayableFileWithASoundTrack()
    {
        var path = Path.Combine(scratch, "silent.mp4");
        var recorder = new Recorder(path, Software(new RecordingConfiguration(RecordingCodec.H264, 320, 180, 30, IncludesAudio: true)));
        long sample = 0;
        for (var frame = 0; frame < 75; frame++)
        {
            var seconds = frame / 30.0;
            recorder.Append(Synthetic.VideoFrame(320, 180, frame, seconds));
            // Sound for the first half second only, then the program goes quiet.
            if (frame < 15)
            {
                recorder.AppendAudio(Synthetic.AudioBuffer(48_000, 2, 1600, sample, seconds));
                sample += 1600;
            }
            // Real time, as the app sends them: twice as fast overflowed the recorder's small queue
            // (it drops rather than fall behind) whenever the PC was busy.
            await Task.Delay(33, Ct);
        }
        await recorder.FinishAsync();
        Assert.Equal(RecorderStateKind.Finished, recorder.Status.State.Kind);
        var probe = MediaProbe.Read(path);
        Assert.True(probe.HasAudio);
        Assert.True(probe.LastVideoTime > 1.5, $"only {probe.LastVideoTime:0.00} s of pictures; {recorder.Status}");
        // Sound covers every picture and stops where the recording does (pictures a busy PC dropped don't shorten it).
        Assert.True(probe.LastAudioTime > probe.LastVideoTime - 0.1 && probe.LastAudioTime < recorder.Status.ElapsedSeconds + 0.1, $"audio ends {probe.LastAudioTime:0.000}, video {probe.LastVideoTime:0.000}; {recorder.Status}; {probe}");
    }

    [Fact]
    public void GapFillerWaitsASecondThenTrailsThePicture()
    {
        var gaps = new AudioGapFiller();
        Assert.True(gaps.Accept(0, 0.5));
        Assert.Null(gaps.Gap(1.2, 48_000, final: false));
        var gap = gaps.Gap(1.6, 48_000, final: false)!.Value;
        Assert.Equal(0.5, gap.Start, 6);
        Assert.Equal(0, gap.Frames % 1024);
        Assert.InRange(gap.Start + gap.Frames / 48_000.0, 1.08, 1.1);
        Assert.False(gaps.Accept(0.6, 0.3), "sound that arrives after the silence covering it is dropped");
        Assert.True(gaps.Accept(1.0, 0.2));
        var last = gaps.Gap(2.0, 48_000, final: true)!.Value;
        Assert.InRange(last.Start + last.Frames / 48_000.0, 1.97, 2.0);
    }

    [Fact]
    public async Task FramesOfAnotherSizeAreScaledToTheRecording()
    {
        var path = Path.Combine(scratch, "scaled.mp4");
        var recorder = new Recorder(path, Software(new RecordingConfiguration(RecordingCodec.H264, 640, 360, 30)));
        for (var frame = 0; frame < 10; frame++)
        {
            recorder.Append(Synthetic.VideoFrame(1920, 1080, frame, frame / 30.0));
            recorder.Append(Synthetic.VideoFrame(640, 480, frame, frame / 30.0 + 0.01));
            await Task.Delay(30, Ct);
        }
        await recorder.FinishAsync();
        Assert.Equal(20, recorder.Status.AppendedFrames);
        var probe = MediaProbe.Read(path);
        Assert.Equal((640, 360), (probe.Width, probe.Height));
    }

    [Fact]
    public async Task AnExistingFileIsNeverOverwritten()
    {
        var path = Path.Combine(scratch, "taken.mp4");
        await File.WriteAllTextAsync(path, "someone else's", Ct);
        var recorder = new Recorder(path, Software(new RecordingConfiguration(RecordingCodec.H264, 320, 180)));
        recorder.Append(Gray(hostTime: 1));
        await recorder.FinishAsync();
        Assert.Equal("someone else's", await File.ReadAllTextAsync(path, Ct));
        Assert.Equal(Path.Combine(scratch, "taken 2.mp4"), recorder.FilePath);
        Assert.True(File.Exists(recorder.FilePath));
    }

    [Fact]
    public void RecoverInterruptedGivesPartialFilesTheirNames()
    {
        File.WriteAllBytes(Path.Combine(scratch, "Sunday" + Recorder.PartialSuffix), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(scratch, "Empty" + Recorder.PartialSuffix), []);
        var recovered = Recorder.RecoverInterrupted(scratch);
        Assert.Equal([Path.Combine(scratch, "Sunday.mp4")], recovered);
        Assert.False(File.Exists(Path.Combine(scratch, "Empty" + Recorder.PartialSuffix)));
    }

    [Fact]
    public async Task ForceKilledRecordingStaysPlayable()
    {
        if (RecorderKillHarness.Target is not null) return;
        var final = Path.Combine(scratch, "killed.mp4");
        var exe = Path.Combine(AppContext.BaseDirectory, "MxU.Slides.Broadcast.Tests.exe");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-method", "MxU.Slides.Broadcast.Tests.RecorderKillHarness.RecordUntilKilled" },
        };
        start.Environment[RecorderKillHarness.Variable] = final;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        try
        {
            var ready = final + ".ready";
            var clock = Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                if (process.HasExited) Assert.Fail($"harness exited early: {await output}");
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "harness never started recording");
                await Task.Delay(50, Ct);
            }
            await Task.Delay(4000, Ct);
        }
        finally
        {
            // Like SIGKILL on the Mac: no chance to finish the file.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(Ct);
        }

        Assert.False(File.Exists(final), "a killed recording never got its final name");
        Assert.True(File.Exists(Recorder.PartialPathFor(final)));
        Assert.Equal([final], Recorder.RecoverInterrupted(scratch));
        var probe = MediaProbe.Read(final);
        Assert.Equal((1280, 720), (probe.Width, probe.Height));
        Assert.True(probe.LastVideoTime > 1.0, $"with 1 s fragments over ~4 s of writing, at least a couple must survive (got {probe.LastVideoTime:0.00} s, {probe.VideoSamples} frames)");
    }
}

/// <summary>
/// The child side of <see cref="RecorderTests.ForceKilledRecordingStaysPlayable"/> (the Mac's
/// recorder-kill-harness): records until the process is killed. Does nothing in a normal test run.
/// </summary>
public sealed class RecorderKillHarness
{
    public const string Variable = "MXU_RECORDER_KILL_HARNESS";

    public static string? Target => Environment.GetEnvironmentVariable(Variable);

    [Fact]
    public async Task RecordUntilKilled()
    {
        if (Target is not { } path) return;
        var recorder = new Recorder(path, new RecordingConfiguration(RecordingCodec.H264, 1280, 720, 30, FragmentInterval: 1) { Encoder = EncoderPreference.Software });
        var pixels = new byte[1280 * 720 * 4];
        Array.Fill(pixels, (byte)0x80);
        var clock = Stopwatch.StartNew();
        for (var frame = 0; ; frame++)
        {
            recorder.Append(new VideoFrame(1280, 720, 1280 * 4, pixels, clock.Elapsed.TotalSeconds));
            if (frame == 5) await File.WriteAllTextAsync(path + ".ready", "HARNESS_RECORDING", TestContext.Current.CancellationToken);
            await Task.Delay(33, TestContext.Current.CancellationToken);
        }
    }
}
