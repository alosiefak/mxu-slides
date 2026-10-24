namespace MxU.Slides.Audio.Multitrack;

/// <summary>
/// A stereo ring buffer between exactly one decoding thread (the writer) and the audio
/// thread (the reader), with no locks: each side only moves its own counter. The counters
/// count frames since the start, so "how much is buffered" is just written − read.
/// The reader never waits: if the writer falls behind it plays silence, counts the gap
/// as starved, and the writer later drops that many frames so the stem stays in step
/// with the others. Port of StemRing.swift.
/// </summary>
internal sealed class StemRing
{
    private readonly float[] left;
    private readonly float[] right;
    private long written;
    private long read;
    private long starved;

    public StemRing(int capacity)
    {
        Capacity = Math.Max(1, capacity);
        left = new float[Capacity];
        right = new float[Capacity];
    }

    public int Capacity { get; }

    public long WrittenFrames => Volatile.Read(ref written);
    public long ReadFrames => Volatile.Read(ref read);
    public long StarvedFrames => Volatile.Read(ref starved);

    public int Buffered => (int)Math.Max(0, WrittenFrames - ReadFrames);

    /// <summary>Frames the reader already played as silence that the writer must now skip.</summary>
    public int FramesToDrop => (int)Math.Max(0, ReadFrames - WrittenFrames);

    public int Space => Capacity - Buffered;

    /// <summary>Writer: skips <paramref name="count"/> frames the reader already passed.</summary>
    public void Drop(int count) => Volatile.Write(ref written, WrittenFrames + count);

    /// <summary>Writer: appends up to <paramref name="count"/> frames; returns how many fitted.</summary>
    public int Write(ReadOnlySpan<float> l, ReadOnlySpan<float> r, int count)
    {
        var w = WrittenFrames;
        var n = Math.Min(count, Capacity - (int)Math.Max(0, w - ReadFrames));
        if (n <= 0) return 0;
        var start = (int)(w % Capacity);
        var first = Math.Min(n, Capacity - start);
        l[..first].CopyTo(left.AsSpan(start));
        r[..first].CopyTo(right.AsSpan(start));
        if (first < n)
        {
            l[first..n].CopyTo(left);
            r[first..n].CopyTo(right);
        }
        Volatile.Write(ref written, w + n);
        return n;
    }

    /// <summary>Reader: jumps ahead without reading (a chase landing inside a song).</summary>
    public void Skip(int count) => Volatile.Write(ref read, ReadFrames + count);

    /// <summary>
    /// Reader: takes <paramref name="count"/> frames, always advancing by that many. What
    /// isn't there yet comes out as silence and is counted as starved.
    /// </summary>
    public void Read(Span<float> l, Span<float> r, int count)
    {
        var available = ReadInto(l, r, count);
        if (available < count)
        {
            l[available..count].Clear();
            r[available..count].Clear();
            Interlocked.Add(ref starved, count - available);
        }
        Volatile.Write(ref read, ReadFrames + count);
    }

    /// <summary>
    /// Reader: takes only what is there (up to <paramref name="count"/>) and returns how
    /// many. Used by the playlist decks, which would rather pause than skip.
    /// </summary>
    public int ReadAvailable(Span<float> l, Span<float> r, int count)
    {
        var available = ReadInto(l, r, count);
        Volatile.Write(ref read, ReadFrames + available);
        return available;
    }

    private int ReadInto(Span<float> l, Span<float> r, int count)
    {
        var rd = ReadFrames;
        var available = Math.Min(count, (int)Math.Max(0, WrittenFrames - rd));
        var start = (int)(rd % Capacity);
        var first = Math.Min(available, Capacity - start);
        left.AsSpan(start, first).CopyTo(l);
        right.AsSpan(start, first).CopyTo(r);
        if (first < available)
        {
            left.AsSpan(0, available - first).CopyTo(l[first..]);
            right.AsSpan(0, available - first).CopyTo(r[first..]);
        }
        return available;
    }
}
