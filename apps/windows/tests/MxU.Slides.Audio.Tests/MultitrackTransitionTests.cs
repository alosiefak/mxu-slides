using MxU.Slides.Core.Automation;
using MxU.Slides.Audio.Multitrack;

namespace MxU.Slides.Audio.Tests;

/// <summary>Port of MultitrackTransitionTests.swift: handovers, crossfades and fades between songs.</summary>
public sealed class MultitrackTransitionTests
{
    private static string File(string name, double seconds, Func<int, float> sample) => TestAudio.Wav($"transition-{name}", 48_000, 1, seconds, sample);

    private static MultitrackSong Level(string id, float value = 0.5f, double seconds = 3) =>
        new(id, [MultitrackStems.Make(File(id, seconds, _ => value), id)]);

    private static MultitrackSong Tone(string id, double hz, double seconds = 3) =>
        new(id, [MultitrackStems.Make(File(id, seconds, i => (float)Math.Sin(2 * Math.PI * hz * i / 48_000) * 0.5f), id)]);

    private static readonly MultitrackRouting Apart = new(
        [new("a", sends: new() { ["mix-a"] = 1 }), new("b", sends: new() { ["mix-b"] = 1 })],
        [new("mix-a", outputs: [new([0])]), new("mix-b", outputs: [new([1])])]);

    private static MultitrackEngine Playing(MultitrackSong a, MultitrackTransition? transition)
    {
        var engine = new MultitrackEngine(48_000, 2);
        engine.SetRouting(Apart);
        engine.Cue(a);
        engine.Transition(transition);
        engine.Play();
        return engine;
    }

    private static bool Near(float value, float expected, float tolerance = 1e-5f) => MathF.Abs(value - expected) <= tolerance;

    private static bool Silent(IEnumerable<float> samples) => samples.All(s => MathF.Abs(s) <= 1e-6);

    private static int FirstSound(List<float> samples) => samples.FindIndex(s => MathF.Abs(s) > 1e-6);

    private static List<float>[] Render(MultitrackEngine engine, double seconds, IReadOnlyList<(double, Action)>? at = null, Action<float[][], int>? perBuffer = null, int buffer = 333) =>
        TestAudio.Render(engine, seconds, buffer, at: at, perBuffer: perBuffer);

    [Fact]
    public void OverlapStartsTheNextSongOnItsFrameAtFullLevel()
    {
        var a = Level("a");
        var b = Level("b");
        using var engine = Playing(a, new MultitrackTransition("a", b, 1.5, null, 0, 0, 2));
        var output = Render(engine, 2.5);
        Assert.Equal(72_000, FirstSound(output[1]));
        Assert.True(Near(output[1][72_000], 0.5f));
        Assert.True(Near(output[0][95_000], 0.5f));
        Assert.True(Near(output[0][96_000 - 240], 0.25f, 0.001f), "half way down the cut");
        Assert.True(Silent(output[0].Skip(96_000).Take(24_000)));
        Assert.Equal("b", engine.SongId);
        Assert.True(Math.Abs(engine.Position() - 1.0) < 0.02, $"b at {engine.Position()}");
        Assert.Null(engine.ArmedTransition);
    }

    [Fact]
    public void CrossfadeKeepsTheSummedPowerAndNeverClicks()
    {
        using var engine = Playing(Level("a"), new MultitrackTransition("a", Level("b"), 1.5, 1.5, 0.5, 0.5, 2));
        var output = Render(engine, 2.5);
        Assert.True(MathF.Abs(output[0][84_000] - 0.5f * MathF.Cos(MathF.PI / 4)) < 0.003);
        Assert.True(MathF.Abs(output[1][84_000] - 0.5f * MathF.Sin(MathF.PI / 4)) < 0.003);
        for (var frame = 72_000; frame < 96_000; frame += 100)
        {
            var power = output[0][frame] * output[0][frame] + output[1][frame] * output[1][frame];
            Assert.True(MathF.Abs(power - 0.25f) < 0.0025, $"power {power} at {frame}");
        }
        Assert.True(Silent(output[0].Skip(96_000)));
        Assert.True(Near(output[1][100_000], 0.5f));
        const float fadeStep = 0.5f / 480;
        Assert.True(TestAudio.BiggestStep(output[0], 70_000, 100_000) <= fadeStep);
        Assert.True(TestAudio.BiggestStep(output[1], 72_001, 100_000) <= fadeStep);
    }

    [Fact]
    public void FadeEndsTheSongThenTheNextStartsAtItsEnd()
    {
        using var engine = Playing(Level("a"), new MultitrackTransition("a", Level("b"), 2, 1.5, 0.5, 0, 2));
        var output = Render(engine, 2.5);
        Assert.True(Near(output[0][71_000], 0.5f));
        Assert.True(MathF.Abs(output[0][84_000] - 0.25f) < 0.003);
        Assert.True(Silent(output[0].Skip(96_000)));
        Assert.Equal(96_000, FirstSound(output[1]));
        Assert.True(TestAudio.BiggestStep(output[0], 70_000, 97_000) <= 0.5f / 480);
    }

    [Fact]
    public void FadeWithNothingNextStopsTheTransport()
    {
        using var engine = Playing(Level("a"), new MultitrackTransition("a", null, 2, 1.5, 0.5, 0, 2));
        var output = Render(engine, 2.5);
        Assert.True(Silent(output[0].Skip(96_000)));
        Assert.False(engine.IsPlaying);
        Assert.Equal("a", engine.SongId);
    }

    [Fact]
    public void AToneCrossfadesIntoAToneWithoutAClick()
    {
        using var engine = Playing(Tone("a", 440), new MultitrackTransition("a", Tone("b", 660), 1, 1, 1, 1, 2));
        var output = Render(engine, 2.5);
        var own = (float)(2 * Math.PI * 660 / 48_000) * 0.5f;
        Assert.True(TestAudio.BiggestStep(output[0], 40_000, 110_000) <= own * 1.01f);
        Assert.True(TestAudio.BiggestStep(output[1], 48_000, 110_000) <= own * 1.01f);
    }

    [Fact]
    public void AFaderMoveMidOverlapMovesBothSongs()
    {
        var quieter = Apart.Clone();
        foreach (var channel in quieter.Channels) channel.Gain = 0.5f;
        using var engine = new MultitrackEngine(48_000, 2);
        engine.SetRouting(Apart);
        engine.Cue(Level("a"));
        engine.Transition(new MultitrackTransition("a", Level("b"), 1, null, 0, 0, 2));
        engine.Play();
        var output = Render(engine, 2, at: [(1.5, () => engine.SetRouting(quieter))]);
        Assert.True(Near(output[0][60_000], 0.5f) && Near(output[1][60_000], 0.5f));
        Assert.True(Near(output[0][75_000], 0.25f));
        Assert.True(Near(output[1][75_000], 0.25f));
    }

    [Fact]
    public void AMixShareCarriesBothSongsFrameForFrame()
    {
        using var share = new TracksShare();
        using var engine = new MultitrackEngine(48_000, 2, share);
        engine.SetRouting(new MultitrackRouting(
            [new("a", sends: new() { ["main"] = 1 }), new("b", sends: new() { ["main"] = 1 })],
            [new("main", outputs: [new([0, 1])], feedSlot: 4)]));
        engine.Cue(Tone("a", 440));
        engine.Transition(new MultitrackTransition("a", Tone("b", 660), 0.5, 0.5, 0.5, 0.5, 1));
        engine.Play();
        var mismatches = 0;
        Render(engine, 1.5, buffer: 512, perBuffer: (outputs, count) =>
        {
            var left = new float[count];
            var right = new float[count];
            share.Read(4, share.WrittenFrames(4) - count, count, left, right);
            for (var index = 0; index < count; index++)
                if (MathF.Abs(left[index] - outputs[0][index]) > 1e-6) mismatches++;
        });
        Assert.Equal(0, mismatches);
        Assert.Equal(72_000, share.WrittenFrames(4));
    }

    [Fact]
    public void StopMidTransitionStopsBothAndDisarms()
    {
        using var engine = Playing(Level("a"), new MultitrackTransition("a", Level("b"), 1, 1, 1, 1, 2));
        var output = Render(engine, 2.5, at: [(1.5, () => engine.Stop())]);
        Assert.True(Silent(output[0].Skip(73_100).Take(120_000 - 73_100)));
        Assert.True(Silent(output[1].Skip(73_100).Take(120_000 - 73_100)));
        Assert.False(engine.IsPlaying);
        Assert.Null(engine.ArmedTransition);
    }

    [Fact]
    public void DisarmingBeforeTheHandoverLeavesTheSongAlone()
    {
        using var engine = Playing(Level("a"), new MultitrackTransition("a", Level("b"), 1, 1, 0.5, 0.5, 1.5));
        var output = Render(engine, 2, at: [(0.5, () => engine.Transition(null))]);
        Assert.True(Silent(output[1]));
        Assert.True(Near(output[0][90_000], 0.5f), "Stop: A plays on");
        Assert.Equal("a", engine.SongId);
    }

    [Fact]
    public void AManualJumpCrossfadesIntoTheOtherSong()
    {
        var b = Level("b");
        using var engine = new MultitrackEngine(48_000, 2);
        engine.SetRouting(Apart);
        engine.Cue(Level("a"));
        engine.Play();
        var output = Render(engine, 2, at: [(0.5, () => engine.Crossfade(b, 0, 0.5))]);
        var start = FirstSound(output[1]);
        Assert.True(Math.Abs(start - 24_000) <= 333, $"starts with the next buffer: {start}");
        var middle = start + 12_000;
        Assert.True(MathF.Abs(output[0][middle] - 0.5f * MathF.Cos(MathF.PI / 4)) < 0.003);
        Assert.True(MathF.Abs(output[1][middle] - 0.5f * MathF.Sin(MathF.PI / 4)) < 0.003);
        Assert.True(Silent(output[0].Skip(start + 24_000)));
        Assert.Equal("b", engine.SongId);
    }

    [Fact]
    public void TheWireCarriesATransition()
    {
        var message = new TracksPlayerMessage.Transition(new MultitrackTransition("a", new MultitrackSong("b", []), 1, 1, 2, 2, 3));
        var decoded = TracksPlayerWire.Decode(TracksPlayerWire.Encode(message));
        Assert.Equal(message, decoded);
        Assert.Equal(new TracksPlayerMessage.Transition(null), TracksPlayerWire.Decode(TracksPlayerWire.Encode(new TracksPlayerMessage.Transition(null))));
    }

    [Fact]
    public void TheMusicLayerFadesOnTheStemsCurves()
    {
        Assert.Equal(0.75f, AudioFadeCurve.Linear.Falling(0.25));
        Assert.True(MathF.Abs(AudioFadeCurve.EqualPower.Falling(0.5) - MathF.Cos(MathF.PI / 4)) < 1e-6);
        Assert.True(MathF.Abs(AudioFadeCurve.Cosine.Falling(0.5) - 0.5f) < 1e-6);
        Assert.True(AudioFadeCurve.Cosine.Falling(1) < 1e-6 && AudioFadeCurve.EqualPower.Falling(0) == 1);
    }
}
