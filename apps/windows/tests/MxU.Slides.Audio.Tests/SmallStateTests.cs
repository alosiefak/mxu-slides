namespace MxU.Slides.Audio.Tests;

/// <summary>Ports of AudioDelayLineTests, ChannelLandingTests, DirectInputLivenessTests, InputHotplugTests and MonitorTakeoverTests.</summary>
public sealed class AudioDelayLineTests
{
    private static PcmBuffer MakeBuffer(float[] samples, int rate = 48_000)
    {
        var buffer = new PcmBuffer(rate, 2, samples.Length) { FrameLength = samples.Length };
        for (var index = 0; index < samples.Length; index++)
        {
            buffer.Data[0][index] = samples[index];
            buffer.Data[1][index] = -samples[index];
        }
        return buffer;
    }

    private static float[] Left(PcmBuffer buffer) => buffer.Data[0].Take(buffer.FrameLength).ToArray();

    [Fact]
    public void ZeroDelayLeavesTheBufferUntouched()
    {
        var line = new AudioDelayLine();
        var samples = new float[64];
        samples[3] = 0.5f;
        var buffer = MakeBuffer(samples);
        line.Process(buffer);
        Assert.Equal(samples, Left(buffer));
    }

    [Fact]
    public void DelayShiftsSamplesAcrossBuffers()
    {
        var line = new AudioDelayLine { DelayMilliseconds = 1 };
        var impulse = new float[48];
        impulse[0] = 1;
        var first = MakeBuffer(impulse);
        line.Process(first);
        Assert.Equal(new float[48], Left(first));
        var second = MakeBuffer(new float[48]);
        line.Process(second);
        Assert.Equal(1, Left(second)[0]);
        Assert.Equal(-1, second.Data[1][0]);
    }

    [Fact]
    public void PartialBufferDelayShiftsWithinTheBuffer()
    {
        var line = new AudioDelayLine { DelayMilliseconds = 0.25 };
        var impulse = new float[48];
        impulse[0] = 1;
        var buffer = MakeBuffer(impulse);
        line.Process(buffer);
        var output = Left(buffer);
        Assert.Equal(1, output[12]);
        Assert.Equal(0, output[0]);
    }

    [Fact]
    public void DelayChangeRebuildsWithSilenceNotArtifacts()
    {
        var line = new AudioDelayLine { DelayMilliseconds = 1 };
        line.Process(MakeBuffer(Enumerable.Repeat(0.7f, 48).ToArray()));
        line.DelayMilliseconds = 2;
        var buffer = MakeBuffer(Enumerable.Repeat(0.7f, 96).ToArray());
        line.Process(buffer);
        Assert.Equal(new float[96], Left(buffer));
    }

    [Fact]
    public void DelayedCopyLeavesTheSharedBufferUntouched()
    {
        var line = new AudioDelayLine { DelayMilliseconds = 1 };
        var impulse = new float[48];
        impulse[0] = 1;
        var shared = MakeBuffer(impulse);
        var delayed = line.DelayedCopy(shared);
        Assert.NotSame(shared, delayed);
        Assert.Equal(impulse, Left(shared));
        Assert.Equal(new float[48], Left(delayed));
        var next = line.DelayedCopy(MakeBuffer(new float[48]));
        Assert.Equal(1, Left(next)[0]);
    }

    [Fact]
    public void DelayedCopyWithoutDelayIsFree()
    {
        var line = new AudioDelayLine();
        var buffer = MakeBuffer([0.1f, 0.2f, 0.3f]);
        Assert.Same(buffer, line.DelayedCopy(buffer));
    }

    [Fact]
    public void RequestedDelayClampsToTheCeiling()
    {
        var line = new AudioDelayLine { DelayMilliseconds = 99_999 };
        Assert.Equal(2000, line.DelayMilliseconds);
        line.DelayMilliseconds = -5;
        Assert.Equal(0, line.DelayMilliseconds);
    }
}

public sealed class ChannelLandingTests
{
    [Fact]
    public void AChannelLandsOnItsRouteAndItsSendsThroughEachMixsGain()
    {
        var gains = new Dictionary<string, float> { ["main"] = 1, ["stream"] = 1, ["lobby"] = 0 };
        var landing = AudioLevel.ChannelLanding([new(0.5f, 0.8f), new(0.5f, 0.8f)], channelGain: 0.5f, routedMix: "main",
            sends: new Dictionary<string, float> { ["stream"] = 0.5f, ["lobby"] = 1, ["main"] = 0.1f },
            mixGain: id => gains.GetValueOrDefault(id, 1));
        Assert.Equal(["main", "stream"], landing.Keys.Order());
        Assert.Equal(0.4f, landing["main"][0].Peak, 1e-6f);
        Assert.Equal(0.2f, landing["stream"][1].Peak, 1e-6f);
        Assert.Equal(0.125f, landing["stream"][0].Rms, 1e-6f);
        Assert.Empty(AudioLevel.ChannelLanding([new(0.5f, 0.8f)], 0, "main", new Dictionary<string, float>(), _ => 1));
        Assert.Empty(AudioLevel.ChannelLanding([LevelReading.Silence, LevelReading.Silence], 1, "main", new Dictionary<string, float>(), _ => 1));
        Assert.Equal(["ears"], AudioLevel.ChannelLanding([new(0.5f, 0.8f)], 1, null, new Dictionary<string, float> { ["ears"] = 1 }, _ => 1).Keys);
    }

    [Fact]
    public void SidesLandThroughThePan()
    {
        var stereo = AudioLevel.ChannelLanding([new(0.4f, 0.6f), new(0.2f, 0.3f)], 1, "main", new Dictionary<string, float>(), _ => 1, pan: 0)["main"];
        Assert.Equal(0.6f, stereo[0].Peak, 1e-6f);
        Assert.Equal(0.3f, stereo[1].Peak, 1e-6f);
        var hardLeft = AudioLevel.ChannelLanding([new(0.4f, 0.6f), new(0.2f, 0.3f)], 1, "main", new Dictionary<string, float>(), _ => 1, pan: -1)["main"];
        Assert.Equal(0.6f, hardLeft[0].Peak, 1e-6f);
        Assert.Equal(0, hardLeft[1].Peak, 1e-6f);
        var mono = AudioLevel.ChannelLanding([new(0.4f, 0.8f)], 0.5f, "main", new Dictionary<string, float>(), _ => 1, pan: 0.5f)["main"];
        Assert.Equal(0.2f, mono[0].Peak, 1e-6f);
        Assert.Equal(0.4f, mono[1].Peak, 1e-6f);
    }

    [Fact]
    public void TheMonoFold()
    {
        Assert.Equal(0.5f, AudioLevel.Mono([new(0.5f, 0.5f), new(0.5f, 0.5f)]).Peak, 1e-6f);
        Assert.Equal(0.4f, AudioLevel.Mono([new(0.5f, 0.8f), LevelReading.Silence]).Peak, 1e-6f);
        Assert.Equal(0.6f, AudioLevel.Mono([new(0.3f, 0.6f)]).Peak, 1e-6f);
        Assert.Equal(0.7f, AudioLevel.Louder([new(0.5f, 0.2f), new(0.1f, 0.7f)]).Peak, 1e-6f);
    }

    [Fact]
    public void DeviceChannelsCarryEachOutputsSides()
    {
        var bridge = AudioLevel.DeviceChannels(
        [
            ([new(0.4f, 0.6f), new(0.2f, 0.3f)], [0, 1]),
            ([new(0.4f, 0.8f), LevelReading.Silence], [5]),
            ([new(0.3f, 0.5f), new(0.3f, 0.5f)], [1]),
        ]);
        Assert.Equal([0, 1, 5], bridge.Keys.Order());
        Assert.Equal(0.6f, bridge[0].Peak, 1e-6f);
        Assert.Equal(0.5f, bridge[1].Peak, 1e-6f);
        Assert.Equal(MathF.Sqrt(0.2f * 0.2f + 0.3f * 0.3f), bridge[1].Rms, 1e-6f);
        Assert.Equal(0.4f, bridge[5].Peak, 1e-6f);
    }

    [Fact]
    public void MeasureSides()
    {
        var stereo = new PcmBuffer(48_000, 2, 64) { FrameLength = 64 };
        Array.Fill(stereo.Data[0], 0.5f);
        Array.Fill(stereo.Data[1], -0.25f);
        var sides = AudioLevel.MeasureSides(stereo);
        Assert.Equal(0.5f, sides.Left.Peak, 1e-6f);
        Assert.Equal(0.25f, sides.Right.Peak, 1e-6f);
        Assert.Equal(0.25f, sides.Right.Rms, 1e-6f);
        var mono = new PcmBuffer(48_000, 1, 64) { FrameLength = 64 };
        Array.Fill(mono.Data[0], 0.3f);
        var doubled = AudioLevel.MeasureSides(mono);
        Assert.Equal(0.3f, doubled.Left.Peak, 1e-6f);
        Assert.Equal(0.3f, doubled.Right.Peak, 1e-6f);
        var window = new LevelWindow();
        window.Fold(sides.Left, sides.Right);
        window.Fold(new LevelReading(0.1f, 0.1f), new LevelReading(0.4f, 0.4f));
        Assert.Equal(0.5f, window.Left.Peak, 1e-6f);
        Assert.Equal(0.4f, window.Right.Peak, 1e-6f);
        Assert.Equal(0.5f, window.Take().Peak, 1e-6f);
        Assert.Equal(0, window.Right.Peak);
    }
}

public sealed class DirectInputLivenessTests
{
    private static DirectInputLiveness OpenInput(double time = 10)
    {
        var liveness = new DirectInputLiveness { Running = true };
        liveness.Opened(time);
        return liveness;
    }

    [Fact]
    public void AQuietOpenReopensAfterThreeSeconds()
    {
        var liveness = OpenInput();
        Assert.False(liveness.ShouldReopen(12.9));
        Assert.True(liveness.ShouldReopen(13.1));
        liveness.Arrived(13);
        Assert.False(liveness.ShouldReopen(15.9), "an arrival restarts the watch");
    }

    [Fact]
    public void OnlyAGoodOpenIsWatched()
    {
        var liveness = OpenInput();
        liveness.Opening();
        Assert.False(liveness.ShouldReopen(100), "an unanswered open is not silence");
        var stopped = OpenInput();
        stopped.Stopped();
        Assert.False(stopped.ShouldReopen(100));
    }

    [Fact]
    public void FailuresRetryAndNoteEachMessageOnce()
    {
        var liveness = OpenInput();
        Assert.Equal(DirectInputLiveness.FailureAction.RetryWith(true), liveness.Failed("device not found", current: true));
        Assert.Equal(DirectInputLiveness.FailureAction.Ignore, liveness.Failed("device not found", current: true));
        Assert.False(liveness.ShouldReopen(100), "a queued retry is not reopened twice");
        liveness.Retrying();
        Assert.Equal(DirectInputLiveness.FailureAction.RetryWith(false), liveness.Failed("device not found", current: true));
        liveness.Retrying();
        Assert.Equal(DirectInputLiveness.FailureAction.RetryWith(true), liveness.Failed("in use by another app", current: true));
        liveness.Retrying();
        liveness.Opened(50);
        Assert.Null(liveness.LastError);
        Assert.Equal(DirectInputLiveness.FailureAction.RetryWith(true), liveness.Failed("device not found", current: true));
    }

    [Fact]
    public void StaleOrStoppedFailuresAreIgnored()
    {
        var liveness = OpenInput();
        Assert.Equal(DirectInputLiveness.FailureAction.Ignore, liveness.Failed("helper unreachable", current: false));
        liveness.Stopped();
        Assert.Equal(DirectInputLiveness.FailureAction.Ignore, liveness.Failed("helper unreachable", current: true));
    }
}

public sealed class InputHotplugTests
{
    [Fact]
    public void ReturningDeviceStarts() => Assert.Equal(InputHotplug.Action.Start, InputHotplug.Decide(devicePresent: true, captureLive: false));

    [Fact]
    public void VanishedDeviceSuspends() => Assert.Equal(InputHotplug.Action.Suspend, InputHotplug.Decide(devicePresent: false, captureLive: true));

    [Fact]
    public void MatchingStatesNeverChurn()
    {
        Assert.Equal(InputHotplug.Action.None, InputHotplug.Decide(devicePresent: true, captureLive: true));
        Assert.Equal(InputHotplug.Action.None, InputHotplug.Decide(devicePresent: false, captureLive: false));
    }
}

public sealed class MonitorTakeoverTests
{
    private static readonly (string Id, string? DeviceUid)[] Outputs =
        [("main", null), ("booth", "usb-interface"), ("lobby", "dante"), ("parked", "none")];

    [Fact]
    public void AMonitorOnSystemDefaultSilencesTheOutputsThatFollowIt() =>
        Assert.Equal(["booth", "main"], MonitorTakeover.SilencedOutputs(Outputs, null, "usb-interface", "none").Order());

    [Fact]
    public void AMonitorOnANamedDeviceLeavesTheRestPlaying()
    {
        Assert.Equal(["lobby"], MonitorTakeover.SilencedOutputs(Outputs, "dante", "speakers", "none"));
        Assert.Empty(MonitorTakeover.SilencedOutputs(Outputs, "headphones", "speakers", "none"));
    }
}
