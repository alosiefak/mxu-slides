using MxU.Slides.Audio.Control;
using MxU.Slides.Audio.Video;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Audio.Tests;

/// <summary>
/// A video's sound played by our own engine and kept with the picture (new on Windows: the
/// Mac's AVPlayer plays its own sound), the pitch-keeping stretch for other play rates, and
/// the playout delay (VideoAudioTapTests.swift's delay cases, ported to the Windows player).
/// The players here render offline: the test plays the device, asking for 10 ms blocks.
/// </summary>
public class VideoSoundTests
{
    private const int Block = 480;
    private const double Host = 50;

    // A ramp whose every sample says where it is: value × 10 = seconds into the file.
    private static string Ramp(double seconds = 6) => TestAudio.Wav("video-ramp", 48_000, 1, seconds, i => i / 480_000f);

    private static VideoSoundPlayer Opened(string path, VideoSoundOptions? options = null)
    {
        var player = new VideoSoundPlayer(path, options, manual: true);
        player.Open();
        Assert.False(player.HasNoSound, player.Status.Problem);
        player.Configure(AudioRouting.Main);
        return player;
    }

    private static float[] Render(VideoSoundPlayer player, double host) => player.RenderForTests(Block, host)[0];

    // Seconds into the file each sample of a block says it is.
    private static void AssertPlays(float[] block, double from, double tolerance = 0.0005, double speed = 1)
    {
        for (var index = 0; index < block.Length; index += 40)
        {
            var heard = block[index] * 10;
            var expected = from + index * speed / 48_000.0;
            Assert.True(Math.Abs(heard - expected) < tolerance, $"sample {index}: heard {heard:0.0000} s of the file, wanted {expected:0.0000} s");
        }
    }

    [Fact]
    public void TheSoundIsSilentUntilThePictureMovesThenPlaysFromWhereItIs()
    {
        var path = Ramp();
        using var player = Opened(path);
        player.Follow(new VideoClockSample(0, Host, true));
        Assert.All(Render(player, Host), sample => Assert.Equal(0, sample));
        player.Follow(new VideoClockSample(0.04, Host + 0.04, true));
        Render(player, Host + 0.05); // fades in
        AssertPlays(Render(player, Host + 0.06), 0.06);
        AssertPlays(Render(player, Host + 0.07), 0.07);
        Assert.Equal(VideoSoundState.Playing, player.Status.State);
        TestAudio.Delete(path);
    }

    [Fact]
    public void APauseStopsTheSoundAndAResumePicksUpWhereThePictureIs()
    {
        var path = Ramp();
        using var player = Opened(path);
        Start(player);
        player.Follow(new VideoClockSample(0.08, Host + 0.08, Playing: false));
        Render(player, Host + 0.08); // fades out
        Assert.All(Render(player, Host + 0.09), sample => Assert.Equal(0, sample));
        Assert.Equal(VideoSoundState.Paused, player.Status.State);

        player.Follow(new VideoClockSample(0.08, Host + 0.5, true));
        player.Follow(new VideoClockSample(0.12, Host + 0.54, true));
        Render(player, Host + 0.55);
        AssertPlays(Render(player, Host + 0.56), 0.14);
        TestAudio.Delete(path);
    }

    [Fact]
    public void AJumpPastTheDecodedSoundSeeksAndStartsOnTheRightFrame()
    {
        var path = Ramp();
        using var player = Opened(path);
        Start(player);
        player.Discontinuity();
        player.Follow(new VideoClockSample(3.0, Host + 1.0, true));
        player.Follow(new VideoClockSample(3.04, Host + 1.04, true));
        Assert.All(Render(player, Host + 1.05), sample => Assert.Equal(0, sample));
        player.DecodeForTests();
        var host = Host + 1.06;
        var silent = 0;
        while (Render(player, host).All(sample => sample == 0) && silent++ < 50) host += 0.01;
        Assert.True(silent < 50, "the sound never started after the seek");
        host += 0.01;
        AssertPlays(Render(player, host), 3.04 + (host - (Host + 1.04)));
        TestAudio.Delete(path);
    }

    [Fact]
    public void AJumpTheVideoAnnouncesIsDecodedBeforeThePictureMoves()
    {
        // A loop or a seek says where it went, so the sound is ready the moment the picture moves on.
        var path = Ramp();
        using var player = Opened(path);
        Start(player);
        player.Discontinuity(to: 3.0);
        player.DecodeForTests();
        player.Follow(new VideoClockSample(3.0, Host + 1.0, true));
        player.Follow(new VideoClockSample(3.04, Host + 1.04, true));
        Assert.Contains(Render(player, Host + 1.05), sample => sample != 0);
        AssertPlays(Render(player, Host + 1.06), 3.06);
        TestAudio.Delete(path);
    }

    [Fact]
    public void NoSoundPlaysPastTheOutPoint()
    {
        var path = Ramp();
        using var player = Opened(path, new VideoSoundOptions(OutPoint: 0.5));
        player.DecodeForTests();
        player.Follow(new VideoClockSample(0.4, Host, true));
        player.Follow(new VideoClockSample(0.44, Host + 0.04, true));
        Render(player, Host + 0.05);
        AssertPlays(Render(player, Host + 0.06), 0.46);
        Render(player, Host + 0.07);
        Render(player, Host + 0.08);
        Render(player, Host + 0.09);
        // (The block that crosses 0.5 s still rings for a few samples through the varispeed's filter.)
        Render(player, Host + 0.10);
        Assert.All(Render(player, Host + 0.11), sample => Assert.Equal(0, sample));
        TestAudio.Delete(path);
    }

    [Fact]
    public void AtDoubleSpeedTheSoundMovesThroughTheFileTwiceAsFast()
    {
        var path = Ramp();
        using var player = Opened(path, new VideoSoundOptions(Rate: 2));
        player.DecodeForTests();
        player.Follow(new VideoClockSample(0, Host, true, 2));
        player.Follow(new VideoClockSample(0.08, Host + 0.04, true, 2));
        Render(player, Host + 0.05);
        // Stretching cuts the ramp into overlapping pieces, so allow a piece's worth of wobble.
        var block = Render(player, Host + 0.06);
        AssertPlays(block, 0.12, tolerance: 0.03, speed: 2);
        TestAudio.Delete(path);
    }

    [Fact]
    public void MonoFoldsBothSidesIntoOneChannel()
    {
        var path = Ramp();
        using var player = Opened(path);
        Start(player);
        var mono = player.RenderForTests(Block, Host + 0.08, mono: true)[0];
        mono = player.RenderForTests(Block, Host + 0.09, mono: true)[0];
        // Both sides at -3 dB each, then the mono leg's own -3 dB: the ramp comes out as it was.
        AssertPlays(mono, 0.09);
        TestAudio.Delete(path);
    }

    [Fact]
    public void MutedOrAtZeroVolumeNothingIsHeard()
    {
        var path = Ramp();
        using var player = Opened(path);
        Start(player);
        player.IsMuted = true;
        Render(player, Host + 0.08);
        Assert.All(Render(player, Host + 0.09), sample => Assert.Equal(0, sample));
        player.IsMuted = false;
        player.Volume = 0.5f;
        Render(player, Host + 0.10);
        var half = Render(player, Host + 0.11);
        Assert.Equal(0.115 / 10 * 0.5, half[240], 4);
        TestAudio.Delete(path);
    }

    // VideoAudioTapTests.testZeroDelayLeavesPlayoutUntouched…, testDelayHoldsPlayoutBack…, testDelayChangeLandsOnTheNextCallback
    [Fact]
    public void TheOutputsDelayHoldsTheSoundBackAndZeroLeavesItAlone()
    {
        var path = Ramp();
        using var player = Opened(path);
        Start(player);
        AssertPlays(Render(player, Host + 0.08), 0.08);
        player.Configure(new AudioRouting(null, 0, AudioRouting.MainKey, DelayMs: 20));
        // The delay starts from silence: the next 20 ms (two blocks) are silent, then the sound comes out 20 ms late.
        Assert.All(Render(player, Host + 0.09), sample => Assert.Equal(0, sample));
        Assert.All(Render(player, Host + 0.10), sample => Assert.Equal(0, sample));
        AssertPlays(Render(player, Host + 0.11), 0.09);
        TestAudio.Delete(path);
    }

    [Fact]
    public void AFileWithNoReadableSoundSaysSo()
    {
        var path = Path.Combine(TestFiles.RunFolder, $"not-sound-{Guid.NewGuid():N}.mp4");
        Directory.CreateDirectory(TestFiles.RunFolder);
        File.WriteAllText(path, "this is not a video");
        using var player = new VideoSoundPlayer(path, manual: true);
        player.Open();
        Assert.True(player.HasNoSound);
        Assert.Equal(VideoSoundState.NoSound, player.Status.State);
        Assert.NotNull(player.Status.Problem);
        TestAudio.Delete(path);
    }

    [Fact]
    public async Task TheManagerOpensEachVideosSoundOffTheCallersThreadAndLetsItGo()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Ramp(2);
        var made = new List<VideoSoundPlayer>();
        using var sound = new VideoSound((file, options) =>
        {
            var player = new VideoSoundPlayer(file, options, manual: true);
            lock (made) made.Add(player);
            return player;
        });
        sound.SetRouting(new AudioRouting(null, 0, "video", DelayMs: 30), deviceLive: false);
        sound.Attach("live:videos:a", path);
        sound.Attach("live:videos:a", path);
        sound.Attach("live:videos:missing", Path.Combine(TestFiles.RunFolder, "nothing-here.mp4"));
        Assert.True(await TestAudio.WaitForAsync(() => sound.Status("live:videos:a")?.State == VideoSoundState.Paused, 5, token));
        Assert.True(await TestAudio.WaitForAsync(() => sound.HasNoSound("live:videos:missing"), 5, token));
        Assert.Equal(2, made.Count);
        Assert.Equal("video", sound.Routing.Key);
        sound.Detach("live:videos:a");
        Assert.Null(sound.Status("live:videos:a"));
        Assert.Single(sound.Keys);
        TestAudio.Delete(path);
    }

    [Fact]
    public async Task OnARealDeviceTheSoundStaysWithAMovingPicture()
    {
        TestAudio.RequireOutputDevice();
        var token = TestContext.Current.CancellationToken;
        var path = Ramp(10);
        using var player = new VideoSoundPlayer(path);
        player.Open();
        player.Volume = 0;
        player.Configure(AudioRouting.Main);
        var started = ShowState.HostClock();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        // Read the "picture's" clock 25 times a second, as the app does, until the sound has settled.
        while (DateTime.UtcNow < deadline)
        {
            var now = ShowState.HostClock();
            player.Follow(new VideoClockSample(now - started, now, true));
            if (now - started > 1.5 && player.Status is { State: VideoSoundState.Playing, SyncError: { } error } && Math.Abs(error) < 0.05) break;
            await Task.Delay(40, token);
        }
        var status = player.Status;
        Assert.Equal(VideoSoundState.Playing, status.State);
        Assert.True(status.SyncError is { } off && Math.Abs(off) < 0.05, $"sound and picture {status.SyncError * 1000:0} ms apart");
        Assert.Equal(AudioOutputState.Playing, status.Output.State);
        TestAudio.Delete(path);
    }

    // Lands the sound at 0.04 s (host 50.04) and renders its first two blocks.
    private static void Start(VideoSoundPlayer player)
    {
        player.Follow(new VideoClockSample(0, Host, true));
        player.Follow(new VideoClockSample(0.04, Host + 0.04, true));
        Render(player, Host + 0.05);
        AssertPlays(Render(player, Host + 0.06), 0.06);
        Render(player, Host + 0.07);
    }
}

/// <summary>Stretching sound to another speed without changing its pitch (WSOLA).</summary>
public class TimeStretchTests
{
    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    [InlineData(1.25)]
    public void TheLengthChangesButThePitchAndLevelDont(double rate)
    {
        const int rate48 = 48_000;
        const double hertz = 440;
        var input = Enumerable.Range(0, rate48 * 2).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * hertz * i / rate48))).ToArray();
        var stretch = new TimeStretch(rate);
        var output = new List<float>();
        var left = new float[4096];
        var right = new float[4096];
        for (var at = 0; at < input.Length; at += 1000)
        {
            var piece = input.AsSpan(at, Math.Min(1000, input.Length - at));
            stretch.Push(piece, piece);
            int got;
            while ((got = stretch.Pull(left, right, left.Length)) > 0) output.AddRange(left.AsSpan(0, got));
        }
        stretch.Finish();
        int tail;
        while ((tail = stretch.Pull(left, right, left.Length)) > 0) output.AddRange(left.AsSpan(0, tail));

        Assert.InRange(output.Count, input.Length / rate * 0.97, input.Length / rate * 1.03);
        // Away from the ends: the same note (zero crossings a second) at the same level.
        var middle = output.Skip(4096).Take(output.Count - 8192).ToArray();
        var crossings = 0;
        for (var i = 1; i < middle.Length; i++) if (middle[i - 1] < 0 && middle[i] >= 0) crossings++;
        var heardHertz = crossings / (middle.Length / (double)rate48);
        Assert.InRange(heardHertz, hertz * 0.98, hertz * 1.02);
        var rms = Math.Sqrt(middle.Average(s => (double)s * s));
        Assert.InRange(rms, 0.5 / Math.Sqrt(2) * 0.9, 0.5 / Math.Sqrt(2) * 1.1);
    }
}
