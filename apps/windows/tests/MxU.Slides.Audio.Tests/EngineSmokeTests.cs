using MxU.Slides.Core.Scene;

namespace MxU.Slides.Audio.Tests;

/// <summary>
/// Port of EngineSmokeTests.swift and TrimCrossfadeTests.swift: the bus playing in real
/// time. On Windows these run with or without a sound card: with none, the engine keeps
/// time on its clock (that is the point), so only the device-routing test skips.
/// </summary>
public sealed class EngineSmokeTests
{
    private sealed class Flag
    {
        private volatile bool raised;

        public bool IsRaised => raised;

        public void Raise() => raised = true;
    }

    [Fact]
    public async Task NaturalEndFiresAndCrossfadeHandsOff()
    {
        var token = TestContext.Current.CancellationToken;
        var shortTone = TestAudio.Tone("smoke-short", 0.4, 440);
        var longTone = TestAudio.Tone("smoke-long", 4, 550);
        using var engine = new AudioEngine { MasterVolume = 0 };
        var ended = new Flag();
        engine.TrackPlayedToEnd += ended.Raise;
        engine.Play(shortTone);
        Assert.True(engine.IsPlaying);
        Assert.True((engine.Position?.Duration ?? 0) > 0.3);
        Assert.True(await TestAudio.WaitForAsync(() => ended.IsRaised, 15, token), "natural-end callback fired");

        engine.Play(longTone);
        await Task.Delay(50, token);
        engine.Play(shortTone, crossfade: 0.2);
        Assert.True(engine.IsPlaying);
        Assert.True(Math.Abs((engine.Position?.Duration ?? 0) - 0.4) < 0.05, "active deck is the incoming track");
        // (The Mac waits 300 ms; 250 still clears the 200 ms ramp with more room before the 0.4 s track ends.)
        await Task.Delay(250, token);
        Assert.True(engine.IsPlaying, "still playing after the ramp resolves");

        // Pause a long track rather than the nearly finished short one, so timing jitter can't end it first.
        engine.Play(longTone);
        engine.Pause();
        Assert.False(engine.IsPlaying);
        var held = engine.Position?.Elapsed ?? -1;
        await Task.Delay(60, token);
        Assert.True(Math.Abs((engine.Position?.Elapsed ?? -2) - held) < 0.01, "pause holds the clock");
        engine.Resume();
        Assert.True(engine.IsPlaying);
        engine.Stop();
        Assert.False(engine.IsPlaying);
        Assert.Null(engine.Position);
    }

    [Fact]
    public async Task SeekJumpsAndPreservesTransportState()
    {
        var token = TestContext.Current.CancellationToken;
        var longTone = TestAudio.Tone("seek-long", 4, 550);
        using var engine = new AudioEngine { MasterVolume = 0 };
        engine.Play(longTone);
        engine.Seek(2.5);
        Assert.True(engine.IsPlaying, "seek keeps a playing track playing");
        Assert.True(Math.Abs((engine.Position?.Elapsed ?? -1) - 2.5) < 0.1, "position lands at the seek target");
        engine.Pause();
        engine.Seek(1.0);
        Assert.False(engine.IsPlaying, "seek keeps a paused track paused");
        Assert.True(Math.Abs((engine.Position?.Elapsed ?? -1) - 1.0) < 0.1);
        await Task.Delay(60, token);
        Assert.True(Math.Abs((engine.Position?.Elapsed ?? -1) - 1.0) < 0.1, "still parked after the seek");
        engine.Seek(99);
        Assert.NotNull(engine.Position);
        Assert.True((engine.Position?.Elapsed ?? 0) > 3.5);
        engine.Stop();
    }

    [Fact]
    public async Task TrimWindowCapsScheduleAndSurvivesSeek()
    {
        var token = TestContext.Current.CancellationToken;
        var tone = TestAudio.Tone("trim", 2, 440);
        using var engine = new AudioEngine { MasterVolume = 0 };
        var ended = new Flag();
        engine.TrackPlayedToEnd += ended.Raise;
        engine.Play(tone, trimStart: 0.5, trimEnd: 0.9);
        Assert.True(Math.Abs((engine.Position?.Duration ?? 0) - 0.9) < 0.05);
        Assert.True((engine.Position?.Elapsed ?? 0) >= 0.45);
        Assert.True(await TestAudio.WaitForAsync(() => ended.IsRaised, 15, token));

        engine.Play(tone, trimStart: 0.5, trimEnd: 1.5);
        engine.Seek(1.0);
        engine.Seek(0.6);
        Assert.True((engine.Position?.Elapsed ?? 0) < 0.9);
        Assert.True((engine.Position?.Elapsed ?? 0) >= 0.5);
        Assert.True(Math.Abs((engine.Position?.Duration ?? 0) - 1.5) < 0.05);
        engine.Seek(0.1);
        Assert.True((engine.Position?.Elapsed ?? 0) >= 0.5);
        engine.Stop();
    }

    [Fact]
    public void ReRoutingToTheSameDeviceLeavesARunningEngineAlone()
    {
        var device = TestAudio.RequireOutputDevice();
        var tone = TestAudio.Tone("reroute", 2, 440);
        using var engine = new AudioEngine { MasterVolume = 0 };
        engine.SetOutputDevice(device);
        engine.Play(tone);
        var rebuilds = engine.OutputRebuilds;
        engine.SetOutputDevice(device);
        engine.SetOutputDevice(null);
        Assert.Equal(rebuilds, engine.OutputRebuilds);
        Assert.True(engine.IsPlaying);
        engine.SetOutputDevice(device, mono: true);
        Assert.Equal(rebuilds + 1, engine.OutputRebuilds);
        Assert.True(engine.IsPlaying);
        engine.Stop();
    }

    [Fact]
    public void AStartDueNowLandsAMomentAheadFurtherIn()
    {
        const double now = 1000;
        var ahead = AudioEngine.ScheduledStart(due: 1000.5, now);
        Assert.Equal(1000.5, ahead.HostTime);
        Assert.Equal(0, ahead.Skip);
        Assert.True(Math.Abs(ahead.Wait - 0.5) < 1e-6);
        var late = AudioEngine.ScheduledStart(due: 999.96, now);
        Assert.True(Math.Abs(late.HostTime - (1000 + AudioEngine.StartLead)) < 1e-6);
        Assert.True(Math.Abs(late.Skip - (0.04 + AudioEngine.StartLead)) < 1e-6);
        Assert.True(Math.Abs(late.Wait - AudioEngine.StartLead) < 1e-6);
    }

    [Fact]
    public void ADueClipLoadsWhereItWillSound()
    {
        var tone = TestAudio.Tone("due", 4, 440);
        using var engine = new AudioEngine { MasterVolume = 0 };
        // Opening a WASAPI device takes longer than CoreAudio; open it first so this measures
        // the scheduling (a clip fired late skips in by however late it is), not the open.
        engine.Play(tone);
        engine.Stop();
        var due = ShowState.HostClock() - 0.1;
        engine.Play(tone, from: 0.5, due: due);
        var loaded = engine.Position?.Elapsed ?? 0;
        Assert.True(loaded >= 0.5 + 0.1 + AudioEngine.StartLead - 0.001);
        Assert.True(loaded < 0.5 + 0.1 + AudioEngine.StartLead + 0.05);
        Assert.True(engine.IsPlaying);
        engine.Stop();
    }

    [Fact]
    public async Task TrimmedTracksCrossfadeInsteadOfCutting()
    {
        var token = TestContext.Current.CancellationToken;
        string Stereo(double seconds, double hertz, string name) =>
            TestAudio.Wav($"trim-xfade-{name}", 44_100, 2, seconds, i => (float)Math.Sin(2 * Math.PI * hertz * i / 44_100) * 0.1f);
        var outgoing = Stereo(10, 440, "out");
        var incoming = Stereo(10, 660, "in");
        using var engine = new AudioEngine { MasterVolume = 0 };
        engine.SetMetering(true);
        engine.Play(outgoing, trimStart: 2, trimEnd: 5);
        const double crossfade = 2.0;
        var fired = false;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !fired)
        {
            if (engine.Position is { } position && position.Duration - position.Elapsed <= crossfade)
            {
                fired = true;
                engine.Play(incoming, crossfade: crossfade, trimStart: 1, trimEnd: 8);
            }
            await Task.Delay(100, token);
        }
        Assert.True(fired, "a trimmed track's poll reaches the crossfade window before its out point");
        engine.TakeMeterLevels();
        var levels = new List<float>();
        for (var index = 0; index < 20; index++)
        {
            await Task.Delay(100, token);
            levels.Add(engine.TakeMeterLevels().Source.Rms);
        }
        var steady = levels.TakeLast(4).Max();
        var dip = levels.Take(12).Min();
        Assert.True(steady > 0.06, "the incoming trimmed track is at full after the ramp");
        Assert.True(dip < steady * 0.75, $"the handoff ramped ({string.Join(", ", levels)})");
        Assert.True(Math.Abs((engine.Position?.Duration ?? 0) - 8) < 0.05, "active deck is the incoming track's window");
        engine.Stop();
    }
}
