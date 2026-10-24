namespace MxU.Slides.Audio.Tests;

/// <summary>Port of AudioLevelTests.swift: meters, faders, playthrough basics, the engine lock and the mix feed.</summary>
public sealed class AudioLevelTests
{
    private static PcmBuffer Buffer(float value, int frames = 480)
    {
        var buffer = new PcmBuffer(48_000, 2, frames) { FrameLength = frames };
        for (var channel = 0; channel < 2; channel++) Array.Fill(buffer.Data[channel], value);
        return buffer;
    }

    [Fact]
    public void LevelMeasuresRmsAndPeak()
    {
        var level = AudioLevel.Measure(Buffer(0.5f));
        Assert.True(MathF.Abs(level.Rms - 0.5f) < 0.001);
        Assert.True(MathF.Abs(level.Peak - 0.5f) < 0.001);
        var silence = AudioLevel.Measure(Buffer(0));
        Assert.Equal(0, silence.Rms);
        Assert.Equal(0, silence.Peak);
    }

    [Fact]
    public void LevelClampsOverloadedSamples()
    {
        var level = AudioLevel.Measure(Buffer(1.8f));
        Assert.Equal(1, level.Rms);
        Assert.Equal(1, level.Peak);
    }

    [Fact]
    public void OutputLevelsFoldEveryMixLandingOnTheOutput()
    {
        var levels = AudioLevel.OutputLevels(
        [
            ([new(0.4f, 0.8f), new(0.1f, 0.1f)], [("lr", 1f), ("stream", 0.5f)]),
            ([new(0.3f, 0.3f), new(0.3f, 0.6f)], [("stream", 1f)]),
            ([new(0.9f, 0.9f), new(0.9f, 0.9f)], []),
        ]);
        Assert.True(MathF.Abs(levels["lr"][0].Rms - 0.4f) < 0.001);
        Assert.True(MathF.Abs(levels["lr"][0].Peak - 0.8f) < 0.001);
        Assert.True(MathF.Abs(levels["lr"][1].Peak - 0.1f) < 0.001);
        Assert.True(MathF.Abs(levels["stream"][0].Rms - MathF.Sqrt(0.2f * 0.2f + 0.3f * 0.3f)) < 0.001);
        Assert.True(MathF.Abs(levels["stream"][0].Peak - 0.4f) < 0.001);
        Assert.True(MathF.Abs(levels["stream"][1].Peak - 0.6f) < 0.001);
        Assert.Equal(2, levels.Count);
    }

    [Fact]
    public void CombinedLevelsSumPowerAndKeepLoudestPeak()
    {
        var sum = AudioLevel.Combine(new LevelReading(0.3f, 0.5f), new LevelReading(0.4f, 0.4f));
        Assert.True(MathF.Abs(sum.Rms - 0.5f) < 0.001);
        Assert.Equal(0.5f, sum.Peak);
        var gained = AudioLevel.Combine(LevelReading.Silence, new LevelReading(0.5f, 0.8f), gain: 0.5f);
        Assert.True(MathF.Abs(gained.Rms - 0.25f) < 0.001);
        Assert.True(MathF.Abs(gained.Peak - 0.4f) < 0.001);
        var hot = AudioLevel.Combine(new LevelReading(0.9f, 0.9f), new LevelReading(0.9f, 0.9f));
        Assert.Equal(1, hot.Rms);
        Assert.Equal(0.9f, hot.Peak);
    }

    [Fact]
    public void MeterBallisticsAttackInstantlyDecayTimed()
    {
        var meter = new MeterBallistics(decayDecibelsPerSecond: 20);
        meter.Update(rms: 0.8f, peak: 0.9f, elapsed: 0);
        Assert.Equal(0.8f, meter.Rms);
        Assert.Equal(0.9f, meter.Peak);
        meter.Update(rms: 0, peak: 0, elapsed: 0.1);
        Assert.True(MathF.Abs(meter.Rms - 0.8f * 0.7943f) < 0.001);
        Assert.True(MathF.Abs(meter.Peak - 0.9f * 0.7943f) < 0.001);
        meter.Update(rms: 0.95f, peak: 1.0f, elapsed: 0.1);
        Assert.Equal(0.95f, meter.Rms);
        Assert.Equal(1.0f, meter.Peak);
    }

    [Fact]
    public void ADrainedMeterSettlesAtZeroAndStopsChanging()
    {
        var meter = new MeterBallistics();
        meter.Update(rms: 0.5f, peak: 1, elapsed: 0);
        var ticks = 0;
        while (meter.Peak > 0 && ticks < 1000)
        {
            meter.Update(rms: 0, peak: 0, elapsed: 1.0 / 15);
            ticks++;
        }
        Assert.True(meter.Peak == 0 && meter.Rms == 0);
        Assert.True(ticks <= 61);
        var settled = meter;
        meter.Update(rms: 0, peak: 0, elapsed: 1.0 / 15);
        Assert.Equal(settled, meter);
        meter.Update(rms: 0.001f, peak: 0.001f, elapsed: 0);
        Assert.Equal(0.001f, meter.Peak);
    }

    [Fact]
    public void PeakHoldOnlyClimbsUntilReset()
    {
        var hold = new PeakHold();
        Assert.Equal(float.NegativeInfinity, hold.Decibels);
        hold.Update(0.5f);
        Assert.True(MathF.Abs(hold.Decibels - -6.02f) < 0.01);
        hold.Update(0.1f);
        Assert.True(MathF.Abs(hold.Decibels - -6.02f) < 0.01);
        hold.Update(0.8f);
        Assert.True(MathF.Abs(hold.Decibels - -1.94f) < 0.01);
        hold.Update(0);
        Assert.True(MathF.Abs(hold.Decibels - -1.94f) < 0.01);
        hold.Reset();
        Assert.Equal(float.NegativeInfinity, hold.Decibels);
    }

    [Fact]
    public void PeakHoldTickModePinsThenFollows()
    {
        var tick = new PeakHold(holdSeconds: 2);
        tick.Update(0.8f, at: 0);
        Assert.True(MathF.Abs(tick.Decibels - -1.94f) < 0.01);
        tick.Update(0.1f, at: 1);
        Assert.True(MathF.Abs(tick.Decibels - -1.94f) < 0.01);
        tick.Update(0.1f, at: 3);
        Assert.True(MathF.Abs(tick.Decibels - -20) < 0.01);
        tick.Update(0.5f, at: 3.5);
        Assert.True(MathF.Abs(tick.Decibels - -6.02f) < 0.01);
    }

    [Fact]
    public void FaderTaperSpendsTravelWhereMixingHappens()
    {
        Assert.Equal(1, AudioLevel.FaderGain(fraction: 1));
        Assert.Equal(0, AudioLevel.FaderGain(fraction: 0));
        Assert.True(MathF.Abs(AudioLevel.Decibels(AudioLevel.FaderGain(fraction: 0.7f)) - -15) < 0.01);
        Assert.True(MathF.Abs(AudioLevel.Decibels(AudioLevel.FaderGain(fraction: 0.4f)) - -30) < 0.01);
        Assert.True(MathF.Abs(AudioLevel.Decibels(AudioLevel.FaderGain(fraction: 0.225f)) - -45) < 0.01);
        Assert.True(MathF.Abs(AudioLevel.Decibels(AudioLevel.FaderGain(fraction: 0.05f)) - -60) < 0.01);
        Assert.True(MathF.Abs(AudioLevel.Decibels(AudioLevel.FaderGain(fraction: 0.025f)) - -75) < 0.01);
        Assert.Equal(1, AudioLevel.FaderFraction(gain: 1));
        Assert.Equal(0, AudioLevel.FaderFraction(gain: 0));
        foreach (var fraction in new[] { 0.225f, 0.05f, 0.025f })
        {
            var roundTrip = AudioLevel.FaderFraction(AudioLevel.FaderGain(fraction: fraction));
            Assert.True(MathF.Abs(roundTrip - fraction) < 0.001);
        }
        Assert.Equal(0, AudioLevel.FaderFraction(gain: 0.00001f));
    }

    [Fact]
    public void FaderGainParsesTypedDecibelEntries()
    {
        Assert.True(MathF.Abs(AudioLevel.Decibels(AudioLevel.FaderGain(entry: "-18")!.Value) - -18) < 0.01);
        Assert.True(MathF.Abs(AudioLevel.Decibels(AudioLevel.FaderGain(entry: "18")!.Value) - -18) < 0.01);
        Assert.True(MathF.Abs(AudioLevel.Decibels(AudioLevel.FaderGain(entry: " -6.5 ")!.Value) - -6.5f) < 0.01);
        Assert.Equal(1f, AudioLevel.FaderGain(entry: "0"));
        Assert.True(MathF.Abs(AudioLevel.Decibels(AudioLevel.FaderGain(entry: "-80")!.Value) - -80) < 0.01);
        Assert.Equal(0f, AudioLevel.FaderGain(entry: "-95"));
        Assert.Null(AudioLevel.FaderGain(entry: "loud"));
        Assert.Null(AudioLevel.FaderGain(entry: ""));
    }

    [Fact]
    public void LevelWindowFoldsEveryBufferAndResetsOnTake()
    {
        var window = new LevelWindow();
        window.Fold(new LevelReading(0.3f, 0.5f));
        window.Fold(new LevelReading(0.2f, 0.9f));
        window.Fold(new LevelReading(0.4f, 0.1f));
        var taken = window.Take();
        Assert.Equal(0.4f, taken.Rms);
        Assert.Equal(0.9f, taken.Peak);
        var empty = window.Take();
        Assert.Equal(0, empty.Rms);
        Assert.Equal(0, empty.Peak);
    }

    [Fact]
    public void MeterFractionMapsTheDecibelFace()
    {
        Assert.Equal(0, AudioLevel.MeterFraction(0));
        Assert.Equal(1, AudioLevel.MeterFraction(1));
        Assert.True(MathF.Abs(AudioLevel.MeterFraction(0.5f) - 0.8445f) < 0.001);
        Assert.True(MathF.Abs(AudioLevel.MeterFraction(0.1f) - 0.4833f) < 0.001);
        Assert.True(MathF.Abs(AudioLevel.MeterFraction(0.001f) - 0.06f) < 0.001);
        Assert.True(MathF.Abs(AudioLevel.MeterFraction(0.0001f) - 0.02f) < 0.001);
        Assert.Equal(0, AudioLevel.MeterFraction(0.00001f));
        Assert.Equal(1, AudioLevel.MeterFraction(1.8f));
        Assert.Equal(0.38f, AudioLevel.MeterFractionForDecibels(-24));
        Assert.True(MathF.Abs(AudioLevel.MeterFractionForDecibels(-20) - 0.4833f) < 0.001);
        Assert.True(MathF.Abs(AudioLevel.MeterFractionForDecibels(-40) - 0.2378f) < 0.001);
        Assert.True(MathF.Abs(AudioLevel.MeterFractionForDecibels(-55) - 0.1044f) < 0.001);
        Assert.True(MathF.Abs(AudioLevel.MeterFractionForDecibels(-75) - 0.03f) < 0.001);
        Assert.Equal(0, AudioLevel.MeterFractionForDecibels(float.NegativeInfinity));
        Assert.Equal(1, AudioLevel.MeterFractionForDecibels(2));
    }

    [Fact]
    public void PlaythroughDropsBuffersWhileStopped()
    {
        using var playthrough = new AudioInputPlaythrough();
        Assert.False(playthrough.Push(Buffer(0.1f)));
        Assert.False(playthrough.IsRunning);
    }

    [Fact]
    public void PlaythroughRestartsBehindTheSourceOnlyWhenDry()
    {
        Assert.Equal(1_440, AudioInputPlaythrough.LeadFrames(pending: 0));
        Assert.Equal(0, AudioInputPlaythrough.LeadFrames(pending: 1));
        Assert.Equal(0, AudioInputPlaythrough.LeadFrames(pending: 1_602));
        var lead = AudioInputPlaythrough.Silence(1_440);
        Assert.Equal(1_440, lead.FrameLength);
        Assert.True(Enumerable.Range(0, 2).All(channel => lead.Data[channel].Take(1_440).All(s => s == 0)));
    }

    [Fact]
    public void PlaythroughIsBornMutedUntilRouted()
    {
        // Without a device the playthrough still starts (on its clock), so this runs everywhere.
        using var playthrough = new AudioInputPlaythrough();
        Assert.True(playthrough.IsMuted);
        Assert.Equal(1, playthrough.Gain);
        playthrough.Start();
        Assert.Equal(0, playthrough.OutputVolume);
        playthrough.IsMuted = false;
        Assert.Equal(1, playthrough.OutputVolume);
        Assert.True(playthrough.Push(Buffer(0.1f)), "a running playthrough takes buffers");
        playthrough.Stop();
    }

    [Fact]
    public void PlaythroughConfigChangeDoesNotBlockPostingThread()
    {
        // The Windows device-change notification must not wait on the playthrough's lock.
        using var playthrough = new AudioInputPlaythrough();
        playthrough.Lock.Enter();
        try
        {
            var posted = TestAudio.FinishesWithin(playthrough.OnDeviceConfigurationChanged);
            Assert.True(posted, "config-change post must not wait on the playthrough lock");
        }
        finally
        {
            playthrough.Lock.Exit();
        }
    }

    [Fact]
    public void PlaythroughPushNeverBlocksOnTheLock()
    {
        using var playthrough = new AudioInputPlaythrough();
        playthrough.Lock.Enter();
        try
        {
            var returned = TestAudio.FinishesWithin(() => playthrough.Push(Buffer(0.1f)));
            Assert.True(returned, "push must not wait on the playthrough lock");
        }
        finally
        {
            playthrough.Lock.Exit();
        }
    }

    [Fact]
    public void PlaybackBusConfigChangeDoesNotBlockPostingThread()
    {
        using var bus = new AudioEngine();
        bus.Lock.Enter();
        try
        {
            var posted = TestAudio.FinishesWithin(bus.OnDeviceConfigurationChanged);
            Assert.True(posted, "config-change post must not wait on the bus lock");
        }
        finally
        {
            bus.Lock.Exit();
        }
    }

    [Fact]
    public void EngineStateLockSemantics()
    {
        var engineLock = new EngineStateLock();
        Assert.True(engineLock.TryWithLock(() => true, out var ran) && ran);
        engineLock.Enter();
        var free = TestAudio.OnOtherThread(() => engineLock.TryWithLock(() => true, out _));
        Assert.False(free);
        engineLock.Exit();
        Assert.False(RealtimeAudioCallback.IsActive);
        RealtimeAudioCallback.Scope(() =>
        {
            Assert.True(RealtimeAudioCallback.IsActive);
            RealtimeAudioCallback.Scope(() => Assert.True(RealtimeAudioCallback.IsActive));
            Assert.True(RealtimeAudioCallback.IsActive);
        });
        Assert.False(RealtimeAudioCallback.IsActive);
    }

    [Fact]
    public void PlaythroughGainAndMuteClamp()
    {
        using var playthrough = new AudioInputPlaythrough();
        playthrough.Gain = 1.7f;
        Assert.Equal(1, playthrough.Gain);
        playthrough.Gain = -0.2f;
        Assert.Equal(0, playthrough.Gain);
        playthrough.IsMuted = true;
        Assert.True(playthrough.IsMuted);
        playthrough.Pan = 1.6f;
        Assert.Equal(1, playthrough.Pan);
        playthrough.Pan = -2;
        Assert.Equal(-1, playthrough.Pan);
    }

    [Fact]
    public void PannedCopyAppliesBalanceLawAndSparesTheOriginal()
    {
        var source = Buffer(0.5f);
        Assert.Null(AudioInputPlaythrough.Panned(source, pan: 0));
        var hardLeft = AudioInputPlaythrough.Panned(source, pan: -1)!;
        Assert.True(MathF.Abs(hardLeft.Data[0][0] - 0.5f) < 0.0001);
        Assert.Equal(0, hardLeft.Data[1][0]);
        var halfRight = AudioInputPlaythrough.Panned(source, pan: 0.5f)!;
        Assert.True(MathF.Abs(halfRight.Data[0][0] - 0.25f) < 0.0001);
        Assert.True(MathF.Abs(halfRight.Data[1][0] - 0.5f) < 0.0001);
        Assert.Equal(0.5f, source.Data[0][0]);
        Assert.Equal(0.5f, source.Data[1][0]);
    }

    [Fact]
    public async Task MixFeedPanPlacesAMemberInTheImage()
    {
        var emitted = new List<PcmBuffer>();
        using var feed = new AudioMixFeed((buffer, _) => { lock (emitted) emitted.Add(buffer); });
        feed.Push("a", Buffer(0.5f, frames: 4800), pan: -1);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        List<PcmBuffer> buffers;
        lock (emitted) buffers = emitted.ToList();
        Assert.NotEmpty(buffers);
        var leftPeak = buffers.Max(b => b.Data[0][0]);
        var rightPeak = buffers.Max(b => b.Data[1][0]);
        Assert.True(MathF.Abs(leftPeak - 0.5f) < 0.01, "left carries the member");
        Assert.Equal(0, rightPeak);
        feed.RemoveMember("a");
    }

    [Fact]
    public async Task MixFeedSumsMembersOnItsOwnClock()
    {
        var emitted = new List<PcmBuffer>();
        using var feed = new AudioMixFeed((buffer, _) => { lock (emitted) emitted.Add(buffer); });
        // The feed ticks on its own 10 ms clock while the test pushes, as real members do: they
        // keep pushing. (Pushing each member once raced the clock: a test paused for 100 ms
        // between the two pushes saw the first mixed away before the second arrived.)
        List<PcmBuffer> buffers = [];
        float peak = 0;
        for (var wait = 0; wait < 60 && peak < 0.49f; wait++)
        {
            feed.Push("a", Buffer(0.25f, frames: 4800));
            feed.Push("b", Buffer(0.5f, frames: 4800), gain: 0.5f);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            lock (emitted) buffers = emitted.ToList();
            peak = buffers.Count == 0 ? 0 : buffers.Max(b => AudioLevel.Measure(b).Peak);
        }
        Assert.NotEmpty(buffers);
        Assert.True(MathF.Abs(peak - 0.5f) < 0.01, $"0.25 + 0.5×0.5 sums to 0.5, got {peak}");
        feed.RemoveMember("a");
        feed.RemoveMember("b");
    }
}
