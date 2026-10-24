using MxU.Slides.Core.Automation;
using MxU.Slides.Audio.Multitrack;

namespace MxU.Slides.Audio.Tests;

/// <summary>Port of MultitrackChaseTests.swift: following timecode with landings and varispeed.</summary>
public sealed class MultitrackChaseTests
{
    private const float Smooth = 0.06f;

    // Host-clock seconds for song time t (an arbitrary, large base, like the Mac's tick offset).
    private static double Host(double seconds) => 1000 + seconds;

    private sealed class Run
    {
        public List<float> Output = [];
        public List<(double At, double Error)> Errors = [];
    }

    private static Run Chase(MultitrackEngine engine, MultitrackSong song, double seconds, int buffer = 512, double from = 0,
        double jitter = 0, Func<double, double?>? timecode = null)
    {
        var run = new Run();
        var storage = new[] { new float[buffer] };
        var nextAnchor = from;
        var random = new Random(1234);
        var done = 0;
        var total = (int)(seconds * 48_000);
        while (done < total)
        {
            var t = from + done / 48_000.0;
            if (t >= nextAnchor)
            {
                nextAnchor += 0.1;
                if (timecode?.Invoke(t) is { } p)
                {
                    var noise = jitter > 0 ? (random.NextDouble() * 2 - 1) * jitter : 0;
                    engine.Chase(song, song.Id, p + noise, Host(t));
                }
            }
            engine.TickNow();
            var count = Math.Min(buffer, total - done);
            engine.Renderer.Render(count, Host(t), storage);
            run.Output.AddRange(storage[0].AsSpan(0, count));
            done += count;
            var after = from + done / 48_000.0;
            if (engine.IsPlaying && timecode?.Invoke(after) is { } expected)
                run.Errors.Add((after, engine.Renderer.Position(Host(after)) - expected));
        }
        return run;
    }

    private static MultitrackSong Sine(string name, double seconds) =>
        new(name, [MultitrackStems.Make(TestAudio.Wav($"chase-{name}", 48_000, 1, seconds, i => (float)Math.Sin(2 * Math.PI * 440 * i / 48_000) * 0.5f), "s")]);

    private static MultitrackEngine Engine()
    {
        var engine = new MultitrackEngine(48_000, 1);
        engine.SetRouting(new MultitrackRouting([new("s", sends: new() { ["m"] = 1 })], [new("m", outputs: [new([0])])]));
        return engine;
    }

    private static double Rms(List<float> samples, int start, int count)
    {
        double sum = 0;
        for (var index = start; index < start + count; index++) sum += samples[index] * samples[index];
        return Math.Sqrt(sum / count);
    }

    [Fact]
    public void StemsStartWhereTimecodeIsOnTheSample()
    {
        var path = TestAudio.Wav("chase-impulse", 48_000, 1, 4, i => i == 96_000 ? 1 : 0);
        var song = new MultitrackSong("impulse", [MultitrackStems.Make(path, "s")]);
        using var engine = Engine();
        var run = Chase(engine, song, 1, buffer: 333, timecode: t => 1.5 + t);
        var peak = TestAudio.PeakIndex(run.Output);
        Assert.True(Math.Abs(peak - 24_000) <= 1, $"impulse at {peak}");
        Assert.Equal(1, engine.CuesDecoded);
    }

    [Fact]
    public void APreloadedSongStartsFromTimecodeWithoutDecoding()
    {
        var path = TestAudio.Wav("chase-top", 48_000, 1, 2, i => i == 9_600 ? 1 : 0);
        var song = new MultitrackSong("top", [MultitrackStems.Make(path, "s")]);
        using var engine = Engine();
        engine.Preload([song]);
        Assert.True(TestAudio.WaitFor(() => engine.PreloadedSongIds.Count > 0));
        var run = Chase(engine, song, 0.5, timecode: t => 0.1 + t);
        var peak = TestAudio.PeakIndex(run.Output);
        Assert.True(Math.Abs(peak - 4_800) <= 1, $"impulse at {peak}");
        Assert.Equal(0, engine.CuesDecoded);
    }

    [Fact]
    public void VarispeedHoldsStemsOnDriftingTimecode()
    {
        var song = Sine("drift", 190);
        using var engine = Engine();
        var run = Chase(engine, song, 180, timecode: t => 5 + t * 1.0001);
        var settled = run.Errors.Where(e => e.At > 10).Select(e => Math.Abs(e.Error)).ToList();
        Assert.True(settled.Max() < 0.002, $"worst {settled.Max()}");
        Assert.Equal(0, engine.ChaseLandings);
        Assert.True(TestAudio.BiggestStep(run.Output) < Smooth, $"step {TestAudio.BiggestStep(run.Output)}");
        Assert.True(Math.Abs(run.Errors[^1].Error) < 0.001);
        Assert.True(Math.Abs(engine.Renderer.Speed - 1.0001) < 0.00002, $"speed {engine.Renderer.Speed}");
    }

    [Fact]
    public void VarispeedKeepsTheTopOctaveSteady()
    {
        var path = TestAudio.Wav("chase-hf", 48_000, 1, 30, i => (float)Math.Sin(2 * Math.PI * 15_000 * i / 48_000) * 0.5f);
        var song = new MultitrackSong("hf", [MultitrackStems.Make(path, "s")]);
        using var engine = Engine();
        var run = Chase(engine, song, 25, timecode: t => 1 + t * 1.00068);
        var decibels = new List<double>();
        for (var start = 48_000 * 4; start < run.Output.Count - 480; start += 480)
            decibels.Add(20 * Math.Log10(Rms(run.Output, start, 480) / (0.5 / Math.Sqrt(2))));
        Assert.True(decibels.Max() - decibels.Min() < 0.1, $"flutter {decibels.Max() - decibels.Min()} dB");
        Assert.True(Math.Abs(decibels.Max()) < 0.1, $"level {decibels.Max()} dB");
        Assert.True(Math.Abs(engine.Renderer.Speed - 1.00068) < 0.00003, $"speed {engine.Renderer.Speed}");
    }

    [Fact]
    public void StemsLandWithJumpsAndLoops()
    {
        var song = Sine("jumps", 70);
        using var engine = Engine();
        var run = Chase(engine, song, 30, timecode: t => t < 10 ? 2 + t : t < 20 ? 22 + t : 5 + t);
        Assert.Equal(2, engine.ChaseLandings);
        var afterJumps = run.Errors.Where(e => (e.At > 10.15 && e.At < 20) || e.At > 20.15).Select(e => Math.Abs(e.Error)).ToList();
        Assert.True(afterJumps.Max() < 0.001, $"worst {afterJumps.Max()}");
        Assert.True(TestAudio.BiggestStep(run.Output) < 0.1, $"step {TestAudio.BiggestStep(run.Output)}");
    }

    [Fact]
    public void ASmallStepBackIsNeverALanding()
    {
        var song = Sine("stepback", 80);
        using var engine = Engine();
        const double back = 3.0 / 24;
        var run = Chase(engine, song, 70, timecode: t => t < 5 ? 1 + t : 1 + t - back);
        Assert.Equal(0, engine.ChaseLandings);
        Assert.True(TestAudio.BiggestStep(run.Output) < Smooth);
        var early = run.Errors.First(e => e.At > 6).Error;
        var late = run.Errors[^1].Error;
        Assert.True(early > 0.1 && late < early - 0.03, $"{early} → {late}");
    }

    [Fact]
    public void ADropoutFreewheelsWithoutAGap()
    {
        var song = Sine("dropout", 20);
        using var engine = Engine();
        var run = Chase(engine, song, 8, timecode: t => t >= 3 && t <= 3.6 ? null : 1 + t);
        Assert.Equal(0, engine.ChaseLandings);
        Assert.True(TestAudio.BiggestStep(run.Output) < Smooth);
        var quietest = double.MaxValue;
        for (var start = 48_000; start < run.Output.Count - 480; start += 480) quietest = Math.Min(quietest, Rms(run.Output, start, 480));
        Assert.True(quietest > 0.3, $"a gap: rms {quietest}");
    }

    [Fact]
    public void TimecodeJitterIsNeverChased()
    {
        var song = Sine("jitter", 40);
        using var engine = Engine();
        var run = Chase(engine, song, 30, jitter: 0.002, timecode: t => 1 + t);
        Assert.Equal(0, engine.ChaseLandings);
        Assert.True(TestAudio.BiggestStep(run.Output) < Smooth);
        Assert.True(run.Errors.Where(e => e.At > 5).Max(e => Math.Abs(e.Error)) < 0.003);
    }

    [Fact]
    public void AStopFadesAndTimecodeStartsTheStemsAgain()
    {
        var song = Sine("stop", 20);
        using var engine = Engine();
        var run = Chase(engine, song, 1, timecode: t => 1 + t);
        engine.Stop();
        run.Output.AddRange(Chase(engine, song, 0.5, from: 1, timecode: _ => null).Output);
        Assert.True(TestAudio.BiggestStep(run.Output) < Smooth);
        Assert.True(run.Output.TakeLast(1000).All(s => s == 0));
        var again = Chase(engine, song, 1, from: 5, timecode: t => 4 + t);
        Assert.True(TestAudio.BiggestStep(again.Output) < Smooth);
        Assert.True(Math.Abs(again.Errors[^1].Error) < 0.001);
    }
}
