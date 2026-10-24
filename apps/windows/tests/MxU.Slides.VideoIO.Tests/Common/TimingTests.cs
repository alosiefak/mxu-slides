using MxU.Slides.VideoIO.Common;

namespace MxU.Slides.VideoIO.Tests.Common;

/// <summary>Port of MediaEngineTests/ArrivalWindowTests.swift (the Metal-only registry test stays on the Mac).</summary>
public sealed class ArrivalWindowTests
{
    [Fact]
    public void ThePeriodAveragesDeliveryJitterOut()
    {
        var window = new ArrivalWindow();
        var random = new Random(7);
        var period = 1001.0 / 30000.0;
        for (var frame = 0; frame < 200; frame++) window.Record(100 + frame * period + (random.NextDouble() * 0.006 - 0.003));
        var cadence = Assert.NotNull(window.Cadence);
        Assert.Equal(period, cadence.Period, period * 0.002);
        Assert.Equal(100 + 199 * period, cadence.LastArrival, 0.0031);
    }

    [Fact]
    public void NothingIsReportedBeforeASecondOfFrames()
    {
        var window = new ArrivalWindow();
        for (var frame = 0; frame < ArrivalWindow.MinimumCount - 1; frame++) window.Record(frame / 30.0);
        Assert.Null(window.Cadence);
    }

    [Fact]
    public void ADroppedSignalRestartsTheWindow()
    {
        var window = new ArrivalWindow();
        for (var frame = 0; frame < 60; frame++) window.Record(frame / 30.0);
        window.Record(10);
        Assert.Null(window.Cadence);
    }
}

/// <summary>Port of OutputEngineTests/CardClockTests.swift.</summary>
public sealed class CardClockTests
{
    private const double Ntsc = 1001.0 / 30000;

    [Fact]
    public void TheClockWaitsForASecondOfCompletions()
    {
        var clock = new CardClock();
        for (var count = 1; count <= 20; count++) clock.Record(count, count * Ntsc, new CardTiming());
        Assert.Null(clock.Cadence);
        for (var count = 21; count <= 40; count++) clock.Record(count, count * Ntsc, new CardTiming());
        Assert.Equal(Ntsc, Assert.NotNull(clock.Cadence).Period, 1e-9);
    }

    [Fact]
    public void ThePeriodComesFromCountsNotReplies()
    {
        var clock = new CardClock();
        foreach (var count in new[] { 1, 3, 3, 4, 7, 7, 8, 12, 20, 33, 34, 40, 41 }) clock.Record(count, count * Ntsc, new CardTiming());
        var cadence = Assert.NotNull(clock.Cadence);
        Assert.Equal(Ntsc, cadence.Period, 1e-9);
        Assert.Equal(41 * Ntsc, cadence.LastArrival, 1e-9);
    }

    [Fact]
    public void ThePhaseIsTheMeanOfTheWindowNotTheNewestSample()
    {
        var clock = new CardClock();
        for (var count = 1; count <= 60; count++)
        {
            var jitter = count % 2 == 0 ? 0.002 : -0.002;
            clock.Record(count, count * Ntsc + jitter, new CardTiming());
        }
        clock.Record(61, 61 * Ntsc + 0.003, new CardTiming());
        Assert.Equal(61 * Ntsc, Assert.NotNull(clock.Cadence).LastArrival, 0.0005);
    }

    [Fact]
    public void CountersReportAsDeltasAndAFreshOpenRestarts()
    {
        var clock = new CardClock();
        clock.Record(10, 1, new CardTiming(Repeats: 2, Skips: 1));
        var delta = clock.Record(11, 1.03, new CardTiming(Repeats: 3, Skips: 1, Late: 1));
        Assert.Equal(new CardTiming(Repeats: 1, Late: 1), delta);

        for (var count = 12; count <= 50; count++)
            clock.Record(count, 1 + (count - 10) * Ntsc, new CardTiming(Repeats: 3, Skips: 1, Late: 1));
        Assert.NotNull(clock.Cadence);
        var reopened = clock.Record(2, 9, new CardTiming(Repeats: 1));
        Assert.Equal(new CardTiming(Repeats: 1), reopened);
        Assert.Null(clock.Cadence);
    }
}

/// <summary>Port of AudioEngineTests/InputHotplugTests.swift.</summary>
public sealed class InputHotplugTests
{
    [Fact]
    public void ReturningDeviceStarts() => Assert.Equal(HotplugAction.Start, InputHotplug.Decide(devicePresent: true, captureLive: false));

    [Fact]
    public void VanishedDeviceSuspends() => Assert.Equal(HotplugAction.Suspend, InputHotplug.Decide(devicePresent: false, captureLive: true));

    [Fact]
    public void MatchingStatesNeverChurn()
    {
        Assert.Equal(HotplugAction.None, InputHotplug.Decide(devicePresent: true, captureLive: true));
        Assert.Equal(HotplugAction.None, InputHotplug.Decide(devicePresent: false, captureLive: false));
    }
}

/// <summary>Port of AudioEngineTests/DirectInputLivenessTests.swift.</summary>
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
        Assert.False(liveness.ShouldReopen(15.9)); // an arrival restarts the watch
    }

    [Fact]
    public void OnlyAGoodOpenIsWatched()
    {
        var liveness = OpenInput();
        liveness.Opening();
        Assert.False(liveness.ShouldReopen(100)); // an unanswered open is not silence
        var stopped = OpenInput();
        stopped.Stopped();
        Assert.False(stopped.ShouldReopen(100));
    }

    [Fact]
    public void FailuresRetryAndNoteEachMessageOnce()
    {
        var liveness = OpenInput();
        Assert.Equal(LivenessFailureAction.RetryWith(note: true), liveness.Failed("device not found", current: true));
        Assert.Equal(LivenessFailureAction.Ignore, liveness.Failed("device not found", current: true)); // one retry at a time
        Assert.False(liveness.ShouldReopen(100)); // a queued retry is not reopened twice
        liveness.Retrying();
        Assert.Equal(LivenessFailureAction.RetryWith(note: false), liveness.Failed("device not found", current: true));
        liveness.Retrying();
        Assert.Equal(LivenessFailureAction.RetryWith(note: true), liveness.Failed("in use by another app", current: true));
        liveness.Retrying();
        liveness.Opened(50);
        Assert.Null(liveness.LastError);
        Assert.Equal(LivenessFailureAction.RetryWith(note: true), liveness.Failed("device not found", current: true)); // a fresh episode notes again
    }

    [Fact]
    public void StaleOrStoppedFailuresAreIgnored()
    {
        var liveness = OpenInput();
        Assert.Equal(LivenessFailureAction.Ignore, liveness.Failed("helper unreachable", current: false));
        liveness.Stopped();
        Assert.Equal(LivenessFailureAction.Ignore, liveness.Failed("helper unreachable", current: true));
    }
}
