namespace MxU.Slides.VideoIO.Common;

/// <summary>
/// Counters a playout card reports about its timing: frames shown twice because
/// nothing new was ready (<see cref="Repeats"/>), frames thrown away because two
/// were waiting (<see cref="Skips"/>), frames the card itself flagged late or dropped,
/// times the schedule had to jump forward, and frames this app held back because the
/// card was still busy. Port of RenderEngine's RenderPulse.CardTiming.
/// </summary>
public readonly record struct CardTiming(int Repeats = 0, int Skips = 0, int Late = 0, int Dropped = 0, int Jumps = 0, int Held = 0)
{
    public static CardTiming operator +(CardTiming left, CardTiming right) => new(
        left.Repeats + right.Repeats, left.Skips + right.Skips, left.Late + right.Late,
        left.Dropped + right.Dropped, left.Jumps + right.Jumps, left.Held + right.Held);
}

/// <summary>
/// Works out a card's real frame clock from the completion counts it reports, so the
/// program feed can pace itself to the card instead of to the PC's clock. The period
/// is a least-squares fit of completion count against time over the last
/// <see cref="Window"/> seconds, and the phase is the fitted line's value at the newest
/// count (the mean of the window, not the jittery newest sample). It also turns the
/// card's running totals into per-reply changes. Not thread-safe; the owner locks.
/// Port of OutputEngine/Export/CardClock.swift.
/// </summary>
public sealed class CardClock
{
    /// <summary>Seconds of completions the fit looks back over.</summary>
    internal const double Window = 4.0;

    /// <summary>No cadence until the samples span at least this many seconds.</summary>
    internal const double MinimumSpan = 1.0;

    private readonly List<(int Completions, double Time)> samples = [];
    private CardTiming reported;

    /// <summary>
    /// Takes one timing reply from the card: its total completions, when the newest one
    /// happened, and its running counters. Returns how much each counter grew since the
    /// last reply. A count that went backwards means the output was reopened, so the
    /// clock starts over.
    /// </summary>
    public CardTiming Record(int completions, double lastCompletion, CardTiming counters)
    {
        if (samples.Count > 0 && completions < samples[^1].Completions)
        {
            samples.Clear();
            reported = new CardTiming();
        }
        if (completions > 0 && (samples.Count == 0 || samples[^1].Completions != completions))
        {
            samples.Add((completions, lastCompletion));
            samples.RemoveAll(sample => lastCompletion - sample.Time > Window);
        }
        var delta = new CardTiming(
            counters.Repeats - reported.Repeats, counters.Skips - reported.Skips,
            counters.Late - reported.Late, counters.Dropped - reported.Dropped,
            counters.Jumps - reported.Jumps);
        reported = counters;
        return delta;
    }

    /// <summary>The card's frame clock, or null until a second of completions has been seen.</summary>
    public LiveCadence? Cadence
    {
        get
        {
            if (samples.Count == 0 || samples[^1].Time - samples[0].Time < MinimumSpan) return null;
            double count = samples.Count;
            var meanCount = samples.Sum(s => (double)s.Completions) / count;
            var meanTime = samples.Sum(s => s.Time) / count;
            var covariance = samples.Sum(s => (s.Completions - meanCount) * (s.Time - meanTime));
            var variance = samples.Sum(s => (s.Completions - meanCount) * (s.Completions - meanCount));
            var period = covariance / variance;
            return new LiveCadence(meanTime + (samples[^1].Completions - meanCount) * period, period);
        }
    }
}
