using MxU.Slides.Audio.Decoding;

namespace MxU.Slides.Audio.Multitrack;

/// <summary>
/// Plays stems a hair faster or slower (±0.2%) so they stay locked to incoming timecode
/// instead of jumping: each output sample is read between source samples through a
/// 32-tap Kaiser-windowed sinc, interpolated across 256 fractional phases. Port of
/// MultitrackVarispeed.swift (vDSP loops written out).
/// </summary>
internal sealed class MultitrackVarispeed
{
    public const int Taps = 32;
    public const int Phases = 256;

    /// <summary>(Phases + 1) rows of Taps weights; row p is for a read p/Phases past a sample.</summary>
    internal static readonly float[] Kernel = BuildKernel();

    private readonly int stride;
    private readonly float[] windows;
    private readonly float[] kernels;
    private readonly int[] bases;
    private double fraction;
    private bool primed;

    public MultitrackVarispeed(int stems, int maxFrames)
    {
        stride = Taps + maxFrames * 2 + 8;
        windows = new float[Math.Max(1, stems * 2 * stride)];
        kernels = new float[maxFrames * Taps];
        bases = new int[maxFrames];
    }

    private static float[] BuildKernel()
    {
        var table = new float[(Phases + 1) * Taps];
        const double cutoff = 0.9, beta = 8.0;
        var row = new double[Taps];
        for (var p = 0; p <= Phases; p++)
        {
            var fraction = p / (double)Phases;
            double sum = 0;
            for (var m = 0; m < Taps; m++)
            {
                var u = 15 + fraction - m;
                if (Math.Abs(u) >= 16)
                {
                    row[m] = 0;
                    continue;
                }
                var sinc = u == 0 ? 1 : Math.Sin(Math.PI * cutoff * u) / (Math.PI * cutoff * u);
                row[m] = cutoff * sinc * SincResampler.I0(beta * Math.Sqrt(1 - u / 16 * (u / 16))) / SincResampler.I0(beta);
                sum += row[m];
            }
            for (var m = 0; m < Taps; m++) table[p * Taps + m] = (float)(row[m] / sum);
        }
        return table;
    }

    /// <summary>Works out the read positions for the next <paramref name="count"/> outputs; returns how many source frames they use.</summary>
    public int Plan(int count, double speed)
    {
        for (var j = 0; j < count; j++)
        {
            var t = fraction + j * speed;
            var whole = (int)t;
            var phase = (t - whole) * Phases;
            var p = Math.Min((int)phase, Phases - 1);
            var a = (float)(phase - p);
            var low = p * Taps;
            var high = low + Taps;
            for (var m = 0; m < Taps; m++) kernels[j * Taps + m] = Kernel[low + m] + a * (Kernel[high + m] - Kernel[low + m]);
            bases[j] = whole;
        }
        return (int)(fraction + count * speed);
    }

    /// <summary>Reads <paramref name="advance"/> new frames of one stem and writes <paramref name="count"/> resampled outputs.</summary>
    public void Render(int stem, StemRing ring, int count, int advance, float[] left, float[] right)
    {
        var leftStart = stem * 2 * stride;
        var rightStart = leftStart + stride;
        if (!primed)
        {
            // The first output sits on the first sample: 15 frames of silence behind it.
            ring.Read(windows.AsSpan(leftStart + 15, Taps - 15), windows.AsSpan(rightStart + 15, Taps - 15), Taps - 15);
        }
        ring.Read(windows.AsSpan(leftStart + Taps, advance), windows.AsSpan(rightStart + Taps, advance), advance);
        Convolve(leftStart, left, count);
        Convolve(rightStart, right, count);
        Array.Copy(windows, leftStart + advance, windows, leftStart, Taps);
        Array.Copy(windows, rightStart + advance, windows, rightStart, Taps);
    }

    public void Finish(int count, double speed, int advance)
    {
        fraction = fraction + count * speed - advance;
        primed = true;
    }

    private void Convolve(int window, float[] output, int count)
    {
        for (var j = 0; j < count; j++)
        {
            var from = window + bases[j];
            var weights = j * Taps;
            float sum = 0;
            for (var m = 0; m < Taps; m++) sum += windows[from + m] * kernels[weights + m];
            output[j] = sum;
        }
    }
}

/// <summary>
/// Follows incoming timecode: a big error (over 150 ms) means jump ("land") there; a small
/// one is steered out by nudging the speed (a smoothed PI controller, at most ±0.2%), so
/// jitter and slow drift never cause a jump. Port of MultitrackChase.swift.
/// </summary>
internal struct MultitrackChase
{
    public readonly record struct Decision(bool Land, double Speed)
    {
        public static readonly Decision LandNow = new(true, 1);

        public static Decision At(double speed) => new(false, speed);
    }

    public const double LandSeconds = 0.15;
    public const double Smoothing = 0.5;
    public const double Proportional = 0.5;
    public const double Integral = 0.0625;
    public const double MaxDeviation = 0.002;

    private double? error;
    private double? lastTime;
    private double drift;

    public void Reset()
    {
        error = null;
        lastTime = null;
    }

    /// <param name="raw">Where we are minus where timecode says (seconds; positive = ahead).</param>
    /// <param name="time">When that was measured (seconds).</param>
    public Decision Step(double raw, double time)
    {
        if (Math.Abs(raw) > LandSeconds)
        {
            Reset();
            return Decision.LandNow;
        }
        var dt = lastTime is { } last ? Math.Clamp(time - last, 0, 1) : 0;
        lastTime = time;
        var smoothed = error is { } held ? held + (raw - held) * dt / (Smoothing + dt) : raw;
        error = smoothed;
        var wanted = -(Proportional * smoothed + drift);
        var limited = Math.Clamp(wanted, -MaxDeviation, MaxDeviation);
        // Only integrate while not pinned at the limit, so the drift term can't wind up.
        if (wanted == limited) drift += Integral * smoothed * dt;
        return Decision.At(1 + limited);
    }
}
