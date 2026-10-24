using System.Text;

namespace MxU.Slides.Audio.Timeline;

/// <summary>
/// A file's waveform: the lowest and highest sample in each 10 ms "bucket", which is all a
/// timeline needs to draw it at any zoom. Saved as a small binary cache file. Port of
/// PresenterCore's Waveform (Waveforms.swift).
/// </summary>
public sealed class Waveform : IEquatable<Waveform>
{
    public const double DefaultBucketsPerSecond = 100;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MXW1");

    public Waveform(float[] mins, float[] maxs, double bucketsPerSecond = DefaultBucketsPerSecond)
    {
        BucketsPerSecond = bucketsPerSecond;
        Mins = mins;
        Maxs = maxs;
    }

    public double BucketsPerSecond { get; }

    public float[] Mins { get; }

    public float[] Maxs { get; }

    public int Count => Maxs.Length;

    public double Seconds => Count / BucketsPerSecond;

    /// <summary>The gain that makes the loudest peak touch the lane's edges (0 for silence).</summary>
    public float FillScale
    {
        get
        {
            var peak = MathF.Max(Maxs.Length == 0 ? 0 : Maxs.Max(), -(Mins.Length == 0 ? 0 : Mins.Min()));
            return peak > 0 ? 1 / peak : 0;
        }
    }

    /// <summary>Every <paramref name="factor"/> buckets merged into one (for zoomed-out drawing).</summary>
    public Waveform Reduced(int factor)
    {
        if (factor <= 1) return this;
        var count = (Count + factor - 1) / factor;
        var mins = new float[count];
        var maxs = new float[count];
        for (var bucket = 0; bucket < count; bucket++)
        {
            var start = bucket * factor;
            var end = Math.Min(start + factor, Count);
            mins[bucket] = Mins[start..end].Min();
            maxs[bucket] = Maxs[start..end].Max();
        }
        return new Waveform(mins, maxs, BucketsPerSecond / factor);
    }

    /// <summary>"MXW1", the bucket rate, the count, then the mins and maxs (little-endian).</summary>
    public byte[] Encoded()
    {
        var bytes = new byte[Magic.Length + 16 + Count * 8];
        Magic.CopyTo(bytes, 0);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), BucketsPerSecond);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), (long)Count);
        Buffer.BlockCopy(Mins, 0, bytes, 20, Count * 4);
        Buffer.BlockCopy(Maxs, 0, bytes, 20 + Count * 4, Count * 4);
        return bytes;
    }

    /// <summary>A cache file read back; null when it isn't one or is torn.</summary>
    public static Waveform? Decode(ReadOnlySpan<byte> data)
    {
        const int header = 4 + 16;
        if (data.Length < header || !data[..4].SequenceEqual(Magic)) return null;
        var rate = BitConverter.ToDouble(data[4..12]);
        var count = BitConverter.ToInt64(data[12..20]);
        if (!(rate > 0) || count < 0 || count > int.MaxValue / 8 || data.Length != header + count * 8) return null;
        var mins = new float[count];
        var maxs = new float[count];
        data.Slice(header, (int)count * 4).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(mins.AsSpan()));
        data.Slice(header + (int)count * 4, (int)count * 4).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(maxs.AsSpan()));
        return new Waveform(mins, maxs, rate);
    }

    public bool Equals(Waveform? other) =>
        other is not null && BucketsPerSecond == other.BucketsPerSecond && Mins.SequenceEqual(other.Mins) && Maxs.SequenceEqual(other.Maxs);

    public override bool Equals(object? obj) => Equals(obj as Waveform);

    public override int GetHashCode() => HashCode.Combine(BucketsPerSecond, Count);
}

/// <summary>
/// Builds a <see cref="Waveform"/> as audio is read, buffer by buffer, across all channels.
/// Bucket edges fall on rounded frame positions so they never drift. Port of WaveformBucketer.
/// </summary>
public sealed class WaveformBucketer
{
    private readonly double framesPerBucket;
    private readonly double bucketsPerSecond;
    private readonly List<float> mins = [];
    private readonly List<float> maxs = [];
    private float low = float.PositiveInfinity;
    private float high = float.NegativeInfinity;
    private long frames;

    public WaveformBucketer(double sampleRate, double bucketsPerSecond = Waveform.DefaultBucketsPerSecond)
    {
        framesPerBucket = sampleRate / bucketsPerSecond;
        this.bucketsPerSecond = bucketsPerSecond;
    }

    /// <summary>Adds <paramref name="frameCount"/> frames (each channel's samples in its own array).</summary>
    public void Add(IReadOnlyList<float[]> channels, int frameCount)
    {
        var start = 0;
        while (start < frameCount)
        {
            var remaining = Math.Max((int)(Math.Round((mins.Count + 1) * framesPerBucket, MidpointRounding.AwayFromZero) - frames), 1);
            var take = Math.Min(remaining, frameCount - start);
            foreach (var channel in channels)
            {
                for (var index = start; index < start + take; index++)
                {
                    low = MathF.Min(low, channel[index]);
                    high = MathF.Max(high, channel[index]);
                }
            }
            frames += take;
            start += take;
            if (take == remaining) CloseBucket();
        }
    }

    private void CloseBucket()
    {
        mins.Add(float.IsFinite(low) ? low : 0);
        maxs.Add(float.IsFinite(high) ? high : 0);
        low = float.PositiveInfinity;
        high = float.NegativeInfinity;
    }

    /// <summary>The waveform so far (a part-filled last bucket included). Doesn't change the bucketer.</summary>
    public Waveform Finished()
    {
        var finalMins = mins.ToList();
        var finalMaxs = maxs.ToList();
        if (float.IsFinite(low))
        {
            finalMins.Add(low);
            finalMaxs.Add(high);
        }
        return new Waveform([.. finalMins], [.. finalMaxs], bucketsPerSecond);
    }
}

/// <summary>One stem's waveform placed on a song: where it starts, how far into the file, how long.</summary>
public sealed record WaveformPart(Waveform Waveform, double At, double InPoint = 0, double? Duration = null);

/// <summary>Combining stems into a song's overview, and choosing what to draw. Port of Waveforms.</summary>
public static class Waveforms
{
    /// <summary>
    /// The song's waveform from <paramref name="origin"/> for <paramref name="length"/> seconds:
    /// the stems summed by power (so two equal stems read √2 louder, not twice).
    /// </summary>
    public static Waveform Combined(IReadOnlyList<WaveformPart> parts, double origin, double length)
    {
        var rate = parts.Count > 0 ? parts[0].Waveform.BucketsPerSecond : Waveform.DefaultBucketsPerSecond;
        var count = Math.Max((int)Math.Ceiling(length * rate), 0);
        var highs = new float[count];
        var lows = new float[count];
        foreach (var part in parts)
        {
            var wave = part.Waveform;
            var playEnd = part.At + Math.Min(part.Duration ?? double.PositiveInfinity, wave.Seconds - part.InPoint);
            var first = Math.Max((int)Math.Floor((part.At - origin) * rate), 0);
            var last = (int)Math.Min(Math.Ceiling((playEnd - origin) * rate), count);
            for (var index = first; index < last; index++)
            {
                var time = origin + index / rate - part.At + part.InPoint;
                var source = (int)Math.Floor(time * wave.BucketsPerSecond);
                if (source < 0 || source >= wave.Count) continue;
                highs[index] += wave.Maxs[source] * wave.Maxs[source];
                lows[index] += wave.Mins[source] * wave.Mins[source];
            }
        }
        return new Waveform(lows.Select(v => -MathF.Sqrt(v)).ToArray(), highs.Select(MathF.Sqrt).ToArray(), rate);
    }

    /// <summary>
    /// The stretch of a lane (in points) worth drawing: the visible part plus a margin,
    /// moving in half-view steps so scrolling redraws rarely.
    /// </summary>
    public static (double Lower, double Upper) DrawnWindow(double visibleMinX, double visibleWidth)
    {
        var step = Math.Max(visibleWidth / 2, 64);
        var margin = Math.Max(visibleWidth, 128);
        var baseX = Math.Floor(visibleMinX / step) * step;
        return (baseX - margin, baseX + visibleWidth + margin);
    }
}

/// <summary>A waveform at every power-of-two zoom, so drawing picks about one bucket per point. Port of WaveformLevels.</summary>
public sealed class WaveformLevels
{
    public WaveformLevels(Waveform baseWaveform)
    {
        var levels = new List<Waveform> { baseWaveform };
        while (levels[^1].BucketsPerSecond / 2 >= 1 && levels[^1].Count > 1) levels.Add(levels[^1].Reduced(2));
        Levels = levels;
        FillScale = baseWaveform.FillScale;
    }

    public IReadOnlyList<Waveform> Levels { get; }

    public float FillScale { get; }

    /// <summary>The coarsest level that still has a bucket for every point at this zoom.</summary>
    public Waveform Level(double pointsPerSecond) => Levels.LastOrDefault(l => l.BucketsPerSecond >= pointsPerSecond) ?? Levels[0];
}
