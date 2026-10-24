using MxU.Slides.Core.Automation;
using System.Text.Json;
using MxU.Slides.Audio.Multitrack;

namespace MxU.Slides.Audio.Tests;

/// <summary>Port of MultitrackTests.swift.</summary>
public sealed class MultitrackTests
{
    private static string Impulse(string name, double rate, int channels, double at)
    {
        var target = (int)Math.Round(at * rate, MidpointRounding.AwayFromZero);
        return TestAudio.Wav($"multitrack-{name}", rate, channels, 3, i => i == target ? 1 : 0);
    }

    [Fact]
    public void RoutingBecomesOneGainTable()
    {
        var routing = new MultitrackRouting(
        [
            new("click", gain: 0.5f, sends: new() { ["clicks"] = 1 }),
            new("keys", pan: -1, sends: new() { ["main"] = 1, ["stream"] = 0.5f }),
            new("bass", muted: true, sends: new() { ["main"] = 1 }),
        ],
        [
            new("clicks", outputs: [new([4])]),
            new("main", gain: 0.8f, outputs: [new([0, 1], level: 0.5f)], feedSlot: 3),
            new("stream", feedSlot: 0),
        ]);
        var matrix = new MultitrackMatrix(routing, ["click", "keys", "bass"], 6);
        Assert.Equal(["main", "stream"], matrix.FeedMixIds);
        Assert.Equal([3, 0], matrix.FeedSlots);
        Assert.Equal(10, matrix.Destinations);
        Assert.Equal(0.25f, matrix.Gain(0, 0, 4));
        Assert.Equal(0.25f, matrix.Gain(0, 1, 4));
        Assert.Equal(0, matrix.Gain(0, 0, 0));
        Assert.True(MathF.Abs(matrix.Gain(1, 0, 0) - 0.4f) < 1e-6);
        Assert.Equal(0, matrix.Gain(1, 1, 1));
        Assert.True(MathF.Abs(matrix.Gain(1, 0, 6) - 0.8f) < 1e-6);
        Assert.Equal(0.5f, matrix.Gain(1, 0, 8));
        Assert.True(Enumerable.Range(0, 10).All(d => matrix.Gain(2, 0, d) == 0));
    }

    [Fact]
    public void StemsAtMixedRatesAndOffsetsStartSampleLocked()
    {
        var song = new MultitrackSong("song",
        [
            MultitrackStems.Make(Impulse("a", 44_100, 1, 1.5), "a"),
            MultitrackStems.Make(Impulse("b", 48_000, 2, 1.5), "b"),
            MultitrackStems.Make(Impulse("c", 48_000, 1, 1.0), "c", at: 0.5),
            MultitrackStems.Make(Impulse("d", 44_100, 2, 1.75), "d", inPoint: 0.25),
        ]);
        using var engine = new MultitrackEngine(48_000, 4);
        engine.SetRouting(TestAudio.Solo("a", "b", "c", "d"));
        engine.Cue(song);
        engine.Play();
        var output = TestAudio.Render(engine, 2, buffer: 333);
        var peaks = output.Select(TestAudio.PeakIndex).ToList();
        foreach (var peak in peaks) Assert.True(Math.Abs(peak - 72_000) <= 1, $"peaks {string.Join(", ", peaks)}");
        Assert.True(peaks.Max() - peaks.Min() <= 1);
    }

    [Fact]
    public void AStarvedStemComesBackInStep()
    {
        var song = new MultitrackSong("song",
        [
            MultitrackStems.Make(Impulse("e", 48_000, 2, 1.5), "e"),
            MultitrackStems.Make(Impulse("f", 44_100, 1, 1.5), "f"),
        ]);
        using var engine = new MultitrackEngine(48_000, 2);
        engine.SetRouting(TestAudio.Solo("e", "f"));
        engine.Cue(song);
        engine.Play();
        var output = TestAudio.Render(engine, 2, tickAt: now => !(now >= 0 && now <= 1.2));
        Assert.True(engine.StarvedFrames > 0);
        Assert.True(output.Select(TestAudio.PeakIndex).All(p => Math.Abs(p - 72_000) <= 1));
    }

    [Fact]
    public void PlayStartsOnTheAskedHostTimeInsideABuffer()
    {
        var path = TestAudio.Wav("ones", 48_000, 2, 1, _ => 0.5f);
        using var engine = new MultitrackEngine(48_000, 2);
        engine.SetRouting(TestAudio.Solo("x"));
        engine.Cue(new MultitrackSong("s", [MultitrackStems.Make(path, "x")]));
        const double bufferStart = 1000;
        engine.Renderer.Play(bufferStart + 100.0 / 48_000);
        var outputs = new[] { new float[512], new float[512] };
        engine.Renderer.Render(512, bufferStart, outputs);
        var first = Array.FindIndex(outputs[0], s => s != 0);
        Assert.True(Math.Abs(first - 100) <= 1, $"first sound at {first}");
    }

    [Fact]
    public void JumpsAndFaderMovesNeverClick()
    {
        var path = TestAudio.Wav("sine", 48_000, 1, 4, i => (float)Math.Sin(2 * Math.PI * 100 * i / 48_000) * 0.5f);
        using var engine = new MultitrackEngine(48_000, 1);
        var routing = TestAudio.Solo("s");
        engine.SetRouting(routing);
        engine.Cue(new MultitrackSong("s", [MultitrackStems.Make(path, "s")]));
        engine.Play();
        var output = TestAudio.Render(engine, 0.5)[0];
        engine.Seek(2.0025);
        output.AddRange(TestAudio.Render(engine, 0.2)[0]);
        routing.Channels[0].Muted = true;
        engine.SetRouting(routing);
        output.AddRange(TestAudio.Render(engine, 0.1)[0]);
        Assert.True(TestAudio.BiggestStep(output) < 0.05, $"step {TestAudio.BiggestStep(output)}");
        Assert.True(output.TakeLast(100).All(s => s == 0));
    }

    [Fact]
    public void StopFadesAndHoldsThenPlayResumes()
    {
        var path = TestAudio.Wav("dc", 48_000, 1, 3, _ => 0.5f);
        using var engine = new MultitrackEngine(48_000, 1);
        engine.SetRouting(TestAudio.Solo("d"));
        engine.Cue(new MultitrackSong("s", [MultitrackStems.Make(path, "d")]));
        engine.Play();
        TestAudio.Render(engine, 0.5);
        engine.Stop();
        var stopped = TestAudio.Render(engine, 0.1)[0];
        Assert.True(TestAudio.BiggestStep(stopped) < 0.01);
        Assert.True(stopped.TakeLast(100).All(s => s == 0));
        var held = engine.Position(0);
        Assert.True(Math.Abs(held - 0.51) < 0.005, $"held at {held}");
        engine.Play();
        var resumed = TestAudio.Render(engine, 0.1)[0];
        Assert.True(TestAudio.BiggestStep(resumed) < 0.01);
        Assert.True(MathF.Abs(resumed[^1] - 0.5f) < 1e-5);
        Assert.True(engine.TakeMeters().TryGetValue("d", out var meter)
            && MathF.Abs(meter.Left.Peak - 0.5f) < 1e-4 && MathF.Abs(meter.Right.Peak - 0.5f) < 1e-4);
    }

    [Fact]
    public void AMixerEditWhileStoppedThenPlayDoesNotTouchTheOldTable()
    {
        var path = TestAudio.Wav("dc2", 48_000, 1, 3, _ => 0.5f);
        using var engine = new MultitrackEngine(48_000, 1);
        engine.SetRouting(TestAudio.Solo("d"));
        engine.Cue(new MultitrackSong("s", [MultitrackStems.Make(path, "d")]));
        engine.Play();
        TestAudio.Render(engine, 0.2);
        engine.Stop();
        TestAudio.Render(engine, 0.1);
        var quieter = TestAudio.Solo("d");
        quieter.Channels[0].Gain = 0.5f;
        engine.SetRouting(quieter);
        TestAudio.Render(engine, 0.02);
        engine.SetRouting(TestAudio.Solo("d"));
        TestAudio.Render(engine, 0.02);
        Thread.Sleep(1100);
        engine.TickNow();
        engine.Play();
        var resumed = TestAudio.Render(engine, 0.1)[0];
        Assert.True(MathF.Abs(resumed[^1] - 0.5f) < 1e-5);
    }

    [Fact]
    public void StemMetersReadLeftAndRight()
    {
        var path = TestAudio.Wav("sides", 48_000, 2, 1, _ => 0.5f, right: _ => 0.25f);
        using var engine = new MultitrackEngine(48_000, 2);
        engine.SetRouting(new MultitrackRouting([new("k", pan: -1, sends: new() { ["main"] = 1 })]));
        engine.Cue(new MultitrackSong("s", [MultitrackStems.Make(path, "k")]));
        engine.Play();
        TestAudio.Render(engine, 0.2);
        var meter = engine.TakeMeters()["k"];
        Assert.True(MathF.Abs(meter.Left.Peak - 0.5f) < 1e-4 && MathF.Abs(meter.Left.Rms - 0.5f) < 1e-3);
        Assert.True(MathF.Abs(meter.Right.Peak - 0.25f) < 1e-4 && MathF.Abs(meter.Right.Rms - 0.25f) < 1e-3);
        var again = engine.TakeMeters()["k"];
        Assert.True(again.Left.Peak == 0 && again.Right.Peak == 0, "a take clears");
    }

    [Fact]
    public void PlaysOnARealDeviceByItsClock()
    {
        if (Environment.GetEnvironmentVariable("MXU_MULTITRACK_LIVE") != "1") Assert.Skip("Set MXU_MULTITRACK_LIVE=1 to play on the default device");
        var device = TestAudio.RequireOutputDevice();
        var path = TestAudio.Wav("live", 44_100, 2, 3, i => (float)Math.Sin(2 * Math.PI * 440 * i / 44_100) * 0.1f);
        using var engine = new MultitrackEngine();
        engine.Attach(device);
        // (The Mac plays this on a virtual cable; here it's turned down to near silence.)
        var quiet = TestAudio.Solo("l");
        quiet.Trim = 0.001f;
        engine.SetRouting(quiet);
        engine.Cue(new MultitrackSong("s", [MultitrackStems.Make(path, "l")]));
        engine.Play();
        Thread.Sleep(1000);
        var position = engine.Position();
        engine.Stop();
        Thread.Sleep(100);
        engine.Detach();
        Assert.True(Math.Abs(position - 1) < 0.1, $"position {position}");
        Assert.Equal(0, engine.StarvedFrames);
        Assert.Equal(device.SampleRate, engine.SampleRate);
    }

    [Fact]
    public void TheTracksPlayOnTheDeviceTheirMixesLandOn()
    {
        List<MultitrackRouting.Channel> channels =
        [
            new("keys", sends: new() { ["keys-mix"] = 1, ["main"] = 0.5f }),
            new("bass", sends: new() { ["bass-mix"] = 1 }),
            new("click", muted: true, sends: new() { ["main"] = 1 }),
        ];
        List<MultitrackRouting.ConsoleMix> mixes =
        [
            new("main", [("speakers", new MultitrackRouting.Output([0, 1]))]),
            new("keys-mix", [("dante", new MultitrackRouting.Output([4]))]),
            new("bass-mix", [("dante", new MultitrackRouting.Output([5]))]),
            new("unused", [("other", new MultitrackRouting.Output([0, 1])), ("other", new MultitrackRouting.Output([2, 3]))]),
        ];
        var resolved = MultitrackRouting.Resolve(channels, mixes, 1, false, "speakers");
        Assert.Equal("dante", resolved.DeviceUid);
        Assert.Equal([new MultitrackRouting.Output([4])], resolved.Routing.Mixes.First(m => m.Id == "keys-mix").Outputs);
        Assert.Empty(resolved.Routing.Mixes.First(m => m.Id == "main").Outputs);
        var idle = MultitrackRouting.Resolve([], mixes, 1, false, "speakers");
        Assert.Equal("speakers", idle.DeviceUid);
    }

    [Fact]
    public void ANewDeviceRateKeepsTheSongsPlace()
    {
        if (Environment.GetEnvironmentVariable("MXU_MULTITRACK_LIVE") != "1") Assert.Skip("Set MXU_MULTITRACK_LIVE=1 to attach to the default device");
        var speakers = TestAudio.RequireOutputDevice();
        var path = TestAudio.Wav("rate", 48_000, 2, 3, _ => 0);
        using var engine = new MultitrackEngine(48_000, 2);
        engine.Cue(new MultitrackSong("s", [MultitrackStems.Make(path, "x")]), 1.5);
        engine.Attach(speakers);
        Assert.Equal(speakers.SampleRate, engine.SampleRate);
        Assert.True(Math.Abs(engine.Position() - 1.5) < 0.01);
        engine.Attach(speakers);
        Assert.True(Math.Abs(engine.Position() - 1.5) < 0.01);
        engine.Detach();
    }

    [Fact]
    public void AMonoChannelFoldsItsStemBeforeThePan()
    {
        var routing = new MultitrackRouting(
            [new("keys", pan: -1, sends: new() { ["main"] = 1 }, mono: true), new("pad", sends: new() { ["main"] = 1 })],
            [new("main", outputs: [new([0, 1])])]);
        var matrix = new MultitrackMatrix(routing, ["keys", "pad"], 2);
        Assert.Equal(0.5f, matrix.Gain(0, 0, 0));
        Assert.Equal(0.5f, matrix.Gain(0, 1, 0));
        Assert.Equal(0, matrix.Gain(0, 0, 1));
        Assert.Equal(0, matrix.Gain(0, 1, 1));
        Assert.Equal(1, matrix.Gain(1, 0, 0));
        Assert.Equal(0, matrix.Gain(1, 1, 0));
        Assert.Equal(1, matrix.Gain(1, 1, 1));
        var old = JsonSerializer.Deserialize<MultitrackRouting.Channel>("""{"id":"x","gain":1,"pan":0,"muted":false,"sends":{}}""")!;
        Assert.False(old.Mono);
    }

    [Fact]
    public void NoDeviceStillRunsTheTracksOnTheClock()
    {
        // Windows-specific: attach with no device keeps time (and says why) instead of failing.
        var path = TestAudio.Wav("clock", 48_000, 2, 3, _ => 0.1f);
        using var engine = new MultitrackEngine();
        engine.SetRouting(TestAudio.Solo("x"));
        engine.Cue(new MultitrackSong("s", [MultitrackStems.Make(path, "x")]));
        engine.Attach(new AudioOutputDevice("{not-a-device}", "Gone", 2));
        engine.Play();
        Assert.True(TestAudio.WaitFor(() => engine.Position() > 0.3, 5), "the transport moves without a device");
        Assert.Equal(AudioOutputState.DeviceMissing, engine.OutputStatus.State);
        engine.Detach();
    }
}

/// <summary>Port of the multitrack half of ClipEnvelopeTests.swift.</summary>
public sealed class ClipEnvelopeTests
{
    private static readonly MultitrackRouting Routing = new(
        [new("x", sends: new() { ["mix"] = 1 })], [new("mix", outputs: [new([0])])]);

    private static string File(string name, double seconds, Func<int, float> sample) => TestAudio.Wav($"clip-{name}", 48_000, 2, seconds, sample);

    private static List<float> Render(IReadOnlyList<MultitrackStem> stems, double seconds)
    {
        using var engine = new MultitrackEngine(48_000, 1);
        engine.SetRouting(Routing);
        engine.Cue(new MultitrackSong("song", stems));
        engine.Play();
        return TestAudio.Render(engine, seconds, buffer: 333)[0];
    }

    private static int At(double seconds) => (int)Math.Round(seconds * 48_000, MidpointRounding.AwayFromZero);

    public static TheoryData<TimelineClips.Shape> Shapes => [.. TimelineClips.Shapes];

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EachFadeShapeComesOutOnItsCurve(TimelineClips.Shape shape)
    {
        var path = File($"flat-{shape}", 3, _ => 0.5f);
        var fadeIn = ClipEnvelopes.Fade(1, shape);
        var fadeOut = ClipEnvelopes.Fade(1, shape, midpoint: 0.25);
        var stem = MultitrackStems.Make(path, "x", duration: 3, envelope: ClipEnvelopes.Make(fadeIn, fadeOut, edge: 0));
        var output = Render([stem], 3);
        foreach (var x in new[] { 0.1, 0.25, 0.5, 0.75, 0.9 })
        {
            var rising = 0.5 * fadeIn.Level(x);
            Assert.True(Math.Abs(output[At(x)] - rising) < 0.002, $"{shape} in at {x}: {output[At(x)]} vs {rising}");
            var falling = 0.5 * fadeOut.Level(x, @out: true);
            Assert.True(Math.Abs(output[At(3 - x)] - falling) < 0.002, $"{shape} out at {x}: {output[At(3 - x)]} vs {falling}");
        }
        Assert.True(MathF.Abs(output[At(1.5)] - 0.5f) < 1e-6);
        Assert.True(MathF.Abs(output[At(0)]) < 1e-3);
    }

    [Fact]
    public void TheShapesAreWhatTheirNamesSay()
    {
        Assert.Equal(0.5, TimelineClips.Shape.Linear.Rising(0.5));
        Assert.True(Math.Abs(TimelineClips.Shape.SCurve.Rising(0.5) - 0.5) < 1e-9);
        Assert.True(TimelineClips.Shape.SCurve.Rising(0.1) < 0.1);
        for (var x = 0.0; x <= 1.0001; x += 0.1)
        {
            var fade = ClipEnvelopes.Fade(1, TimelineClips.Shape.EqualPower);
            var incoming = fade.Level(x);
            var outgoing = fade.Level(1 - x, @out: true);
            Assert.True(Math.Abs(incoming * incoming + outgoing * outgoing - 1) < 1e-9);
        }
        Assert.True(ClipEnvelopes.Fade(1, TimelineClips.Shape.Fast).Level(0.25) > 0.25);
        Assert.True(ClipEnvelopes.Fade(1, TimelineClips.Shape.Fast).Level(0.75, @out: true) < 0.75);
        Assert.True(ClipEnvelopes.Fade(1, TimelineClips.Shape.Slow).Level(0.25) < 0.25);
        Assert.True(Math.Abs(ClipEnvelopes.Fade(1, midpoint: 0.25).Level(0.25) - 0.5) < 1e-9);
        Assert.True(Math.Abs(ClipEnvelopes.Fade(1, midpoint: 0.25).Level(0.75, @out: true) - 0.5) < 1e-9);
    }

    [Fact]
    public void TwoClipsOnOneChannelCrossfade()
    {
        var a = File("a", 3, _ => 0.5f);
        var b = File("b", 3, i => i == At(0.25) ? 1 : 0.5f);
        var fade = ClipEnvelopes.Fade(0.5, TimelineClips.Shape.EqualPower);
        var output = Render(
        [
            MultitrackStems.Make(a, "x", duration: 2, envelope: ClipEnvelopes.Make(fadeOut: fade)),
            MultitrackStems.Make(b, "x", at: 1.5, duration: 1.5, envelope: ClipEnvelopes.Make(fadeIn: fade)),
        ], 3);
        Assert.True(MathF.Abs(output[At(1)] - 0.5f) < 1e-6);
        Assert.True(Math.Abs(output[At(1.75) - 1] - 0.5 * 2 * Math.Sin(Math.PI / 4)) < 0.003);
        Assert.True(MathF.Abs(output[At(2.25)] - 0.5f) < 1e-6);
        var beat = output[At(1.75)] - output[At(1.75) - 1];
        Assert.True(Math.Abs(beat - 0.5 * Math.Sin(Math.PI / 4)) < 0.01, $"beat {beat}");
        Assert.True(TestAudio.BiggestStep(output, At(1.4), At(1.74)) < 0.002);
    }

    [Fact]
    public void EdgeFadesMoveNoBeat()
    {
        var path = File("beat", 3, i => i == At(1.25) ? 1 : 0.5f);
        var stem = MultitrackStems.Make(path, "x", at: 0.5, inPoint: 0.25, duration: 1.5, envelope: ClipEnvelopes.Make());
        var output = Render([stem], 2.5);
        var peak = Enumerable.Range(0, output.Count).MaxBy(i => output[i]);
        Assert.Equal(At(1.5), peak);
        Assert.Equal(1, output[peak]);
        Assert.Equal(0, output[At(0.5)]);
        Assert.True(MathF.Abs(output[At(0.5025)] - 0.25f) < 0.005);
        Assert.True(MathF.Abs(output[At(0.505)] - 0.5f) < 1e-6);
        Assert.True(MathF.Abs(output[At(1.9975)] - 0.25f) < 0.005);
        Assert.Equal(0, output[At(2.0)]);
        Assert.True(TestAudio.BiggestStep(output, 0, At(1.4)) <= 0.5f / 240 + 1e-4);
        var bare = Render([MultitrackStems.Make(path, "x", at: 0.5, inPoint: 0.25, duration: 1.5)], 1);
        Assert.Equal(0.5f, bare[At(0.5)]);
    }

    [Fact]
    public void ShapeNamesMatchTheSchemas() =>
        Assert.Equal(["linear", "sCurve", "equalPower", "fast", "slow"], TimelineClips.Shapes.Select(s => s.Raw()));
}
