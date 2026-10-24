using System.Numerics;

namespace MxU.Slides.Audio.Video;

/// <summary>
/// Plays sound faster or slower without changing its pitch, for a video set to play at, say,
/// half speed (the Mac's AVPlayer does this for its videos). The method is WSOLA ("waveform
/// similarity overlap-add"): the sound is cut into overlapping 21 ms pieces, taken from the
/// input <c>rate</c> times further apart than they are laid down in the output, and each
/// piece is nudged (by up to 5 ms) to wherever it best continues the one before, so the
/// waves line up and nothing warbles. Speech and music come out at the same pitch.
/// <para>
/// Input goes in with <see cref="Push"/>, stretched output comes out with <see cref="Pull"/>:
/// one output frame stands for <see cref="Rate"/> input frames. Stereo; both sides are cut
/// at the same places (chosen on their sum) so the stereo image holds. One thread at a time.
/// </para>
/// </summary>
internal sealed class TimeStretch
{
    public const int FrameLength = 1024;
    public const int Hop = FrameLength / 2;
    public const int Tolerance = 256;

    // A periodic Hann window: two of them half a frame apart add up to exactly 1, so the
    // overlapped pieces keep the level.
    private static readonly float[] Window = BuildWindow();

    private readonly double analysisHop;
    private float[] inLeft = new float[16384];
    private float[] inRight = new float[16384];
    private float[] inMono = new float[16384];
    private long inBase;
    private int inCount;
    private bool finished;

    private long frameIndex;
    private long previousStart = -1;
    private readonly float[] overlapLeft = new float[Hop];
    private readonly float[] overlapRight = new float[Hop];
    private readonly float[] pieceLeft = new float[FrameLength];
    private readonly float[] pieceRight = new float[FrameLength];

    private float[] outLeft = new float[8192];
    private float[] outRight = new float[8192];
    private int outStart;
    private int outCount;
    private bool flushed;

    public TimeStretch(double rate)
    {
        Rate = Math.Clamp(rate, 0.25, 4);
        analysisHop = Hop * Rate;
    }

    /// <summary>Input frames per output frame (2 = twice as fast).</summary>
    public double Rate { get; }

    /// <summary>Stretched frames ready to <see cref="Pull"/>.</summary>
    public int Available => outCount;

    /// <summary>True once <see cref="Finish"/> was called and every frame has been pulled.</summary>
    public bool IsDrained => flushed && outCount == 0;

    public void Push(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var count = Math.Min(left.Length, right.Length);
        Grow(ref inLeft, inCount + count);
        Grow(ref inRight, inCount + count);
        Grow(ref inMono, inCount + count);
        left[..count].CopyTo(inLeft.AsSpan(inCount));
        right[..count].CopyTo(inRight.AsSpan(inCount));
        for (var i = 0; i < count; i++) inMono[inCount + i] = left[i] + right[i];
        inCount += count;
        Produce();
    }

    /// <summary>No more input: the last pieces come out (the missing tail counts as silence).</summary>
    public void Finish()
    {
        finished = true;
        Produce();
    }

    public int Pull(Span<float> left, Span<float> right, int count)
    {
        var take = Math.Min(count, outCount);
        outLeft.AsSpan(outStart, take).CopyTo(left);
        outRight.AsSpan(outStart, take).CopyTo(right);
        outStart += take;
        outCount -= take;
        if (outCount == 0) outStart = 0;
        return take;
    }

    private void Produce()
    {
        while (TryFrame()) { }
        if (finished && !flushed && !HasMoreFrames())
        {
            // The last piece's second half has nothing to overlap with: let it out as it is.
            Emit(overlapLeft, overlapRight, Hop);
            flushed = true;
        }
    }

    // Is there input left for another piece, once input has ended?
    private bool HasMoreFrames() => (long)Math.Round(frameIndex * analysisHop) < inBase + inCount;

    private bool TryFrame()
    {
        var nominal = (long)Math.Round(frameIndex * analysisHop);
        long start;
        if (frameIndex == 0)
        {
            if (!Have(0, FrameLength)) return false;
            start = 0;
        }
        else
        {
            if (finished && !HasMoreFrames()) return false;
            var natural = previousStart + Hop;
            var lowest = Math.Max(0, nominal - Tolerance);
            var highest = nominal + Tolerance;
            var needed = Math.Max(highest, natural) + FrameLength;
            if (!Have(lowest, needed - lowest)) return false;
            start = BestStart(natural, lowest, highest);
        }
        // Lay the piece down: its first half onto the last piece's second half.
        var left = pieceLeft;
        var right = pieceRight;
        for (var i = 0; i < FrameLength; i++)
        {
            left[i] = Sample(inLeft, start + i) * Window[i];
            right[i] = Sample(inRight, start + i) * Window[i];
        }
        for (var i = 0; i < Hop; i++)
        {
            overlapLeft[i] += left[i];
            overlapRight[i] += right[i];
        }
        Emit(overlapLeft, overlapRight, Hop);
        Array.Copy(left, Hop, overlapLeft, 0, Hop);
        Array.Copy(right, Hop, overlapRight, 0, Hop);
        previousStart = start;
        frameIndex++;
        Discard();
        return true;
    }

    // Is input from `from` for `length` frames here (or, once input has ended, as much as there will be)?
    private bool Have(long from, long length) => finished || from + length <= inBase + inCount;

    private float Sample(float[] channel, long index)
    {
        var at = index - inBase;
        return at >= 0 && at < inCount ? channel[at] : 0;
    }

    // The piece start, near `nominal`, whose sound best continues the piece laid down last
    // (its "natural" continuation): normalised cross-correlation, coarse then fine.
    private long BestStart(long natural, long lowest, long highest)
    {
        var target = Slice(natural);
        var best = lowest;
        var bestScore = double.NegativeInfinity;
        for (var candidate = lowest; candidate <= highest; candidate += 4)
            Score(candidate, target, ref best, ref bestScore);
        var around = best;
        for (var candidate = Math.Max(lowest, around - 3); candidate <= Math.Min(highest, around + 3); candidate++)
            Score(candidate, target, ref best, ref bestScore);
        return best;
    }

    private void Score(long candidate, ReadOnlySpan<float> target, ref long best, ref double bestScore)
    {
        var piece = Slice(candidate);
        var dot = Dot(piece, target);
        var energy = Dot(piece, piece);
        var score = energy > 1e-12 ? dot / Math.Sqrt(energy) : 0;
        if (score > bestScore)
        {
            bestScore = score;
            best = candidate;
        }
    }

    // FrameLength frames of the mono sum from `from` (zeros past what has arrived).
    private ReadOnlySpan<float> Slice(long from)
    {
        var at = from - inBase;
        if (at >= 0 && at + FrameLength <= inCount) return inMono.AsSpan((int)at, FrameLength);
        var copy = new float[FrameLength];
        for (var i = 0; i < FrameLength; i++) copy[i] = Sample(inMono, from + i);
        return copy;
    }

    private static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
            sum += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        double total = Vector.Sum(sum);
        for (; i < a.Length; i++) total += a[i] * b[i];
        return total;
    }

    private void Emit(float[] left, float[] right, int count)
    {
        if (outStart + outCount + count > outLeft.Length)
        {
            // Slide what's waiting to the front before growing.
            Array.Copy(outLeft, outStart, outLeft, 0, outCount);
            Array.Copy(outRight, outStart, outRight, 0, outCount);
            outStart = 0;
            Grow(ref outLeft, outCount + count);
            Grow(ref outRight, outCount + count);
        }
        Array.Copy(left, 0, outLeft, outStart + outCount, count);
        Array.Copy(right, 0, outRight, outStart + outCount, count);
        outCount += count;
    }

    // Drops input no later piece can reach.
    private void Discard()
    {
        var nextLowest = Math.Max(0, (long)Math.Round(frameIndex * analysisHop) - Tolerance);
        var keepFrom = Math.Min(nextLowest, previousStart + Hop);
        var drop = (int)Math.Clamp(keepFrom - inBase, 0, inCount);
        if (drop == 0) return;
        Array.Copy(inLeft, drop, inLeft, 0, inCount - drop);
        Array.Copy(inRight, drop, inRight, 0, inCount - drop);
        Array.Copy(inMono, drop, inMono, 0, inCount - drop);
        inCount -= drop;
        inBase += drop;
    }

    private static void Grow(ref float[] buffer, int needed)
    {
        if (buffer.Length >= needed) return;
        Array.Resize(ref buffer, Math.Max(needed, buffer.Length * 2));
    }

    private static float[] BuildWindow()
    {
        var window = new float[FrameLength];
        for (var i = 0; i < FrameLength; i++) window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FrameLength));
        return window;
    }
}
