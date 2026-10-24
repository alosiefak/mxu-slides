using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace MxU.Slides.Audio.Multitrack;

/// <summary>
/// Where the tracks player leaves each streamed mix for the app to pick up: 32 slots, each
/// a one-second stereo ring with a frame counter. The writer (the audio thread) never
/// waits; a reader just follows the counter. It can be plain memory (one process) or a
/// named shared-memory mapping, so a tracks player running as its own process can hand
/// mixes to the app. Port of TracksShare.swift (POSIX shm there, a Windows mapping here).
/// </summary>
public sealed unsafe class TracksShare : IDisposable
{
    public const int Slots = 32;
    public const int SlotFrames = 48_000;
    private const long Magic = 0x4D78_5554_726B_0002;
    private const int HeaderBytes = 64;
    private const int LineBytes = 64;

    internal static long ByteCount => HeaderBytes + Slots * LineBytes + (long)Slots * 2 * SlotFrames * sizeof(float);

    private readonly byte* memory;
    private readonly bool owned;
    private readonly MemoryMappedFile? mapping;
    private readonly MemoryMappedViewAccessor? view;
    private readonly long[] written = new long[Slots];
    private bool disposed;

    private TracksShare(byte* memory, bool owned, MemoryMappedFile? mapping, MemoryMappedViewAccessor? view)
    {
        this.memory = memory;
        this.owned = owned;
        this.mapping = mapping;
        this.view = view;
        for (var slot = 0; slot < Slots; slot++) written[slot] = Volatile.Read(ref *Counter(slot));
    }

    /// <summary>A share in this process's own memory.</summary>
    public TracksShare() : this(Allocate(), owned: true, null, null) => Stamp();

    ~TracksShare() => Release();

    private static byte* Allocate()
    {
        var block = (byte*)NativeMemory.AlignedAlloc((nuint)ByteCount, 64);
        NativeMemory.Clear(block, (nuint)ByteCount);
        return block;
    }

    /// <summary>Creates the named share a separate tracks player writes; null if it can't (one already exists).</summary>
    public static TracksShare? Create(string name)
    {
        MemoryMappedFile? mapping = null;
        try
        {
            mapping = MemoryMappedFile.CreateNew(name, ByteCount, MemoryMappedFileAccess.ReadWrite);
            var view = mapping.CreateViewAccessor(0, ByteCount, MemoryMappedFileAccess.ReadWrite);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            NativeMemory.Clear(pointer, (nuint)ByteCount);
            var share = new TracksShare(pointer, owned: false, mapping, view);
            share.Stamp();
            return share;
        }
        catch
        {
            mapping?.Dispose();
            return null;
        }
    }

    /// <summary>Opens a share another process created (read-only); null when there isn't one.</summary>
    public static TracksShare? Open(string name)
    {
        MemoryMappedFile? mapping = null;
        try
        {
            mapping = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (view.Capacity < ByteCount)
            {
                view.Dispose();
                mapping.Dispose();
                return null;
            }
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            var header = (long*)pointer;
            if (Volatile.Read(ref header[0]) != Magic || Volatile.Read(ref header[1]) != SlotFrames)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                view.Dispose();
                mapping.Dispose();
                return null;
            }
            return new TracksShare(pointer, owned: false, mapping, view);
        }
        catch
        {
            mapping?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Stops sharing by name. (Windows removes a mapping once every process has closed it,
    /// so a reader still holding it open keeps it alive; POSIX unlinks the name at once.)
    /// </summary>
    public void Remove() => Dispose();

    private void Stamp()
    {
        var header = (long*)memory;
        Volatile.Write(ref header[1], SlotFrames);
        Volatile.Write(ref header[0], Magic);
    }

    private long* Counter(int slot) => (long*)(memory + HeaderBytes + slot * LineBytes);

    private float* Samples(int slot, int side) =>
        (float*)(memory + HeaderBytes + Slots * LineBytes) + ((long)slot * 2 + side) * SlotFrames;

    /// <summary>The rate the player renders at (the device's), so readers can convert.</summary>
    public double SampleRate
    {
        get => BitConverter.Int64BitsToDouble(Volatile.Read(ref ((long*)memory)[2]));
        set => Volatile.Write(ref ((long*)memory)[2], BitConverter.DoubleToInt64Bits(value));
    }

    /// <summary>Appends <paramref name="count"/> frames to a slot. One writer per slot.</summary>
    internal void Write(int slot, ReadOnlySpan<float> left, ReadOnlySpan<float> right, int count) => Append(slot, left, right, count, silent: false);

    /// <summary>Appends silence (the part of a block before a scheduled start).</summary>
    internal void WriteSilence(int slot, int count) => Append(slot, default, default, count, silent: true);

    private void Append(int slot, ReadOnlySpan<float> left, ReadOnlySpan<float> right, int count, bool silent)
    {
        if (slot < 0 || slot >= Slots || disposed) return;
        var position = written[slot];
        var done = 0;
        while (done < count)
        {
            var start = (int)(position % SlotFrames);
            var run = Math.Min(count - done, SlotFrames - start);
            var leftTarget = new Span<float>(Samples(slot, 0) + start, run);
            var rightTarget = new Span<float>(Samples(slot, 1) + start, run);
            if (silent)
            {
                leftTarget.Clear();
                rightTarget.Clear();
            }
            else
            {
                left.Slice(done, run).CopyTo(leftTarget);
                right.Slice(done, run).CopyTo(rightTarget);
            }
            position += run;
            done += run;
        }
        written[slot] = position;
        Volatile.Write(ref *Counter(slot), position);
    }

    public long WrittenFrames(int slot) => Volatile.Read(ref *Counter(slot));

    /// <summary>Copies <paramref name="count"/> frames of a slot starting at frame <paramref name="from"/>.</summary>
    public void Read(int slot, long from, int count, Span<float> left, Span<float> right)
    {
        var position = from;
        var done = 0;
        while (done < count)
        {
            var start = (int)(((position % SlotFrames) + SlotFrames) % SlotFrames);
            var run = Math.Min(count - done, SlotFrames - start);
            new ReadOnlySpan<float>(Samples(slot, 0) + start, run).CopyTo(left[done..]);
            new ReadOnlySpan<float>(Samples(slot, 1) + start, run).CopyTo(right[done..]);
            position += run;
            done += run;
        }
    }

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    private void Release()
    {
        if (disposed) return;
        disposed = true;
        if (owned) NativeMemory.AlignedFree(memory);
        if (view is not null)
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
            view.Dispose();
        }
        mapping?.Dispose();
    }
}

/// <summary>
/// Reads the share's mixes back on a 10 ms clock and hands each slot's new audio, at 48 kHz,
/// to a sink (the app's mix feeds). If it falls more than 40 ms behind it skips to just
/// behind the writer rather than drift late. Port of TracksShareFeeder.swift.
/// </summary>
public sealed class TracksShareFeeder : IDisposable
{
    internal const double MaxLagSeconds = 0.04;
    internal const double KeepSeconds = 0.01;
    private const int OutputRate = 48_000;

    private readonly TracksShare share;
    private readonly Action<int, PcmBuffer> sink;
    private readonly Lock gate = new();
    private readonly Dictionary<int, long> positions = [];
    private readonly Dictionary<int, Decoding.SincResampler> converters = [];
    private Timer? timer;
    private double rate;
    private long droppedFrames;

    /// <param name="sink">Gets (slot, 48 kHz stereo audio) on the feeder's timer thread.</param>
    public TracksShareFeeder(TracksShare share, Action<int, PcmBuffer> sink, bool startsClock = true)
    {
        this.share = share;
        this.sink = sink;
        if (startsClock) timer = new Timer(_ => TryDrain(), null, 0, 10);
    }

    /// <summary>Frames skipped because the reader fell behind (diagnostics).</summary>
    public long DroppedFrames => Interlocked.Read(ref droppedFrames);

    public void Stop()
    {
        lock (gate)
        {
            timer?.Dispose();
            timer = null;
        }
    }

    public void Dispose() => Stop();

    private void TryDrain()
    {
        if (!gate.TryEnter()) return;
        try { DrainLocked(); }
        finally { gate.Exit(); }
    }

    internal void DrainNow()
    {
        lock (gate) DrainLocked();
    }

    private void DrainLocked()
    {
        var shareRate = share.SampleRate;
        if (!(shareRate > 0)) return;
        if (shareRate != rate)
        {
            rate = shareRate;
            converters.Clear();
        }
        var lag = (long)(MaxLagSeconds * shareRate);
        var keep = (long)(KeepSeconds * shareRate);
        for (var slot = 0; slot < TracksShare.Slots; slot++)
        {
            var written = share.WrittenFrames(slot);
            var from = positions.TryGetValue(slot, out var held) ? Math.Min(held, written) : Math.Max(0, written - keep);
            if (written - from > lag)
            {
                Interlocked.Add(ref droppedFrames, written - keep - from);
                from = written - keep;
            }
            positions[slot] = written;
            var count = (int)(written - from);
            if (count <= 0) continue;
            var buffer = new PcmBuffer((int)Math.Round(shareRate), 2, count) { FrameLength = count };
            share.Read(slot, from, count, buffer.Data[0], buffer.Data[1]);
            // If the writer lapped the ring while we copied, what we read is torn: skip it.
            if (share.WrittenFrames(slot) - from > TracksShare.SlotFrames) continue;
            if (Convert(buffer, slot) is { } converted) sink(slot, converted);
        }
    }

    private PcmBuffer? Convert(PcmBuffer buffer, int slot)
    {
        if (buffer.SampleRate == OutputRate) return buffer;
        if (!converters.TryGetValue(slot, out var converter))
            converters[slot] = converter = new Decoding.SincResampler(buffer.SampleRate, OutputRate, 2);
        var output = new PcmBuffer(OutputRate, 2, converter.MaxOutput(buffer.FrameLength));
        output.FrameLength = converter.Process(buffer.Data, 0, buffer.FrameLength, output.Data, 0);
        return output.FrameLength > 0 ? output : null;
    }
}
