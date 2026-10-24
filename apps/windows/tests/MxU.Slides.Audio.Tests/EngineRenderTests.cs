using MxU.Slides.Core.Automation;
namespace MxU.Slides.Audio.Tests;

/// <summary>Port of OfflineRenderTests.swift: rendering the bus by hand, sample-checking cuts and crossfades.</summary>
public sealed class OfflineRenderTests
{
    private static string Tone(double seconds, double hertz, double rate, string name) =>
        TestAudio.Wav($"forensics-{name}", rate, 1, seconds, i => (float)Math.Sin(2 * Math.PI * hertz * i / rate) * 0.5f);

    internal static int Discontinuities(PcmBuffer buffer, float threshold = 0.08f)
    {
        var data = buffer.Data[0];
        var count = 0;
        for (var index = 1; index < buffer.FrameLength; index++)
            if (MathF.Abs(data[index] - data[index - 1]) > threshold) count++;
        return count;
    }

    private static double DominantHertz(PcmBuffer buffer, int frames)
    {
        var data = buffer.Data[0];
        var count = Math.Min(frames, buffer.FrameLength);
        var crossings = 0;
        for (var index = 1; index < count; index++)
            if (data[index - 1] < 0 != data[index] < 0) crossings++;
        return crossings / 2.0 * buffer.SampleRate / count;
    }

    [Fact]
    public void CrossfadeAdvanceRendersCleanSamples()
    {
        var first = Tone(4, 440, 44_100, "a441");
        var second = Tone(4, 330, 44_100, "b441");
        using var engine = new AudioEngine();
        engine.EnableManualRenderingForTests();
        engine.Play(first);
        var before = engine.RenderOfflineForTests(24_000);
        Assert.Equal(0, Discontinuities(before));
        engine.Play(second, crossfade: 1.0);
        var during = engine.RenderOfflineForTests(96_000);
        Assert.Equal(0, Discontinuities(during));
    }

    [Fact]
    public void SameFileCrossfadeRendersCleanSamples()
    {
        var only = Tone(4, 440, 44_100, "dup441");
        using var engine = new AudioEngine();
        engine.EnableManualRenderingForTests();
        engine.Play(only);
        engine.RenderOfflineForTests(24_000);
        engine.Play(only, crossfade: 1.0);
        var during = engine.RenderOfflineForTests(96_000);
        Assert.Equal(0, Discontinuities(during));
    }

    [Fact]
    public void CutAdvanceRendersTheNewTrackImmediately()
    {
        var first = Tone(4, 440, 44_100, "cut-a");
        var second = Tone(4, 330, 44_100, "cut-b");
        using var engine = new AudioEngine();
        engine.EnableManualRenderingForTests();
        engine.Play(first);
        engine.RenderOfflineForTests(24_000);
        engine.Play(second);
        var during = engine.RenderOfflineForTests(96_000);
        Assert.True(Discontinuities(during) <= 2, "cut path emits no stale audio");
        var hertz = DominantHertz(during, 24_000);
        Assert.True(Math.Abs(hertz - 330) < 20, $"post-cut audio is the new file ({hertz} Hz)");
    }

    [Fact]
    public void RealLibraryFilesRenderCleanly()
    {
        var folder = Environment.GetEnvironmentVariable("AUDIO_FORENSICS_DIR");
        if (folder is null) Assert.Skip("Set AUDIO_FORENSICS_DIR to a folder of real tracks");
        var files = Directory.EnumerateFiles(folder!)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".mp3" or ".m4a" or ".wav" or ".aac").ToList();
        Assert.NotEmpty(files);
        foreach (var first in files)
            foreach (var second in files)
            {
                using var engine = new AudioEngine();
                engine.EnableManualRenderingForTests();
                engine.Play(first);
                var start = engine.RenderOfflineForTests(96_000);
                engine.Play(second, crossfade: 1.0);
                var handoff = engine.RenderOfflineForTests(96_000);
                Assert.Equal(0, Discontinuities(start, 0.3f));
                Assert.Equal(0, Discontinuities(handoff, 0.3f));
            }
    }

    [Fact]
    public void RestartAfterPauseBeginsAtTheTop()
    {
        var song = Tone(4, 440, 44_100, "restart");
        using var engine = new AudioEngine();
        engine.EnableManualRenderingForTests();
        engine.Play(song);
        engine.RenderOfflineForTests(48_000);
        engine.Pause();
        var held = engine.Position!.Value.Elapsed;
        Assert.True(held > 0.9, "paused a second into the track");
        engine.Play(song);
        Assert.True(engine.Position!.Value.Elapsed < 0.05, "position snapped to the top");
        var after = engine.RenderOfflineForTests(24_000);
        Assert.True(engine.IsPlaying);
        Assert.True(engine.Position!.Value.Elapsed > 0.4, "playing forward from the top");
        Assert.Equal(0, Discontinuities(after));
    }

    [Fact]
    public void AMonoOutputFoldsToUnity()
    {
        var file = Tone(1, 100, 48_000, "mono100");
        using var engine = new AudioEngine();
        engine.EnableManualRenderingForTests(mono: true);
        engine.Play(file);
        var output = engine.RenderOfflineForTests(24_000);
        Assert.Equal(1, output.ChannelCount);
        var peak = output.Data[0].Take(output.FrameLength).Max(MathF.Abs);
        Assert.True(MathF.Abs(peak - 0.5f) < 0.01, $"peak {peak}");
    }
}

/// <summary>Port of OutputDelayRenderTests.swift.</summary>
public sealed class OutputDelayRenderTests
{
    private static string FlipTone(string name) =>
        TestAudio.Wav($"output-delay-{name}", 48_000, 1, 4, i => (float)Math.Sin(2 * Math.PI * (i < 48_000 ? 440 : 330) * i / 48_000) * 0.5f);

    private static float Peak(PcmBuffer buffer, int start, int end) =>
        buffer.Data[0].Skip(start).Take(Math.Min(end, buffer.FrameLength) - start).Max(MathF.Abs);

    private static double Hertz(PcmBuffer buffer, int start, int end)
    {
        var data = buffer.Data[0];
        var crossings = 0;
        for (var index = start + 1; index < Math.Min(end, buffer.FrameLength); index++)
            if (data[index - 1] < 0 != data[index] < 0) crossings++;
        return crossings / 2.0 * 48_000 / (end - start);
    }

    [Fact]
    public void OutputDelaySetBeforePlayHoldsTheProgramBack()
    {
        using var engine = new AudioEngine();
        engine.EnableManualRenderingForTests();
        engine.OutputDelayMilliseconds = 500;
        engine.Play(FlipTone("a"));
        var rendered = engine.RenderOfflineForTests(96_000);
        Assert.True(Peak(rendered, 0, 22_000) < 0.01, "opens with the delay's silence");
        Assert.True(Math.Abs(Hertz(rendered, 55_000, 65_000) - 440) < 15);
        Assert.True(Math.Abs(Hertz(rendered, 80_000, 96_000) - 330) < 15);
    }

    [Fact]
    public void OutputDelayChangedMidPlayShiftsTheProgram()
    {
        using var engine = new AudioEngine();
        engine.EnableManualRenderingForTests();
        engine.Play(FlipTone("b"));
        engine.RenderOfflineForTests(4_800);
        engine.OutputDelayMilliseconds = 500;
        var rendered = engine.RenderOfflineForTests(96_000);
        Assert.True(Math.Abs(Hertz(rendered, 48_000, 62_000) - 440) < 15, "still the first tone: the flip has been held back");
        Assert.True(Math.Abs(Hertz(rendered, 75_000, 96_000) - 330) < 15);
    }
}

/// <summary>Port of ConsoleChainTests.swift: the bus fader, mute and hardware switch against meters and the feed.</summary>
public sealed class ConsoleChainTests
{
    private static string Tone(string name) =>
        TestAudio.Wav($"console-chain-{name}", 44_100, 1, 2, i => (float)Math.Sin(2 * Math.PI * 440 * i / 44_100) * 0.5f);

    private sealed class FeedPeak
    {
        private readonly Lock gate = new();
        private float value;

        public float Peak { get { lock (gate) return value; } }

        public void Note(PcmBuffer buffer)
        {
            var measured = AudioLevel.Measure(buffer).Peak;
            lock (gate) value = MathF.Max(value, measured);
        }
    }

    [Fact]
    public void VirtualBusFeedsAndMetersWithHardwareSilent()
    {
        using var engine = new AudioEngine();
        engine.EnableManualRenderingForTests();
        engine.SetMetering(true);
        var feed = new FeedPeak();
        engine.SetFeedConsumer(feed.Note);
        engine.MasterVolume = 0.8f;
        engine.HardwareOutputEnabled = false;
        engine.Play(Tone("virtual"));
        var rendered = engine.RenderOfflineForTests(24_000);
        Assert.True(AudioLevel.Measure(rendered).Peak < 0.001, "no hardware leg, no device audio");
        Assert.True(feed.Peak > 0.3, "the stream feed still hears the bus at console gain");
        Assert.True(feed.Peak < 0.45, "feed carries gain 0.8 over the 0.5 tone, not raw");
        Assert.True(engine.SourceMeterLevel.Peak > 0.4, "source meter reads raw program");
        Assert.True(engine.MeterLevel.Peak > 0.3, "post meter reads console-scaled program");
    }

    [Fact]
    public void MutedBusStillMetersSource()
    {
        using var engine = new AudioEngine();
        engine.EnableManualRenderingForTests();
        engine.SetMetering(true);
        var feed = new FeedPeak();
        engine.SetFeedConsumer(feed.Note);
        engine.IsMuted = true;
        engine.Play(Tone("muted"));
        var rendered = engine.RenderOfflineForTests(24_000);
        Assert.True(AudioLevel.Measure(rendered).Peak < 0.001, "muted bus is silent at the device");
        Assert.True(feed.Peak < 0.001, "muted bus pushes silence to feeds");
        Assert.True(engine.MeterLevel.Peak < 0.001, "post meter follows the mute");
        Assert.True(engine.SourceMeterLevel.Peak > 0.4, "fader down is not meter dead: source still reads");
    }

    [Fact]
    public void ConsoleGainScalesHardwareAndFeedAlike()
    {
        using var engine = new AudioEngine();
        engine.EnableManualRenderingForTests();
        engine.SetMetering(true);
        var feed = new FeedPeak();
        engine.SetFeedConsumer(feed.Note);
        engine.MasterVolume = 0.5f;
        engine.Play(Tone("physical"));
        var rendered = engine.RenderOfflineForTests(24_000);
        var hardware = AudioLevel.Measure(rendered).Peak;
        Assert.True(MathF.Abs(hardware - 0.25f) < 0.05, $"device hears gain 0.5 over the 0.5 tone ({hardware})");
        Assert.True(MathF.Abs(feed.Peak - 0.25f) < 0.05, $"feed hears the same console gain ({feed.Peak})");
    }
}

/// <summary>Port of the stereo-player half of ClipEnvelopeTests.swift.</summary>
public sealed class StereoClipTests
{
    private static int At(double seconds) => (int)Math.Round(seconds * 48_000, MidpointRounding.AwayFromZero);

    private static string Flat(string name, double seconds) => TestAudio.Wav($"clip-{name}", 48_000, 2, seconds, _ => 0.5f);

    [Fact]
    public void TheStereoPlayerShapesAClipAsItReads()
    {
        var path = Flat("deck", 4);
        using var engine = new AudioEngine();
        engine.EnableManualRenderingForTests();
        var shape = new AudioClipShape(ClipEnvelopes.Make(ClipEnvelopes.Fade(1), edge: 0), Start: 1, End: 3);
        engine.Play(path, trimStart: 1, trimEnd: 3, from: 1.5, shape: shape);
        var output = engine.RenderOfflineForTests(48_000);
        var left = output.Data[0];
        var full = left[At(0.75)];
        Assert.True(full > 0.1);
        Assert.True(MathF.Abs(left[At(0.05)] / full - 0.55f) < 0.01);
        Assert.True(MathF.Abs(left[At(0.25)] / full - 0.75f) < 0.01);
        Assert.True(MathF.Abs(left[At(0.6)] / full - 1) < 1e-4);
    }

    [Fact]
    public void TwoMusicClipsCrossfadeOnTheTwoDecks()
    {
        var path = Flat("decks", 4);
        using var engine = new AudioEngine();
        engine.EnableManualRenderingForTests();
        var fade = ClipEnvelopes.Fade(1);
        engine.Play(path, trimStart: 0, trimEnd: 2, shape: new AudioClipShape(ClipEnvelopes.Make(fadeOut: fade, edge: 0), 0, 2));
        var first = engine.RenderOfflineForTests(48_000);
        var full = first.Data[0][At(0.5)];
        engine.Play(path, trimStart: 1, trimEnd: 3, shape: new AudioClipShape(ClipEnvelopes.Make(fadeIn: fade, edge: 0), 1, 3), overlap: true);
        var across = engine.RenderOfflineForTests(48_000);
        var left = across.Data[0];
        foreach (var t in new[] { 0.1, 0.5, 0.9 })
            Assert.True(MathF.Abs(left[At(t)] / full - 1) < 0.01, $"at {t}: {left[At(t)] / full}");
    }
}
