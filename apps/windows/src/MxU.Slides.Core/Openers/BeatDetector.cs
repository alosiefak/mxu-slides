namespace MxU.Slides.Core.Openers;

/// <summary>
/// What <see cref="BeatDetector"/> found: the beat times in seconds (empty when the music has no
/// clear beat), the tempo, and how sure it is (0–1: how strongly the music's loudness pattern
/// repeats every beat).
/// </summary>
public sealed record BeatAnalysis(IReadOnlyList<double> Beats, double BeatsPerMinute, double Confidence, double Seconds)
{
    /// <summary>The length and beats, as the montage's plan takes them.</summary>
    public OpenerMusic Music => new(Seconds, Beats);
}

/// <summary>
/// Finds the beats of a piece of music, so a montage can cut on them. It's the simplest method
/// that works on songs with drums or a strummed guitar, an "energy onset" detector:
/// <list type="number">
/// <item><b>Loudness.</b> Every 5 ms, the loudness of the last 20 ms is measured (on a log
/// scale, as the ear hears it).</item>
/// <item><b>Onsets.</b> A beat is a sudden rise in loudness, so each step keeps only how much
/// louder it is than the one before (never negative), less the local average: what's left
/// spikes at each drum hit or strum.</item>
/// <item><b>Tempo.</b> If the spikes repeat every <i>L</i> steps, sliding the pattern along by
/// <i>L</i> lines it up with itself (its "autocorrelation" peaks at <i>L</i>). The best
/// <i>L</i> between 60 and 180 beats a minute is the beat's length; how tall the peak is, is
/// the confidence. A pad, a piano ballad or speech has no such peak: no clear beat, and the
/// montage cuts evenly instead.</item>
/// <item><b>Where the beats are.</b> A comb of teeth one beat apart is slid along to where it
/// catches the most spikes; then each beat in turn is nudged to the spike nearest to where it
/// was expected, so a band that drifts a little is still followed.</item>
/// </list>
/// Sound is fed in pieces (<see cref="Add"/>), so a long song never has to be in memory at once.
/// Use from one thread.
/// </summary>
public sealed class BeatDetector
{
    private const double StepSeconds = 0.005;
    private const double SlowestBeat = 1.0;   // 60 beats a minute
    private const double FastestBeat = 1 / 3.0; // 180

    /// <summary>Below this the music is treated as having no clear beat.</summary>
    public const double ClearBeat = 0.2;

    private const float SmallestRise = 0.1f;

    private readonly int sampleRate;
    private readonly int step;
    private readonly List<float> loudness = [];
    private readonly double[] recent = new double[4];
    private long steps;
    private double sum;
    private int filled;
    private long samples;

    public BeatDetector(int sampleRate)
    {
        this.sampleRate = Math.Max(1000, sampleRate);
        step = Math.Max(1, (int)Math.Round(this.sampleRate * StepSeconds));
    }

    /// <summary>The next piece of the music, one channel (mix stereo down first).</summary>
    public void Add(ReadOnlySpan<float> mono)
    {
        samples += mono.Length;
        foreach (var sample in mono)
        {
            sum += sample * (double)sample;
            if (++filled < step) continue;
            // Loudness is taken over the last four steps (20 ms), moving on a step at a time. Over one
            // step alone, a low note (a few waves long) reads louder or quieter depending on where
            // its waves fall, and that flutter looks like a rhythm. On a log scale, so a hit in a
            // quiet verse counts as much as one in a loud chorus.
            recent[steps++ % recent.Length] = sum;
            loudness.Add((float)Math.Log(1 + 1000 * Math.Sqrt(recent.Sum() / (step * Math.Min(steps, recent.Length)))));
            sum = 0;
            filled = 0;
        }
    }

    /// <summary>The beats of a whole piece already in memory.</summary>
    public static BeatAnalysis Detect(ReadOnlySpan<float> mono, int sampleRate)
    {
        var detector = new BeatDetector(sampleRate);
        detector.Add(mono);
        return detector.Finish();
    }

    /// <summary>Works out the beats of everything added so far.</summary>
    public BeatAnalysis Finish()
    {
        var seconds = samples / (double)sampleRate;
        var stepSeconds = step / (double)sampleRate;
        var onsets = Onsets();
        var slowest = (int)Math.Round(SlowestBeat / stepSeconds);
        var fastest = (int)Math.Round(FastestBeat / stepSeconds);
        // At least four of the slowest beats, or there's nothing to measure a tempo from.
        if (onsets.Length < slowest * 4) return new BeatAnalysis([], 0, 0, seconds);

        // For measuring the tempo each spike is spread over its neighbours a little, so a beat that
        // falls between two steps still lines up with itself.
        var soft = new float[onsets.Length];
        for (var i = 1; i < onsets.Length - 1; i++) soft[i] = 0.25f * onsets[i - 1] + 0.5f * onsets[i] + 0.25f * onsets[i + 1];
        double energy = 0;
        foreach (var o in soft) energy += o * (double)o;
        if (energy <= 1e-9) return new BeatAnalysis([], 0, 0, seconds);

        // How well the onsets line up with themselves, slid along by each possible beat length.
        var match = new double[slowest * 4 + 2];
        for (var lag = fastest / 2; lag < match.Length; lag++)
        {
            double total = 0;
            for (var i = 0; i + lag < soft.Length; i++) total += soft[i] * (double)soft[i + lag];
            // Fewer pairs overlap at a longer slide; scale so every length is judged alike.
            match[lag] = total / energy * onsets.Length / (onsets.Length - lag);
        }

        // The beat length that also lines up at twice, three and four times itself: in fine steps,
        // because a tempo rarely falls on a whole number of 5 ms steps, and an error here adds up
        // beat after beat.
        double best = 0, bestScore = double.MinValue;
        for (var length = (double)fastest; length <= slowest; length += 0.05)
        {
            double score = 0;
            for (var multiple = 1; multiple <= 4; multiple++) score += At(match, length * multiple);
            // A gentle lean towards 120 a minute (half a second), where most songs' pulse sits:
            // without it a song is as likely to be read at half or double speed.
            var octaves = Math.Log2(length * stepSeconds / 0.5);
            score *= Math.Exp(-0.5 * octaves * octaves / (0.9 * 0.9));
            if (score > bestScore) (best, bestScore) = (length, score);
        }
        var confidence = Math.Clamp((At(match, best) + At(match, best * 2)) / 2, 0, 1);
        var bpm = 60 / (best * stepSeconds);
        if (confidence < ClearBeat) return new BeatAnalysis([], bpm, confidence, seconds);

        // Slide a comb of teeth one beat apart to where it catches the most onsets.
        double offset = 0, caught = double.MinValue;
        for (var start = 0.0; start < best; start += 0.5)
        {
            double total = 0;
            for (var at = start; at < onsets.Length - 1; at += best) total += At(onsets, at);
            if (total > caught) (offset, caught) = (start, total);
        }
        var typical = caught / Math.Max(1, (onsets.Length - offset) / best);

        // Walk the beats, letting each settle on the strongest onset within a twelfth of a beat of
        // where it was expected, if there is a real one there.
        var beats = new List<double>();
        var reach = Math.Max(1, (int)Math.Round(best / 12));
        for (var at = offset; at < onsets.Length; at += best)
        {
            var centre = (int)Math.Round(at);
            var peak = centre;
            for (var i = Math.Max(0, centre - reach); i <= Math.Min(onsets.Length - 1, centre + reach); i++)
                if (onsets[i] > onsets[Math.Clamp(peak, 0, onsets.Length - 1)]) peak = i;
            if (peak >= 0 && peak < onsets.Length && onsets[peak] >= typical * 0.5) at = peak;
            // Onset i is the rise from step i − 1 to step i, so the sound began at the start of step i.
            var time = at * stepSeconds;
            if (time <= seconds) beats.Add(time);
        }
        return beats.Count >= 4 ? new BeatAnalysis(beats, bpm, confidence, seconds) : new BeatAnalysis([], bpm, confidence, seconds);
    }

    /// <summary>How much louder each step is than the one before, less the average around it; never negative.</summary>
    private float[] Onsets()
    {
        var count = loudness.Count;
        var rises = new float[count];
        // A rise has to be a real one: the loudness of a steady note wobbles a little from step to
        // step, and that wobble must not be mistaken for a rhythm.
        for (var i = 1; i < count; i++) rises[i] = Math.Max(0, loudness[i] - loudness[i - 1] - SmallestRise);
        // The average over about a second each side, kept as a running sum.
        var half = (int)Math.Round(0.5 / StepSeconds);
        var onsets = new float[count];
        double running = 0;
        for (var i = 0; i < Math.Min(count, half); i++) running += rises[i];
        for (var i = 0; i < count; i++)
        {
            if (i + half < count) running += rises[i + half];
            if (i - half - 1 >= 0) running -= rises[i - half - 1];
            var width = Math.Min(count - 1, i + half) - Math.Max(0, i - half) + 1;
            onsets[i] = Math.Max(0, rises[i] - (float)(running / width));
        }
        return onsets;
    }

    /// <summary>The value between two steps (a straight line between them); 0 past the end.</summary>
    private static double At(double[] values, double position)
    {
        var i = (int)position;
        if (i < 0 || i + 1 >= values.Length) return 0;
        var part = position - i;
        return values[i] * (1 - part) + values[i + 1] * part;
    }

    private static double At(float[] values, double position)
    {
        var i = (int)position;
        if (i < 0 || i + 1 >= values.Length) return 0;
        var part = position - i;
        return values[i] * (1 - part) + values[i + 1] * part;
    }
}
