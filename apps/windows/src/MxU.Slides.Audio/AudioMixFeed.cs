using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Audio;

/// <summary>
/// Sums a mix for a stream or recording on its own steady 10 ms clock. Members (a playlist
/// bus, a microphone, the tracks…) push whatever they have whenever it arrives; every tick
/// takes 480 frames (10 ms at 48 kHz) from each member, adds them up, and hands the
/// result to the consumer. A member that runs ahead for a long while is trimmed back to
/// 20 ms of backlog so the stream doesn't drift late. Port of AudioMixFeed.swift.
/// </summary>
public sealed class AudioMixFeed : IDisposable
{
    /// <summary>Receives each tick's mix: 48 kHz stereo, and the host-clock time it stands for.</summary>
    public delegate void Consumer(PcmBuffer buffer, double hostSeconds);

    internal const int MaxRingFrames = 24_000;
    private const int TickFrames = 480;
    private const double TickSeconds = 0.010;

    internal const int MaxCatchUpTicks = 25;
    internal const int TrimWindowTicks = 200;
    internal const int TrimThresholdFrames = 2_880;
    internal const int TrimTargetFrames = 960;

    // One member's queued audio (gain and pan already applied).
    private sealed class Ring
    {
        private float[] left = new float[MaxRingFrames + TickFrames];
        private float[] right = new float[MaxRingFrames + TickFrames];
        private int head;

        public int Count { get; private set; }

        // The lowest backlog seen since the last trim: a member whose backlog never dips
        // below the threshold for the whole window is running steadily late.
        public int Trough = int.MaxValue;

        public void Push(PcmBuffer buffer, float gain, float pan)
        {
            if (!(gain > 0) || buffer.FrameLength == 0) return;
            var leftGain = gain * MathF.Min(1, 1 - pan);
            var rightGain = gain * MathF.Min(1, 1 + pan);
            var frames = buffer.FrameLength;
            var rightChannel = buffer.ChannelCount > 1 ? 1 : 0;
            EnsureRoom(frames);
            for (var frame = 0; frame < frames; frame++)
            {
                var at = (head + Count + frame) % left.Length;
                left[at] = buffer.Data[0][frame] * leftGain;
                right[at] = buffer.Data[rightChannel][frame] * rightGain;
            }
            Count += frames;
            if (Count > MaxRingFrames) Remove(Count - MaxRingFrames);
        }

        public void Drain(float[] leftOut, float[] rightOut, int frames)
        {
            var available = Math.Min(frames, Count);
            for (var frame = 0; frame < available; frame++)
            {
                var at = (head + frame) % left.Length;
                leftOut[frame] += left[at];
                rightOut[frame] += right[at];
            }
            Remove(available);
            Trough = Math.Min(Trough, Count);
        }

        public int TrimExcess(int threshold, int target)
        {
            var excess = Trough == int.MaxValue || Trough <= threshold ? 0 : Trough - target;
            Trough = int.MaxValue;
            if (excess > 0) Remove(excess);
            return excess;
        }

        private void Remove(int frames)
        {
            frames = Math.Min(frames, Count);
            head = (head + frames) % left.Length;
            Count -= frames;
        }

        private void EnsureRoom(int frames)
        {
            if (Count + frames <= left.Length) return;
            var size = Math.Max(left.Length * 2, Count + frames);
            var newLeft = new float[size];
            var newRight = new float[size];
            for (var frame = 0; frame < Count; frame++)
            {
                newLeft[frame] = left[(head + frame) % left.Length];
                newRight[frame] = right[(head + frame) % left.Length];
            }
            left = newLeft;
            right = newRight;
            head = 0;
        }
    }

    private readonly Lock gate = new();
    private readonly Dictionary<string, Ring> rings = new(StringComparer.Ordinal);
    private readonly Consumer consumer;
    private readonly ITimer? timer;
    private int ticking;
    private (string Member, Consumer Consumer)? partial;
    private double? clockStart;
    private int pulls;
    private int ticksSinceTrim;
    private long trimmedFrames;

    /// <param name="startsClock">False for tests, which call <see cref="Tick"/> themselves.</param>
    public AudioMixFeed(Consumer consumer, bool startsClock = true)
    {
        this.consumer = consumer;
        if (startsClock) timer = ShowTimeProvider.Shared.CreateTimer(_ => TimerFired(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
    }

    public void Dispose() => timer?.Dispose();

    private void TimerFired()
    {
        // Windows timers fire late and in bursts; one tick at a time, and Tick catches up.
        if (Interlocked.Exchange(ref ticking, 1) == 1) return;
        try { Tick(ShowState.HostClock()); }
        finally { Volatile.Write(ref ticking, 0); }
    }

    /// <summary>Queues a member's audio, at its gain and pan (-1 left … 1 right).</summary>
    public void Push(string member, PcmBuffer buffer, float gain = 1, float pan = 0)
    {
        lock (gate)
        {
            if (!rings.TryGetValue(member, out var ring)) rings[member] = ring = new Ring();
            ring.Push(buffer, gain, Math.Clamp(pan, -1, 1));
        }
    }

    /// <summary>The deepest member backlog, in milliseconds.</summary>
    public double BacklogMilliseconds
    {
        get
        {
            int frames;
            lock (gate) frames = rings.Values.Select(r => r.Count).DefaultIfEmpty(0).Max();
            return frames / 48_000.0 * 1000;
        }
    }

    public void RemoveMember(string member)
    {
        lock (gate) rings.Remove(member);
    }

    /// <summary>A second consumer that gets the same mix without one member (a stream that carries the tracks separately).</summary>
    public void SetPartialConsumer(string excluding, Consumer? partialConsumer)
    {
        lock (gate) partial = partialConsumer is null ? null : (excluding, partialConsumer);
    }

    /// <summary>Audio thrown away by trimming, in milliseconds (diagnostics).</summary>
    public double TrimmedMilliseconds => Interlocked.Read(ref trimmedFrames) / 48_000.0 * 1000;

    /// <summary>
    /// Pulls every 10 ms the clock owes since it started, stamping each with its nominal
    /// time; after a long stall (over 250 ms) it forgives the debt and starts again from now.
    /// </summary>
    internal void Tick(double now)
    {
        var start = clockStart ?? now;
        clockStart = start;
        var owed = (int)Math.Floor((now - start) / TickSeconds) + 1 - pulls;
        if (owed > MaxCatchUpTicks)
        {
            clockStart = now;
            pulls = 0;
            owed = 1;
        }
        for (var index = 0; index < Math.Max(owed, 0); index++)
        {
            Pull((clockStart ?? now) + pulls * TickSeconds);
            pulls++;
        }
    }

    private void Pull(double hostSeconds)
    {
        var left = new float[TickFrames];
        var right = new float[TickFrames];
        ticksSinceTrim++;
        var trimDue = ticksSinceTrim >= TrimWindowTicks;
        if (trimDue) ticksSinceTrim = 0;
        (float[] Left, float[] Right)? dry = null;
        (string Member, Consumer Consumer)? partialNow;
        lock (gate)
        {
            var excluded = partial?.Member;
            // The excluded member goes last, so the mix without it can be copied off just before.
            var order = rings.Keys.Where(k => k != excluded).ToList();
            if (excluded is not null && rings.ContainsKey(excluded)) order.Add(excluded);
            foreach (var key in order)
            {
                if (key == excluded) dry = ((float[])left.Clone(), (float[])right.Clone());
                rings[key].Drain(left, right, TickFrames);
                if (trimDue) Interlocked.Add(ref trimmedFrames, rings[key].TrimExcess(TrimThresholdFrames, TrimTargetFrames));
            }
            partialNow = partial;
        }
        var output = Buffer(left, right);
        consumer(output, hostSeconds);
        if (partialNow is { } other) other.Consumer(dry is { } d ? Buffer(d.Left, d.Right) : output, hostSeconds);
    }

    private static PcmBuffer Buffer(float[] left, float[] right)
    {
        var output = new PcmBuffer(48_000, 2, left.Length) { FrameLength = left.Length };
        for (var frame = 0; frame < left.Length; frame++)
        {
            output.Data[0][frame] = Math.Clamp(left[frame], -1, 1);
            output.Data[1][frame] = Math.Clamp(right[frame], -1, 1);
        }
        return output;
    }
}
