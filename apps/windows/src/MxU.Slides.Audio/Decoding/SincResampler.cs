namespace MxU.Slides.Audio.Decoding;

/// <summary>
/// Converts a stream from one sample rate to another with a Kaiser-windowed sinc filter
/// centred on each output sample, so nothing is delayed: output frame k is source time
/// k × (from/to) exactly. That keeps stems at 44.1 kHz and 48 kHz sample-locked, which is
/// what AVAudioConverter gives the Mac (NAudio's resampler adds filter delay instead).
/// Feed source frames with <see cref="Process"/>, then <see cref="Flush"/> at the end.
/// </summary>
internal sealed class SincResampler
{
    private const int Phases = 256;
    private const double Beta = 8;

    private readonly int channels;
    private readonly double step;
    private readonly int half;
    private readonly float[] kernel;
    private readonly float[] row;

    // Source samples not yet fully used, per channel; `position` is the next output's
    // source time measured from history[0].
    private readonly float[][] history;
    private int buffered;
    private double position;

    public SincResampler(int fromRate, int toRate, int channels)
    {
        if (fromRate <= 0 || toRate <= 0) throw new ArgumentOutOfRangeException(nameof(fromRate));
        FromRate = fromRate;
        ToRate = toRate;
        this.channels = channels;
        step = fromRate / (double)toRate;
        // Going down in rate the filter must cut lower (and so needs more taps) to stop aliasing.
        var shrink = Math.Max(1, step);
        half = (int)Math.Ceiling(16 * shrink);
        kernel = BuildKernel(half, 0.9 / shrink);
        row = new float[2 * half];
        history = new float[channels][];
        for (var channel = 0; channel < channels; channel++) history[channel] = new float[4096 + 2 * half];
        Reset();
    }

    public int FromRate { get; }
    public int ToRate { get; }

    /// <summary>How many output frames <paramref name="sourceFrames"/> more input could produce at most.</summary>
    public int MaxOutput(int sourceFrames) => (int)Math.Ceiling((buffered + sourceFrames) / step) + 2;

    /// <summary>Back to the start: silence before the first sample.</summary>
    public void Reset()
    {
        foreach (var channel in history) Array.Clear(channel);
        buffered = half - 1;
        position = half - 1;
    }

    /// <summary>
    /// Starts again with <paramref name="count"/> frames of real audio before the first new
    /// sample (after a seek), so the filter's left side isn't fed silence.
    /// </summary>
    public void Prime(float[][] source, int offset, int count)
    {
        Reset();
        count = Math.Min(count, half - 1);
        for (var channel = 0; channel < channels; channel++)
            Array.Copy(source[channel], offset, history[channel], half - 1 - count, count);
    }

    /// <summary>
    /// Takes <paramref name="count"/> source frames and writes the output frames they complete
    /// into <paramref name="output"/> at <paramref name="outputOffset"/>; returns how many.
    /// </summary>
    public int Process(float[][] source, int offset, int count, float[][] output, int outputOffset)
    {
        var written = 0;
        var taken = 0;
        while (taken < count)
        {
            var room = history[0].Length - buffered;
            if (room <= 0)
            {
                Compact();
                room = history[0].Length - buffered;
            }
            var take = Math.Min(room, count - taken);
            for (var channel = 0; channel < channels; channel++)
                Array.Copy(source[channel], offset + taken, history[channel], buffered, take);
            buffered += take;
            taken += take;
            written += Drain(output, outputOffset + written, double.PositiveInfinity);
        }
        return written;
    }

    /// <summary>Pads the end with silence so the last source samples come out too.</summary>
    public int Flush(float[][] output, int outputOffset)
    {
        var end = buffered;
        var silence = new float[channels][];
        for (var channel = 0; channel < channels; channel++) silence[channel] = new float[half];
        var written = 0;
        var taken = 0;
        while (taken < half)
        {
            var room = history[0].Length - buffered;
            if (room <= 0)
            {
                end -= Compact();
                room = history[0].Length - buffered;
            }
            var take = Math.Min(room, half - taken);
            for (var channel = 0; channel < channels; channel++) Array.Clear(history[channel], buffered, take);
            buffered += take;
            taken += take;
            written += Drain(output, outputOffset + written, end);
        }
        return written;
    }

    // Produces every output the buffered samples allow (each needs `half` samples past its
    // time) whose source time is before `endExclusive`.
    private int Drain(float[][] output, int outputOffset, double endExclusive)
    {
        var written = 0;
        while (position < endExclusive)
        {
            var whole = (int)Math.Floor(position);
            if (whole + half >= buffered) break;
            var phase = (position - whole) * Phases;
            var p = Math.Min((int)phase, Phases - 1);
            var a = (float)(phase - p);
            var low = p * 2 * half;
            var high = low + 2 * half;
            for (var m = 0; m < 2 * half; m++) row[m] = kernel[low + m] + a * (kernel[high + m] - kernel[low + m]);
            var first = whole - half + 1;
            for (var channel = 0; channel < channels; channel++)
            {
                var samples = history[channel];
                float sum = 0;
                for (var m = 0; m < 2 * half; m++) sum += samples[first + m] * row[m];
                output[channel][outputOffset + written] = sum;
            }
            written++;
            position += step;
        }
        return written;
    }

    // Drops samples no future output needs; returns how many.
    private int Compact()
    {
        var keep = (int)Math.Floor(position) - half + 1;
        if (keep <= 0) throw new InvalidOperationException("Resampler history is too small");
        for (var channel = 0; channel < channels; channel++)
            Array.Copy(history[channel], keep, history[channel], 0, buffered - keep);
        buffered -= keep;
        position -= keep;
        return keep;
    }

    // Row p (fraction p/Phases) holds the weights for samples whole-half+1 … whole+half.
    private static float[] BuildKernel(int half, double cutoff)
    {
        var table = new float[(Phases + 1) * 2 * half];
        var i0Beta = I0(Beta);
        var weights = new double[2 * half];
        for (var p = 0; p <= Phases; p++)
        {
            var fraction = p / (double)Phases;
            double sum = 0;
            for (var m = 0; m < 2 * half; m++)
            {
                var d = fraction + half - 1 - m;
                if (Math.Abs(d) >= half)
                {
                    weights[m] = 0;
                    continue;
                }
                var x = Math.PI * cutoff * d;
                var sinc = d == 0 ? 1 : Math.Sin(x) / x;
                var window = I0(Beta * Math.Sqrt(1 - d / half * (d / half))) / i0Beta;
                weights[m] = cutoff * sinc * window;
                sum += weights[m];
            }
            // Unity gain at DC: a steady level comes out at exactly that level.
            for (var m = 0; m < 2 * half; m++) table[p * 2 * half + m] = (float)(weights[m] / sum);
        }
        return table;
    }

    internal static double I0(double x)
    {
        double sum = 1, term = 1;
        for (var k = 1; k < 40; k++)
        {
            term *= x / 2 * (x / 2) / (k * (double)k);
            sum += term;
        }
        return sum;
    }
}
