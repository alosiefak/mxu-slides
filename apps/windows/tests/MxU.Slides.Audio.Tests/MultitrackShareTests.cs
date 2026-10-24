using MxU.Slides.Core.Automation;
using MxU.Slides.Audio.Multitrack;

namespace MxU.Slides.Audio.Tests;

/// <summary>Port of MultitrackShareTests.swift: mixes handed to streams through the share.</summary>
public sealed class MultitrackShareTests
{
    private static string File(string name, double seconds, Func<int, float> sample, double rate = 48_000) =>
        TestAudio.Wav($"share-{name}", rate, 1, seconds, sample);

    private static float Ramp(int index) => index % 4801 / 9602f + 0.01f;

    private static (float[] Left, float[] Right) ShareFrames(TracksShare share, int slot, int count)
    {
        var left = new float[count];
        var right = new float[count];
        share.Read(slot, share.WrittenFrames(slot) - count, count, left, right);
        return (left, right);
    }

    private static MultitrackRouting MainOnPair(int? slot, double delayMs = 0) => new(
        [new("keys", sends: new() { ["main"] = 1 })],
        [new("main", outputs: [new([0, 1], delayMs: delayMs)], feedSlot: slot)]);

    [Fact]
    public void AMixShareMatchesTheDeviceFrameForFrameForAMinute()
    {
        using var share = new TracksShare();
        using var engine = new MultitrackEngine(48_000, 2, share);
        engine.SetRouting(MainOnPair(5));
        engine.Cue(new MultitrackSong("s", [MultitrackStems.Make(File("long", 61, Ramp), "keys")]));
        engine.Renderer.Play(1000 + 100.0 / 48_000);
        var rendered = 0;
        var mismatches = 0;
        var firstOk = true;
        TestAudio.Render(engine, 60, buffer: 333, hostTime: now => now == 0 ? 1000 : 0, perBuffer: (outputs, count) =>
        {
            var shared = ShareFrames(share, 5, count);
            if (rendered == 0)
                firstOk = shared.Left.Take(100).All(s => s == 0) && MathF.Abs(shared.Left[100] - 0.01f) < 1e-6;
            rendered += count;
            for (var frame = 0; frame < count; frame++)
                if (shared.Left[frame] != outputs[0][frame] || shared.Right[frame] != outputs[1][frame]) mismatches++;
        });
        Assert.True(firstOk, "the share starts with the same 100 silent frames as the device");
        Assert.Equal(0, mismatches);
        Assert.Equal(rendered, share.WrittenFrames(5));
        Assert.Equal(0, share.WrittenFrames(4));
    }

    [Fact]
    public void AMixKeepsItsSlotThroughMixerEdits()
    {
        using var share = new TracksShare();
        using var engine = new MultitrackEngine(48_000, 2, share);
        engine.SetRouting(MainOnPair(2));
        engine.Cue(new MultitrackSong("s", [MultitrackStems.Make(File("edit", 2, _ => 0.5f), "keys")]));
        engine.Play();
        TestAudio.Render(engine, 0.2);
        var quieter = MainOnPair(2);
        quieter.Channels[0].Gain = 0.5f;
        engine.SetRouting(quieter);
        TestAudio.Render(engine, 0.2);
        Assert.Equal(19_200, share.WrittenFrames(2));
        Assert.True(MathF.Abs(ShareFrames(share, 2, 1).Left[0] - 0.25f) < 1e-6);
    }

    [Fact]
    public void SlotsStayWithTheirMixes()
    {
        var held = MultitrackRouting.AssignSlots(new HashSet<string> { "stream", "main" }, new Dictionary<string, int>());
        Assert.Equal(new Dictionary<string, int> { ["main"] = 0, ["stream"] = 1 }, held);
        held = MultitrackRouting.AssignSlots(new HashSet<string> { "stream", "lobby" }, held);
        Assert.Equal(new Dictionary<string, int> { ["main"] = 0, ["stream"] = 1, ["lobby"] = 2 }, held);
        var full = Enumerable.Range(0, TracksShare.Slots).ToDictionary(slot => $"m{slot}", slot => slot);
        var reused = MultitrackRouting.AssignSlots(new HashSet<string> { "m1", "new" }, full);
        Assert.True(reused["new"] == 0 && !reused.ContainsKey("m0") && reused["m1"] == 1);
        var routing = new MultitrackRouting(mixes: [new("main"), new("stream")]).Feeding(new HashSet<string> { "stream" }, held);
        Assert.Equal([null, 1], routing.Mixes.Select(m => m.FeedSlot));
        var reach = new MultitrackRouting([new("a", sends: new() { ["main"] = 1, ["zero"] = 0 }), new("b", muted: true, sends: new() { ["b"] = 1 })]);
        Assert.Equal(["main"], reach.ReachedMixIds);
    }

    [Fact]
    public void TheFeederConvertsTheDeviceRate()
    {
        using var share = new TracksShare { SampleRate = 44_100 };
        var received = new List<float>();
        var slots = new HashSet<int>();
        var rates = new HashSet<int>();
        using var feeder = new TracksShareFeeder(share, (slot, buffer) =>
        {
            slots.Add(slot);
            rates.Add(buffer.SampleRate);
            received.AddRange(buffer.Data[0].Take(buffer.FrameLength));
        }, startsClock: false);
        var ones = Enumerable.Repeat(0.5f, 441).ToArray();
        for (var index = 0; index < 100; index++)
        {
            share.Write(7, ones, ones, 441);
            feeder.DrainNow();
        }
        Assert.Equal([7], slots);
        Assert.Equal([48_000], rates);
        Assert.True(Math.Abs(received.Count - 48_000) < 200, $"{received.Count} frames");
        Assert.True(received.TakeLast(1000).All(s => MathF.Abs(s - 0.5f) < 1e-3));
        Assert.Equal(0, feeder.DroppedFrames);
    }

    [Fact]
    public void AStalledFeederPicksUpJustBehindTheWriter()
    {
        using var share = new TracksShare { SampleRate = 48_000 };
        var received = 0;
        using var feeder = new TracksShareFeeder(share, (_, buffer) => received += buffer.FrameLength, startsClock: false);
        var chunk = Enumerable.Repeat(0.1f, 480).ToArray();
        share.Write(0, chunk, chunk, 480);
        feeder.DrainNow();
        Assert.Equal(480, received);
        for (var index = 0; index < 50; index++) share.Write(0, chunk, chunk, 480);
        feeder.DrainNow();
        Assert.Equal(480 + 480, received);
        Assert.Equal(24_000 - 480, feeder.DroppedFrames);
    }

    [Fact]
    public void AnOutputsDelayShiftsTheTracksOnIt()
    {
        using var share = new TracksShare();
        using var engine = new MultitrackEngine(48_000, 3, share);
        MultitrackRouting Routing(float gain) => new(
            [new("k", gain: gain, sends: new() { ["fx"] = 1, ["dry"] = 1 })],
            [new("fx", outputs: [new([0], delayMs: 10)], feedSlot: 0), new("dry", outputs: [new([2])])]);
        engine.SetRouting(Routing(1));
        const int at = 24_000;
        engine.Cue(new MultitrackSong("s", [MultitrackStems.Make(File("impulse", 1, i => i == at ? 1 : 0), "k")]));
        engine.Play();
        var shared = new List<float>();
        var output = TestAudio.Render(engine, 0.505, perBuffer: (_, count) => shared.AddRange(ShareFrames(share, 0, count).Left));
        engine.SetRouting(Routing(0.999f));
        var more = TestAudio.Render(engine, 0.095, perBuffer: (_, count) => shared.AddRange(ShareFrames(share, 0, count).Left));
        for (var channel = 0; channel < 3; channel++) output[channel].AddRange(more[channel]);
        Assert.Equal(at + 480, TestAudio.PeakIndex(output[0]));
        Assert.Equal(at, TestAudio.PeakIndex(output[2]));
        Assert.Equal(at, TestAudio.PeakIndex(shared));
        Assert.True(output[1].All(s => s == 0));
        Assert.Equal([480, 0, 0], Routing(1).DelayFrames(3, 48_000));
    }

    [Fact]
    public void APreloadedSongCuesWithoutDecoding()
    {
        using var engine = new MultitrackEngine(48_000, 1);
        var next = new MultitrackSong("next", [MultitrackStems.Make(File("next", 2, i => i == 4_800 ? 1 : 0), "a")]);
        var other = new MultitrackSong("other", [MultitrackStems.Make(File("other", 1, _ => 0), "a")]);
        var third = new MultitrackSong("third", [MultitrackStems.Make(File("third", 1, _ => 0), "a")]);
        engine.SetRouting(new MultitrackRouting([new("a", sends: new() { ["m"] = 1 })], [new("m", outputs: [new([0])])]));
        engine.Preload([other, next]);
        Assert.True(TestAudio.WaitFor(() => engine.PreloadedSongIds.Count >= 2));
        Assert.Equal(["next", "other"], engine.PreloadedSongIds.Order());
        engine.Cue(next);
        Assert.Equal(0, engine.CuesDecoded);
        engine.Play();
        var output = TestAudio.Render(engine, 0.2)[0];
        Assert.Equal(4_800, TestAudio.PeakIndex(output));
        engine.Preload([third]);
        Assert.DoesNotContain("other", engine.PreloadedSongIds);
        engine.Seek(0.5);
        Assert.Equal(1, engine.CuesDecoded);
    }
}
